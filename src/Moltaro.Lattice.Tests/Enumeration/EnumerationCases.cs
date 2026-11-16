using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// The data of the theories of the enumeration: every form of an enumeration (EnumerationForms), on
// both implementations or on the engine alone.
//
// Until Epic 1, sprint S08, part 2 the engine fails an enumeration during which an entity was inserted
// or deleted, so the rows of the engine of a test that inserts or deletes during a pass are skipped
// for the one reason of that part (NotImplemented); the placeholder takes what a pass returns at its
// first step and runs them today. The placeholder validates the read limits of its options and
// otherwise does not use them until S12, so the tests of the limits run on the engine alone.
internal static class EnumerationCases
{
    // Every form, on both implementations.
    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> FormsOnBoth =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from form in EnumerationForms.All
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, form);

    // Every form, on both implementations, the rows of the engine skipped until it enumerates under
    // change.
    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> FormsOnBothUntilTheEngineEnumeratesUnderChange =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from form in EnumerationForms.All
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, form)
        {
            Skip = implementation == DatabaseImplementationEnum.Engine ? NotImplemented.EnumerationUnderChange : null,
        };

    // Every form, on the engine.
    public static IEnumerable<TheoryDataRow<string>> FormsOnTheEngine =>
        EnumerationForms.All.Select(form => new TheoryDataRow<string>(form));
}
