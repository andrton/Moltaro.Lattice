namespace Moltaro.Lattice.Storage;

// Names one lifetime of one root: the generation of its slot in the high 32 bits and the slot of
// its entry in the root table in the low 32 bits. The first generation of a slot is 1, and every
// reuse of the slot advances it, so a handle of an earlier lifetime no longer matches the slot
// and cannot reach the root that lives there now. The default value, generation 0, is never
// current (RootTable.IsCurrent).
//
// A generation is local lifetime protection, not a second key of an entity and not a durable
// revision: a handle is local to one process, never persisted and never returned through the
// public API (milestone contract, "Native layout and allocation"). A matching generation alone
// does not make an access safe either; that takes the protection of reads of the arena protocol
// ("Deferred reclamation, 16 November 2026").
internal readonly struct RootHandle : IEquatable<RootHandle>
{
    // The packed handle: the generation in the high 32 bits, the slot in the low 32 bits.
    private readonly ulong _value;

    // Packs a generation and a slot. It does not check that such a lifetime exists: only the root
    // table can tell, through RootTable.IsCurrent.
    public RootHandle(uint generation, uint slot) =>
        _value = ((ulong)generation << 32) | slot;

    private RootHandle(ulong packed) => _value = packed;

    // The packed handle as one number, the generation above the slot: what orders the identities of the
    // entries of a secondary index (Engine.Indexes.IndexOrder).
    public ulong Packed => _value;

    // The handle whose packed number follows this one: where a search starts after this identity. A slot is
    // below 2^31, so no handle is the largest number and this never wraps.
    public RootHandle Next => new(_value + 1);

    // The generation the slot had when this lifetime began.
    public uint Generation => (uint)(_value >> 32);

    // The slot of the root's entry in the root table.
    public uint Slot => (uint)_value;

    // Two handles are equal when they name the same slot with the same generation.
    public bool Equals(RootHandle other) => _value == other._value;

    public override bool Equals(object? obj) => obj is RootHandle other && Equals(other);

    public override int GetHashCode() => _value.GetHashCode();

    // The generation and the slot, for a message or a failed assertion.
    public override string ToString() => $"{Generation}@{Slot}";

    public static bool operator ==(RootHandle left, RootHandle right) => left.Equals(right);

    public static bool operator !=(RootHandle left, RootHandle right) => !left.Equals(right);
}
