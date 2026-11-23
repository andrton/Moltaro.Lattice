using System.Globalization;

namespace Moltaro.Lattice.Tests.Indexes.Model;

// One entity as the model of a search holds it (IndexModel): its id and the values of the fields of the
// index in the order of the index, null for none, each in the .NET type a record holds for its field.
internal sealed record ModelEntry(long Id, IReadOnlyList<object?> Key)
{
    public override string ToString() => $"{Id}: ({string.Join(", ", Key.Select(Show))})";

    // A value with what tells values of one key apart: the sign of a zero and the payload of a NaN, the
    // offset of a DateTimeOffset, and the length of a long text.
    public static string Show(object? value) => value switch
    {
        null => "null",
        float number => $"{number.ToString("R", CultureInfo.InvariantCulture)} (0x{BitConverter.SingleToInt32Bits(number):x8})",
        double number => $"{number.ToString("R", CultureInfo.InvariantCulture)} (0x{BitConverter.DoubleToInt64Bits(number):x16})",
        DateTimeOffset moment => moment.ToString("O", CultureInfo.InvariantCulture),
        string text when text.Length > 12 => $"\"{text[..12]}…\" ({text.Length})",
        string text => $"\"{text}\"",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };
}
