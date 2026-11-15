namespace Moltaro.Lattice.Engine.Tests.Storage;

// The one reason for which the tests of Epic 1, sprint S04 are skipped. The arena and the root
// table are in the contract, and every member that acts throws NotSupportedException until the
// implementation of the sprint arrives. The implementation removes every skip that names this
// reason, and the tests have to pass as they are written; then this type goes too.
internal static class NotImplemented
{
    public const string Arena = "The arena of Epic 1, sprint S04 is not implemented yet.";
}
