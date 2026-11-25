using System.Buffers;
using System.Collections;
using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Writes and reads the values beneath a structure's members. Owned values are flattened into
// their containing structure, and collection entries are packed or stored in blocks of records as
// their layout requires. A large Map or Set also has a derived lookup. The same entry placement
// and copy operations serve the root and its descendants.
//
// ValueHolder places the inline and out-of-line bytes. The placement of a List of records is
// shared by a local write, a forwarded write and receipt. Its descriptor must name a whole List
// or nothing. On refusal the placement gives back the blocks it took, including when a native
// allocation throws. The root writer or importer owns the remaining cleanup.
//
// A read copies out each value into the caller's memory. The caller protects the root while it
// reads. See docs/design/arena-protocol.md, "Stored structures and root transfer," for the offset
// frames, inline sizing and both List forms.
internal static class NestedValues
{
    // Starts the record at the offset at of the holder's block, with every bit of its bitmap set and
    // the bits after the last one clear.
    public static void StartRecord(ref ValueHolder holder, StructureLayout layout, int at)
    {
        var record = holder.At(at, layout.Size);
        record.Clear();

        var bitmap = record[..layout.NullBitmapLength];

        bitmap.Fill(byte.MaxValue);

        if (layout.NullableCount % 8 != 0)
        {
            bitmap[^1] = (byte)((1 << (layout.NullableCount % 8)) - 1);
        }
    }

    // Writes the value, not null, of the place at the position of the structure at the offset at: clears
    // its bit and writes the value, or, for an owned value, every field of it that has one.
    public static void Write(ref ValueHolder holder, StructureLayout layout, int at, int position, object value)
    {
        ref readonly var place = ref layout.Place(position);

        if (place.Type is StoredTypeEnum.Map or StoredTypeEnum.Set)
        {
            // A refusal leaves a nullable field absent, so the writer can still complete.
            PlaceCollection(ref holder, place, layout.ElementOf(position), at + place.Offset, value);
            if (!holder.Refused)
            {
                MarkPresent(ref holder, layout, at, place);
            }

            return;
        }

        MarkPresent(ref holder, layout, at, place);

        switch (place.Type)
        {
            case StoredTypeEnum.Owned:
                var owned = (LatticeOwnedValue)value;

                foreach (var field in owned.OwnedType.FieldsByOrdinal)
                {
                    if (owned.Values[field.Ordinal] is { } fieldValue)
                    {
                        Write(ref holder, layout, at, layout.ChildOf(position, field), fieldValue);
                    }
                }

                break;

            case StoredTypeEnum.String:
                holder.PlaceText(place, at + place.Offset, (string)value);
                break;

            case StoredTypeEnum.Binary:
                var bytes = (byte[])value;
                var source = ValueSource.Bytes(bytes);

                holder.PlaceBytes(place, at + place.Offset, bytes.Length, ref source);
                break;

            case StoredTypeEnum.List:
                PlaceList(ref holder, place, layout.ElementOf(position), at + place.Offset, value);
                break;

            default:
                StoredScalars.WriteBoxed(holder.At(at + place.Offset, place.Width), value);
                break;
        }
    }

    public static void MarkPresent(ref ValueHolder holder, StructureLayout layout, int at, in StoredField place)
    {
        if (place.IsNullable)
        {
            holder.At(at + layout.Start + (place.NullBit / 8), 1)[0] &= (byte)~(1 << (place.NullBit % 8));
        }
    }

    // The value of a place from the structure's bytes, in the form a record holds. Inline locations
    // refer to the payload of the whole block. A packed record needs neither an arena nor a block payload.
    public static object? Read(Arena arena, StructureLayout layout, ReadOnlySpan<byte> structure, ReadOnlySpan<byte> payload, int position, LatticeStringEncodingEnum encoding)
    {
        ref readonly var place = ref layout.Place(position);

        if (place.IsNullable && (structure[place.NullBit / 8] & (1 << (place.NullBit % 8))) != 0)
        {
            return null;
        }

        var declaration = layout.DeclarationOf(position);
        var offset = place.Offset - layout.Start;

        switch (place.Type)
        {
            case StoredTypeEnum.Owned:
                var owned = new LatticeOwnedValue(declaration.OwnedType!);

                foreach (var field in owned.OwnedType.FieldsByOrdinal)
                {
                    owned.Values[field.Ordinal] = Read(arena, layout, structure, payload, layout.ChildOf(position, field), encoding);
                }

                return owned;

            case StoredTypeEnum.String:
                return StoredText.Read(arena, ResidentDescriptor.Read(structure[offset..]), payload, encoding);

            case StoredTypeEnum.Binary:
                var descriptor = ResidentDescriptor.Read(structure[offset..]);

                if (descriptor.Length == 0)
                {
                    return Array.Empty<byte>();
                }

                var copy = GC.AllocateUninitializedArray<byte>((int)descriptor.Length);

                ValueBlocks.CopyTo(arena, descriptor, payload, copy);

                return copy;

            case StoredTypeEnum.List:
                return ReadList(arena, layout.ElementOf(position), ResidentDescriptor.Read(structure[offset..]), payload, encoding);
            case StoredTypeEnum.Map or StoredTypeEnum.Set:
                return ReadCollection(arena, layout.ElementOf(position), ResidentDescriptor.Read(structure[offset..]), payload, encoding);

            default:
                return StoredScalars.ReadBoxed(structure.Slice(offset, place.Width), declaration.ScalarType);
        }
    }

    // The List a descriptor names, whose element has the layout given, as a new List of the form a record
    // holds; the payload is that of the block that holds the descriptor, where a List inline lies.
    private static object ReadList(Arena arena, StructureLayout element, in ValueDescriptor descriptor, ReadOnlySpan<byte> payload, LatticeStringEncodingEnum encoding)
    {
        if (element.Packed is { } packed)
        {
            return ReadPacked(arena, packed, descriptor, payload);
        }

        var list = (IList)element.Collection!.Sequence!.Create();
        var blocks = new RecordBlocks(arena, descriptor, element);

        while (blocks.MoveNext())
        {
            ReadRecords(arena, element, blocks.Block, blocks.Count, list, encoding);
        }

        return list;
    }

    private static object ReadCollection(Arena arena, StructureLayout element, ValueDescriptor descriptor, ReadOnlySpan<byte> payload, LatticeStringEncodingEnum encoding)
    {
        if (descriptor.Kind == DescriptorKindEnum.Lookup)
        {
            payload = arena.Payload(ValueBlocks.AddressOf(descriptor));
            descriptor = ResidentDescriptor.Read(payload);
        }

        if (element.Packed is { } packed)
        {
            return ReadPacked(arena, packed, descriptor, payload);
        }

        var map = element.Collection!.Map;
        var set = element.Collection.Sequence;
        var count = (int)descriptor.Length;
        var result = map is null ? set!.Create(count) : map.Create(count);
        var blocks = new RecordBlocks(arena, descriptor, element);
        while (blocks.MoveNext())
        {
            var records = arena.Payload(blocks.Block);
            for (var index = 0; index < blocks.Count; index++)
            {
                var record = records.Slice(index * element.Size, element.Size);
                var key = Read(arena, element, record, records, position: 0, encoding);
                if (map is null)
                {
                    set!.Add(result, key);
                }
                else
                {
                    map.TryAdd(result, key!, Read(arena, element, record, records, position: 1, encoding));
                }
            }
        }

        return result;
    }

    // Whether the records of a List of kind Records lie in chunks under a directory rather than in one block.
    public static bool LiesInChunks(in ValueDescriptor records, StructureLayout element) => records.Length > element.RecordsPerChunk;

    // The one placement of a List of records, of count elements, its descriptor at the offset given of the
    // block of the holder: what a write of values places (ListRecords) and what a node received
    // (Replication.Load.ReceivedRecords) alike. One block of kind Value holds up to RecordsPerChunk records,
    // else a directory of kind ChunkDirectory holds a chunk for each RecordsPerChunk of them; each block is
    // allocated with the length the source gives, for its records and the values inline of their
    // descriptors, and filled by the source before the next one is allocated, so that a source that reads
    // its records from an input reads them in their order. The parent of the block or the directory is the
    // block of the holder, the image of a root naming none until its writer completes the root; the parent
    // of a chunk is its directory.
    //
    // A List above the bound of a List is refused with ValidationFailed, as the guard of the stored form
    // (see the class). When the arena refuses a block, for the List or for a value of a record, the List
    // frees everything it took and the holder is Refused; an OutOfMemoryException of the native allocator
    // passes after the same free. Its descriptor then names nothing: written as zero once it named a block,
    // or still as it was when the arena refused the first one, zero in the image of a root and pending in a
    // block that was received (Replication.Load.BlockImport). So the descriptor names a whole List
    // or nothing. The descriptor names the block or the directory before they are filled, so that a free of
    // the List halfway reaches what it took. For a root for a forwarded write each block is kept apart, with
    // the values of its records (ExportedValues.StoreRecords), and the descriptor has the location 0.
    public static void PlaceRecords<TSource>(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, ref TSource source)
        where TSource : struct, IRecordSource
    {
        if (count > element.MostElements)
        {
            throw AboveTheBound(place, count, element);
        }

        if (count == 0)
        {
            holder.SetDescriptor(offset, new ValueDescriptor { Kind = DescriptorKindEnum.Records });

            return;
        }

        if (holder.Refused)
        {
            return;
        }

        if (holder.Exported is not null)
        {
            ExportRecords(ref holder, element, offset, count, ref source);

            return;
        }

        // What the holder counted before the List, which a List that is not placed gives back.
        var slotBytes = holder.SlotBytes;
        var descriptor = new ValueDescriptor { Kind = DescriptorKindEnum.Records, Length = (uint)count };

        try
        {
            if (TryPlaceRecords(ref holder, element, offset, count, ref descriptor, ref source))
            {
                return;
            }
        }
        catch
        {
            FreeRecords(ref holder, element, offset, descriptor, slotBytes);

            throw;
        }

        FreeRecords(ref holder, element, offset, descriptor, slotBytes);
        holder.Refused = true;
    }

    // The bytes a value of the place at the position takes inline in the block of its structure, as Write
    // places it: what ListRecords.BlockLength adds up, so this sizing has to agree with the writing.
    public static int InlineBytes(StructureLayout layout, int position, object value, LatticeStringEncodingEnum encoding)
    {
        ref readonly var place = ref layout.Place(position);

        switch (place.Type)
        {
            case StoredTypeEnum.Owned:
                var owned = (LatticeOwnedValue)value;
                var bytes = 0;

                foreach (var field in owned.OwnedType.FieldsByOrdinal)
                {
                    if (owned.Values[field.Ordinal] is { } fieldValue)
                    {
                        bytes += InlineBytes(layout, layout.ChildOf(position, field), fieldValue, encoding);
                    }
                }

                return bytes;

            case StoredTypeEnum.String:
                var length = StoredText.EncodedLength((string)value, encoding);

                return place.LiesInTextEntry(length) ? 0 : LengthWhenInline(length);

            case StoredTypeEnum.Binary:
                return LengthWhenInline(((byte[])value).Length);

            case StoredTypeEnum.List:
                var packed = layout.ElementOf(position).Packed;

                if (packed is null)
                {
                    return 0;
                }

                return LengthWhenInline(packed.Count(value) * packed.Stride);

            case StoredTypeEnum.Map or StoredTypeEnum.Set:
                var entry = layout.ElementOf(position);
                if (entry.Packed is null)
                {
                    return 0;
                }

                var count = entry.CountEntries(value);
                return count <= CollectionLookup.MostCompactEntries ? LengthWhenInline(count * entry.Size) : 0;

            default:
                return 0;
        }
    }

    private static int LengthWhenInline(int length) => length <= ValueBlocks.InlineLimit ? length : 0;

    // Places a List of the place, its descriptor at the offset given, in the form its element has.
    private static void PlaceList(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, object list)
    {
        if (element.Packed is { } packed)
        {
            var count = packed.Count(list);
            if (count > element.MostElements)
            {
                throw AboveTheBound(place, count, element);
            }

            PlacePacked(ref holder, place, packed, offset, list, count, allowInline: true);

            return;
        }

        var records = (IList)list;
        var source = new ListRecords(records, holder.Encoding);

        PlaceRecords(ref holder, place, element, offset, records.Count, ref source);
    }

    // A hashed resident collection installs its lookup owner before placing entries. Forwarded
    // writes carry the same entries as a List, keeping short hashed entries out of line.
    private static void PlaceCollection(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, object value)
    {
        var count = element.CountEntries(value);
        if (count > element.MostElements)
        {
            throw AboveTheBound(place, count, element);
        }

        var source = new ManagedCollection(value);
        PlaceCollection(ref holder, place, element, offset, count, ref source);
    }

    public static void PlaceCollection<TSource>(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, ref TSource source)
        where TSource : struct, ICollectionEntrySource
        => PlaceCollection(ref holder, place, element, offset, count, CollectionLookup.IsCompact(count), ref source);

    public static void PlaceCollection<TSource>(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, bool compact, ref TSource source)
        where TSource : struct, ICollectionEntrySource
    {
        if (holder.Refused)
        {
            return;
        }

        if (holder.Exported is not null)
        {
            source.Place(ref holder, place, element, offset, count, allowInline: compact);
            return;
        }

        var before = holder.SlotBytes;
        var inline = holder.InlineEnd;
        try
        {
            if (compact)
            {
                source.Place(ref holder, place, element, offset, count, allowInline: true);
                if (!holder.Refused)
                {
                    CheckUnique(holder, place, element, offset);
                    return;
                }
            }
            else if (CollectionLookup.TryPrepare(ref holder, offset, count, out var entries))
            {
                source.Place(ref entries, place, element, CollectionLookup.EntriesOffset, count, allowInline: false);
                if (!entries.Refused)
                {
                    holder.SlotBytes += entries.SlotBytes;
                    CheckUnique(holder, place, element, offset);
                    return;
                }
            }
        }
        catch
        {
            DiscardCollection(ref holder, element, offset, before, inline);
            throw;
        }

        DiscardCollection(ref holder, element, offset, before, inline);
        holder.Refused = true;
    }

    private readonly struct ManagedCollection(object value) : ICollectionEntrySource
    {
        public void Place(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, bool allowInline)
        {
            if (element.Packed is PackedCollection packed)
            {
                using var cursor = packed.OpenEntries(value);
                PlacePacked(ref holder, place, packed, offset, cursor, count, allowInline);
                return;
            }

            var declaration = element.Collection!;
            var entries = declaration.Map is { } map ? map.Entries(value) : Members(declaration.Sequence!, value);
            using var source = entries.GetEnumerator();
            var records = new CollectionRecords(source, Math.Min(count, element.RecordsPerChunk), holder.Encoding);
            try
            {
                PlaceRecords(ref holder, place, element, offset, count, ref records);
            }
            finally
            {
                records.Dispose();
            }
        }
    }

    private static IEnumerable<KeyValuePair<object?, object?>> Members(SequenceForm form, object set)
    {
        foreach (var member in form.Elements(set))
        {
            yield return new(member, null);
        }
    }

    public static void CheckUnique(in ValueHolder holder, in StoredField place, StructureLayout element, int offset)
    {
        var descriptor = holder.DescriptorAt(offset);
        var payload = holder.Bytes[(BlockHeader.Size - holder.Origin)..];
        var keys = new CollectionKeys(holder.Arena, element, descriptor, payload, holder.Encoding);
        var slots = new CollectionLookup(holder.Arena, descriptor);
        var duplicate = CollectionLookup.AddEntries(ref keys, ref slots, keys.Count);
        if (duplicate >= 0)
        {
            throw Duplicate(place, element, duplicate);
        }
    }

    public static LatticeException Duplicate(in StoredField place, StructureLayout element, int ordinal) =>
        new(LatticeErrorEnum.ValidationFailed, $"The {element.Collection!.Type} {place.Field.Name} holds an equal entry more than once, at entry {ordinal + 1}.");

    // Give back only this field and release its inline space. The writer may set the same field
    // again, or abandon the root. Neither path should reach what this attempt owned.
    public static void DiscardCollection(ref ValueHolder holder, StructureLayout element, int offset, long before, int inline)
    {
        var bytes = holder.At(offset, ValueBlocks.DescriptorSize);
        var descriptor = holder.DescriptorAt(offset);
        RootStorage.FreeValue(holder.Arena, descriptor, element, holder.Address, offset);
        bytes.Clear();
        holder.SlotBytes = before;
        holder.InlineEnd = inline;
    }

    // Packed records use the Binary placement. Only the boundary of a chunk divides a record,
    // so only entries in chunks need a scratch record.
    private static void PlacePacked(ref ValueHolder holder, in StoredField place, PackedElements packed, int offset, object source, int count, bool allowInline)
    {
        var length = count * packed.Stride;

        if (length <= ValueBlocks.SingleBlockLimit)
        {
            var whole = new PackedSource(packed, source, default);

            holder.PlaceBytes(place, offset, length, ref whole, allowInline);

            return;
        }

        var record = ArrayPool<byte>.Shared.Rent(packed.Stride);

        try
        {
            var packedSource = new PackedSource(packed, source, record.AsSpan(0, packed.Stride));

            holder.PlaceBytes(place, offset, length, ref packedSource, allowInline);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(record);
        }
    }

    // The blocks of a List of records, each allocated, named by the descriptor or the directory, and filled
    // in turn (PlaceRecords); false when the arena refused a block, with the descriptor in place naming what
    // was taken so far, or still as it was when nothing was.
    private static bool TryPlaceRecords<TSource>(ref ValueHolder holder, StructureLayout element, int offset, int count, ref ValueDescriptor descriptor, ref TSource source)
        where TSource : struct, IRecordSource
    {
        var perChunk = element.RecordsPerChunk;

        if (count <= perChunk)
        {
            if (!holder.TryAllocate(source.BlockLength(element, 0, count), BlockKindEnum.Value, holder.Address, offset, out var block))
            {
                return false;
            }

            descriptor.Location = block.Value;
            holder.SetDescriptor(offset, descriptor);

            return TryFill(ref holder, element, block, 0, count, ref source);
        }

        var chunks = (count + perChunk - 1) / perChunk;

        if (!holder.TryAllocate(chunks * ValueBlocks.DescriptorSize, BlockKindEnum.ChunkDirectory, holder.Address, offset, out var directory))
        {
            return false;
        }

        // The entries start zero: an entry the placement did not reach names no chunk (RecordBlocks).
        var entries = holder.Arena.Payload(directory);

        entries.Clear();
        descriptor.Location = directory.Value;
        holder.SetDescriptor(offset, descriptor);

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var first = chunk * perChunk;
            var records = Math.Min(perChunk, count - first);
            var length = source.BlockLength(element, first, records);

            if (!holder.TryAllocate(length, BlockKindEnum.Chunk, directory, BlockHeader.Size + (chunk * ValueBlocks.DescriptorSize), out var block))
            {
                return false;
            }

            ResidentDescriptor.Write(entries[(chunk * ValueBlocks.DescriptorSize)..], new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)length, Location = block.Value });

            if (!TryFill(ref holder, element, block, first, records, ref source))
            {
                return false;
            }
        }

        return true;
    }

    // Fills a block of count records of the List from the element first on, by the source; what the values of
    // the records take out of line counts in the holder of the List. False when the arena refused a block for
    // one of them.
    private static bool TryFill<TSource>(ref ValueHolder holder, StructureLayout element, ArenaAddress block, int first, int count, ref TSource source)
        where TSource : struct, IRecordSource
    {
        var records = new ValueHolder(holder.Arena, holder.Layout, holder.Root, holder.Arena.Payload(block), BlockHeader.Size, block, BlockHeader.Size + (count * element.Size));

        source.Fill(ref records, element, first, count);
        Debug.Assert(records.Refused || records.InlineEnd == records.End);
        holder.SlotBytes += records.SlotBytes;

        return !records.Refused;
    }

    // Frees a List of records the placement did not complete, from what its descriptor names, writes its
    // descriptor as zero and gives back what the holder counted for it.
    private static void FreeRecords(ref ValueHolder holder, StructureLayout element, int offset, in ValueDescriptor descriptor, long slotBytes)
    {
        if (descriptor.Location != 0)
        {
            RootStorage.FreeValue(holder.Arena, descriptor, element, holder.Address, offset);
            holder.SetDescriptor(offset, default);
        }

        holder.SlotBytes = slotBytes;
    }

    // A List of records of a root for a forwarded write: each block as the source fills it, kept apart with the
    // values of its records, and the descriptor with the location 0, as the wire carries it.
    private static void ExportRecords<TSource>(ref ValueHolder holder, StructureLayout element, int offset, int count, ref TSource source)
        where TSource : struct, IRecordSource
    {
        for (var first = 0; first < count; first += element.RecordsPerChunk)
        {
            var records = Math.Min(element.RecordsPerChunk, count - first);
            var block = new byte[source.BlockLength(element, first, records)];
            var holderOfBlock = new ValueHolder(null!, holder.Layout, default, block, BlockHeader.Size, default, BlockHeader.Size + (records * element.Size))
            {
                Exported = ExportedValues.OfRecords(element, records),
            };

            source.Fill(ref holderOfBlock, element, first, records);
            Debug.Assert(holderOfBlock.Refused || holderOfBlock.InlineEnd == holderOfBlock.End);
            holder.Exported!.StoreRecords(offset, block, holderOfBlock.Exported!);
        }

        holder.SetDescriptor(offset, new ValueDescriptor { Kind = DescriptorKindEnum.Records, Length = (uint)count });
    }

    // A packed List read from where its descriptor says it lies, a record that a chunk boundary divides read
    // whole through a record of scratch.
    private static object ReadPacked(Arena arena, PackedElements packed, in ValueDescriptor descriptor, ReadOnlySpan<byte> payload)
    {
        var count = (int)(descriptor.Length / (uint)packed.Stride);
        var list = packed.Create(count);

        if (!ValueBlocks.LiesInChunks(descriptor))
        {
            packed.Read(list, ValueBlocks.Contiguous(arena, descriptor, payload));

            return list;
        }

        var record = ArrayPool<byte>.Shared.Rent(packed.Stride);

        try
        {
            var entries = ValueBlocks.Directory(arena, descriptor);
            var started = 0;

            for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
            {
                var bytes = ValueBlocks.Chunk(arena, entries, chunk);
                var taken = 0;

                // The rest of the record the chunk before divided.
                if (started > 0)
                {
                    taken = packed.Stride - started;
                    bytes[..taken].CopyTo(record.AsSpan(started));
                    packed.Read(list, record.AsSpan(0, packed.Stride));
                    started = 0;
                }

                var whole = (bytes.Length - taken) / packed.Stride * packed.Stride;

                packed.Read(list, bytes.Slice(taken, whole));
                taken += whole;

                if (taken < bytes.Length)
                {
                    bytes[taken..].CopyTo(record);
                    started = bytes.Length - taken;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(record);
        }

        return list;
    }

    // Adds to the list the elements of count records of the block of records at the address.
    private static void ReadRecords(Arena arena, StructureLayout element, ArenaAddress block, int count, IList list, LatticeStringEncodingEnum encoding)
    {
        var payload = arena.Payload(block);

        for (var record = 0; record < count; record++)
        {
            var structure = payload.Slice(record * element.Size, element.Size);

            list.Add(Read(arena, element, structure, payload, 0, encoding));
        }
    }

    // The refusal of a List of more elements than one List of its elements may hold.
    private static LatticeException AboveTheBound(in StoredField place, int count, StructureLayout element) =>
        new(LatticeErrorEnum.ValidationFailed, $"The {element.Collection!.Type} {place.Field.Name} holds {count} elements, more than the {element.MostElements} one {element.Collection.Type} of its elements may hold.");
}
