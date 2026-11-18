namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a fix package that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of the package removes the skips and the
// constant.
internal static class SkipReason
{
    // Epic 1, the fixes after sprint S13: the findings of the outside review and of the review of S13
    // that change behavior, each stated by a test that fails before its fix.
    public const string BeforeTheFixesAfterSprint13 = "Epic 1, the fixes after sprint S13: the behavior this test states is not built yet.";
}
