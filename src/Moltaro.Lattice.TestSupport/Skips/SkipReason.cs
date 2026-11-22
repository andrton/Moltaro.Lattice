namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // The commit of a write batch.
    public const string WriteBatch = "Executing a write batch is not implemented yet.";

    // What the engine needs for a write batch beside its commit: a publication exposed whole, the linear
    // reservation of an index and the forwarded batch.
    public const string WriteBatchMechanisms = "The mechanisms of the engine for a write batch are not implemented yet.";
}
