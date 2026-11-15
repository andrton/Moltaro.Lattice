namespace Moltaro.Lattice.Tests;

// The reasons for which tests of Epic 1, sprint S07 are skipped, one for each thing they wait for.
// The implementation of the sprint removes every skip that names one of them, and the tests have to
// pass as they are written; then this type goes too.
internal static class NotImplemented
{
    // The engine stores an entity type with any key type of the catalog, with a String compared
    // without case, with an Enum field, and in a schema of the UTF-16 encoding: the runs of the tests
    // of this sprint on the engine.
    public const string EngineStorage = "The key types and declared values that the engine stores from Epic 1, sprint S07, are not implemented yet.";

    // The validation of the core checks that an integer is a value its enum type declares, on both
    // implementations.
    public const string EnumValidation = "The check of enum values by the validation of Epic 1, sprint S07, is not implemented yet.";
}
