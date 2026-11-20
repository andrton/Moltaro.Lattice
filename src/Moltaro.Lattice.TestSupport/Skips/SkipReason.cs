namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of a sprint removes its skips and its
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S14, part 2: the closed form of the reservation of index nodes and the standing
    // reserve; the upkeep of an index skipped for a write that leaves its entry as it was; a search held by
    // one object; and FindAsync delivering its failures by the rule of every asynchronous member.
    public const string BeforeSprint14Part2 = "Epic 1, sprint S14, part 2: the closed form of the reservation, the standing reserve of index nodes, the upkeep skipped for an unchanged entry, a search held by one object and the delivery of the failures of FindAsync are not implemented yet.";

    // Epic 1, sprint S15: a search by the leading fields of an index, with a range, in reverse, or with
    // Offset or Limit; and a search that returns no entity twice, also one deleted and created again
    // while it runs.
    public const string BeforeSprint15 = "Epic 1, sprint S15: a search by leading fields, with a range, in reverse or with Offset or Limit, and no entity returned twice across its re-creation, are not implemented yet.";
}
