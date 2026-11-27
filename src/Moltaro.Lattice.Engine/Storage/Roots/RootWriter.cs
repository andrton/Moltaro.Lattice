using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots.Edits;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Writes one root: collects the values of its fields, validates them and allocates in the arena
// what they need, placed as its RootLayout says. A root is written and read field by field, so
// that neither a class nor a record has to box its scalar values; an owned value and a List are
// set whole, each with the tree beneath it (SetOwned, SetList), placed by NestedValues: the fields
// of an owned value in the fixed part of its holder, the records of a List inline, in one block or
// in chunks. The blocks are the private blocks of a
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
// An entity has no size limit of its own: only the memory of the arena bounds it
// (docs/decisions/write-admission.md, "Configurable root-object size limit"). Only Complete reports
// the memory of the arena, with WriteRejected: no Set… member fails for it. One String or Binary
// value is bounded by the storage format: one directory block holds ValueBlocks.MostChunks chunks
// of ChunkPayload bytes, so a value takes at most 268,173,376 bytes of its encoding,
// ValueBlocks.MostValueBytes. A larger value is refused by its Set… member with
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
// The blocks are prepared (Arena.TryAllocatePrepared), as the blocks of a write of the database are: they
// count as prepared until the write is published with RootStorage.Publish.
//
// A writer ends in one of two ways. Complete checks the root as a whole and returns the address of
// its root block; the root then belongs to the caller, who publishes it and frees it later with
// RootStorage.Free. Abandon frees everything the writer allocated. After Complete, whether it
// returned or failed, and after Abandon, every Set… member, SetNull and Complete are
// InvalidOperationException, and so is Abandon after a Complete that returned. Abandon after a
// failed Complete, and a second Abandon, do nothing.
//
// How it works. The fixed part and the inline values are collected in an image of the root block, a
// byte array: for a writer in an arena, the arena's one root image, charged to its budget and grown
// when a wider image is needed (Arena.RootImage), so that a write whose image fits allocates none;
// for a forwarded write, an array rented from ArrayPool<byte>.Shared when the writer starts and
// returned when it ends. The bytes of the last root stay in the array until its next user overwrites
// them. They are bytes and reference nothing, so they keep no memory alive, and clearing them would
// protect nothing. The size of the root block is known only when every value
// is placed, so Complete allocates the root block, copies the canonical image and encodes its
// descriptors in the native format. A
// value too long to lie inline is copied into native memory at its Set… member, into its value
// block or its chunks, because the span it comes in lives only for that call. A value is stored
// whole or not at all (ValueBlocks.TryStore), so a descriptor in the image names a whole value or
// nothing, and everything the writer holds is reachable from the image, where Complete or Abandon
// frees it; the blocks of a List's records name, in their turn, what the records hold. Once the arena
// refused a block, the writer allocates nothing more: Complete refuses the entity anyway. Every String,
// Binary and packed List is placed by the one placement of ValueHolder, at every depth, the image of the
// root being the holder of the values of its fixed part.
//
// As a ref struct it lives on the stack of the method that writes the root: it cannot be kept on
// the heap or across an await. It is passed by reference and never copied: a copy would share the
// image and the blocks of the original, and ending both would free them twice.
//
// The arena takes no lock and has one changing thread at a time: a writer of the database runs under
// the lock of the writes (Engine.Writes.WriteCoordinator), and a writer of the tests is the only
// thread that changes its arena.
//
// A root for a forwarded write. A child forwards the root of a write to its master in the load format
// (Replication.Forwarding.ForwardEncoding), and writes it without its arena: a writer made with
// ExportedValues places the fixed part and the values inline in its image as always, keeps each value
// that does not lie inline in managed memory instead of blocks, with the descriptor of the blocks it
// would take and the location 0, as Replication.Load.BlockExport writes a stored root, a List of records
// as the blocks of its records with the values of their descriptors, at every depth, and writes the
// whole root in that form with Export instead of Complete. It allocates nothing in an arena and needs
// no lock: what it holds is its own. Every check of a value is the same, so the root is the one a
// master would store for the values.
//
// What it allocated. SlotBytes counts the bytes of the slots of every block the writer allocated and
// still holds, as it allocates them: the slot of the root block, and for every value stored in blocks
// the slots of its value block, or of its directory and its chunks, by the length the value has. A
// write of the database takes it as its encoded size (Engine.Writes.PreparedWrite.EncodedSize), so
// that nothing walks the blocks of a root to count them afterwards.
internal ref struct RootWriter
{
    private readonly RootLayout _layout;

    // The image array, null once the writer has ended. Its first RootLayout.ImageCapacity bytes are
    // the image of the root block, its header bytes unused, so that an offset of the layout is an
    // index into it; after them, one bit for each field, by LatticeField.Ordinal, set once the field
    // was set.
    private byte[]? _scratch;

    // Whether the array was rented, for a forwarded write, and goes back to the pool at the end.
    private readonly bool _rented;

    // The image of the root as the block its values are placed into (ValueHolder): the arena and the root,
    // the values out of line of a root for a forwarded write, the bytes of the root block so far as
    // InlineEnd, its fixed part and the inline values placed, the slots the writer holds, and whether the
    // arena refused a block for its budget, after which Complete refuses the entity, so nothing more is
    // allocated for it.
    private ValueHolder _values;

    private WriterStateEnum _state;

    // The bytes of the slots of the blocks the writer allocated and holds (see the class): after a
    // Complete that returned, those of the whole root; 0 after Abandon or a failed Complete.
    public readonly long SlotBytes => _values.SlotBytes;

    // Starts a root of the entity type of the layout, in the arena, for the root that the handle
    // names, in prepared blocks. Refuses a null arena or layout with ArgumentNullException.
    public RootWriter(Arena arena, RootLayout layout, RootHandle root)
    {
        ArgumentNullException.ThrowIfNull(arena);
        ArgumentNullException.ThrowIfNull(layout);

        _layout = layout;
        _scratch = arena.RootImage(layout.ImageCapacity + RootLayout.BitBytes(layout.FieldCount));
        _values = new ValueHolder(arena, layout, root, Image, origin: 0, address: default, layout.FixedSize);
        Start();
    }

    // Starts a root of the entity type of the layout for a forwarded write (see the class), whose values
    // out of line the given store keeps. Refuses a null layout or store with ArgumentNullException.
    public RootWriter(RootLayout layout, ExportedValues exported)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(exported);

        _layout = layout;
        _scratch = ArrayPool<byte>.Shared.Rent(layout.ImageCapacity + RootLayout.BitBytes(layout.FieldCount));
        _rented = true;
        _values = new ValueHolder(null!, layout, default, Image, origin: 0, address: default, layout.FixedSize) { Exported = exported };
        Start();
    }

    // The image of a root that has no field set yet.
    private readonly void Start()
    {
        var layout = _layout;

        // A reused array holds what its last user left. The fixed part starts zero: the bytes of a
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

    public void PlaceUpdate(RootUpdate update)
    {
        ThrowIfEnded();
        update.Place(ref _values);
        // Omitted fields come from the validated predecessor.
        SetFields.Fill(byte.MaxValue);
    }

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

        StoredScalars.WriteBoolean(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose
    // enum type has a signed underlying type, at the width of that type. A value outside its range
    // is ValidationFailed; it is never cut, wrapped or widened to make it fit
    // (docs/decisions/schema-and-types.md, "Small integer widths and Boolean values"). Whether an
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

        StoredScalars.WriteInteger(place, value);
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

        StoredScalars.WriteUnsigned(place, value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Float32 field. Its bits are stored as they are, those of a NaN included.
    public void SetFloat32(LatticeField field, float value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Float32, nameof(SetFloat32));

        StoredScalars.WriteFloat32(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Float64 field. Its bits are stored as they are, those of a NaN included.
    public void SetFloat64(LatticeField field, double value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Float64, nameof(SetFloat64));

        StoredScalars.WriteFloat64(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Decimal field, a .NET decimal of 16 bytes (docs/decisions/schema-and-types.md):
    // the four integers of decimal.GetBits, so that its scale and its sign are kept.
    public void SetDecimal(LatticeField field, decimal value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Decimal, nameof(SetDecimal));
        StoredScalars.WriteDecimal(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a Guid field, its 16 bytes, never text (docs/decisions/schema-and-types.md,
    // "Primary-key types and Guid fields").
    public void SetGuid(LatticeField field, Guid value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Guid, nameof(SetGuid));

        StoredScalars.WriteGuid(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a DateTimeOffset field: the instant, stored as its UTC ticks. The offset of
    // the value is not kept; it is read back with offset zero (docs/decisions/schema-and-types.md,
    // "UTC timestamps").
    public void SetDateTimeOffset(LatticeField field, DateTimeOffset value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.DateTimeOffset, nameof(SetDateTimeOffset));

        StoredScalars.WriteDateTimeOffset(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a DateOnly field, stored as its day number (docs/decisions/schema-and-types.md,
    // "DateOnly, TimeOnly and TimeSpan").
    public void SetDateOnly(LatticeField field, DateOnly value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.DateOnly, nameof(SetDateOnly));

        StoredScalars.WriteDateOnly(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a TimeOnly field, stored as its ticks since midnight
    // (docs/decisions/schema-and-types.md, "DateOnly, TimeOnly and TimeSpan").
    public void SetTimeOnly(LatticeField field, TimeOnly value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.TimeOnly, nameof(SetTimeOnly));

        StoredScalars.WriteTimeOnly(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a TimeSpan field, stored as its signed ticks (docs/decisions/schema-and-types.md,
    // "DateOnly, TimeOnly and TimeSpan").
    public void SetTimeSpan(LatticeField field, TimeSpan value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.TimeSpan, nameof(SetTimeSpan));

        StoredScalars.WriteTimeSpan(Place(stored), value);
        MarkSet(stored, isNull: false);
    }

    // Sets the value of a String field: in a UTF-8 database the UTF-8 of its characters, in a UTF-16
    // database its code units, placed by its encoded length as RootLayout says for a field with Plain
    // storage; a text that lies in an entry of the text table (StoredField.LiesInTextEntry: one that is not
    // empty of a field with Dictionary storage, and one longer than ValueBlocks.InlineLimit of a field that
    // is part of an index), in the entry that holds the same bytes, found or added (ValueHolder.PlaceText). Only
    // well-formed text is stored, in both encodings (docs/decisions/strings.md, "Input validity and
    // preservation"): text that is not well-formed UTF-16, a surrogate without its pair, is refused
    // with LatticeException, ValidationFailed, at the call. Then nothing is allocated for the value,
    // the counters of the arena are the same before and after the call, no character is replaced by
    // another, the field is not set, and the writer stays usable. Text whose encoding takes more than
    // the 268,173,376 bytes of one value is refused in the same way, ValidationFailed with the field
    // named. An empty string is a value, not null.
    //
    // Both refusals are the guard of the stored form, for whoever calls the writer inside the engine;
    // they are not a second validation of the contract. The validation of the core refuses such text
    // before a write reaches the engine, with the same check, WellFormedText, and the same bound,
    // ValueBound.
    public void SetString(LatticeField field, ReadOnlySpan<char> value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.String, nameof(SetString));

        _values.PlaceText(stored, stored.Offset, value);
        MarkSet(stored, isNull: false);
    }

    // Sets an owned value of the entity, of the owned type of the field, as a record holds it: its fields,
    // flattened into the root (StructureLayout), each placed as a field of its type is, at every depth, those
    // of its own owned values and Lists included (NestedValues). A field of the owned value without a value is
    // null; the validation of the core has checked that it may be. Text that is not well-formed, and a value or
    // a List above its bound, at any depth, is refused with ValidationFailed as by SetString, and an
    // OutOfMemoryException of the native allocator passes through; then what the owned value placed before
    // is the writer's, so a caller abandons the writer, which frees it.
    public void SetOwned(LatticeField field, LatticeOwnedValue value) => SetNested(field, StoredTypeEnum.Owned, nameof(SetOwned), value);

    // Sets a List of the entity, the List<T> a record holds for it, placed in the form of its element: as the
    // bytes of a Binary value when its elements hold no descriptor, as records otherwise, whatever their depth
    // (NestedValues). An element given as null is null. The refusals are those of SetOwned.
    public void SetList(LatticeField field, object value) => SetNested(field, StoredTypeEnum.List, nameof(SetList), value);

    public void SetMap(LatticeField field, object value) => SetNested(field, StoredTypeEnum.Map, nameof(SetMap), value);

    public void SetSet(LatticeField field, object value) => SetNested(field, StoredTypeEnum.Set, nameof(SetSet), value);

    // Sets the value of a Binary field: the bytes as they are, in their order, placed by their
    // length as RootLayout says. More than the 268,173,376 bytes of one value is refused with
    // LatticeException, ValidationFailed, with the field named, at the call: nothing is allocated
    // for it, the field is not set, and the writer stays usable. An empty value is a value, not null
    // (docs/decisions/schema-and-types.md, "Binary fields"). The refusal is the guard of the stored
    // form, as that of SetString.
    public void SetBinary(LatticeField field, ReadOnlySpan<byte> value)
    {
        ref readonly var stored = ref Take(field, StoredTypeEnum.Binary, nameof(SetBinary));
        var source = ValueSource.Bytes(value);

        _values.PlaceBytes(stored, stored.Offset, value.Length, ref source);
        MarkSet(stored, isNull: false);
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

        if (_values.Exported is not null)
        {
            throw new InvalidOperationException("A root for a forwarded write ends with Export.");
        }

        CheckRequiredFields();

        if (_values.Refused)
        {
            throw RefuseForMemory();
        }

        ArenaAddress root;
        bool allocated;

        try
        {
            var payloadLength = _values.InlineEnd - BlockHeader.Size;

            allocated = _values.Arena.TryAllocatePrepared(payloadLength, BlockKindEnum.Root, _values.Root, out root);

            if (allocated)
            {
                _values.SlotBytes += Arena.BlockSizeFor(payloadLength);
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

        // TryAllocatePrepared wrote the root, the kind and the length; a root has no parent and the offset 0.
        _values.Arena.Header(root).LayoutVersion = _layout.LayoutVersion;
        var payload = _values.Arena.Payload(root);
        Image[BlockHeader.Size.._values.InlineEnd].CopyTo(payload);

        // The value blocks, the directories and the blocks of records the root block holds the descriptors of
        // were allocated before their parent existed.
        var descriptors = new RootDescriptors(_layout, payload);

        while (descriptors.MoveNext())
        {
            var descriptor = ValueBlocks.Read(descriptors.Place);
            ResidentDescriptor.Write(descriptors.Place, descriptor);
            RootStorage.AdoptValue(_values.Arena, descriptor, root, descriptors.Offset);
        }

        End(WriterStateEnum.Completed);

        return root;
    }

    // The bytes Export writes for a root for a forwarded write, once its values are set.
    public readonly long ExportLength => sizeof(uint) + (_values.InlineEnd - BlockHeader.Size) + _values.Exported!.Length;

    // Ends a root for a forwarded write (see the class) and writes it into the output as BlockExport writes
    // a stored one: the layout version (UInt32, little-endian), the root payload, and each value out of
    // line in the order of its descriptor, a List of records as each block of its records followed by the
    // values of its records. Refuses with ValidationFailed, as Complete does, a field that is
    // not nullable and was not set; the writer has ended either way.
    public void Export(IBufferWriter<byte> output)
    {
        ThrowIfEnded();

        if (_values.Exported is not { } exported)
        {
            throw new InvalidOperationException("Only a root for a forwarded write is exported.");
        }

        CheckRequiredFields();

        var payload = Image[BlockHeader.Size.._values.InlineEnd];

        BinaryPrimitives.WriteUInt32LittleEndian(output.GetSpan(sizeof(uint)), _layout.LayoutVersion);
        output.Advance(sizeof(uint));

        // The payload fits one span: it is at most the payload of one block.
        payload.CopyTo(output.GetSpan(payload.Length));
        output.Advance(payload.Length);

        exported.WriteTo(output);

        End(WriterStateEnum.Completed);
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

    // An owned value or a List of the entity, written into the image of the root by NestedValues, with what it
    // takes counted here.
    //
    // Not inlined. Inlined into the write of the values of a root (Engine.Entities.RootValues.Write), it grows
    // that method for every write of an entity of scalar fields: in the JIT listing of a Release build,
    // inlined, 4,767 bytes with a frame of 344; called, 4,672 bytes with a frame of 328, as before a root for a
    // forwarded write carried nested values.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SetNested(LatticeField field, StoredTypeEnum type, string member, object value)
    {
        ref readonly var stored = ref Take(field, type, member);

        NestedValues.Write(ref _values, _layout.Structure, 0, field.Ordinal, value);
        MarkSet(stored, isNull: false);
    }

    // Every field that is not nullable was set; ValidationFailed for the first one that was not, with
    // everything the writer allocated freed and the writer ended.
    private void CheckRequiredFields()
    {
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

    // Frees every block the descriptors of the image name and releases every reference they hold to a
    // text entry: everything the writer allocated and took, since the root block is allocated last, by a
    // Complete that then cannot fail any more.
    private void FreeValues()
    {
        // A root for a forwarded write holds no block: its values out of line are managed memory.
        if (_values.Exported is not null)
        {
            return;
        }

        RootStorage.FreeImage(_values.Arena, _layout, Image);

        _values.SlotBytes = 0;
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

        return RefusalForMemory(_layout);
    }

    // The refusal of an entity of the layout that the arena has no room for, the same for a root this
    // writer writes and for one a master places from what a child forwarded (Engine.Writes.WriteExecutor).
    public static LatticeException RefusalForMemory(RootLayout layout) =>
        new(LatticeErrorEnum.WriteRejected, $"The memory budget of the database cannot hold the entity of {layout.EntityType.Name}.")
        {
            Limit = LatticeLimitEnum.Memory,
        };

    // Returns a rented image to the pool; nothing reads or writes the image afterwards.
    private void End(WriterStateEnum state)
    {
        if (_rented)
        {
            ArrayPool<byte>.Shared.Return(_scratch!);
        }

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

    private readonly LatticeException OutOfRange<TValue>(LatticeField field, TValue value) =>
        new(LatticeErrorEnum.ValidationFailed, $"The value {value} is outside the range of the field {Name(field)}, which is {field.Declaration.ScalarType}.");
}
