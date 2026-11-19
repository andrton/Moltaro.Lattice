using System.Globalization;
using System.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// One line of output per case: the scenario, then name=value pairs, its parameters first and its
// results after them, in the invariant culture, so that the lines can be compared and parsed.
internal static class Line
{
    public static void Write(string scenario, params (string Name, object Value)[] fields)
    {
        var line = new StringBuilder(scenario);

        foreach (var (name, value) in fields)
        {
            line.Append(' ').Append(name).Append('=').Append(Format(value));
        }

        Console.WriteLine(line.ToString());
    }

    public static string PerEntry(long bytes, long entries) => entries == 0 ? "-" : ((double)bytes / entries).ToString("F1", CultureInfo.InvariantCulture);

    private static string Format(object value) => value switch
    {
        double number => number.ToString("F1", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };
}
