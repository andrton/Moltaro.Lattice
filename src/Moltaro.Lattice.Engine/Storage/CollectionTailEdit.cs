namespace Moltaro.Lattice.Storage;

internal readonly record struct CollectionTailEdit(
    CollectionTail Tail, ArenaAddress Former, int Active, int Need, bool NewOwner, bool Ended);
