namespace Moltaro.Lattice.Tests;

// Variants of a JSON descriptor for the schema tests: the same text with one declaration
// written differently.
internal static class Descriptor
{
    // Replaces a part that occurs exactly once in the descriptor, so that a variant changes
    // the one declaration it names and nothing else. The part and its replacement are written
    // with ' in place of ", so that they fit on one line of a test attribute:
    // 'Comparison': 'Ordinal' stands for "Comparison": "Ordinal".
    public static string With(string json, string part, string replacement)
    {
        var jsonPart = part.Replace('\'', '"');
        var jsonReplacement = replacement.Replace('\'', '"');

        var first = json.IndexOf(jsonPart, StringComparison.Ordinal);
        var isUnique = first >= 0 && json.IndexOf(jsonPart, first + 1, StringComparison.Ordinal) < 0;
        Assert.True(isUnique, $"The descriptor must contain this part exactly once: {jsonPart}");

        return json.Replace(jsonPart, jsonReplacement, StringComparison.Ordinal);
    }

    // The descriptor compiles, and the variant with one part written differently does not:
    // the change is what is refused.
    public static LatticeException ExpectRefused(string json, string part, string replacement)
    {
        LatticeSchema.Compile(json);
        var variant = With(json, part, replacement);

        var exception = Assert.Throws<LatticeException>(() => LatticeSchema.Compile(variant));
        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        return exception;
    }
}
