using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Storage;

// Writes one root: collects the values of its fields, validates them and allocates in the arena
// what they need, placed as its RootLayout says. A root is written and read field by field, so
// that neither a class nor a record has to box its values. The blocks are the private blocks of a
// write that is not published yet (arena protocol, "Read and mutation paths"): they are filled and
// validated before the write goes further, and a write that does not go on gives every one of them
// back.
//
// Every block the writer allocates belongs to the root that the handle root names: its header
// carries that handle (BlockHeader.Root), and it names the block that holds its descriptor, by
// Parent and DescriptorOffset, the offset counted from the start of that block, its header
// included, as RootLayout.FixedSize counts. The root block has no parent and the offset 0.
//
// maxEntitySize bounds the encoded bytes of one entity with all it owns: its fixed fields, its
// strings and its Binary values, however they are split into blocks and chunks (Q14,
// "Configurable root-object size limit"). It becomes the option MaxEntitySize of the database in
// S09, 1 MiB to start (milestone contract, "Initial bounded settings"). An entity above it is
// refused, and one just below it is stored.
//
// A writer ends in one of two ways. Complete checks the root as a whole and returns the address of
// its root block; publishing the root is its caller's (S06), and RootStorage.Free frees it later.
// Abandon gives back everything the writer allocated.
//
// As a ref struct it lives on the stack of the method that writes the root: it cannot be kept on
// the heap or across an await.
internal ref struct RootWriter
{
    // Starts a root of the entity type of the layout, in the arena, for the root that the handle
    // names, with the limit maxEntitySize.
    public RootWriter(Arena arena, RootLayout layout, RootHandle root, int maxEntitySize) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of the field to null.
    public void SetNull(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Boolean field.
    public void SetBoolean(LatticeField field, bool value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a field of a signed integer type, Int8, Int16, Int32 or Int64, or of an Enum
    // field. The value is stored at the width of the type, for an Enum at the width of the
    // underlying type of its enum type. It is range checked against that width: a value is never
    // cut, wrapped or widened to make it fit (Q03.1).
    public void SetInteger(LatticeField field, long value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a field of an unsigned integer type, UInt8, UInt16, UInt32 or UInt64, stored
    // at the width of that type. As for every integer, a value is never cut, wrapped or widened to
    // make it fit (Q03.1).
    public void SetUnsigned(LatticeField field, ulong value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Float32 field.
    public void SetFloat32(LatticeField field, float value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Float64 field.
    public void SetFloat64(LatticeField field, double value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Decimal field, a .NET decimal of 16 bytes (Q03.1).
    public void SetDecimal(LatticeField field, decimal value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Guid field, its 16 bytes, never text (Q03.4.1, "Primary-key types and Guid
    // fields").
    public void SetGuid(LatticeField field, Guid value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a DateTimeOffset field: the instant, stored as its UTC ticks. The offset of
    // the value is not kept; it is read back with offset zero (Q03.4.1, "UTC timestamps").
    public void SetDateTimeOffset(LatticeField field, DateTimeOffset value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a DateOnly field, stored as its day number (Q03.4.1, "DateOnly, TimeOnly and
    // TimeSpan").
    public void SetDateOnly(LatticeField field, DateOnly value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a TimeOnly field, stored as its ticks since midnight (Q03.4.1).
    public void SetTimeOnly(LatticeField field, TimeOnly value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a TimeSpan field, stored as its signed ticks (Q03.4.1).
    public void SetTimeSpan(LatticeField field, TimeSpan value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a String field with Plain storage. The text is encoded in the database
    // encoding of the schema, UTF-8 or UTF-16, and placed by its encoded length, as RootLayout says.
    // Only well-formed text is stored, in both encodings (Q04.5): text that is not well-formed
    // UTF-16, such as a surrogate without its pair, is refused with LatticeException,
    // ValidationFailed, before anything is allocated, and no character is ever replaced by another.
    // An empty string is a value, not null.
    public void SetString(LatticeField field, ReadOnlySpan<char> value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Binary field: the bytes as they are, in their order, placed by their
    // length, as RootLayout says. An empty value is a value, not null (Q03.4.1, "Binary fields").
    public void SetBinary(LatticeField field, ReadOnlySpan<byte> value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Ends the root and returns the address of its root block. Refuses with LatticeException:
    // ValidationFailed when a required field is missing; WriteRejected when the entity is over
    // maxEntitySize, or when the arena is out of memory for it.
    public ArenaAddress Complete() =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Ends the writing without a root: frees everything the writer allocated so far.
    public void Abandon() =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");
}
