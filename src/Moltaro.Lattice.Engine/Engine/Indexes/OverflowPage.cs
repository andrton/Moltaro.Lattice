namespace Moltaro.Lattice.Engine.Indexes;

// One page of overflow buckets (OverflowBuckets): its pinned array of words and the index of the
// first word of its first bucket, on a line boundary. Written once, before any bucket of it is
// linked, and never changed.
internal readonly record struct OverflowPage(long[] Words, int First);
