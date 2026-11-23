namespace Moltaro.Lattice.TestSupport.Skips;

// The reasons with which the tests that cannot pass before their feature is implemented are skipped
// (AGENTS.md, "Tests And Samples"), one for each feature, shared by every test project. The implementer
// of the sprint that implements a feature removes its skips and its constant.
internal static class SkipReason
{
    // A write of an entity type with an Owned or a Reference field is refused with NotSupportedException
    // (LatticeFieldTypeEnum.Owned and LatticeFieldTypeEnum.Reference).
    public const string OwnedValuesAndReferences = "Storing owned values and references is not implemented yet.";
}
