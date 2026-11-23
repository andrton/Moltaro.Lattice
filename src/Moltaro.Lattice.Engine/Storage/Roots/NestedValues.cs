using System.Buffers;
using System.Collections;
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
// inline. The values are those the validation of the core took and checked (Access.Values.RecordValues); a
// List above its bound is refused with ValidationFailed as the guard of the stored form, as ValueHolder
// refuses a value above its bound, and the writer is then abandoned. Once the arena refused a block, the
// writing takes nothing more and the write is refused for its memory; a List frees what it took before the
// refusal, so that a descriptor always names a whole value or nothing, and the writer of the root frees the
// rest. An OutOfMemoryException of the native allocator passes through a List after it has freed what it
// took.
//
// Reading copies every value into memory of the caller, a new object for each, so that two reads share
// nothing; an element of a packed List is read without a box (Values.PackedElements). The caller holds the
// protection of the root.
internal static class NestedValues
{
    // Starts the structure at the offset at of the block of the holder, whose bytes are zero or are cleared
    // here: every bit of its bitmap set, the bits after the last one clear.
    public static void StartRecord(ref ValueHolder holder, StructureLayout layout, int at)
    {
        holder.At(at, layout.Size).Clear();

        var bitmap = holder.At(at + layout.Start, layout.NullBitmapLength);

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

    // The value of the place at the position of the structure at the offset at of the block whose bytes are
    // given, which lie at the offset origin of the block: null when its bit is set, else a new value in the
    // form a record holds. The arena is that of the block; a packed record, which holds no descriptor, needs none.
    public static object? Read(Arena arena, StructureLayout layout, ReadOnlySpan<byte> bytes, int origin, int at, int position, LatticeStringEncodingEnum encoding)
    {
        ref readonly var place = ref layout.Place(position);

        if (place.IsNullable && (bytes[at + layout.Start + (place.NullBit / 8) - origin] & (1 << (place.NullBit % 8))) != 0)
        {
            return null;
        }

        var declaration = layout.DeclarationOf(position);

        switch (place.Type)
        {
            case StoredTypeEnum.Owned:
                var owned = new LatticeOwnedValue(declaration.OwnedType!);

                foreach (var field in owned.OwnedType.FieldsByOrdinal)
                {
                    owned.Values[field.Ordinal] = Read(arena, layout, bytes, origin, at, layout.ChildOf(position, field), encoding);
                }

                return owned;

            case StoredTypeEnum.String:
                return StoredText.Read(arena, DescriptorAt(bytes, origin, at + place.Offset), BlockPayload(bytes, origin), encoding);

            case StoredTypeEnum.Binary:
                var descriptor = DescriptorAt(bytes, origin, at + place.Offset);

                if (descriptor.Length == 0)
                {
                    return Array.Empty<byte>();
                }

                var copy = GC.AllocateUninitializedArray<byte>((int)descriptor.Length);

                ValueBlocks.CopyTo(arena, descriptor, BlockPayload(bytes, origin), copy);

                return copy;

            case StoredTypeEnum.List:
                return ReadList(arena, layout.ElementOf(position), DescriptorAt(bytes, origin, at + place.Offset), BlockPayload(bytes, origin), encoding);

            default:
                return StoredScalars.ReadBoxed(bytes.Slice(at + place.Offset - origin, place.Width), declaration.ScalarType);
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

        if (descriptor.Length == 0)
        {
            return list;
        }

        var address = ValueBlocks.AddressOf(descriptor);

        if (descriptor.Length <= element.RecordsPerChunk)
        {
            ReadRecords(arena, element, address, (int)descriptor.Length, list, encoding);

            return list;
        }

        var entries = ValueBlocks.Directory(arena, descriptor);
        var left = (int)descriptor.Length;

        for (var chunk = 0; chunk < entries.Length / ValueBlocks.DescriptorSize; chunk++)
        {
            var records = Math.Min(left, element.RecordsPerChunk);

            ReadRecords(arena, element, ValueBlocks.AddressOf(ValueBlocks.Read(entries[(chunk * ValueBlocks.DescriptorSize)..])), records, list, encoding);
            left -= records;
        }

        return list;
    }

    // Whether the records of a List of kind Records lie in chunks under a directory rather than in one block.
    public static bool LiesInChunks(in ValueDescriptor records, StructureLayout element) => records.Length > element.RecordsPerChunk;

    // Places a List of the place, its descriptor at the offset given, in the form its element has.
    private static void PlaceList(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, object list)
    {
        if (element.Packed is { } packed)
        {
            PlacePacked(ref holder, place, element, packed, offset, list);
        }
        else
        {
            PlaceRecords(ref holder, place, element, offset, (IList)list);
        }
    }

    // A packed List: the records of its elements, as the bytes of a Binary value are placed.
    private static void PlacePacked(ref ValueHolder holder, in StoredField place, StructureLayout element, PackedElements packed, int offset, object list)
    {
        var count = packed.Count(list);

        if (count > element.MostElements)
        {
            throw AboveTheBound(place, count, element);
        }

        var length = count * packed.Stride;

        if (length <= ValueBlocks.InlineLimit && holder.InlineEnd + length <= holder.End && !holder.Refused)
        {
            packed.Write(list, 0, holder.At(holder.InlineEnd, length));
            ValueBlocks.Write(holder.At(offset, ValueBlocks.DescriptorSize), new ValueDescriptor { Kind = DescriptorKindEnum.Inline, Length = (uint)length, Location = (ulong)holder.InlineEnd });
            holder.InlineEnd += length;

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

    // A List of records: one block of them or a directory of chunks of them, each made for its records and
    // the values inline of their descriptors, then filled.
    private static void PlaceRecords(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, IList list)
    {
        var count = list.Count;
        var descriptorPlace = holder.At(offset, ValueBlocks.DescriptorSize);

        if (count > element.MostElements)
        {
            throw AboveTheBound(place, count, element);
        }

        if (count == 0)
        {
            ValueBlocks.Write(descriptorPlace, new ValueDescriptor { Kind = DescriptorKindEnum.Records });

            return;
        }

        if (holder.Refused)
        {
            return;
        }

        var descriptor = new ValueDescriptor { Kind = DescriptorKindEnum.Records, Length = (uint)count };

        if (!TryAllocateRecords(ref holder, element, list, ref descriptor, holder.Address, offset))
        {
            holder.Refused = true;

            return;
        }

        ValueBlocks.Write(descriptorPlace, descriptor);

        try
        {
            if (!TryFillRecords(ref holder, element, list, descriptor))
            {
                RootStorage.FreeValue(holder.Arena, descriptor, element);
                ValueBlocks.Write(descriptorPlace, default);
                holder.Refused = true;
            }
        }
        catch (OutOfMemoryException)
        {
            RootStorage.FreeValue(holder.Arena, descriptor, element);
            ValueBlocks.Write(descriptorPlace, default);

            throw;
        }
    }

    // Allocates the blocks of a List of records, prepared and cleared: one block of kind Value up to
    // RecordsPerChunk records, else a directory and its chunks, every chunk but the last of RecordsPerChunk
    // records. The descriptor takes the address of the block or the directory. False when the arena refused
    // one, with nothing of them left allocated; an OutOfMemoryException of the native allocator passes on
    // with nothing of them left allocated either, as ValueBlocks.TryStore leaves a value in chunks.
    private static bool TryAllocateRecords(ref ValueHolder holder, StructureLayout element, IList list, ref ValueDescriptor descriptor, ArenaAddress parent, int offset)
    {
        var perChunk = element.RecordsPerChunk;
        var count = list.Count;

        if (count <= perChunk)
        {
            if (!ValueBlocks.TryAllocate(holder.Arena, BlockLength(element, list, 0, count, holder.Encoding), BlockKindEnum.Value, holder.Root, holder.Layout.LayoutVersion, parent, (uint)offset, out var block))
            {
                return false;
            }

            holder.Arena.Payload(block).Clear();
            holder.SlotBytes += Arena.BlockSizeFor((int)holder.Arena.Header(block).PayloadLength);
            descriptor.Location = block.Value;

            return true;
        }

        var chunks = (count + perChunk - 1) / perChunk;

        if (!ValueBlocks.TryAllocate(holder.Arena, chunks * ValueBlocks.DescriptorSize, BlockKindEnum.ChunkDirectory, holder.Root, holder.Layout.LayoutVersion, parent, (uint)offset, out var directory))
        {
            return false;
        }

        // The entries start zero, so that the free of a directory whose chunks are not all allocated frees
        // those that are.
        var entries = holder.Arena.Payload(directory);
        var slotBytes = (long)Arena.BlockSizeFor(chunks * ValueBlocks.DescriptorSize);

        entries.Clear();

        try
        {
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var first = chunk * perChunk;
                var length = BlockLength(element, list, first, Math.Min(perChunk, count - first), holder.Encoding);

                if (!ValueBlocks.TryAllocate(holder.Arena, length, BlockKindEnum.Chunk, holder.Root, holder.Layout.LayoutVersion, directory, (uint)(BlockHeader.Size + (chunk * ValueBlocks.DescriptorSize)), out var block))
                {
                    ValueBlocks.FreeBlocks(holder.Arena, directory, chunks: true);

                    return false;
                }

                holder.Arena.Payload(block).Clear();
                slotBytes += Arena.BlockSizeFor(length);
                ValueBlocks.Write(entries[(chunk * ValueBlocks.DescriptorSize)..], new ValueDescriptor { Kind = DescriptorKindEnum.Block, Length = (uint)length, Location = block.Value });
            }
        }
        catch (OutOfMemoryException)
        {
            // The chunks allocated so far and the directory go before the failure passes on.
            ValueBlocks.FreeBlocks(holder.Arena, directory, chunks: true);

            throw;
        }

        holder.SlotBytes += slotBytes;
        descriptor.Location = directory.Value;

        return true;
    }

    // Writes the records of a List into the blocks TryAllocateRecords allocated; false when the arena refused
    // a block for a value of a record.
    private static bool TryFillRecords(ref ValueHolder holder, StructureLayout element, IList list, in ValueDescriptor descriptor)
    {
        var address = ValueBlocks.AddressOf(descriptor);

        if (!LiesInChunks(descriptor, element))
        {
            return TryFillBlock(ref holder, element, list, address, 0, list.Count);
        }

        var entries = ValueBlocks.Directory(holder.Arena, descriptor);

        for (var chunk = 0; chunk * element.RecordsPerChunk < list.Count; chunk++)
        {
            var first = chunk * element.RecordsPerChunk;
            var block = ValueBlocks.AddressOf(ValueBlocks.Read(entries[(chunk * ValueBlocks.DescriptorSize)..]));

            if (!TryFillBlock(ref holder, element, list, block, first, Math.Min(element.RecordsPerChunk, list.Count - first)))
            {
                return false;
            }
        }

        return true;
    }

    // Writes count records of the List from the element first on into the block, the values inline after the
    // records; what the values take out of line counts in the holder of the List.
    private static bool TryFillBlock(ref ValueHolder holder, StructureLayout element, IList list, ArenaAddress block, int first, int count)
    {
        var records = new ValueHolder(holder.Arena, holder.Layout, holder.Root, holder.Arena.Payload(block), BlockHeader.Size, block, BlockHeader.Size + (count * element.Size));

        for (var record = 0; record < count && !records.Refused; record++)
        {
            var at = BlockHeader.Size + (record * element.Size);

            StartRecord(ref records, element, at);

            if (list[first + record] is { } value)
            {
                Write(ref records, element, at, 0, value);
            }
        }

        holder.SlotBytes += records.SlotBytes;

        return !records.Refused;
    }

    // The payload of a block of count records of the List from the element first on: the records, and every
    // value of their descriptors that lies inline, as Write places them.
    private static int BlockLength(StructureLayout element, IList list, int first, int count, LatticeStringEncodingEnum encoding)
    {
        var length = count * element.Size;

        for (var record = first; record < first + count; record++)
        {
            if (list[record] is { } value)
            {
                length += InlineBytes(element, 0, value, encoding);
            }
        }

        return length;
    }

    // The bytes a value of the place at the position takes inline in the block of its structure.
    private static int InlineBytes(StructureLayout layout, int position, object value, LatticeStringEncodingEnum encoding)
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

                return length <= ValueBlocks.InlineLimit && !place.LiesInTextEntry(length) ? length : 0;

            case StoredTypeEnum.Binary:
                var binaryLength = ((byte[])value).Length;

                return binaryLength <= ValueBlocks.InlineLimit ? binaryLength : 0;

            case StoredTypeEnum.List:
                var packed = layout.ElementOf(position).Packed;

                if (packed is null)
                {
                    return 0;
                }

                var packedLength = packed.Count(value) * packed.Stride;

                return packedLength <= ValueBlocks.InlineLimit ? packedLength : 0;

            default:
                return 0;
        }
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
            list.Add(Read(arena, element, payload, BlockHeader.Size, BlockHeader.Size + (record * element.Size), 0, encoding));
        }
    }

    // The descriptor at the offset given of the block whose bytes are given.
    private static ValueDescriptor DescriptorAt(ReadOnlySpan<byte> bytes, int origin, int offset) =>
        ValueBlocks.Read(bytes.Slice(offset - origin, ValueBlocks.DescriptorSize));

    // The payload of a block whose bytes, given at the offset origin, are its payload or the whole block; the
    // values inline of the block count their Location from its start, as ValueBlocks.Contiguous reads them.
    private static ReadOnlySpan<byte> BlockPayload(ReadOnlySpan<byte> bytes, int origin) => bytes[(BlockHeader.Size - origin)..];

    // Names the block of the holder as the parent of the blocks of a value placed out of line, when the block
    // exists; the writer of a root names the root block when it completes the root.

    private static LatticeException AboveTheBound(in StoredField place, int count, StructureLayout element) =>
        new(LatticeErrorEnum.ValidationFailed, $"The List {place.Field.Name} holds {count} elements, more than the {element.MostElements} one List of its elements may hold.");
}
