namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // Owned values, Lists and references stored, copied, replaced and followed by children, with what comes
    // with them: the bound of a value at every depth, a value written by name bound as a part of a search
    // key, and the timestamps of a read from a source the tests drive.
    public const string NestedValues = "Storing owned values, Lists and references is not implemented yet.";
}
