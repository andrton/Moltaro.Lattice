using System.Runtime.InteropServices;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage;

// Reads one root in place: each member reads one field from the root block, or from the blocks the
// root owns, where its RootLayout places it. A root is written and read field by field, so that
// neither a class nor a record has to box its values.
//
// Every member reads one field of the entity type of the layout: IsNull any field, GetLength a
// String or a Binary field, and each other Get… member a field of the type it serves. A field of
// another type, or of another entity type, is refused with ArgumentException. A Get… member, and GetLength, of a field whose value is null is
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
internal readonly ref struct RootReader
{
    private readonly Arena _arena;

    private readonly RootLayout _layout;

    // The payload of the root block: the fixed part after the header, then the inline values. The
    // layout counts its offsets from the start of the block, header included, so a field at offset
    // o lies at o - BlockHeader.Size here.
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

    // Whether the value of the field is null.
    public bool IsNull(LatticeField field) => HoldsNull(_layout.StoredFieldOf(field));

    // The value of a Boolean field.
    public bool GetBoolean(LatticeField field) => Value(field, StoredTypeEnum.Boolean, nameof(GetBoolean))[0] != 0;

    // The value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose enum
    // type has a signed underlying type.
    public long GetInteger(LatticeField field)
    {
        var place = Value(field, StoredTypeEnum.SignedInteger, nameof(GetInteger));

        return place.Length switch
        {
            sizeof(sbyte) => (sbyte)place[0],
            sizeof(short) => MemoryMarshal.Read<short>(place),
            sizeof(int) => MemoryMarshal.Read<int>(place),
            _ => MemoryMarshal.Read<long>(place),
        };
    }

    // The value of a field of an unsigned integer type, UInt8 to UInt64, or of an Enum field whose
    // enum type has an unsigned underlying type.
    public ulong GetUnsigned(LatticeField field)
    {
        var place = Value(field, StoredTypeEnum.UnsignedInteger, nameof(GetUnsigned));

        return place.Length switch
        {
            sizeof(byte) => place[0],
            sizeof(ushort) => MemoryMarshal.Read<ushort>(place),
            sizeof(uint) => MemoryMarshal.Read<uint>(place),
            _ => MemoryMarshal.Read<ulong>(place),
        };
    }

    // The value of a Float32 field.
    public float GetFloat32(LatticeField field) =>
        BitConverter.Int32BitsToSingle(MemoryMarshal.Read<int>(Value(field, StoredTypeEnum.Float32, nameof(GetFloat32))));

    // The value of a Float64 field.
    public double GetFloat64(LatticeField field) =>
        BitConverter.Int64BitsToDouble(MemoryMarshal.Read<long>(Value(field, StoredTypeEnum.Float64, nameof(GetFloat64))));

    // The value of a Decimal field.
    public decimal GetDecimal(LatticeField field)
    {
        Span<int> bits = stackalloc int[4];

        Value(field, StoredTypeEnum.Decimal, nameof(GetDecimal)).CopyTo(MemoryMarshal.AsBytes(bits));

        return new decimal(bits);
    }

    // The value of a Guid field.
    public Guid GetGuid(LatticeField field) => new(Value(field, StoredTypeEnum.Guid, nameof(GetGuid)));

    // The value of a DateTimeOffset field: the stored instant, with offset zero (Q03.4.1, "UTC
    // timestamps").
    public DateTimeOffset GetDateTimeOffset(LatticeField field) =>
        new(MemoryMarshal.Read<long>(Value(field, StoredTypeEnum.DateTimeOffset, nameof(GetDateTimeOffset))), TimeSpan.Zero);

    // The value of a DateOnly field.
    public DateOnly GetDateOnly(LatticeField field) =>
        DateOnly.FromDayNumber(MemoryMarshal.Read<int>(Value(field, StoredTypeEnum.DateOnly, nameof(GetDateOnly))));

    // The value of a TimeOnly field.
    public TimeOnly GetTimeOnly(LatticeField field) =>
        new(MemoryMarshal.Read<long>(Value(field, StoredTypeEnum.TimeOnly, nameof(GetTimeOnly))));

    // The value of a TimeSpan field.
    public TimeSpan GetTimeSpan(LatticeField field) =>
        new(MemoryMarshal.Read<long>(Value(field, StoredTypeEnum.TimeSpan, nameof(GetTimeSpan))));

    // The value of a String field, as a new string decoded from the database encoding. A chunk
    // boundary inside a character does not damage it: the decoder carries the state of a character
    // across chunks (milestone contract, "Native layout and allocation").
    public string GetString(LatticeField field) =>
        StoredText.Read(_arena, Descriptor(field, StoredTypeEnum.String, nameof(GetString)), _payload, _layout.StringEncoding);

    // The value of a Binary field, as a new array that belongs to the caller. An empty value is the
    // empty array, which every caller shares and none can change.
    public byte[] GetBinary(LatticeField field)
    {
        var descriptor = Descriptor(field, StoredTypeEnum.Binary, nameof(GetBinary));

        if (descriptor.Length == 0)
        {
            return [];
        }

        var bytes = GC.AllocateUninitializedArray<byte>((int)descriptor.Length);

        ValueBlocks.CopyTo(_arena, descriptor, _payload, bytes);

        return bytes;
    }

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

    private ReadOnlySpan<byte> NotNull(in StoredField stored)
    {
        if (HoldsNull(stored))
        {
            throw new InvalidOperationException($"The field {Name(stored.Field)} is null; ask IsNull first.");
        }

        return _payload.Slice(stored.Offset - BlockHeader.Size, stored.Width);
    }

    private string Name(LatticeField field) => $"{_layout.EntityType.Name}.{field.Name}";
}
