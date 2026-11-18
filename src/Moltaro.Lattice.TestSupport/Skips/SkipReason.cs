namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason of every test of Epic 1, sprint S12 that cannot pass before its implementation, in the
// scenario tests and in the tests of the engine alike. The implementer of S12 removes every skip that
// names it, and then this constant and its folder.
internal static class SkipReason
{
    public const string BeforeSprint12 = "Epic 1, sprint S12: LatticeDatabase still opens the placeholder, Validate does not check the form of an address yet, and a child of the engine does not forward writes to its master yet.";
}
