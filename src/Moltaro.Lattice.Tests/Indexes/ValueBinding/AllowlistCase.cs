using System.Globalization;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// One row of the allowlist (AllowlistTests): a field of Item, a value given for it, and the value of the
// field type it is bound to, or null when it is refused; with the values stored beside it that a wrong
// binding would find instead.
internal sealed record AllowlistCase(string Field, object Given, object? Bound, object[] AlsoStored)
{
    public string Name => $"{Field} from {Given.GetType().Name} {Show(Given)}{(Bound is null ? ", refused" : $", bound to {Show(Bound)}")}";

    public static AllowlistCase Binds(string field, object given, object bound, params object[] alsoStored) => new(field, given, bound, alsoStored);

    public static AllowlistCase Refused(string field, object given) => new(field, given, null, []);

    private static string Show(object value) => value switch
    {
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
