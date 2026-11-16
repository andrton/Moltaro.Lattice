namespace Moltaro.Lattice.Engine.Writes;

// What the storage step of the commit stage reports for a group: the sequence of the first position
// it assigned to the group. The writes of the group take consecutive positions from it, in the order
// of the group (ICommitStorage.CommitAsync; brief of Epic 1, sprint S09, "Types and members of S09"
// and "Positions").
internal readonly record struct CommitOutcome(ulong FirstSequence);
