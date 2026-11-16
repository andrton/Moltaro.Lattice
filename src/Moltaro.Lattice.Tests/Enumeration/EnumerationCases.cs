using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// The data of the theories of the enumeration: every form of an enumeration (EnumerationForms), on
// both implementations or on the engine alone. The placeholder validates the read limits of its
// options and otherwise does not use them until S12, so the tests of the limits run on the engine
// alone.
internal static class EnumerationCases
{
    // Every form, on both implementations.
    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> FormsOnBoth =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from form in EnumerationForms.All
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, form);

    // Every form, on the engine.
    public static IEnumerable<TheoryDataRow<string>> FormsOnTheEngine =>
        EnumerationForms.All.Select(form => new TheoryDataRow<string>(form));
}
