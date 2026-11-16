namespace Moltaro.Lattice.TestSupport.Enumeration;

// The one reason for which the tests of Epic 1, sprint S08, part 2 are skipped until the engine has
// what they state: an enumeration that goes on, correct, while entities change and the primary index
// grows, and the read limits of the options of a database (LatticeDatabaseOptions.ReadMemoryLimit and
// ReadTimeLimit), which an enumeration is subject to. The tests of both test projects take it from
// here. A test that states what already holds is not skipped. The implementer of that part removes
// the skips and does not change what the tests assert.
internal static class NotImplemented
{
    public const string EnumerationUnderChange = "An enumeration under change and the read limits are not implemented yet in the engine.";
}
