namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason of every test of the fixes after the review of Epic 1, sprint S12 that cannot pass
// before those fixes, in the scenario tests and in the tests of the engine alike. The implementer of the
// fixes removes every skip that names it, and then this constant and its folder.
internal static class SkipReason
{
    public const string BeforeTheFixesOfSprint12 = "Epic 1, sprint S12, the fixes after the review: the frame Closed, a refusal recorded before the write leaves the barrier, and the other fixes of \"Decided at the review of S12\" are not implemented yet.";
}
