namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of a sprint removes its skips and its
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S14: every declared secondary index kept with the data on every node, and the
    // search by the full key of an index.
    public const string BeforeSprint14 = "Epic 1, sprint S14: secondary indexes and the search by the full key of an index are not implemented yet.";

    // Epic 1, sprint S15: a search by the leading fields of an index, with a range, in reverse, or with
    // Offset or Limit.
    public const string BeforeSprint15 = "Epic 1, sprint S15: a search by leading fields, with a range, in reverse or with Offset or Limit is not implemented yet.";
}
