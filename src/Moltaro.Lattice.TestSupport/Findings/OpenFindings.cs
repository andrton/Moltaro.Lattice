namespace Moltaro.Lattice.TestSupport.Findings;

// The reasons of tests that state a defect a review found and that the implementation does not meet
// yet. Such a test is written first and skipped with the reason of its review, in every test project
// alike; the implementer removes the Skip with the fix, and the test has to pass unchanged.
internal static class OpenFindings
{
    // Review round 2 of Epic 1, sprint S09, part 2.
    public const string PartTwoReviewRound2 = "A defect found in review round 2 of Epic 1, sprint S09, part 2, that the engine does not meet yet.";
}
