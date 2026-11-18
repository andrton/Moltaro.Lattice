namespace Moltaro.Lattice.Engine.Tests;

// The one reason for which the tests of the findings of review round 1 of Epic 1, sprint S11 that fail on
// the engine before their fix are skipped. The implementer of the fixes removes the skips that name it,
// and then this constant.
internal static class ReviewFindings
{
    public const string NotFixed = "A finding of review round 1 of Epic 1, sprint S11 that is not fixed yet.";
}
