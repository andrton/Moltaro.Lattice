namespace Moltaro.Lattice.TestSupport;

// Why a test written before its implementation is skipped: one reason per feature, shared by
// every test project that has tests of it. The implementer of the feature removes the skips
// that name its reason, and then the reason.
internal static class NotImplemented
{
    // Epic 1, sprint 2: the descriptor with enum types, entity types and index identities,
    // text policies, enum and Flags types, the binding of .NET enums, and the fingerprints.
    public const string SchemaDeclarations = "Schema identities, text policies, enum types and fingerprints are not implemented yet.";
}
