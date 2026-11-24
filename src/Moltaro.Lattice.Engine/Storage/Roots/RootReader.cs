using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// Reads one root in place: each member reads one field from the root block, or from the blocks the
// root owns, where its RootLayout places it. A root is written and read field by field, so that
// neither a class nor a record has to box its values.
//
// Every member reads one field of the entity type of the layout: IsNull any field, GetLength a
// String or a Binary field, and each other Get… member a field of the type it serves. A field of
// another type, or of another entity type, is refused with ArgumentException. The overloads that take
// an in StoredField, IsNull, StoredBytes and ReadText, check neither: they trust a field this layout
// resolved (Ordered.EntryPart.Stored). A Get… member, and GetLength, of a field whose value is null is
// InvalidOperationException: the caller asks IsNull first.
//
// A scalar is read from the root block where it lies and allocates nothing; GetString and GetBinary
// allocate the value they return and nothing else. The reader finds its root block once, when it is
// made, and keeps a span over it.
//
// Valid only while the caller holds the protection of that root: owned blocks are accessed only
// under root protection, and neither a span nor an address escapes it (arena protocol, "Lock and
// lifetime order"). As a readonly ref struct the reader lives on the stack of the read: it cannot
// be kept on the heap or across an await.
//
// A reader of a copy. CopyFixedPart copies the fixed part of the root block, the null bitmap, the
// fixed fields and the descriptors, into memory of the caller, and a reader made over that copy
// reads IsNull and every Get… member of a fixed-width type from it, with no arena and so with no
// protection: a read copies under the protection of the root and builds its result after releasing
// it. The values of String and Binary fields lie outside the fixed part; a reader of a copy refuses
// GetString, GetBinary and ReadText with InvalidOperationException.
internal readonly ref struct RootReader
{
    // Null for a reader of a copy.
    private readonly Arena? _arena;

    private readonly RootLayout _layout;

    // The payload of the root block: the fixed part after the header, then the inline values; for a
    // reader of a copy, the copy of the fixed part. The layout counts its offsets from the start of
    // the block, header included, so a field at offset o lies at o - BlockHeader.Size here.
    private readonly ReadOnlySpan<byte> _payload;

    // Reads the root whose root block is at the address, written with the layout. Refuses with
    // ArgumentException an address that is not a valid block of the arena, a block whose kind is not
    // Root, and a block whose layout version is not that of the layout.
    public RootReader(Arena arena, RootLayout layout, ArenaAddress root)
    {
        ArgumentNullException.ThrowIfNull(layout);

        _payload = layout.RootPayload(arena, root);
        _arena = arena;
        _layout = layout;
    }

    // Reads a copy of the fixed part of a root written with the layout, which CopyFixedPart made. A
    // span of another length than FixedPartLength of the layout is refused with ArgumentException.
    public RootReader(RootLayout layout, ReadOnlySpan<byte> fixedPart)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (fixedPart.Length != FixedPartLength(layout))
        {
            throw new ArgumentException($"A copy of the fixed part of a root of {layout.EntityType.Name} has {FixedPartLength(layout)} bytes, not {fixedPart.Length}.", nameof(fixedPart));
        }

        _payload = fixedPart;
        _arena = null;
        _layout = layout;
    }

    // Everything stored for the entity of the root, by the stored lengths: the bytes of the root block, its
    // header, its fixed part and its inline values, and the encoded bytes of every value kept outside it, in a
    // block of its own, in chunks with the 16 bytes its directory holds for each chunk, or in the entry of the
    // text table a Dictionary text lies in, which every root that refers to it counts; for a List of records,
    // the payload of each block of its records, with the 16 bytes its directory holds for each chunk, and what
    // its records hold outside them, at every depth. What a search counts of an entity it holds in its answer
    // (LatticeDatabaseOptions.ReadMemoryLimit): a measure from the lengths the descriptors hold and the blocks
    // of records report, read in one walk over the root (StructureWalk), not an account of the slots of the
    // arena. Not for a reader of a copy.
    public long EntityBytes()
    {
        var counting = new Counting();

        StructureWalk.WalkPayload(ArenaOfValues(), _layout, _payload, ref counting);

        return BlockHeader.Size + _payload.Length + counting.Bytes;
    }

    // The value of an owned field, as a new LatticeOwnedValue of its owned type that holds every value
    // beneath it, as a record holds it (NestedValues).
    public LatticeOwnedValue GetOwned(LatticeField field)
    {
        ref readonly var stored = ref _layout.StoredFieldOf(field);

        CheckType(field, stored, StoredTypeEnum.Owned, nameof(GetOwned));

        var arena = ArenaOfValues();
        var structure = _layout.Structure;
        var payload = NotNullPayload(stored);

        return (LatticeOwnedValue)NestedValues.Read(arena, structure, payload[..structure.Size], payload, field.Ordinal, _layout.StringEncoding)!;
    }

    // The value of a List field, as a new List of the form a record holds, with every element at every depth
    // (NestedValues); the elements of a List whose elements hold no descriptor are read without a box.
    public object GetList(LatticeField field)
    {
        ref readonly var stored = ref _layout.StoredFieldOf(field);

        CheckType(field, stored, StoredTypeEnum.List, nameof(GetList));

        // A field of the entity has its ordinal as its position in the structure of the root (StructureLayout).
        return NestedValues.ReadList(ArenaOfValues(), _layout.Structure.ElementOf(field.Ordinal), ValueBlocks.Read(NotNull(stored)), _payload, _layout.StringEncoding);
    }

    // The bytes of the fixed part of a root block of the layout after its header: what CopyFixedPart
    // copies.
    public static int FixedPartLength(RootLayout layout) => layout.FixedSize - BlockHeader.Size;

    // Copies the fixed part of the root block into the destination, which has FixedPartLength bytes
    // at least, for a reader of a copy.
    public void CopyFixedPart(Span<byte> destination) => _payload[..FixedPartLength(_layout)].CopyTo(destination);

    // Whether the value of the field is null.
    public bool IsNull(LatticeField field) => HoldsNull(_layout.StoredFieldOf(field));

    // Whether the value of a field is null, for a caller that holds where the layout of this reader stores
    // it (Ordered.EntryPart.Stored), resolved once with the layout of an index.
    public bool IsNull(in StoredField stored) => HoldsNull(stored);

    // The value of a Boolean field.
    public bool GetBoolean(LatticeField field) => StoredScalars.ReadBoolean(Value(field, StoredTypeEnum.Boolean, nameof(GetBoolean)));

    // The value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose enum
    // type has a signed underlying type.
    // Kept out of line, as the JIT compiled it before the stored form moved into StoredScalars, whose
    // switch on the width is inlined here: inlined in turn, it would copy that switch into each of its
    // callers, the read by key among them (EntityTable.KeyEquals, 498 bytes of code instead of 762), and
    // leave the build of a class entity no budget to inline DateOnly.FromDayNumber (JIT listing at full
    // optimization).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public long GetInteger(LatticeField field) => StoredScalars.ReadInteger(Value(field, StoredTypeEnum.SignedInteger, nameof(GetInteger)));

    // The value of a field of an unsigned integer type, UInt8 to UInt64, or of an Enum field whose
    // enum type has an unsigned underlying type.
    // Kept out of line, as GetInteger is (see there).
    [MethodImpl(MethodImplOptions.NoInlining)]
    public ulong GetUnsigned(LatticeField field) => StoredScalars.ReadUnsigned(Value(field, StoredTypeEnum.UnsignedInteger, nameof(GetUnsigned)));

    // The value of a Float32 field.
    public float GetFloat32(LatticeField field) => StoredScalars.ReadFloat32(Value(field, StoredTypeEnum.Float32, nameof(GetFloat32)));

    // The value of a Float64 field.
    public double GetFloat64(LatticeField field) => StoredScalars.ReadFloat64(Value(field, StoredTypeEnum.Float64, nameof(GetFloat64)));

    // The value of a Decimal field.
    public decimal GetDecimal(LatticeField field) => StoredScalars.ReadDecimal(Value(field, StoredTypeEnum.Decimal, nameof(GetDecimal)));

    // The value of a Guid field.
    public Guid GetGuid(LatticeField field) => StoredScalars.ReadGuid(Value(field, StoredTypeEnum.Guid, nameof(GetGuid)));

    // The value of a DateTimeOffset field: the stored instant, with offset zero (Q03.4.1, "UTC
    // timestamps").
    public DateTimeOffset GetDateTimeOffset(LatticeField field) =>
        StoredScalars.ReadDateTimeOffset(Value(field, StoredTypeEnum.DateTimeOffset, nameof(GetDateTimeOffset)));

    // The value of a DateOnly field.
    public DateOnly GetDateOnly(LatticeField field) => StoredScalars.ReadDateOnly(Value(field, StoredTypeEnum.DateOnly, nameof(GetDateOnly)));

    // The value of a TimeOnly field.
    public TimeOnly GetTimeOnly(LatticeField field) => StoredScalars.ReadTimeOnly(Value(field, StoredTypeEnum.TimeOnly, nameof(GetTimeOnly)));

    // The value of a TimeSpan field.
    public TimeSpan GetTimeSpan(LatticeField field) => StoredScalars.ReadTimeSpan(Value(field, StoredTypeEnum.TimeSpan, nameof(GetTimeSpan)));

    // The value of a String field, as a new string decoded from the database encoding. A chunk
    // boundary inside a character does not damage it: the decoder carries the state of a character
    // across chunks (milestone contract, "Native layout and allocation").
    public string GetString(LatticeField field) =>
        StoredText.Read(ArenaOfValues(), Descriptor(field, StoredTypeEnum.String, nameof(GetString)), _payload, _layout.StringEncoding);

    // The text of a String field, to be read as UTF-16 a piece at a time without allocating
    // (StoredTextReader): what the comparison of the schema compares with a given text. Valid while
    // this reader is.
    public StoredTextReader ReadText(LatticeField field) =>
        new(ArenaOfValues(), Descriptor(field, StoredTypeEnum.String, nameof(ReadText)), _payload, _layout.StringEncoding);

    // The same reader for a caller that holds where the layout of this reader stores a String field, as
    // IsNull takes it.
    public StoredTextReader ReadText(in StoredField stored) =>
        new(ArenaOfValues(), ValueBlocks.Read(NotNull(stored)), _payload, _layout.StringEncoding);

    // The value of a Binary field, as a new array that belongs to the caller. An empty value is the
    // empty array, which every caller shares and none can change.
    public byte[] GetBinary(LatticeField field)
    {
        var arena = ArenaOfValues();
        var descriptor = Descriptor(field, StoredTypeEnum.Binary, nameof(GetBinary));

        if (descriptor.Length == 0)
        {
            return [];
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)descriptor.Length);

        ValueBlocks.CopyTo(arena, descriptor, _payload, bytes);

        return bytes;
    }

    // The bytes the fixed part stores for a field that is not null, as they lie: the value of a
    // fixed-width field as Values.StoredScalars wrote it, or the descriptor of a String or Binary value.
    // What an entry of an index copies of its root, and what the order of an index compares a key with
    // (Ordered.IndexLayout, Engine.Indexes.IndexOrder), for a caller that holds where the layout of this
    // reader stores the field, as IsNull takes it. InvalidOperationException for a field whose value is null.
    public ReadOnlySpan<byte> StoredBytes(in StoredField stored) => NotNull(stored);

    // The bytes of a value of this root that does not lie in chunks, which the descriptor given, one of
    // its own, names: inline in the root block, in a block of its own, or in the block of its text entry.
    public ReadOnlySpan<byte> ValueBytes(in ValueDescriptor descriptor) => ValueBlocks.Contiguous(ArenaOfValues(), descriptor, _payload);

    // The number of encoded bytes of the value of a String or Binary field, as stored: for a String,
    // its bytes in the database encoding, UTF-8 or UTF-16.
    public int GetLength(LatticeField field)
    {
        ref readonly var stored = ref _layout.StoredFieldOf(field);

        if (!stored.IsVariable)
        {
            throw new ArgumentException($"GetLength does not serve the field {Name(field)}, which is {field.Declaration.Type}.", nameof(field));
        }

        return (int)ValueBlocks.Read(NotNull(stored)).Length;
    }

    // The arena the values of String and Binary fields lie in; a reader of a copy has none of them.
    private Arena ArenaOfValues() =>
        _arena ?? throw new InvalidOperationException($"A reader of a copy of the fixed part of a root of {_layout.EntityType.Name} holds no String or Binary value.");

    private bool HoldsNull(in StoredField stored) =>
        stored.IsNullable && (_payload[RootLayout.NullBitmapOffset - BlockHeader.Size + stored.NullBit / 8] & (1 << (stored.NullBit % 8))) != 0;

    // The bytes of a field of the stored type, after checking that the member serves it and that it
    // is not null.
    private ReadOnlySpan<byte> Value(LatticeField field, StoredTypeEnum type, string member)
    {
        ref readonly var stored = ref _layout.StoredFieldOf(field);

        if (stored.Type != type)
        {
            throw new ArgumentException($"{member} does not serve the field {Name(field)}, which is {field.Declaration.Type}.", nameof(field));
        }

        return NotNull(stored);
    }

    // The descriptor of a String or a Binary field.
    private ValueDescriptor Descriptor(LatticeField field, StoredTypeEnum type, string member) =>
        ValueBlocks.Read(Value(field, type, member));

    // A field of the stored type, for a member that serves it.
    private void CheckType(LatticeField field, in StoredField stored, StoredTypeEnum type, string member)
    {
        if (stored.Type != type)
        {
            throw new ArgumentException($"{member} does not serve the field {Name(field)}, which is {field.Declaration.Type}.", nameof(field));
        }
    }

    // The payload of the root, after checking that the value of the field, which may be an owned value with
    // no bytes of its own, is not null.
    private ReadOnlySpan<byte> NotNullPayload(in StoredField stored)
    {
        if (HoldsNull(stored))
        {
            throw new InvalidOperationException($"The field {Name(stored.Field)} is null; ask IsNull first.");
        }

        return _payload;
    }

    private ReadOnlySpan<byte> NotNull(in StoredField stored)
    {
        if (HoldsNull(stored))
        {
            throw new InvalidOperationException($"The field {Name(stored.Field)} is null; ask IsNull first.");
        }

        return _payload.Slice(stored.Offset - BlockHeader.Size, stored.Width);
    }

    private string Name(LatticeField field) => $"{_layout.EntityType.Name}.{field.Name}";

    // What EntityBytes counts of the descriptors of a root, at every depth.
    private struct Counting : IStructureVisitor
    {
        public long Bytes { get; private set; }

        public readonly void EnterRecordBlock(ReadOnlySpan<byte> payload, StructureLayout element, int count) { }

        public void Visit(Arena arena, in ValueDescriptor descriptor, ArenaAddress holder, int offset, StructureLayout? element)
        {
            switch (descriptor.Kind)
            {
                case DescriptorKindEnum.Block or DescriptorKindEnum.Chunked or DescriptorKindEnum.Text:
                    Bytes += descriptor.Length;

                    if (ValueBlocks.LiesInChunks(descriptor))
                    {
                        Bytes += (long)ValueBlocks.ChunkCount((int)descriptor.Length) * ValueBlocks.DescriptorSize;
                    }

                    break;

                case DescriptorKindEnum.Records when descriptor.Location != 0:
                    var address = ValueBlocks.AddressOf(descriptor);

                    if (!NestedValues.LiesInChunks(descriptor, element!))
                    {
                        Bytes += arena.Header(address).PayloadLength;

                        break;
                    }

                    var entries = arena.Payload(address);

                    Bytes += entries.Length;

                    for (var entry = 0; entry < entries.Length; entry += ValueBlocks.DescriptorSize)
                    {
                        Bytes += ValueBlocks.Read(entries[entry..]).Length;
                    }

                    break;
            }
        }
    }
}
