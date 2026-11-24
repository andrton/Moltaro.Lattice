using System.Buffers;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Validates collection keys in a checked root whose newer local state makes placement
// unnecessary. It walks the checked record blocks in wire order, retaining key slices
// only until a collection is checked. It owns no arena blocks.
internal readonly struct SkippedCollections(LatticeStringEncodingEnum encoding, ulong seed)
{
    public void Check(in StoredField place, StructureLayout element, in ValueDescriptor descriptor, in ReadOnlySequence<byte> block, ref ReadOnlySequence<byte> values)
    {
        var count = element.StoredEntryCount(descriptor);
        if (count == 0)
        {
            return;
        }

        var associative = place.Type is StoredTypeEnum.Map or StoredTypeEnum.Set;
        if (!element.HoldsDescriptors)
        {
            var bytes = TakeBytes(descriptor, block, ref values);
            if (associative && count > 1)
            {
                CheckPacked(place, element, bytes, count);
            }

            return;
        }

        var keys = associative && count > 1 ? ArrayPool<WireKey>.Shared.Rent(count) : null;
        try
        {
            ScanRecords(element, count, keys, ref values);
            if (keys is not null)
            {
                var source = new WireCollectionKeys(element, encoding, seed, keys.AsSpan(0, count), packed: false);
                CheckUnique(place, element, count, ref source);
            }
        }
        finally
        {
            if (keys is not null)
            {
                ArrayPool<WireKey>.Shared.Return(keys, clearArray: true);
            }
        }
    }

    private void CheckPacked(in StoredField place, StructureLayout element, ReadOnlySequence<byte> bytes, int count)
    {
        // Seek within a group rather than from the start of a possibly segmented value.
        var perGroup = WireCollectionKeys.PackedPerGroup(element);
        var groups = (count + perGroup - 1) / perGroup;
        var keys = ArrayPool<WireKey>.Shared.Rent(groups);
        try
        {
            for (var group = 0; group < groups; group++)
            {
                var length = Math.Min(perGroup * element.Size, bytes.Length);
                keys[group] = new WireKey(bytes.Slice(0, length), IsNull: false);
                bytes = bytes.Slice(length);
            }

            var source = new WireCollectionKeys(element, encoding, seed, keys.AsSpan(0, groups), packed: true);
            CheckUnique(place, element, count, ref source);
        }
        finally
        {
            ArrayPool<WireKey>.Shared.Return(keys, clearArray: true);
        }
    }

    private static void CheckUnique(in StoredField place, StructureLayout element, int count, ref WireCollectionKeys keys)
    {
        var capacity = count <= CollectionLookup.MostCompactEntries ? 0 : CollectionLookup.SlotsNeeded(count);
        var slots = capacity == 0 ? null : ArrayPool<ulong>.Shared.Rent(capacity);
        try
        {
            var table = new WireSlots(slots.AsSpan(0, capacity));
            var duplicate = CollectionLookup.AddEntries(ref keys, ref table, count);
            if (duplicate >= 0)
            {
                throw NestedValues.Duplicate(place, element, duplicate);
            }
        }
        finally
        {
            if (slots is not null)
            {
                ArrayPool<ulong>.Shared.Return(slots);
            }
        }
    }

    private void ScanRecords(StructureLayout element, int count, WireKey[]? keys, ref ReadOnlySequence<byte> values)
    {
        ref readonly var key = ref element.Place(0);
        Span<byte> bytes = stackalloc byte[ValueBlocks.DescriptorSize];
        for (var first = 0; first < count; first += element.RecordsPerChunk)
        {
            var records = Math.Min(element.RecordsPerChunk, count - first);
            var length = ReceivedRecords.BlockLength(element, records, values);
            var block = values.Slice(0, length);
            values = values.Slice(length);
            for (var record = 0; record < records; record++)
            {
                var ordinal = first + record;
                var at = record * element.Size;
                var recordBytes = block.Slice(at, element.Size);
                var nullKey = keys is not null && WireCollectionKeys.IsNull(key, recordBytes);
                if (keys is not null)
                {
                    keys[ordinal] = key.Type == StoredTypeEnum.String || nullKey
                        ? new WireKey(default, nullKey)
                        : new WireKey(recordBytes.Slice(key.Offset, key.Width), IsNull: false);
                }

                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    ref readonly var field = ref element.DescriptorField(index);
                    recordBytes.Slice(field.Offset, ValueBlocks.DescriptorSize).CopyTo(bytes);
                    var descriptor = ValueBlocks.Read(bytes);
                    if (element.DescriptorElement(index) is { } nested)
                    {
                        Check(field, nested, descriptor, block, ref values);
                    }
                    else
                    {
                        var value = TakeBytes(descriptor, block, ref values);
                        if (keys is not null && !nullKey && key.Type == StoredTypeEnum.String && field.Offset == key.Offset)
                        {
                            keys[ordinal] = new WireKey(value, IsNull: false);
                        }
                    }
                }
            }
        }
    }

    private static ReadOnlySequence<byte> TakeBytes(in ValueDescriptor descriptor, in ReadOnlySequence<byte> block, ref ReadOnlySequence<byte> values)
    {
        if (descriptor.Kind == DescriptorKindEnum.Inline)
        {
            return block.Slice((long)descriptor.Location - BlockHeader.Size, descriptor.Length);
        }

        var bytes = values.Slice(0, descriptor.Length);
        values = values.Slice(descriptor.Length);
        return bytes;
    }

    private readonly ref struct WireSlots : ICollectionSlots
    {
        private readonly Span<ulong> _slots;

        public WireSlots(Span<ulong> slots)
        {
            _slots = slots;
            _slots.Clear();
        }

        public int Capacity => _slots.Length;

        public ref ulong Slot(int index) => ref _slots[index];
    }
}
