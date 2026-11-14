namespace Moltaro.Lattice.Tests;

// Why a test written before its implementation is skipped: one reason for one addition to a
// sprint. The implementer removes the skips that name the reason, and then the reason.
internal static class NotImplemented
{
    // Epic 1, sprint 3, by the owner's answer to Q28: a declaration is at most 32 deep, counted
    // through collections and owned types together.
    public const string DeclarationDepth = "The depth of a declaration, at most 32 through collections and owned types, is not implemented yet.";
}
