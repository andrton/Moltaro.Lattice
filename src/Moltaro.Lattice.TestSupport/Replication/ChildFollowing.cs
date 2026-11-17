namespace Moltaro.Lattice.TestSupport.Replication;

// The one reason for which the tests of Epic 1, sprint S11 are skipped until its implementation: a
// child of the engine that loads while its master keeps writing, ends at a fixed position of the
// master, and from there applies the master's changes in order, and a master of the engine that
// accepts writes while children are connected or loading. The implementer of S11 removes the skips that
// name it, and then this constant.
internal static class ChildFollowing
{
    public const string NotImplemented = "A child of the engine that loads under writes and follows its master is not implemented yet (Epic 1, sprint S11).";
}
