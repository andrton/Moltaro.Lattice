namespace Moltaro.Lattice.TestSupport.Skips;

// The one reason with which the tests of a sprint that cannot pass before its implementation are
// skipped (AGENTS.md, "Tests And Samples"). The implementer of a sprint removes its skips and its
// constant.
internal static class SkipReason
{
    // Epic 1, sprint S15, part 2: the binding of a value of an index key and of a bound beyond the type of
    // its field and an integer of another width.
    public const string BeforeSprint15Part2 = "Epic 1, sprint S15, part 2: the binding of a number of another type, of an Enum value its enum type does not allow and of a null with a type is not implemented yet.";
}
