namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // The publisher of a node that lets the scratch of a large batch go once it has published it.
    public const string PublisherScratch = "Letting the scratch of the publisher of a large batch go is not implemented yet.";
}
