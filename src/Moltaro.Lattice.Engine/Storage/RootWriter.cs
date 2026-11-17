using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage;

// Writes one root: collects the values of its fields, validates them and allocates in the arena
// what they need, placed as its RootLayout says. A root is written and read field by field, so
// that neither a class nor a record has to box its values. The blocks are the private blocks of a
// write that is not published yet (arena protocol, "Read and mutation paths").
//
// Every block the writer allocates belongs to the root that the handle root names: its header
// carries that handle (BlockHeader.Root) and the layout version of the layout
// (RootLayout.LayoutVersion), and it names the block that holds its descriptor by Parent and
// DescriptorOffset, the offset counted from the start of that block, its header included. The
// root block has no parent and the offset 0; RootLayout says which block is the parent of which.
//
// Every Set… member sets one field, of the entity type of the layout and of the field type the
// member serves; SetNull serves every field. A field of another type, or of another entity type,
// is refused with ArgumentException. A value outside the range of the field's width is refused
// with LatticeException, ValidationFailed, at the call. A field is set once: a second Set… or
// SetNull of a field that was set is InvalidOperationException. A call that was refused, for
// malformed text, a value out of range, a value above the bound of one value or SetNull of a field
// that is not nullable, did not set the field, and a valid value may follow.
//
// An entity has no size limit of its own: only the memory of the arena bounds it (decision O19 of
// Epic 1). Only Complete reports the memory of the arena, with WriteRejected: no Set… member fails
// for it. One String or Binary value is bounded by the storage format: one directory block holds
// ValueBlocks.MostChunks chunks of ChunkPayload bytes, so a value takes at most 268,173,376 bytes of
// its encoding, ValueBlocks.MostValueBytes. A larger value is refused by its Set… member with
// LatticeException, ValidationFailed, with the field named, as every other validation of a value,
// before anything is allocated for it: a fixed rule of the format, the same on every node, and not
// a WriteRejected of Complete. As the refusal of text that is not well-formed, this refusal is the
// guard of the stored form, for whoever calls the writer inside the engine, and not a second
// validation of the contract: the validation of the core refuses such a value before a write
// reaches the engine, by the same number (Access.Values.ValueBound).
//
// An OutOfMemoryException of the native allocator passes through whichever member meets it. Met
// inside Complete, it is a failed Complete: everything is freed before it passes. Met inside a Set…
// member, it passes after the member has freed everything it allocated for that value: the field is
// not set, as after any refused call, and the writer stays usable, so the caller may set the field
// again, complete or abandon.
//
// The blocks are live, as Arena.TryAllocate gives them, or, for a writer made with prepared true,
// prepared (Arena.TryAllocatePrepared): the blocks of a write of the database, which count as
// prepared until the write is published with RootStorage.Publish (brief of S06, the planner's
// answer to Q30).
//
// A writer ends in one of two ways. Complete checks the root as a whole and returns the address of
// its root block; the root then belongs to the caller, who publishes it (S06) and frees it later
// with RootStorage.Free. Abandon frees everything the writer allocated. After Complete, whether
// it returned or failed, and after Abandon, every Set… member, SetNull and Complete are
// InvalidOperationException, and so is Abandon after a Complete that returned. Abandon after a
// failed Complete, and a second Abandon, do nothing.
//
// How it works. The fixed part and the inline values are collected in an image of the root block, a
// byte array rented from ArrayPool<byte>.Shared when the writer starts and returned when it ends, so
// that a write allocates no managed memory once the pool holds an array of its size. The array goes
// back as it is: the bytes of the last root stay in it until its next user overwrites them. They are
// bytes and reference nothing, so they keep no memory alive, and the pool is the process's own, so
// clearing them would protect nothing (the coordinator's decision after the second review of S06). The size of the
// root block is known only when every value is placed, so the root block itself is allocated by
// Complete, which copies the image into it. A value too long to lie inline is copied into native
// memory at its Set… member, into its value block or its chunks, because the span it comes in lives
// only for that call. A value is stored whole or not at all (ValueBlocks.TryStore), so a descriptor
// in the image names a whole value or nothing, and everything the writer holds is reachable from
// the image, where Complete or Abandon frees it. Once the arena refused a block, the writer
// allocates nothing more: Complete refuses the entity anyway.
//
// As a ref struct it lives on the stack of the method that writes the root: it cannot be kept on
// the heap or across an await. It is passed by reference and never copied: a copy would share the
// rented image and the blocks of the original, and ending both would free them twice.
//
// The arena takes no lock and has one changing thread at a time: a writer of the database runs under
// the lock of the writes (Engine.Writes.WriteCoordinator), and a writer of the tests is the only
// thread that changes its arena.
//
// What it allocated. SlotBytes counts the bytes of the slots of every block the writer allocated and
// still holds, as it allocates them: the slot of the root block, and for every value stored in blocks
// the slots of its value block, or of its directory and its chunks, by the length the value has. A
// write of the database takes it as its encoded size (Engine.Writes.PreparedWrite.EncodedSize), so
// that nothing walks the blocks of a root to count them afterwards.
internal ref struct RootWriter
{
    private readonly Arena _arena;

    private readonly RootLayout _layout;

    private readonly RootHandle _root;

    // Whether the blocks are allocated prepared.
    private readonly bool _prepared;

    // The rented array, null once the writer has ended. Its first RootLayout.ImageCapacity bytes are
    // the image of the root block, its header bytes unused, so that an offset of the layout is an
    // index into it; after them, one bit for each field, by LatticeField.Ordinal, set once the field
    // was set.
    private byte[]? _scratch;

    // The bytes of the root block so far: the fixed part and the inline values placed.
    private int _rootBytes;

    // The arena refused a block for its budget: Complete refuses the entity, so nothing more is
    // allocated for it.
    private bool _refusedByArena;

    private WriterStateEnum _state;

    // The bytes of the slots of the blocks the writer allocated and holds (see the class): after a
    // Complete that returned, those of the whole root; 0 after Abandon or a failed Complete.
    public long SlotBytes { get; private set; }

    // Starts a root of the entity type of the layout, in the arena, for the root that the handle
    // names, in live blocks. Refuses a null arena or layout with ArgumentNullException.
    public RootWriter(Arena arena, RootLayout layout, RootHandle root)
        : this(arena, layout, root, prepared: false)
    {
    }

    // The same, in prepared blocks when prepared is true.
    public RootWriter(Arena arena, RootLayout layout, RootHandle root, bool prepared)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(layout);

        _arena = arena;
        _layout = layout;
        _root = root;
        _prepared = prepared;
        _scratch = ArrayPool<byte>.Shared.Rent(layout.ImageCapacity + RootLayout.BitBytes(layout.FieldCount));
        _rootBytes = layout.FixedSize;

        // A rented array holds what its last user left. The fixed part starts zero: the bytes of a
        // fixed field that stays null, and the descriptor of a String or Binary that stays null.
        Image[..layout.FixedSize].Clear();
        SetFields.Clear();

        // Every nullable field is null until it is set; the bits after the last one stay zero.
        var bitmap = Image.Slice(RootLayout.NullBitmapOffset, layout.NullBitmapLength);

        bitmap.Fill(byte.MaxValue);

        if (layout.NullableCount % 8 != 0)
        {
            bitmap[^1] = (byte)((1 << (layout.NullableCount % 8)) - 1);
        }
    }

    private readonly Span<byte> Image => _scratch.AsSpan(0, _layout.ImageCapacity);

    private readonly Span<byte> SetFields => _scratch.AsSpan(_layout.ImageCapacity, RootLayout.BitBytes(_layout.FieldCount));

    // Sets the value of a nullable field, of any type, to null. A field that is not nullable is
    // refused with LatticeException, ValidationFailed, at the call.
    public void SetNull(LatticeField field)
    {
        ref readonly var stored = ref Take(field);

        if (!stored.IsNullable)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The field {Name(field)} is not nullable.");
        }

        // The image holds a null already: the bit is set, and the bytes or the descriptor are zero.
        MarkSet(stored, isNull: true);
    }

    // Sets the value of a Boolean field.
    public void SetBoolean(LatticeField field, bool value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Boolean, nameof(SetBoolean));

        Image[stored.Offset] = value ? (byte)1 : (byte)0;
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose
    // enum type has a signed underlying type, at the width of that type. A value outside its range
    // is ValidationFailed; it is never cut, wrapped or widened to make it fit (Q03.1). Whether an
    // Enum value is one its enum type allows is checked by the validation of the core before a write
    // reaches the engine (Access.Values.EnumValues), not here.
    public void SetInteger(LatticeField field, long value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.SignedInteger, nameof(SetInteger));
        var place = Place(stored);

        if (stored.Width < sizeof(long))
        {
            var maximum = (1L << (8 * stored.Width - 1)) - 1;

            if (value > maximum || value < -maximum - 1)
            {
                throw OutOfRange(field, value);
            }
        }

        switch (stored.Width)
        {
            case sizeof(sbyte):
                place[0] = (byte)(sbyte)value;
                break;
            case sizeof(short):
                MemoryMarshal.Write(place, (short)value);
                break;
            case sizeof(int):
                MemoryMarshal.Write(place, (int)value);
                break;
            default:
                MemoryMarshal.Write(place, value);
                break;
        }

        MarkSet(stored, isNull: false);
    }

    // Sets the value of a field of an unsigned integer type, UInt8 to UInt64, or of an Enum field
    // whose enum type has an unsigned underlying type, at the width of that type. A value outside
    // its range is ValidationFailed, as for SetInteger.
    public void SetUnsigned(LatticeField field, ulong value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.UnsignedInteger, nameof(SetUnsigned));
        var place = Place(stored);

        if (stored.Width < sizeof(ulong) && value >> (8 * stored.Width) != 0)
        {
            throw OutOfRange(field, value);
        }

        switch (stored.Width)
        {
            case sizeof(byte):
                place[0] = (byte)value;
                break;
            case sizeof(ushort):
                MemoryMarshal.Write(place, (ushort)value);
                break;
            case sizeof(uint):
                MemoryMarshal.Write(place, (uint)value);
                break;
            default:
                MemoryMarshal.Write(place, value);
                break;
        }

        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Float32 field. Its bits are stored as they are, those of a NaN included.
    public void SetFloat32(LatticeField field, float value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Float32, nameof(SetFloat32));

        MemoryMarshal.Write(Place(stored), BitConverter.SingleToInt32Bits(value));
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Float64 field. Its bits are stored as they are, those of a NaN included.
    public void SetFloat64(LatticeField field, double value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Float64, nameof(SetFloat64));

        MemoryMarshal.Write(Place(stored), BitConverter.DoubleToInt64Bits(value));
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Decimal field, a .NET decimal of 16 bytes (Q03.1): the four integers of
    // decimal.GetBits, so that its scale and its sign are kept.
    public void SetDecimal(LatticeField field, decimal value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Decimal, nameof(SetDecimal));
        Span<int> bits = stackalloc int[4];

        decimal.GetBits(value, bits);
        MemoryMarshal.AsBytes(bits).CopyTo(Place(stored));
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Guid field, its 16 bytes, never text (Q03.4.1, "Primary-key types and Guid
    // fields").
    public void SetGuid(LatticeField field, Guid value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Guid, nameof(SetGuid));

        value.TryWriteBytes(Place(stored));
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a DateTimeOffset field: the instant, stored as its UTC ticks. The offset of
    // the value is not kept; it is read back with offset zero (Q03.4.1, "UTC timestamps").
    public void SetDateTimeOffset(LatticeField field, DateTimeOffset value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.DateTimeOffset, nameof(SetDateTimeOffset));

        MemoryMarshal.Write(Place(stored), value.UtcTicks);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a DateOnly field, stored as its day number (Q03.4.1, "DateOnly, TimeOnly and
    // TimeSpan").
    public void SetDateOnly(LatticeField field, DateOnly value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.DateOnly, nameof(SetDateOnly));

        MemoryMarshal.Write(Place(stored), value.DayNumber);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a TimeOnly field, stored as its ticks since midnight (Q03.4.1).
    public void SetTimeOnly(LatticeField field, TimeOnly value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.TimeOnly, nameof(SetTimeOnly));

        MemoryMarshal.Write(Place(stored), value.Ticks);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a TimeSpan field, stored as its signed ticks (Q03.4.1).
    public void SetTimeSpan(LatticeField field, TimeSpan value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.TimeSpan, nameof(SetTimeSpan));

        MemoryMarshal.Write(Place(stored), value.Ticks);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a String field with Plain storage: in a UTF-8 database the UTF-8 of its
    // characters, in a UTF-16 database its code units, placed by its encoded length as RootLayout
    // says. Only well-formed text is stored, in both encodings (Q04.5): text that is not well-formed
    // UTF-16, a surrogate without its pair, is refused with LatticeException, ValidationFailed, at
    // the call. Then nothing is allocated for the value, the counters of the arena are the same
    // before and after the call, no character is replaced by another, the field is not set, and the
    // writer stays usable. Text whose encoding takes more than the 268,173,376 bytes of one value is
    // refused in the same way, ValidationFailed with the field named. An empty string is a value,
    // not null.
    //
    // Both refusals are the guard of the stored form, for whoever calls the writer inside the engine;
    // they are not a second validation of the contract. The validation of the core refuses such text
    // before a write reaches the engine, with the same check, WellFormedText (the planner's answer to
    // Q41), and the same bound, ValueBound (after the second review of S06).
    public void SetString(LatticeField field, ReadOnlySpan<char> value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.String, nameof(SetString));

        // A code unit takes one byte at least in UTF-8 and two in UTF-16, so text of more code units
        // than the bound has bytes is above it in either encoding: it is refused before it is
        // scanned, and the text that is counted below has an encoded length that fits an int.
        if (value.Length > ValueBlocks.MostValueBytes)
        {
            throw AboveTheBound(stored.Field);
        }

        // The whole text is checked before anything is placed: a lone surrogate at its end must not
        // be found after its first chunks were allocated. Checking, counting and, in a UTF-8 database,
        // encoding are three passes over the text, kept apart on purpose: Q04.5 asks to combine them
        // where possible, and that is done when a measurement shows it is worth the code.
        if (!WellFormedText.IsWellFormed(value))
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The text for the field {Name(field)} is not well-formed: it has a surrogate without its pair.");
        }

        var source = ValueSource.Text(value, _layout.StringEncoding);

        PlaceValue(stored, StoredText.EncodedLength(value, _layout.StringEncoding), ref source);
    }

    // Sets the value of a Binary field: the bytes as they are, in their order, placed by their
    // length as RootLayout says. More than the 268,173,376 bytes of one value is refused with
    // LatticeException, ValidationFailed, with the field named, at the call: nothing is allocated
    // for it, the field is not set, and the writer stays usable. An empty value is a value, not null
    // (Q03.4.1, "Binary fields"). The refusal is the guard of the stored form, as that of SetString.
    public void SetBinary(LatticeField field, ReadOnlySpan<byte> value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Binary, nameof(SetBinary));
        var source = ValueSource.Bytes(value);

        PlaceValue(stored, value.Length, ref source);
    }

    // Ends the root and returns the address of its root block. A nullable field that was never set
    // is null. Refuses with LatticeException: ValidationFailed when a field that is not nullable was
    // never set; WriteRejected when the arena cannot give the blocks of the entity, with Limit Memory,
    // since the limit of the arena of a database is its memory budget. When it fails,
    // everything the writer allocated is freed: LiveBytes, ClassSlackBytes and PreparedBytes of the
    // arena are as they were before the writer, while a segment the arena obtained for the writer
    // may stay in its reserve, so Segments and ReservedSegments may differ.
    public ArenaAddress Complete()
    {
        ThrowIfEnded();

        var setFields = SetFields;
        var requiredFields = _layout.RequiredFields;

        for (var index = 0; index < requiredFields.Length; index++)
        {
            var missing = requiredFields[index] & ~setFields[index];

            if (missing != 0)
            {
                var field = _layout.EntityType.Fields[8 * index + BitOperations.TrailingZeroCount(missing)];

                throw Refuse(LatticeErrorEnum.ValidationFailed, $"The field {Name(field)} is not nullable and was not set.");
            }
        }

        if (_refusedByArena)
        {
            throw RefuseForMemory();
        }

        ArenaAddress root;
        bool allocated;

        try
        {
            var payloadLength = _rootBytes - BlockHeader.Size;

            allocated = _prepared
                ? _arena.TryAllocatePrepared(payloadLength, BlockKindEnum.Root, _root, out root)
                : _arena.TryAllocate(payloadLength, BlockKindEnum.Root, _root, out root);

            if (allocated)
            {
                SlotBytes += Arena.BlockSizeFor(payloadLength);
            }
        }
        catch (OutOfMemoryException)
        {
            // A failure of the native allocator here is a failed Complete like any other: everything
            // is freed before it passes on.
            FreeValues();
            End(WriterStateEnum.Failed);

            throw;
        }

        if (!allocated)
        {
            throw RefuseForMemory();
        }

        // TryAllocate wrote the root, the kind and the length; a root has no parent and the offset 0.
        _arena.Header(root).LayoutVersion = _layout.LayoutVersion;
        Image[BlockHeader.Size.._rootBytes].CopyTo(_arena.Payload(root));

        // The value blocks and the directories were allocated before their parent existed.
        for (var index = 0; index < _layout.DescriptorCount; index++)
        {
            var offset = _layout.DescriptorOffset + index * ValueBlocks.DescriptorSize;

            ValueBlocks.Adopt(_arena, ValueBlocks.Read(Image[offset..]), root, offset);
        }

        End(WriterStateEnum.Completed);

        return root;
    }

    // Ends the writing without a root: frees everything the writer allocated so far.
    public void Abandon()
    {
        switch (_state)
        {
            case WriterStateEnum.Completed:
                throw new InvalidOperationException("The writer completed its root, which belongs to the caller now.");
            case WriterStateEnum.Failed or WriterStateEnum.Abandoned:
                return;
        }

        FreeValues();
        End(WriterStateEnum.Abandoned);
    }

    // The field, of the entity type of the layout and not set yet, for SetNull. The checks come in
    // this order: the writer is open, the field is one of its entity type, it was not set.
    private readonly ref readonly StoredField Take(LatticeField field)
    {
        ThrowIfEnded();

        ref readonly var stored = ref _layout.StoredFieldOf(field);

        ThrowIfSet(stored);

        return ref stored;
    }

    // The field, for a Set… member that serves fields of the stored type: a field of another type is
    // refused before it is asked whether it was set.
    private readonly ref readonly StoredField Take(LatticeField field, StoredTypeEnum type, string member)
    {
        ThrowIfEnded();

        ref readonly var stored = ref _layout.StoredFieldOf(field);

        if (stored.Type != type)
        {
            throw new ArgumentException($"{member} does not serve the field {Name(field)}, which is {field.Declaration.Type}.", nameof(field));
        }

        ThrowIfSet(stored);

        return ref stored;
    }

    private readonly void ThrowIfSet(in StoredField stored)
    {
        var ordinal = stored.Field.Ordinal;

        if ((SetFields[ordinal / 8] & (1 << (ordinal % 8))) != 0)
        {
            throw new InvalidOperationException($"The field {Name(stored.Field)} was set already.");
        }
    }

    // The bytes of a fixed field in the image.
    private readonly Span<byte> Place(scoped in StoredField stored) => Image.Slice(stored.Offset, stored.Width);

    // Places a String or Binary value of length encoded bytes: inline, in its blocks, or not at all
    // when the arena refused a block before and Complete refuses the entity anyway. A value above the
    // bound of one value is refused first, with ValidationFailed: nothing is allocated for it and the
    // field is not set. Otherwise the field is set when the call returns; when an
    // OutOfMemoryException passes through, the writer is as it was before the call.
    private void PlaceValue(in StoredField stored, int length, ref ValueSource source)
    {
        if (length > ValueBlocks.MostValueBytes)
        {
            throw AboveTheBound(stored.Field);
        }

        var descriptor = Place(stored);

        if (_refusedByArena)
        {
            // Nothing more is allocated for an entity that Complete refuses.
        }
        else if (length <= RootLayout.InlineLimit && _rootBytes + length <= Arena.MaxBlockSize)
        {
            source.Fill(Image.Slice(_rootBytes, length));
            ValueBlocks.Write(descriptor, new ValueDescriptor { Kind = DescriptorKindEnum.Inline, Length = (uint)length, Location = (ulong)_rootBytes });
            _rootBytes += length;
        }
        else if (ValueBlocks.TryStore(_arena, _root, _layout.LayoutVersion, _prepared, length, ref source, descriptor))
        {
            SlotBytes += ValueBlocks.SlotBytes(ValueBlocks.Read(descriptor));
        }
        else
        {
            _refusedByArena = true;
        }

        MarkSet(stored, isNull: false);
    }

    // Records that the field was set; a value that is not null clears the field's null bit.
    private readonly void MarkSet(in StoredField stored, bool isNull)
    {
        var ordinal = stored.Field.Ordinal;

        SetFields[ordinal / 8] |= (byte)(1 << (ordinal % 8));

        if (!isNull && stored.IsNullable)
        {
            Image[RootLayout.NullBitmapOffset + stored.NullBit / 8] &= (byte)~(1 << (stored.NullBit % 8));
        }
    }

    // Frees every block the descriptors of the image name: everything the writer allocated, since
    // the root block is allocated last, by a Complete that then cannot fail any more.
    private void FreeValues()
    {
        for (var index = 0; index < _layout.DescriptorCount; index++)
        {
            var offset = _layout.DescriptorOffset + index * ValueBlocks.DescriptorSize;

            ValueBlocks.Free(_arena, ValueBlocks.Read(Image[offset..]));
        }

        SlotBytes = 0;
    }

    // A failed Complete: everything the writer allocated is freed, and the writer has ended.
    private LatticeException Refuse(LatticeErrorEnum error, string message)
    {
        FreeValues();
        End(WriterStateEnum.Failed);

        return new LatticeException(error, message);
    }

    // A failed Complete because the arena has no room for the entity: its limit is the memory budget
    // of the database (LatticeDatabaseOptions.MemoryLimit), which the refusal names.
    private LatticeException RefuseForMemory()
    {
        FreeValues();
        End(WriterStateEnum.Failed);

        return new LatticeException(LatticeErrorEnum.WriteRejected, $"The memory budget of the database cannot hold the entity of {_layout.EntityType.Name}.")
        {
            Limit = LatticeLimitEnum.Memory,
        };
    }

    // Returns the image to the pool; nothing reads or writes it afterwards.
    private void End(WriterStateEnum state)
    {
        ArrayPool<byte>.Shared.Return(_scratch!);
        _scratch = null;
        _state = state;
    }

    private readonly void ThrowIfEnded()
    {
        if (_state != WriterStateEnum.Open)
        {
            throw new InvalidOperationException("The writer has ended: Complete or Abandon was called.");
        }
    }

    private readonly string Name(LatticeField field) => $"{_layout.EntityType.Name}.{field.Name}";

    // The refusal of a String or Binary value above the bound of one value.
    private readonly LatticeException AboveTheBound(LatticeField field) =>
        new(LatticeErrorEnum.ValidationFailed, $"The value for the field {Name(field)} takes more than the {ValueBlocks.MostValueBytes} bytes one value may take.");

    private readonly LatticeException OutOfRange<TValue>(LatticeField field, TValue value) =>
        new(LatticeErrorEnum.ValidationFailed, $"The value {value} is outside the range of the field {Name(field)}, which is {field.Declaration.ScalarType}.");
}
