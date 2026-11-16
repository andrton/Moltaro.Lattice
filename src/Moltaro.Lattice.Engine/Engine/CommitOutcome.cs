namespace Moltaro.Lattice.Engine;

// What the storage step of the commit stage reports for a group: the sequence of the first position
// it assigned to the group (ICommitStorage.CommitAsync; brief of Epic 1, sprint S09, "Types and
// members of S09").
internal readonly record struct CommitOutcome(ulong FirstSequence);
