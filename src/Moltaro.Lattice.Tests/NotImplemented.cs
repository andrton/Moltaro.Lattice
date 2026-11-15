namespace Moltaro.Lattice.Tests;

// The reasons for which tests of this project are skipped, one for each rule they wait for. The
// implementation removes every skip that names one of them, and the tests have to pass as they are
// written; then this type goes too.
internal static class NotImplemented
{
    // The planner's answer to Q41 after the first review of Epic 1, sprint S06: the validation of
    // the core refuses text that is not well-formed, ValidationFailed with the field named, before
    // the conditions of a write and before a refusal as not implemented, on both implementations;
    // a String key given as an argument that is not well-formed is ArgumentException.
    public const string TextValidation = "The validation of text that is not well-formed by the core (Q41 of Epic 1) is not implemented yet.";

    // The planner's word on the first review of S06: the engine runs no code of the application
    // inside a gate or the close barrier.
    public const string ApplicationCodeOutsideGates = "The engine running no code of the application inside a gate or the close barrier (first review of S06) is not implemented yet.";
}
