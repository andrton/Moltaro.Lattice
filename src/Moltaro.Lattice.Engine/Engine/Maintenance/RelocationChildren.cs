using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Maintenance;

// Validates the direct edges of a previously resolved live holder under
// Writes. Publication uses a second pass over those same immutable edge
// positions, after the destination has been reserved and copied.
internal static class RelocationChildren
{
    public static bool Validate(Arena arena, in RelocationBlock parent)
    {
        var count = parent.EdgeCount;
        var payload = count == 0 ? default : arena.Payload(parent.Address);
        for (var index = 0; index < count; index++)
        {
            if (!TryRead(arena, parent, payload, index, out _, out _))
            {
                return false;
            }
        }

        return true;
    }

    public static void Adopt(Arena arena, in RelocationBlock parent, ArenaAddress replacement)
    {
        var count = parent.EdgeCount;
        var payload = count == 0 ? default : arena.Payload(parent.Address);
        for (var index = 0; index < count; index++)
        {
            var child = parent.IsLookupWord(index)
                ? CollectionLookup.ReadChunk(CollectionLookup.ChunkAddresses(payload), index - 1)
                : RootStorage.OwnedAddress(ReadDescriptor(payload, parent.OffsetOf(index)));

            if (!child.IsNull)
            {
                arena.Header(child).Parent = replacement;
            }
        }
    }

    public static bool TryRead(Arena arena, in RelocationBlock parent, Span<byte> payload, int index,
        out RelocationBlock child, out DescriptorKindEnum descriptorKind)
    {
        child = default;
        descriptorKind = default;
        var offset = parent.OffsetOf(index);
        if (parent.IsLookupWord(index))
        {
            return TryReadLookupChunk(arena, parent, payload, index, offset, out child);
        }

        var edge = ReadDescriptor(payload, offset);
        descriptorKind = edge.Kind;
        if (parent.Header.Kind == BlockKindEnum.ChunkDirectory)
        {
            return TryReadDirectoryChunk(arena, parent, index, offset, edge, out child);
        }

        var element = parent.Element(index);
        if (parent.Header.Kind == BlockKindEnum.Lookup)
        {
            if (!MatchesLookupEntries(parent, edge))
            {
                return false;
            }
        }
        else if (!HasOwnedBlock(parent, index, element, edge))
        {
            return IsUnownedValue(parent, index, element, edge);
        }

        var address = ValueBlocks.AddressOf(edge);
        if (!TryReadHeader(arena, parent, offset, address, out var header))
        {
            return false;
        }

        return TryDescribeValue(address, header, element, edge, out child);
    }

    private static bool HasOwnedBlock(in RelocationBlock parent, int index, StructureLayout? element, in ValueDescriptor edge) =>
        edge.Kind switch
        {
            DescriptorKindEnum.Block or DescriptorKindEnum.Chunked =>
                element is null
                    ? parent.FieldType(index) is StoredTypeEnum.String or StoredTypeEnum.Binary
                    : !element.HoldsDescriptors && edge.Length % (uint)element.Size == 0,
            DescriptorKindEnum.Records => element is { HoldsDescriptors: true } && edge.Length != 0,
            DescriptorKindEnum.Lookup => element?.Collection?.Type is LatticeFieldTypeEnum.Map or LatticeFieldTypeEnum.Set,
            _ => false,
        };

    private static bool IsUnownedValue(in RelocationBlock parent, int index, StructureLayout? element, in ValueDescriptor edge)
    {
        return edge.Kind switch
        {
            DescriptorKindEnum.Null => edge.Location == 0 && edge.Length == 0,
            DescriptorKindEnum.Inline => IsInline(parent, edge),
            DescriptorKindEnum.Records => element is { HoldsDescriptors: true } && edge.Location == 0 && edge.Length == 0,
            DescriptorKindEnum.Text => parent.FieldType(index) == StoredTypeEnum.String && edge.Location != 0 && edge.Length != 0,
            _ => false,
        };
    }

    private static bool IsInline(in RelocationBlock parent, in ValueDescriptor edge)
    {
        var end = BlockHeader.Size + parent.Header.PayloadLength;
        return edge.Location >= (ulong)parent.InlineStart && edge.Location <= end && edge.Length <= end - edge.Location;
    }

    private static bool MatchesLookupEntries(in RelocationBlock lookup, in ValueDescriptor edge)
    {
        var element = lookup.Layout!;
        return element.HoldsDescriptors
            ? edge.Kind == DescriptorKindEnum.Records && edge.Length == (uint)lookup.Length
            : edge.Kind is DescriptorKindEnum.Block or DescriptorKindEnum.Chunked && edge.Length == (uint)(element.Size * lookup.Length);
    }

    private static bool TryDescribeValue(ArenaAddress address, in BlockHeader header, StructureLayout? element, in ValueDescriptor edge,
        out RelocationBlock child)
    {
        var length = (int)edge.Length;
        if (edge.Kind == DescriptorKindEnum.Records)
        {
            return TryDescribeRecords(address, header, element!, length, out child);
        }

        if (edge.Kind == DescriptorKindEnum.Lookup)
        {
            return TryDescribeLookup(address, header, element!, length, out child);
        }

        child = new RelocationBlock(address, header, null, length);
        return header.Flags == 0 && (edge.Kind == DescriptorKindEnum.Chunked
            ? RelocationEdges.IsRawDirectory(header, edge.Length)
            : header.Kind == BlockKindEnum.Value && header.PayloadLength == edge.Length && length <= ValueBlocks.SingleBlockLimit);
    }

    private static bool TryDescribeRecords(ArenaAddress address, in BlockHeader header, StructureLayout element, int count,
        out RelocationBlock child)
    {
        child = new RelocationBlock(address, header, element, count);
        if (count <= 0 || count > element.MostElements)
        {
            return false;
        }

        if (count <= element.RecordsPerChunk)
        {
            return IsRecordBlock(header, BlockKindEnum.Value, element, count);
        }

        return header.Kind == BlockKindEnum.ChunkDirectory && header.Flags == 0 &&
            header.PayloadLength == RecordBlocks.ChunkCount(count, element.RecordsPerChunk) * ValueBlocks.DescriptorSize;
    }

    private static bool TryDescribeLookup(ArenaAddress address, in BlockHeader header, StructureLayout element, int count,
        out RelocationBlock child)
    {
        child = new RelocationBlock(address, header, element, count);
        if (header.Kind != BlockKindEnum.Lookup || header.Flags != 0 || count <= 0 || count > element.MostElements)
        {
            return false;
        }

        var slots = CollectionLookup.SlotsNeeded(count);
        return header.PayloadLength == CollectionLookup.PayloadForSlots(slots, CollectionLookup.ChunkCountForSlots(slots));
    }

    private static bool TryReadDirectoryChunk(Arena arena, in RelocationBlock directory, int index, int offset, in ValueDescriptor edge,
        out RelocationBlock child)
    {
        var address = ValueBlocks.AddressOf(edge);
        child = default;
        if (edge.Kind != DescriptorKindEnum.Block || !TryReadHeader(arena, directory, offset, address, out var header) ||
            header.PayloadLength != edge.Length)
        {
            return false;
        }

        if (directory.Layout is { } element)
        {
            var first = index * element.RecordsPerChunk;
            var count = RecordBlocks.RecordsInChunk(directory.Length, first, element.RecordsPerChunk);
            child = new RelocationBlock(address, header, element, count);
            return IsRecordBlock(header, BlockKindEnum.Chunk, element, count);
        }

        var length = Math.Min(directory.Length - index * ValueBlocks.ChunkPayload, ValueBlocks.ChunkPayload);
        child = new RelocationBlock(address, header, null, length);
        return header.Kind == BlockKindEnum.Chunk && header.Flags == 0 && edge.Length == length;
    }

    private static bool TryReadLookupChunk(Arena arena, in RelocationBlock lookup, Span<byte> payload, int index, int offset,
        out RelocationBlock child)
    {
        var address = CollectionLookup.ReadChunk(CollectionLookup.ChunkAddresses(payload), index - 1);
        child = default;
        if (!TryReadHeader(arena, lookup, offset, address, out var header) ||
            header.Kind != BlockKindEnum.Chunk || header.Flags != BlockHeader.LookupSlotsFlag)
        {
            return false;
        }

        child = new RelocationBlock(address, header, null, 0);
        var slots = CollectionLookup.SlotsNeeded(lookup.Length);
        return header.PayloadLength == CollectionLookup.ChunkPayloadForSlots(slots, index - 1);
    }

    private static bool IsRecordBlock(in BlockHeader header, BlockKindEnum kind, StructureLayout element, int count) =>
        header.Kind == kind && header.Flags == BlockHeader.RecordBlockFlag &&
        header.PayloadLength >= count * element.Size &&
        header.PayloadLength <= count * (element.Size + ValueBlocks.InlineLimit * element.DescriptorCount);

    private static bool TryReadHeader(Arena arena, in RelocationBlock parent, int offset, ArenaAddress address, out BlockHeader header) =>
        arena.TryReadLiveHeader(address, out header) &&
        RelocationEdges.HasOwner(header, parent.Header.Root, parent.Address, (uint)offset, parent.Header.LayoutVersion);

    private static ValueDescriptor ReadDescriptor(ReadOnlySpan<byte> payload, int offset) =>
        ResidentDescriptor.Read(payload[(offset - BlockHeader.Size)..]);
}
