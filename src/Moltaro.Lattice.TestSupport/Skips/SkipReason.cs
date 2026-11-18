namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of the sprint removes the skips and the
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S13: text entries and Dictionary strings, and the removal of the unused package
    // Arc.Collections that rides with it.
    public const string BeforeSprint13 = "Epic 1, sprint S13: text entries and Dictionary strings are not implemented yet.";
}
