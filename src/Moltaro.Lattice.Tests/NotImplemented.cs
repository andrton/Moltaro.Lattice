namespace Moltaro.Lattice.Tests;

// The reason for which tests carried by Epic 1, sprint S07 are skipped. The implementation removes
// every skip that names it, and the tests have to pass as they are written; then this type goes too.
internal static class NotImplemented
{
    // The check of a write batch that two operations address one entity when their keys are equal as
    // the schema compares them (the planner's answer to Q45).
    public const string BatchKeyComparison = "The check of a write batch that compares keys as the schema does, by the answer to Q45 in Epic 1, sprint S07, is not implemented yet.";
}
