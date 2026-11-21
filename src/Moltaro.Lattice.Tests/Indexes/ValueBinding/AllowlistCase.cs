using System.Globalization;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// One row of the allowlist (AllowlistTests): a field of Item, a value given for it, and the value of the
// field type it is bound to, or null when it is refused; with the values stored beside it that a wrong
// binding would find instead.
internal sealed record AllowlistCase(string Field, object Given, object? Bound, object[] AlsoStored)
{
    private static readonly string[] IntegerFields = ["Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64"];

    public string Name => $"{Field} from {Given.GetType().Name} {Show(Given)}{(Bound is null ? ", refused" : $", bound to {Show(Bound)}")}";

    // Whether the binding exists before the rules of S15, part 2: the type of the field, or an integer of
    // another width that fits it, which is also the only kind of value refused before them.
    public bool BindsBeforeTheRules => Bound is null || (IntegerFields.Contains(Field) && Given is sbyte or byte or short or ushort or int or uint or long or ulong);

    public static AllowlistCase Binds(string field, object given, object bound, params object[] alsoStored) => new(field, given, bound, alsoStored);

    public static AllowlistCase Refused(string field, object given) => new(field, given, null, []);

    private static string Show(object value) => value switch
    {
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
