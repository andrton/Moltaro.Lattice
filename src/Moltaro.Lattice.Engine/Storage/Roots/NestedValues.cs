using System.Buffers;
using System.Collections;
using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Writes and reads the values of a structure that lie beneath its members: the fields of its owned values,
// flattened into it, and its Lists in both their forms (StructureLayout), from and into the form a record
// holds (LatticeValueDeclaration.ValueType): a LatticeOwnedValue for an owned value, a List<T> for a List.
// The writer of a root writes an owned value or a List of the entity through it (RootWriter.SetOwned and
// SetList) and the reader of a root reads them back (RootReader.GetOwned and GetList); the records of a List
// and the elements of a packed List are written and read here too, by the same rules, at every depth.
//
// Where a structure lies. Its holder (ValueHolder) has the bytes of a block, and the structure lies at the
// offset at of that block, its header counted: a place lies at at plus its Offset. A root lies at 0, since
// the offsets of its places count from the start of its block; the record r of a block of records at
// 32 + r × Size.
//
// Writing. A structure starts with every bit of its bitmap set (StartRecord; the writer of a root starts its
// image so), and writing a value clears its bit: of a nullable owned value its presence bit, and of every
// field beneath it the bit it has. A value given as null leaves its bit set and its bytes zero. A String, a
// Binary and a packed List are placed by the one placement of ValueHolder, in the block that holds their
// descriptor or out of it. A List of records lies in one block of records or a directory of them, never
// inline, placed by the one placement of such a List (PlaceRecords), which serves a root a node received as
// well (Replication.Load.BlockImport). The values are those the validation of the core took and checked
// (Access.Values.RecordValues); a List above its bound is refused with ValidationFailed as the guard of the
// stored form, as ValueHolder refuses a value above its bound, and the writer is then abandoned. Once the
// arena refused a block, the writing takes nothing more and the write is refused for its memory; a List frees
// what it took before the refusal, so that a descriptor always names a whole value or nothing, and the writer
// of the root frees the rest. An OutOfMemoryException of the native allocator passes through a List after it
// has freed what it took.
//
// Reading copies every value into memory of the caller, a new object for each, so that two reads share
// nothing; an element of a packed List is read without a box (Values.PackedElements). The caller holds the
// protection of the root.
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

        if (place.IsNullable)
        {
            holder.At(at + layout.Start + (place.NullBit / 8), 1)[0] &= (byte)~(1 << (place.NullBit % 8));
        }

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
                return StoredText.Read(arena, ValueBlocks.Read(structure[offset..]), payload, encoding);

            case StoredTypeEnum.Binary:
                var descriptor = ValueBlocks.Read(structure[offset..]);

                if (descriptor.Length == 0)
                {
                    return Array.Empty<byte>();
                }

                var copy = GC.AllocateUninitializedArray<byte>((int)descriptor.Length);

                ValueBlocks.CopyTo(arena, descriptor, payload, copy);

                return copy;

            case StoredTypeEnum.List:
                return ReadList(arena, layout.ElementOf(position), ValueBlocks.Read(structure[offset..]), payload, encoding);

            default:
                return StoredScalars.ReadBoxed(structure.Slice(offset, place.Width), declaration.ScalarType);
        }
    }

    // The List a descriptor names, whose element has the layout given, as a new List of the form a record
    // holds; the payload is that of the block that holds the descriptor, where a List inline lies.
    public static object ReadList(Arena arena, StructureLayout element, in ValueDescriptor descriptor, ReadOnlySpan<byte> payload, LatticeStringEncodingEnum encoding)
    {
        if (element.Packed is { } packed)
        {
            return ReadPacked(arena, packed, descriptor, payload);
        }

        var list = (IList)element.List!.Sequence!.Create();
        var blocks = new RecordBlocks(arena, descriptor, element);

        while (blocks.MoveNext())
        {
            ReadRecords(arena, element, blocks.Block, blocks.Count, list, encoding);
        }

        return list;
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
    // block that was received (Replication.Load.BlockImport, "Pending"). So the descriptor names a whole List
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

        var descriptorPlace = holder.At(offset, ValueBlocks.DescriptorSize);

        if (count == 0)
        {
            ValueBlocks.Write(descriptorPlace, new ValueDescriptor { Kind = DescriptorKindEnum.Records });

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
            if (TryPlaceRecords(ref holder, element, offset, count, descriptorPlace, ref descriptor, ref source))
            {
                return;
            }
        }
        catch (OutOfMemoryException)
        {
            FreeRecords(ref holder, element, descriptorPlace, descriptor, slotBytes);

            throw;
        }

        FreeRecords(ref holder, element, descriptorPlace, descriptor, slotBytes);
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
            PlacePacked(ref holder, place, element, packed, offset, list);

            return;
        }

        var records = (IList)list;
        var source = new ListRecords(records, holder.Encoding);

        PlaceRecords(ref holder, place, element, offset, records.Count, ref source);
    }

    // A packed List: the records of its elements, placed as the bytes of a Binary value are (ValueHolder). Only
    // the boundary of a chunk divides a record: inline and in one block, one place holds the whole List. So only
    // a List that lies in chunks takes a record of scratch.
    private static void PlacePacked(ref ValueHolder holder, in StoredField place, StructureLayout element, PackedElements packed, int offset, object list)
    {
        var count = packed.Count(list);

        if (count > element.MostElements)
        {
            throw AboveTheBound(place, count, element);
        }

        var length = count * packed.Stride;

        if (length <= ValueBlocks.SingleBlockLimit)
        {
            var whole = new PackedSource(packed, list, default);

            holder.PlaceBytes(place, offset, length, ref whole);

            return;
        }

        var record = ArrayPool<byte>.Shared.Rent(packed.Stride);

        try
        {
            var source = new PackedSource(packed, list, record.AsSpan(0, packed.Stride));

            holder.PlaceBytes(place, offset, length, ref source);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(record);
        }
    }

    // The blocks of a List of records, each allocated, named by the descriptor or the directory, and filled
    // in turn (PlaceRecords); false when the arena refused a block, with the descriptor in place naming what
    // was taken so far, or still as it was when nothing was.
    private static bool TryPlaceRecords<TSource>(ref ValueHolder holder, StructureLayout element, int offset, int count, Span<byte> descriptorPlace, ref ValueDescriptor descriptor, ref TSource source)
        where TSource : struct, IRecordSource
    {
        var perChunk = element.RecordsPerChunk;

        if (count <= perChunk)
        {
            if (!TryAllocate(ref holder, source.BlockLength(element, 0, count), BlockKindEnum.Value, holder.Address, offset, out var block))
            {
                return false;
            }

            descriptor.Location = block.Value;
            ValueBlocks.Write(descriptorPlace, descriptor);

            return TryFill(ref holder, element, block, 0, count, ref source);
        }

        var chunks = (count + perChunk - 1) / perChunk;

        if (!TryAllocate(ref holder, chunks * ValueBlocks.DescriptorSize, BlockKindEnum.ChunkDirectory, holder.Address, offset, out var directory))
        {
            return false;
        }

        // The entries start zero: an entry the placement did not reach names no chunk (RecordBlocks).
        var entries = holder.Arena.Payload(directory);

        entries.Clear();
        descriptor.Location = directory.Value;
        ValueBlocks.Write(descriptorPlace, descriptor);

        for (var chunk = 0; chunk < chunks; chunk++)
        {
            var first = chunk * perChunk;
            var records = Math.Min(perChunk, count - first);
            var length = source.BlockLength(element, first, records);

            if (!TryAllocate(ref holder, length, BlockKindEnum.Chunk, directory, BlockHeader.Size + (chunk * ValueBlocks.DescriptorSize), out var block))
            {
                return false;
            }

            ValueBlocks.Write(entries[(chunk * ValueBlocks.DescriptorSize)..], new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)length, Location = block.Value });

            if (!TryFill(ref holder, element, block, first, records, ref source))
            {
                return false;
            }
        }

        return true;
    }

    // A prepared block of a List of records, of the root of the holder, counted in the holder.
    private static bool TryAllocate(ref ValueHolder holder, int length, BlockKindEnum kind, ArenaAddress parent, int offset, out ArenaAddress block)
    {
        if (!ValueBlocks.TryAllocate(holder.Arena, length, kind, holder.Root, holder.Layout.LayoutVersion, parent, (uint)offset, out block))
        {
            return false;
        }

        holder.SlotBytes += Arena.BlockSizeFor(length);

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
    private static void FreeRecords(ref ValueHolder holder, StructureLayout element, Span<byte> descriptorPlace, in ValueDescriptor descriptor, long slotBytes)
    {
        if (descriptor.Location != 0)
        {
            RootStorage.FreeValue(holder.Arena, descriptor, element);
            ValueBlocks.Write(descriptorPlace, default);
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

        ValueBlocks.Write(holder.At(offset, ValueBlocks.DescriptorSize), new ValueDescriptor { Kind = DescriptorKindEnum.Records, Length = (uint)count });
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
        new(LatticeErrorEnum.ValidationFailed, $"The List {place.Field.Name} holds {count} elements, more than the {element.MostElements} one List of its elements may hold.");
}
