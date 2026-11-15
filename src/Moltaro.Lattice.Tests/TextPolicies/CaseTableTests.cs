using System.Reflection;
using Moltaro.Lattice.TestSupport.Text;

namespace Moltaro.Lattice.Tests.TextPolicies;

// The case table by which text compared without case is compared (decision on strings, Q04, the
// addition of 15 November 2026; the owner's answer to Q46) ships with the core, is the same in every
// process, and is the simple uppercase mapping of Unicode 16.0.0: for every scalar value it gives the
// one-to-one mapping of field 12 of UnicodeData.txt of that version, and the scalar value itself where
// that field is empty. Its version is 1. The test walks every scalar value, 0 to 10FFFF without the
// surrogates, against the data file kept in Moltaro.Lattice.TestSupport (CaseRule).
//
// The table has no public surface: it is the internal static class Moltaro.Lattice.Schema.CaseTable of
// the core, with int Version and static int ToUpper(int scalarValue), which the implementation of the
// answer to Q46 adds, and this project is a friend of the core. Until the type exists it is reached by
// its name, the one way the test compiles before the type does.
public sealed class CaseTableTests
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    // How many scalar values a failure lists.
    private const int Listed = 20;

    [Fact(Skip = NotImplemented.CaseTable)]
    public void Case_table_has_version_1()
    {
        var table = CaseTable();
        var version = table.GetField("Version", Static)?.GetValue(null) ?? table.GetProperty("Version", Static)?.GetValue(null);

        Assert.Equal(1, Assert.IsType<int>(version));
    }

    [Fact(Skip = NotImplemented.CaseTable)]
    public void Case_table_maps_every_scalar_value_to_its_simple_uppercase_mapping_of_Unicode_16_0_0()
    {
        var toUpper = CaseTable().GetMethod("ToUpper", Static, [typeof(int)])!.CreateDelegate<Func<int, int>>();
        var wrong = 0;
        var listed = new List<string>();

        for (var scalarValue = 0; scalarValue <= 0x10FFFF; scalarValue++)
        {
            if (scalarValue is >= 0xD800 and <= 0xDFFF)
            {
                continue;
            }

            var expected = CaseRule.ToUpper(scalarValue);
            var actual = toUpper(scalarValue);

            if (actual != expected)
            {
                wrong++;

                if (listed.Count < Listed)
                {
                    listed.Add($"U+{scalarValue:X4} to U+{actual:X4} instead of U+{expected:X4}");
                }
            }
        }

        Assert.True(wrong == 0, $"{wrong} scalar values are mapped otherwise than Unicode {CaseRule.UnicodeVersion} maps them, among them: {string.Join(", ", listed)}.");
    }

    private static Type CaseTable() =>
        typeof(LatticeSchema).Assembly.GetType("Moltaro.Lattice.Schema.CaseTable", throwOnError: true)!;
}
