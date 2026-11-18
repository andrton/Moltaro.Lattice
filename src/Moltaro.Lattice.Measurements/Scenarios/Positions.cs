using System.Globalization;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The history and the sequence of a position, which the contract keeps internal, from its text: the
// history, a hyphen and the sequence, both in hexadecimal (LatticePosition.ToString); and a position of
// a history and a sequence, from that text. For scenarios that wait for positions a database has not
// reached yet.
internal static class Positions
{
    public static ulong HistoryOf(LatticePosition position)
    {
        var text = position.ToString();

        return ulong.Parse(text.AsSpan(0, text.IndexOf('-', StringComparison.Ordinal)), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static ulong SequenceOf(LatticePosition position)
    {
        var text = position.ToString();

        return ulong.Parse(text.AsSpan(text.IndexOf('-', StringComparison.Ordinal) + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }

    public static LatticePosition Of(ulong history, ulong sequence) =>
        LatticePosition.Parse(string.Create(CultureInfo.InvariantCulture, $"{history:x16}-{sequence:x}"));
}
