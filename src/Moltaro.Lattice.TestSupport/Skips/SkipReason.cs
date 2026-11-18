namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of the sprint removes the skips and the
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S13: text whose last reference lies in a retired root waits for the next look of the
    // drain no longer than other retired memory does.
    public const string BeforeSprint13 = "Epic 1, sprint S13: dead text held by retired roots does not count toward the next look of the drain yet.";
}
