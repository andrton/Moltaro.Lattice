namespace Moltaro.Lattice.Storage;

// The change one preparation makes to a tail, named by the index of its record (CollectionTails).
internal readonly record struct CollectionTailEdit(
    int Tail, ArenaAddress Former, int Active, int Need, bool NewOwner, bool Ended);
