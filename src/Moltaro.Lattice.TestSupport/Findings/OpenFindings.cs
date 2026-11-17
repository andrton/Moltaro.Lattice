namespace Moltaro.Lattice.TestSupport.Findings;

// The reasons of tests that state a defect a review found and that the implementation does not meet
// yet. Such a test is written first and skipped with the reason of its review, in every test project
// alike; the implementer removes the Skip with the fix, and the test has to pass unchanged.
internal static class OpenFindings
{
    // The outside review of main at 6564720, carried by Epic 1, sprint S09, part 2.
    public const string OutsideReview = "A defect found by the outside review of main after Epic 1, sprint S09, that the engine does not meet yet.";
}
