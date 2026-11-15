using Moltaro.Lattice.Schema;

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
// Valid only while the caller holds the protection of that root: owned blocks are accessed only
// under root protection, and neither a span nor an address escapes it (arena protocol, "Lock and
// lifetime order"). As a readonly ref struct the reader lives on the stack of the read: it cannot
// be kept on the heap or across an await.
internal readonly ref struct RootReader
{
    // Reads the root whose root block is at the address, written with the layout. Refuses with
    // ArgumentException an address that is not a valid block of the arena, a block whose kind is not
    // Root, and a block whose layout version is not that of the layout.
    public RootReader(Arena arena, RootLayout layout, ArenaAddress root) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Whether the value of the field is null.
    public bool IsNull(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Boolean field.
    public bool GetBoolean(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose enum
    // type has a signed underlying type.
    public long GetInteger(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a field of an unsigned integer type, UInt8 to UInt64, or of an Enum field whose
    // enum type has an unsigned underlying type.
    public ulong GetUnsigned(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Float32 field.
    public float GetFloat32(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Float64 field.
    public double GetFloat64(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Decimal field.
    public decimal GetDecimal(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Guid field.
    public Guid GetGuid(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a DateTimeOffset field: the stored instant, with offset zero (Q03.4.1, "UTC
    // timestamps").
    public DateTimeOffset GetDateTimeOffset(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a DateOnly field.
    public DateOnly GetDateOnly(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a TimeOnly field.
    public TimeOnly GetTimeOnly(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a TimeSpan field.
    public TimeSpan GetTimeSpan(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a String field, as a new string decoded from the database encoding. A chunk
    // boundary inside a character does not damage it: the decoder carries the state of a character
    // across chunks (milestone contract, "Native layout and allocation").
    public string GetString(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The value of a Binary field, as a new array that belongs to the caller.
    public byte[] GetBinary(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // The number of encoded bytes of the value of a String or Binary field, as stored: for a String,
    // its bytes in the database encoding, UTF-8 or UTF-16.
    public int GetLength(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");
}
