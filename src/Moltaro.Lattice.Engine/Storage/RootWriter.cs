using Moltaro.Lattice.Schema;

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
// malformed text, a value out of range or SetNull of a field that is not nullable, did not set
// the field, and a valid value may follow.
//
// maxEntitySize bounds FixedSize plus the encoded bytes of every String and Binary value of the
// entity, wherever a value went (Q14, "Configurable root-object size limit"). It becomes the
// option MaxEntitySize of the database in S09, 1 MiB to start (milestone contract, "Initial
// bounded settings"). An entity of exactly maxEntitySize bytes is stored; one byte more is
// refused. Only Complete reports the size and the memory of the arena: no Set… member fails for
// either. An OutOfMemoryException of the native allocator passes through whichever member meets
// it; the caller then calls Abandon, which frees everything.
//
// A writer ends in one of two ways. Complete checks the root as a whole and returns the address of
// its root block; the root then belongs to the caller, who publishes it (S06) and frees it later
// with RootStorage.Free. Abandon frees everything the writer allocated. After Complete, whether
// it returned or failed, and after Abandon, every Set… member, SetNull and Complete are
// InvalidOperationException, and so is Abandon after a Complete that returned. Abandon after a
// failed Complete, and a second Abandon, do nothing.
//
// As a ref struct it lives on the stack of the method that writes the root: it cannot be kept on
// the heap or across an await.
internal ref struct RootWriter
{
    // Starts a root of the entity type of the layout, in the arena, for the root that the handle
    // names, with the limit maxEntitySize. Refuses a null arena or layout with
    // ArgumentNullException, and a maxEntitySize below 1 with ArgumentOutOfRangeException.
    public RootWriter(Arena arena, RootLayout layout, RootHandle root, int maxEntitySize) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a nullable field, of any type, to null. A field that is not nullable is
    // refused with LatticeException, ValidationFailed, at the call.
    public void SetNull(LatticeField field) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Boolean field.
    public void SetBoolean(LatticeField field, bool value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a field of a signed integer type, Int8 to Int64, or of an Enum field whose
    // enum type has a signed underlying type, at the width of that type. A value outside its range
    // is ValidationFailed; it is never cut, wrapped or widened to make it fit (Q03.1). Whether an
    // Enum value is a declared member is checked from S07 on.
    public void SetInteger(LatticeField field, long value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a field of an unsigned integer type, UInt8 to UInt64, or of an Enum field
    // whose enum type has an unsigned underlying type, at the width of that type. A value outside
    // its range is ValidationFailed, as for SetInteger.
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

    // Sets the value of a String field with Plain storage: in a UTF-8 database the UTF-8 of its
    // characters, in a UTF-16 database its code units, placed by its encoded length as RootLayout
    // says. Only well-formed text is stored, in both encodings (Q04.5): text that is not well-formed
    // UTF-16, a surrogate without its pair, is refused with LatticeException, ValidationFailed, at
    // the call. Then nothing is allocated for the value, the counters of the arena are the same
    // before and after the call, no character is replaced by another, the field is not set, and the
    // writer stays usable. An empty string is a value, not null.
    public void SetString(LatticeField field, ReadOnlySpan<char> value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Sets the value of a Binary field: the bytes as they are, in their order, placed by their
    // length as RootLayout says. An empty value is a value, not null (Q03.4.1, "Binary fields").
    public void SetBinary(LatticeField field, ReadOnlySpan<byte> value) =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Ends the root and returns the address of its root block. A nullable field that was never set
    // is null. Refuses with LatticeException: ValidationFailed when a field that is not nullable was
    // never set; WriteRejected when the entity is over maxEntitySize, when the arena is out of
    // memory for it, or when a value needs more chunks than one directory block holds. When it
    // fails, everything the writer allocated is freed: LiveBytes and ClassSlackBytes of the arena
    // are as they were before the writer, while a segment the arena obtained for the writer may
    // stay in its reserve, so Segments and ReservedSegments may differ.
    public ArenaAddress Complete() =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");

    // Ends the writing without a root: frees everything the writer allocated so far.
    public void Abandon() =>
        throw new NotSupportedException("The root layout of Epic 1, sprint S05 is not implemented yet.");
}
