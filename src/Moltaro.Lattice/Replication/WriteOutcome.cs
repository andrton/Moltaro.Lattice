namespace Moltaro.Lattice.Replication;

// What the master reports for a completed write. Position includes the write; for a write
// that had nothing to do it is the position the master was at.
internal readonly record struct WriteOutcome(bool Changed, LatticePosition Position);
