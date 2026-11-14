namespace Moltaro.Lattice.TestSupport;

// Why a test written before its implementation is skipped: one reason per feature, shared by
// every test project that has tests of it. The implementer of the feature removes the skips
// that name its reason, and then the reason.
internal static class NotImplemented
{
    // Epic 1, sprint 2, decision O15: a .NET enum of a bound property agrees with the enum
    // type of its field, or the class does not bind.
    public const string DotNetEnumAgreement = "Refusing a .NET enum that does not agree with the enum type of its field is not implemented yet.";
}
