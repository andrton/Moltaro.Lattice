namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of a sprint removes its skips and its
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S15: a search by the leading fields of an index, with a range, in reverse, or with
    // Offset or Limit; and a search that returns no entity twice, also one deleted and created again
    // while it runs.
    public const string BeforeSprint15 = "Epic 1, sprint S15: a search by leading fields, with a range, in reverse or with Offset or Limit, and no entity returned twice across its re-creation, are not implemented yet.";
}
