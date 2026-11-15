namespace Moltaro.Lattice.Tests;

// The reasons for which tests of Epic 1, sprint S06 are skipped, one for each thing they wait for.
// The implementation of the sprint removes every skip that names one of them, and the tests have to
// pass as they are written; then this type goes too.
internal static class NotImplemented
{
    // The engine database: the runs of the scenario tests on the engine (TestDatabases), and the
    // tests that run on the engine only.
    public const string EngineDatabase = "The engine database of Epic 1, sprint S06, is not implemented yet.";

    // The two fixes of the binding of properties by the owner's answer to Q29: a class that
    // overrides one accessor of a virtual property binds with the accessor of its base class, and a
    // public field that hides a property of a base class is refused.
    public const string BindingFixes = "The two fixes of the binding of properties of Epic 1, sprint S06, are not implemented yet.";
}
