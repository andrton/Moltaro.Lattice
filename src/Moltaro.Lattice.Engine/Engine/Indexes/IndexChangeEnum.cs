namespace Moltaro.Lattice.Engine.Indexes;

// What one write does to one secondary index: the entry the old state of the entity contributes
// against the entry its new state contributes (SecondaryIndex.ChangeOf). A state of a root contributes
// one entry to each index, a null part included; an index on the members of a collection, whose states
// would contribute sets of entries, is not built.
internal enum IndexChangeEnum
{
    // The two entries are the same bytes: the index does not change.
    None = 0,

    // An insert of the entity: the new entry is added, before the position of the write.
    Insert = 1,

    // A delete of the entity: the old entry is taken away, after the position of the write.
    Remove = 2,

    // The key changed: the new entry is added before the position and the old one taken away after it.
    Move = 3,

    // The key is equal by the order and its bytes differ, a text under another case or a Decimal of
    // another scale: the entry is replaced at its place, after the position.
    Rewrite = 4,
}
