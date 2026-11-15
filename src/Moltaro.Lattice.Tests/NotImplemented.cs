namespace Moltaro.Lattice.Tests;

// The reasons for which tests of this project are skipped, one for each thing that is not
// implemented yet, so that the implementer removes one reason and finds every test that waits for it.
internal static class NotImplemented
{
    public const string BoundOfOneValueInTheCore =
        "The validation of the core does not check the bound of one value yet: today only the engine checks it, after the conditions of the write.";
}
