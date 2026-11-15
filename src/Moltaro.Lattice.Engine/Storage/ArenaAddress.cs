namespace Moltaro.Lattice.Storage;

// Where a block of the arena is: the segment, a slot of the arena's segment table, in the high
// 32 bits and the byte offset of the block in that segment in the low 32 bits. The address is
// relative: only the arena that owns the segment turns it into memory, and only after checking
// it. It is local to one arena and never appears in a durable record or in a public result
// (milestone contract, "Native layout and allocation").
//
// The address zero is null. It names no block, and the default value is null. The arena never
// gives slot 0 of its segment table to a segment, so no block has an address with segment 0.
internal readonly struct ArenaAddress : IEquatable<ArenaAddress>
{
    // The packed address that Value returns.
    private readonly ulong _value;

    // Packs a segment and an offset. It does not check that a block is there: only the arena can
    // tell, through Arena.IsValid.
    public ArenaAddress(uint segment, uint offset) =>
        _value = ((ulong)segment << 32) | offset;

    // The slot of the segment in the arena's segment table.
    public uint Segment => (uint)(_value >> 32);

    // The byte offset of the block within its segment.
    public uint Offset => (uint)_value;

    // Whether this is the null address, zero.
    public bool IsNull => _value == 0;

    // The address as one number: the segment in the high 32 bits, the offset in the low 32 bits.
    public ulong Value => _value;

    // Two addresses are equal when they name the same segment and the same offset.
    public bool Equals(ArenaAddress other) => _value == other._value;

    public override bool Equals(object? obj) => obj is ArenaAddress other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    // The segment and the offset, for a message or a failed assertion.
    public override string ToString() => $"{Segment}:{Offset}";

    public static bool operator ==(ArenaAddress left, ArenaAddress right) => left.Equals(right);

    public static bool operator !=(ArenaAddress left, ArenaAddress right) => !left.Equals(right);
}
