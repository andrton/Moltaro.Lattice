using System.Globalization;
using System.Text;

namespace Moltaro.Lattice.TestSupport.Text;

// The rule of equality without case (decision on strings, Q04, the addition of 15 November 2026; the
// owner's answer to Q46), read from the data that defines it, so that a test computes what is equal
// without case from the rule and never from the runtime it runs on: two texts are equal without case
// when they are equal scalar value by scalar value after each scalar value is replaced by its simple
// uppercase mapping of Unicode 16.0.0. That is the one-to-one mapping of field 12 of UnicodeData.txt:
// no full mapping, no folding of several characters, no culture and no normalization; a scalar value
// without a mapping stands for itself, and one of a supplementary plane is mapped as itself, never as
// its two UTF-16 code units.
//
// The data is UnicodeData.txt of Unicode 16.0.0, embedded in this assembly, with the licence of
// Unicode beside it (THIRD-PARTY-NOTICES.md at the root of the repository). It is read once in a test
// process, when a test first needs it: 1,477 scalar values have a mapping, and no mapping leads out of
// the plane of its scalar value.
internal static class CaseRule
{
    public const string UnicodeVersion = "16.0.0";

    private const string DataResource = "Moltaro.Lattice.TestSupport.Text.UnicodeData.txt";

    // The fields of a line of UnicodeData.txt: the code point, and its simple uppercase mapping.
    private const int CodePointField = 0;

    private const int SimpleUppercaseField = 12;

    private static readonly Lazy<Dictionary<int, int>> Mappings = new(Read);

    // Every scalar value that has a simple uppercase mapping, with its mapping.
    public static IReadOnlyDictionary<int, int> UppercaseMappings => Mappings.Value;

    // The simple uppercase mapping of the scalar value; the scalar value itself when it has none.
    public static int ToUpper(int scalarValue) =>
        Mappings.Value.TryGetValue(scalarValue, out var upper) ? upper : scalarValue;

    // The well-formed text with each of its scalar values replaced by its mapping: the spelling of the
    // text that every spelling equal to it without case has in common.
    public static string ToUpper(string text)
    {
        var upper = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            upper.Append(new Rune(ToUpper(rune.Value)));
        }

        return upper.ToString();
    }

    // Whether two well-formed texts are equal without case.
    public static bool Equal(string left, string right) =>
        string.Equals(ToUpper(left), ToUpper(right), StringComparison.Ordinal);

    private static Dictionary<int, int> Read()
    {
        using var data = typeof(CaseRule).Assembly.GetManifestResourceStream(DataResource)
            ?? throw new InvalidOperationException($"The resource {DataResource} is not embedded.");
        using var reader = new StreamReader(data, Encoding.UTF8);
        var mappings = new Dictionary<int, int>();

        while (reader.ReadLine() is { } line)
        {
            var fields = line.Split(';');

            if (fields[SimpleUppercaseField].Length > 0)
            {
                mappings.Add(Hex(fields[CodePointField]), Hex(fields[SimpleUppercaseField]));
            }
        }

        return mappings;
    }

    private static int Hex(string field) => int.Parse(field, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
}
