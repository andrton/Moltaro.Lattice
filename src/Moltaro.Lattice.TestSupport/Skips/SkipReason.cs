namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // A write batch committed through a child, which forwards it to its master.
    public const string BatchThroughAChild = "Executing a write batch through a child is not implemented yet.";
}
