namespace Moltaro.Lattice.Storage;

// Test audit of an owner-local lease for a detached lookup slot. The snapshot
// is taken under Writes, including when a prepared lookup has borrowed the slot.
internal readonly record struct CollectionSlotOwnership(
    RootHandle Owner, int DescriptorOffset, ArenaAddress FormerLookup, ArenaAddress Address, bool Borrowed);
