namespace Moltaro.Lattice.Engine.Tests;

// The reason for which tests of this project are skipped. The implementation removes every skip that
// names it, and the tests have to pass as they are written; then this type goes too.
internal static class NotImplemented
{
    // The planner's word on the first review of Epic 1, sprint S06: the engine runs no code of the
    // application inside a gate or the close barrier.
    public const string ApplicationCodeOutsideGates = "The engine running no code of the application inside a gate or the close barrier (first review of S06) is not implemented yet.";
}
