namespace Moltaro.Lattice.Engine.Tests;

// The reason for which the tests of Epic 1, sprint S06 in this project are skipped: the engine
// database is in the contract, and every member that acts throws NotSupportedException until the
// implementation of the sprint arrives. The implementation removes every skip that names it, and the
// tests have to pass as they are written; then this type goes too.
internal static class NotImplemented
{
    public const string EngineDatabase = "The engine database of Epic 1, sprint S06, is not implemented yet.";
}
