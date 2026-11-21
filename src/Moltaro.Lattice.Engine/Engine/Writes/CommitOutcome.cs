namespace Moltaro.Lattice.Engine.Writes;

// What the persistence step of the commit stage reports for a group: the sequence of the first position
// it assigned to the group. The writes of the group take consecutive positions from it, in the order
// of the group (IPersistence.CommitAsync; docs/design/milestone-1-engine-contract.md, "Write
// coordinator and revision semantics").
internal readonly record struct CommitOutcome(ulong FirstSequence);
