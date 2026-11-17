using System.Globalization;

namespace Moltaro.Lattice.TestSupport.Positions;

// The sequence of a position, for tests that state an order of positions, which the contract does
// not compare: two positions are equal or not, and nothing else. It is read from the text of the
// position, whose form the documentation does not describe: its history, a hyphen and its sequence in
// hexadecimal, as LatticePosition.ToString writes it.
internal static class PositionSequence
{
    public static ulong Of(LatticePosition position)
    {
        var text = position.ToString();
        var separator = text.IndexOf('-', StringComparison.Ordinal);

        return ulong.Parse(text.AsSpan(separator + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
