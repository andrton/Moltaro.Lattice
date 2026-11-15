namespace Moltaro.Lattice.Engine.Tests;

// The reasons for which the tests of Epic 1, sprint S05 are skipped: one for the root layout, its
// writer, its reader and RootStorage, one for the reference fixture of the measurement program.
// Both are in the contract, and every member that acts throws NotSupportedException until the
// implementation of the sprint arrives. The implementation removes every skip that names one of
// these reasons, and the tests have to pass as they are written; then this type goes too.
internal static class NotImplemented
{
    public const string RootLayout = "The root layout of Epic 1, sprint S05 is not implemented yet.";

    public const string ReferenceFixture = "The reference fixture of Epic 1, sprint S05 is not implemented yet.";
}
