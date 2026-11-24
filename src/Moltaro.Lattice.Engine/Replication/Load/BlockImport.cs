using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Replication.Load;

// Places one root that a node received in its arena: the reverse of BlockExport. The input holds the root
// after its type id and its last mutation sequence, which the caller has read to choose the layout and to
// take the entry of the root: its layout version, the bytes of its root payload, and the values it holds
// out of line, in preorder (BlockExport), and nothing else.
//
// The whole input is checked by the layout before it is used, and before anything is allocated for the root
// (Check), each structure in it, the root and every record of a List of records, by the same rules:
// - the layout version against the layout;
// - the null bitmap: no bit set beyond the bits of the structure, and bits that agree, as a writer sets them
//   (StructureLayout.Beneath): every bit beneath an absent owned value set, and the bit of a place that is
//   not nullable beneath a present one clear; and the bit of each nullable String, Binary or List against
//   its descriptor: a value that is null has the descriptor of zeros, one that is not has a descriptor of a
//   value. The records of a packed List whose elements have bits are held to the same bits;
// - the kind of each descriptor against its length or count and its place, as the writer places values: a
//   text that lies in an entry of the text table (Storage.Values.StoredField.LiesInTextEntry: one that is
//   not empty of a field with Dictionary storage, one longer than ValueBlocks.InlineLimit of a field that is
//   part of an index) of kind Text, up to ValueBlocks.MostValueBytes bytes out of line; any other String or
//   Binary value, and a List whose elements hold no descriptor, whose length is then a whole number of its
//   records, inline at most ValueBlocks.InlineLimit bytes, in a block of its own 1 to
//   ValueBlocks.SingleBlockLimit bytes, in chunks more than that and at most ValueBlocks.MostValueBytes; a
//   List whose elements hold descriptors of kind Records, at most as many elements as such a List holds
//   (StructureLayout.MostElements); no other kind. The location of a value out of line and of a List of
//   records is 0 on the wire: nothing received is used as an address;
// - the lengths, before anything is read past them: the values inline of a structure lie inside its block
//   and after its fixed part or its records; the records of a List, a count times a stride counted in 64
//   bits, are not more than the bytes left; each value out of line and each block of records is not more
//   than the bytes left; and the bytes of the input are exactly the root and its values, so a root payload
//   and a block of records fit one block.
// Each failure is a LatticeException with ValidationFailed, and nothing is allocated, nor any managed memory
// that grows with what the input announces. The child then reads the key of the root from what was
// checked (FixedPart, Text), so that it knows whether the root is the state of a key it holds before it
// takes anything for it (Engine.Child.Loading.ReceivedChanges).
//
// Then Place places it through the placement a write of values goes through (Storage.Roots.ValueHolder,
// NestedValues.PlaceRecords): the root block is allocated, prepared, and filled with the payload, and each
// value out of line is stored as a writer stores it, which allocates its blocks and writes every header and
// location itself; a List of records gets its blocks of records, each filled with the payload received and
// then with the values of its records, at every depth (ReceivedRecords). The bytes of a Dictionary text
// become a reference to the entry of the text table of this node that holds them, found or added with the
// seed of this node (Storage.Text.TextTable.TryAcquire), so a node stores each distinct text once whatever
// it receives. Place counts the slots it took as the root writer counts those of a write
// (Storage.Roots.RootWriter.SlotBytes): the root block, the blocks of each value and of each List of
// records and each entry it added, so that a root a child forwards has on its master the encoded size the
// same write made there has (Engine.Writes.PreparedWrite.EncodedSize). The caller publishes the root.
//
// Pending. A structure is copied into its block with the descriptor of each value out of line pending: of
// kind Null, with the length or the count it was received with and the location 0, until the value is
// placed and its descriptor written over it. A walk over what a root owns takes a descriptor of kind Null to
// name nothing (StructureWalk), so a root whose placement stops halfway is freed by the free of a root,
// whatever it had placed (Storage.Roots.RootStorage.Free). The check leaves the copy of the root payload so,
// and ReceivedRecords each block of records.
//
// A refusal of the arena for its budget returns the null address, and an OutOfMemoryException of the
// native allocator passes through; either way nothing of the root is left allocated. The caller decides
// what a refusal means (Engine.Child.Loading, Engine.Writes.WriteExecutor).
//
// The arena has one changing thread at a time: the caller holds the lock of the writes of its node.
internal sealed class BlockImport
{
    private readonly Arena _arena;

    // The root payload as received, checked here before any of it reaches the arena: at most the
    // payload of one block. Reused for every root, so a checked root is valid until the next check.
    private readonly byte[] _payload = new byte[Arena.MaxBlockSize - BlockHeader.Size];

    // One record of a List of records, or the bits of one record of a packed List, as received, while it is
    // checked: at most the payload of one chunk, by the rule of the schema language that a record fits one.
    private readonly byte[] _record = new byte[ValueBlocks.ChunkPayload];

    // Where the value each descriptor of the root checked last names begins among its values out of line,
    // by the index of the descriptor; as long as the most descriptors a root checked had.
    private long[] _starts = [];

    public BlockImport(Arena arena)
    {
        ArgumentNullException.ThrowIfNull(arena);

        _arena = arena;
    }

    // Checks the root of the layout that the rest of the input holds, and nothing more (see the class),
    // with the input advanced to its end. ValidationFailed when a length, a count, a kind or a bit is not
    // valid; nothing is dereferenced before it is checked, and nothing is allocated. The layout is that of a
    // table of the node's own schema, which a received type id selects among them
    // (Engine.Entities.EntityTables).
    public ReceivedRoot Check(ref SequenceReader<byte> input, RootLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var payloadLength = ReadPayload(ref input, layout);
        var values = input.UnreadSequence;

        CheckValuesOfRoot(layout, payloadLength, ref input);

        if (!input.End)
        {
            throw MoreThanDescribed(layout, input.Remaining);
        }

        return new ReceivedRoot(layout, payloadLength, values);
    }

    // A reader of the fixed part of the root checked last, which reads its fixed fields and descriptors
    // (Storage.Roots.RootReader, a reader of a copy).
    public RootReader FixedPart(in ReceivedRoot root) =>
        new(root.Layout, _payload.AsSpan(0, RootReader.FixedPartLength(root.Layout)));

    // The text of a String field of the root checked last, which is not null, as a new string decoded
    // from the encoding of the database, where the root holds it: inline in its payload, or out of line;
    // for the key of the root (Engine.Child.Loading.ReceivedChanges), the one text of a root that is
    // decoded on receipt. ValidationFailed when it is not well-formed in that encoding, as a key
    // received in a Delete is not (Engine.Entities.PrimaryKeys.ReadKey): a key that a master stores is
    // well-formed, since every write checks its text (docs/decisions/strings.md, "Input validity and
    // preservation"), so such a root did not come from one. Every other text of a root is placed as the
    // bytes it is and not decoded.
    public string Text(in ReceivedRoot root, LatticeField field)
    {
        var layout = root.Layout;
        ref readonly var stored = ref layout.StoredFieldOf(field);
        var descriptor = ValueBlocks.Read(_payload.AsSpan(stored.Offset - BlockHeader.Size));
        var bytes = descriptor.Kind == DescriptorKindEnum.Inline
            ? new ReadOnlySequence<byte>(_payload, (int)descriptor.Location - BlockHeader.Size, (int)descriptor.Length)
            : root.OutOfLine.Slice(_starts[(stored.Offset - layout.DescriptorOffset) / ValueBlocks.DescriptorSize], descriptor.Length);

        if (layout.StringEncoding == LatticeStringEncodingEnum.Utf8)
        {
            try
            {
                return Protocol.StrictUtf8.GetString(bytes);
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(layout, $"the text of the field {field.Name} is not well-formed UTF-8 ({exception.Message})");
            }
        }

        if (bytes.Length % sizeof(char) != 0)
        {
            throw Invalid(layout, $"the text of the field {field.Name} has {bytes.Length} bytes, which are no UTF-16 code units.");
        }

        var text = string.Create((int)(bytes.Length / sizeof(char)), bytes, static (units, value) => value.CopyTo(MemoryMarshal.AsBytes(units)));

        if (!WellFormedText.IsWellFormed(text))
        {
            throw Invalid(layout, $"the text of the field {field.Name} is not well-formed UTF-16: it holds a surrogate without its pair.");
        }

        return text;
    }

    // Places the root checked last for the lifetime the handle names (see the class): the address of its
    // root block, prepared, with the bytes of the slots it took; the null address and 0 when the arena
    // refused it for its budget, with nothing of it left allocated.
    public ArenaAddress Place(in ReceivedRoot root, RootHandle handle, out long slotBytes)
    {
        var layout = root.Layout;

        slotBytes = 0;

        if (!_arena.TryAllocatePrepared(root.PayloadLength, BlockKindEnum.Root, handle, out var block))
        {
            return default;
        }

        _arena.Header(block).LayoutVersion = layout.LayoutVersion;

        var target = _arena.Payload(block);

        _payload.AsSpan(0, root.PayloadLength).CopyTo(target);

        // The root block holds what it received: every value placed into it lies out of line, as it came.
        var holder = new ValueHolder(_arena, layout, handle, target, BlockHeader.Size, block, BlockHeader.Size + root.PayloadLength);
        var values = root.OutOfLine;

        try
        {
            PlaceValues(ref holder, layout.Structure, 0, ref values);
        }
        catch
        {
            RootStorage.Free(_arena, layout, block);
            throw;
        }

        if (holder.Refused)
        {
            RootStorage.Free(_arena, layout, block);

            return default;
        }

        slotBytes = Arena.BlockSizeFor(root.PayloadLength) + holder.SlotBytes;

        return block;
    }

    // Places the values of the structure at the offset at of the block of the holder whose descriptors are
    // pending (see the class), from the values given, which begin with the first of them, and leaves the
    // values after the last; nothing more once the arena refused a block (ValueHolder.Refused). A null value,
    // a value inline and an empty List of records came whole with the structure. For the root here, and for
    // each record of a List of records (ReceivedRecords).
    public static void PlaceValues(ref ValueHolder holder, StructureLayout structure, int at, ref ReadOnlySequence<byte> values)
    {
        for (var index = 0; index < structure.DescriptorCount && !holder.Refused; index++)
        {
            ref readonly var place = ref structure.DescriptorField(index);
            var offset = at + place.Offset;
            var pending = ValueBlocks.Read(holder.At(offset, ValueBlocks.DescriptorSize));

            if (pending.Kind != DescriptorKindEnum.Null || pending.Length == 0)
            {
                continue;
            }

            var length = (int)pending.Length;

            if (place.Type == StoredTypeEnum.List && structure.DescriptorElement(index) is { HoldsDescriptors: true } element)
            {
                PlaceRecords(ref holder, place, element, offset, length, ref values);

                continue;
            }

            var bytes = values.Slice(0, length);

            values = values.Slice(length);

            if (place.LiesInTextEntry(length))
            {
                holder.PlaceEntry(offset, bytes);
            }
            else
            {
                var source = ValueSource.Received(bytes);

                holder.PlaceBytes(place, offset, length, ref source);
            }
        }
    }

    // A List of records of count elements (ReceivedRecords), the values standing at the first of its blocks.
    // Not inlined: inlined, the source of its records grows the frame of PlaceValues, which every root a node
    // receives goes through (JIT listing of a Release build).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PlaceRecords(ref ValueHolder holder, in StoredField place, StructureLayout element, int offset, int count, ref ReadOnlySequence<byte> values)
    {
        var records = new ReceivedRecords(values);

        NestedValues.PlaceRecords(ref holder, place, element, offset, count, ref records);
        values = records.Values;
    }

    // Whether a descriptor as it was received names a value that follows its structure on the wire, and so
    // is copied pending (see the class): a value out of line, or a List of records that is not empty.
    public static bool IsPending(in ValueDescriptor received) =>
        ValueBlocks.LiesOutOfLine(received) || (received.Kind == DescriptorKindEnum.Records && received.Length != 0);

    private static LatticeException Invalid(RootLayout layout, string message) =>
        new(LatticeErrorEnum.ValidationFailed, $"A root of {layout.EntityType.Name} received from the master is not valid: {message}");

    // Refusals the check of every root can reach, their messages made here, so that the check of a root holds
    // no message of its own (JIT listing of a Release build).
    private static LatticeException MoreThanDescribed(RootLayout layout, long more) =>
        Invalid(layout, $"it has {more} bytes more than its root and its values describe.");

    private static LatticeException BeyondTheInput(RootLayout layout, in StoredField place, uint length, long left) =>
        Invalid(layout, $"the value of the field {place.Field.Name} takes {length} bytes, more than the {left} left.");

    private static LatticeException ValuesBeyondTheInput(RootLayout layout, long length, long left) =>
        Invalid(layout, $"its values out of line take {length} bytes, more than the {left} left.");

    // Reads the layout version and the root payload into _payload after checking the structure of the root
    // (see the class), and returns the length of the payload; the input stands at the first value out of line.
    private int ReadPayload(ref SequenceReader<byte> input, RootLayout layout)
    {
        if (!input.TryReadLittleEndian(out int version) || (uint)version != layout.LayoutVersion)
        {
            throw Invalid(layout, $"its layout version is not {layout.LayoutVersion}.");
        }

        var fixedPart = RootReader.FixedPartLength(layout);

        if (input.Remaining < fixedPart)
        {
            throw Invalid(layout, $"it has {input.Remaining} bytes after its layout version, fewer than the {fixedPart} of its fixed part.");
        }

        var payload = _payload.AsSpan();

        input.UnreadSequence.Slice(0, fixedPart).CopyTo(payload);

        var inline = 0L;
        var inlineEnd = 0UL;

        CheckStructure(layout, layout.Structure, payload[..fixedPart], (ulong)layout.FixedSize, ref inline, ref inlineEnd);

        var payloadLength = fixedPart + inline;

        if (payloadLength > payload.Length || payloadLength > input.Remaining)
        {
            throw Invalid(layout, $"it has {input.Remaining} bytes after its layout version, fewer than its fixed part of {fixedPart} bytes and its values inline describe, {payloadLength}.");
        }

        if (inlineEnd > (ulong)(BlockHeader.Size + payloadLength))
        {
            throw Invalid(layout, "a value inline lies beyond the end of the root payload.");
        }

        input.UnreadSequence.Slice(fixedPart, payloadLength - fixedPart).CopyTo(payload[fixedPart..]);
        input.Advance(payloadLength);

        return (int)payloadLength;
    }

    // The values out of line of the root in _payload, in the order of its descriptors, each checked and passed
    // over (CheckValue), and where each begins kept for Text; each descriptor that names one is then left
    // pending (see the class).
    private void CheckValuesOfRoot(RootLayout layout, int payloadLength, ref SequenceReader<byte> input)
    {
        var structure = layout.Structure;
        var payload = _payload.AsSpan(0, payloadLength);
        var start = input.Consumed;

        if (_starts.Length < structure.DescriptorCount)
        {
            _starts = new long[structure.DescriptorCount];
        }

        var block = new ReadOnlySequence<byte>(_payload, 0, payloadLength);

        // The bytes of the values checked before the Strings and Binaries out of line since the input last
        // moved, and the bytes of those: they are passed over together, as all a root of scalar fields holds
        // out of line, and a List is checked where it stands, with its records or its elements.
        var checkedBytes = 0L;
        var passed = 0L;

        for (var index = 0; index < structure.DescriptorCount; index++)
        {
            ref readonly var place = ref structure.DescriptorField(index);
            var at = payload[(place.Offset - BlockHeader.Size)..];
            var received = ValueBlocks.Read(at);

            _starts[index] = checkedBytes + passed;

            if (place.Type != StoredTypeEnum.List)
            {
                if (!ValueBlocks.LiesOutOfLine(received))
                {
                    continue;
                }

                passed += received.Length;
            }
            else
            {
                Pass(layout, passed, ref input);
                passed = 0;
                CheckValue(layout, place, structure.DescriptorElement(index), received, block, ref input);
                checkedBytes = input.Consumed - start;

                if (!IsPending(received))
                {
                    continue;
                }
            }

            ValueBlocks.Write(at, new ValueDescriptor { Length = received.Length });
        }

        Pass(layout, passed, ref input);
    }

    // Passes over that many bytes of values, which the input has to hold.
    private static void Pass(RootLayout layout, long length, ref SequenceReader<byte> input)
    {
        if (length > input.Remaining)
        {
            throw ValuesBeyondTheInput(layout, length, input.Remaining);
        }

        input.Advance(length);
    }

    // The value one descriptor of a structure names, its kind checked with the structure: a value out of line,
    // at most the bytes left, passed over; a List of records, checked whole (CheckRecords). The records of a
    // packed List whose elements have bits are held to them, inline in the block given, whose payload holds
    // the structure, or out of line.
    private void CheckValue(RootLayout layout, in StoredField place, StructureLayout? element, in ValueDescriptor descriptor, in ReadOnlySequence<byte> block, ref SequenceReader<byte> input)
    {
        switch (descriptor.Kind)
        {
            case DescriptorKindEnum.Inline when element is { NullableCount: > 0 }:
                CheckPacked(layout, element, block.Slice((long)descriptor.Location - BlockHeader.Size, descriptor.Length));
                break;

            case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked or DescriptorKindEnum.Text:
                if (descriptor.Length > input.Remaining)
                {
                    throw BeyondTheInput(layout, place, descriptor.Length, input.Remaining);
                }

                if (element is { NullableCount: > 0 })
                {
                    CheckPacked(layout, element, input.UnreadSequence.Slice(0, descriptor.Length));
                }

                input.Advance(descriptor.Length);
                break;

            case DescriptorKindEnum.Records when descriptor.Length > 0:
                CheckRecords(layout, place, element!, (int)descriptor.Length, ref input);
                break;
        }
    }

    // The records of a List of records and what they hold, as the wire carries them: for each block of its
    // records, the records, which are checked as a structure is and give the length of the block, then the
    // values of the records, in their order.
    private void CheckRecords(RootLayout layout, in StoredField place, StructureLayout element, int count, ref SequenceReader<byte> input)
    {
        if ((long)count * element.Size > input.Remaining)
        {
            throw Invalid(layout, $"the List {place.Field.Name} has {count} records of {element.Size} bytes, more than the {input.Remaining} bytes left.");
        }

        Span<byte> descriptor = stackalloc byte[ValueBlocks.DescriptorSize];

        for (var first = 0; first < count; first += element.RecordsPerChunk)
        {
            var records = Math.Min(element.RecordsPerChunk, count - first);
            var length = CheckBlockOfRecords(layout, place, element, records, input);

            if (length > input.Remaining)
            {
                throw Invalid(layout, $"a block of the records of the List {place.Field.Name} takes {length} bytes, more than the {input.Remaining} left.");
            }

            var block = input.UnreadSequence.Slice(0, length);

            input.Advance(length);

            for (var record = 0; record < records; record++)
            {
                for (var index = 0; index < element.DescriptorCount; index++)
                {
                    ref readonly var field = ref element.DescriptorField(index);

                    block.Slice((record * element.Size) + field.Offset, ValueBlocks.DescriptorSize).CopyTo(descriptor);
                    CheckValue(layout, field, field.Type == StoredTypeEnum.List ? element.DescriptorElement(index) : null, ValueBlocks.Read(descriptor), block, ref input);
                }
            }
        }
    }

    // The records of one block of records that begin the input, count of them, each checked as a structure
    // (CheckStructure), and the length of the block: the records and their values inline, which lie after the
    // records and inside the block. The input is a copy, and stays where it was for the caller.
    private long CheckBlockOfRecords(RootLayout layout, in StoredField place, StructureLayout element, int count, SequenceReader<byte> input)
    {
        if ((long)count * element.Size > input.Remaining)
        {
            throw Invalid(layout, $"the List {place.Field.Name} has {count} records of {element.Size} bytes left to read, more than the {input.Remaining} bytes left.");
        }

        var record = _record.AsSpan(0, element.Size);
        var inline = 0L;
        var inlineEnd = 0UL;

        for (var index = 0; index < count; index++)
        {
            input.TryCopyTo(record);
            input.Advance(element.Size);
            CheckStructure(layout, element, record, (ulong)(BlockHeader.Size + (count * element.Size)), ref inline, ref inlineEnd);
        }

        var length = ((long)count * element.Size) + inline;

        if (inlineEnd > (ulong)(BlockHeader.Size + length))
        {
            throw Invalid(layout, $"a value inline in a block of the records of the List {place.Field.Name} lies beyond the end of the block.");
        }

        return length;
    }

    // The records of a packed List whose elements have bits, which the bytes given hold, each held to the bits
    // of its record (CheckBits); the bytes are a whole number of records, as the kind of the List was checked.
    private void CheckPacked(RootLayout layout, StructureLayout element, in ReadOnlySequence<byte> bytes)
    {
        var reader = new SequenceReader<byte>(bytes);
        var bits = _record.AsSpan(0, element.NullBitmapLength);

        while (!reader.End)
        {
            reader.TryCopyTo(bits);
            reader.Advance(element.Size);
            CheckBits(layout, element, bits);
        }
    }

    // One structure as it was received, a root payload or a record, whose bytes begin at its start (see the
    // class): its bits, and the kind of each descriptor against its place, adding up the bytes of its values
    // inline and the furthest end of one, which lie at inlineStart or after. A location inline is bounded by
    // the largest block before its length is added to it, so the sum cannot wrap around.
    private static void CheckStructure(RootLayout layout, StructureLayout structure, ReadOnlySpan<byte> bytes, ulong inlineStart, ref long inline, ref ulong inlineEnd)
    {
        var bitmap = bytes[..structure.NullBitmapLength];

        CheckBits(layout, structure, bitmap);

        for (var index = 0; index < structure.DescriptorCount; index++)
        {
            ref readonly var place = ref structure.DescriptorField(index);
            var descriptor = ValueBlocks.Read(bytes[(place.Offset - structure.Start)..]);

            if (place.IsNullable && IsSet(bitmap, place.NullBit))
            {
                if (descriptor.Kind != DescriptorKindEnum.Null || descriptor.Length != 0 || descriptor.Location != 0)
                {
                    throw Invalid(layout, $"the field {place.Field.Name} is null by its bit and has the descriptor of a value.");
                }

                continue;
            }

            var element = place.Type == StoredTypeEnum.List ? structure.DescriptorElement(index) : null;

            if (!Describes(place, element, descriptor, inlineStart))
            {
                throw Invalid(layout, $"the field {place.Field.Name} has a descriptor of the kind {descriptor.Kind} with the length {descriptor.Length} and the location {descriptor.Location}, which does not describe a stored value of its storage.");
            }

            if (descriptor.Kind == DescriptorKindEnum.Inline)
            {
                inline += descriptor.Length;
                inlineEnd = Math.Max(inlineEnd, descriptor.Location + descriptor.Length);
            }
        }
    }

    // Whether the descriptor of a value that is not null describes a value its place stores (see the class);
    // the element is the layout of the records of a List place. Inlined into the check of a structure, which
    // calls it for every descriptor (JIT listing of a Release build: called, one call a descriptor).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Describes(in StoredField place, StructureLayout? element, in ValueDescriptor descriptor, ulong inlineStart)
    {
        var length = descriptor.Length;

        if (element is { HoldsDescriptors: true })
        {
            return descriptor.Kind == DescriptorKindEnum.Records && length <= element.MostElements && descriptor.Location == 0;
        }

        // A packed List lies as the bytes of a Binary value, a whole number of its records.
        if (element is not null && length % element.Size != 0)
        {
            return false;
        }

        var inEntry = place.LiesInTextEntry(length);

        return descriptor.Kind switch
        {
            DescriptorKindEnum.Inline => !inEntry && length <= ValueBlocks.InlineLimit && descriptor.Location >= inlineStart && descriptor.Location <= Arena.MaxBlockSize,
            DescriptorKindEnum.Block => !inEntry && length is > 0 and <= ValueBlocks.SingleBlockLimit && descriptor.Location == 0,
            DescriptorKindEnum.Chunked => !inEntry && length is > ValueBlocks.SingleBlockLimit and <= ValueBlocks.MostValueBytes && descriptor.Location == 0,
            DescriptorKindEnum.Text => inEntry && length <= ValueBlocks.MostValueBytes && descriptor.Location == 0,
            _ => false,
        };
    }

    // The bits of one structure (see the class): none set beyond those of the structure, and those beneath a
    // nullable owned value as a writer sets them (StructureLayout.Beneath).
    private static void CheckBits(RootLayout layout, StructureLayout structure, ReadOnlySpan<byte> bitmap)
    {
        for (var bit = structure.NullableCount; bit < structure.NullBitmapLength * 8; bit++)
        {
            if (IsSet(bitmap, bit))
            {
                throw Invalid(layout, $"its null bitmap sets the bit {bit}, beyond its {structure.NullableCount} nullable fields.");
            }
        }

        foreach (var (position, above) in structure.Beneath)
        {
            var isNull = IsSet(bitmap, structure.Place(position).NullBit);

            if (IsSet(bitmap, above) && !isNull)
            {
                throw Invalid(layout, $"the field {structure.Place(position).Field.Name} has a value inside an owned value that is absent by its bit.");
            }

            if (!IsSet(bitmap, above) && isNull && !structure.DeclarationOf(position).IsNullable)
            {
                throw Invalid(layout, $"the field {structure.Place(position).Field.Name} is not nullable and is null inside an owned value that is present.");
            }
        }
    }

    private static bool IsSet(ReadOnlySpan<byte> bitmap, int bit) => (bitmap[bit / 8] & (1 << (bit % 8))) != 0;
}
