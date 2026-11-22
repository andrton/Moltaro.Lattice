namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of a sprint removes its skips and its
// constant.
internal static class SkipReason
{
    public const string UniqueIndexes = "Unique indexes are not implemented yet.";
}
