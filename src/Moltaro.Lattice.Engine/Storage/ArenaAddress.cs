namespace Moltaro.Lattice.Storage;

// Where a block of the arena is: the segment, a slot of the arena's segment table, in the high
// 32 bits and the byte offset of the block in that segment in the low 32 bits. The address is
// relative: only the arena that owns the segment turns it into memory, and only after checking
// it. It is local to one arena and never appears in a durable record or in a public result
// (milestone contract, "Native layout and allocation").
//
// The address zero is null. It names no block, and the default value is null.
internal readonly struct ArenaAddress : IEquatable<ArenaAddress>
{
    // The packed address that Value returns. It is declared so that an address takes its 8 bytes
    // in BlockHeader and RootEntry; the members that write and read it are not implemented yet.
#pragma warning disable CS0169 // Never used until the arena of S04 is implemented.
    private readonly ulong _value;
#pragma warning restore CS0169

    // Packs a segment and an offset. It does not check that a block is there: only the arena can
    // tell, through Arena.IsValid.
    public ArenaAddress(uint segment, uint offset) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The slot of the segment in the arena's segment table.
    public uint Segment =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The byte offset of the block within its segment.
    public uint Offset =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Whether this is the null address, zero.
    public bool IsNull =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The address as one number: the segment in the high 32 bits, the offset in the low 32 bits.
    public ulong Value =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Two addresses are equal when they name the same segment and the same offset.
    public bool Equals(ArenaAddress other) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public override bool Equals(object? obj) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public override int GetHashCode() =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public static bool operator ==(ArenaAddress left, ArenaAddress right) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public static bool operator !=(ArenaAddress left, ArenaAddress right) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");
}
