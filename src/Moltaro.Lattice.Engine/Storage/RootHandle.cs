namespace Moltaro.Lattice.Storage;

// Names one lifetime of one root: the generation of its slot in the high 32 bits and the slot of
// its entry in the root table in the low 32 bits. When a released slot is reused its generation
// advances, so a handle of the earlier lifetime no longer matches the slot and cannot reach the
// root that lives there now (RootTable.IsCurrent).
//
// A generation is local lifetime protection, not a second key of an entity and not a durable
// revision: a handle is local to one process, never persisted and never returned through the
// public API (milestone contract, "Native layout and allocation"). A matching generation alone
// does not make an access safe either; that takes the claims and the root protection of the
// arena protocol ("Lock and lifetime order").
internal readonly struct RootHandle : IEquatable<RootHandle>
{
    // The packed handle. It is declared so that a handle takes its 8 bytes in BlockHeader; the
    // members that write and read it are not implemented yet.
#pragma warning disable CS0169 // Never used until the arena of S04 is implemented.
    private readonly ulong _value;
#pragma warning restore CS0169

    // The generation the slot had when this lifetime began.
    public uint Generation =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // The slot of the root's entry in the root table.
    public uint Slot =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    // Two handles are equal when they name the same slot with the same generation.
    public bool Equals(RootHandle other) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public override bool Equals(object? obj) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public override int GetHashCode() =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public static bool operator ==(RootHandle left, RootHandle right) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");

    public static bool operator !=(RootHandle left, RootHandle right) =>
        throw new NotSupportedException("The arena of Epic 1, sprint S04 is not implemented yet.");
}
