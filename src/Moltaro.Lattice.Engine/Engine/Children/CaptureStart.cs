namespace Moltaro.Lattice.Engine.Children;

// What the capture of a child starts from, taken under the lock of the writes in the hold that registers
// the child (Capture.Register): the sequence of the master, at which the load ends because no write is
// published while the child is registered; the slots of the root table the capture walks, those below
// SlotExtent; and the live roots of each entity type, in the order of the schema, which the child sizes
// its indexes from.
internal readonly record struct CaptureStart(ulong Sequence, int SlotExtent, long[] Counts);
