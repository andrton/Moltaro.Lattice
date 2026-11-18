using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Tests.Enumeration;

// The data of the theories of the enumeration: every form of an enumeration (EnumerationForms).
internal static class EnumerationCases
{
    public static IEnumerable<TheoryDataRow<string>> Forms =>
        EnumerationForms.All.Select(form => new TheoryDataRow<string>(form));
}
