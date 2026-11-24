namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // What remains of owned values, Lists and references: a child of a master that stores them, which loads
    // and follows them; a write batch, and a write forwarded by a child, that holds them; and an index over a
    // field inside an owned value.
    public const string NestedValues = "Owned values, Lists and references on a child, in a write batch and in an index over a path are not implemented yet.";
}
