using System.Globalization;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The options of a scenario on the command line: pairs of --name value. A list is comma-separated;
// none is the empty list.
internal sealed class Options
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private Options()
    {
    }

    public static Options Parse(ReadOnlySpan<string> arguments)
    {
        var options = new Options();

        for (var index = 0; index < arguments.Length; index += 2)
        {
            if (!arguments[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= arguments.Length)
            {
                throw new ArgumentException($"Options come as --name value pairs; '{arguments[index]}' has no value.");
            }

            options._values[arguments[index][2..]] = arguments[index + 1];
        }

        return options;
    }

    public int Number(string name, int fallback) =>
        _values.TryGetValue(name, out var value) ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;

    public string Word(string name, string fallback) => _values.GetValueOrDefault(name, fallback);

    public string[] Words(string name, string fallback) =>
        Word(name, fallback) == "none" ? [] : Word(name, fallback).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public int[] Numbers(string name, string fallback) => [.. Words(name, fallback).Select(word => int.Parse(word, CultureInfo.InvariantCulture))];
}
