namespace Moltaro.Lattice.Engine.Indexes;

// Which entry one descent of a reader finds against its probe (OrderedTree.Seek): the first entry at or
// after it, for a walk in the index order, or the last entry before it, for a walk in its reverse.
internal enum SeekSideEnum
{
    AtOrAfter,

    Before,
}
