namespace Moltaro.Lattice.TestSupport.Replication;

// The one reason for which the tests of Epic 1, sprint S10 are skipped until its implementation: a
// child of the engine that connects to its master over TCP, loads the whole database and becomes
// ready, and a master of the engine that accepts it. The implementer of S10 removes the skips that
// name it, and then this constant.
internal static class ChildLoading
{
    public const string NotImplemented = "A child of the engine that loads the database from its master over TCP is not implemented yet (Epic 1, sprint S10).";
}
