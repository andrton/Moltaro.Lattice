namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// One entity of a binding test: its id and the value of the one field it holds, in the .NET type a record
// holds for that field; null for none.
internal sealed record Stored(long Id, object? Value)
{
    public override string ToString() => $"{Id}: {Show(Value)}";

    // The value with what tells values of one key apart: the sign of a zero and the payload of a NaN.
    private static string Show(object? value) => value switch
    {
        null => "null",
        float number => $"{number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} (0x{BitConverter.SingleToInt32Bits(number):x8})",
        double number => $"{number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)} (0x{BitConverter.DoubleToInt64Bits(number):x16})",
        string text => $"\"{text}\"",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
    };
}
