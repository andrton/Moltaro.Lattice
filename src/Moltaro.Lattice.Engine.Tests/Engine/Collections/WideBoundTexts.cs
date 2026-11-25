using System.Globalization;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class WideBoundTexts
{
    public string Part0 { get; set; } = "";
    public string Part1 { get; set; } = "";
    public string Part2 { get; set; } = "";
    public string Part3 { get; set; } = "";
    public string Part4 { get; set; } = "";
    public string Part5 { get; set; } = "";
    public string Part6 { get; set; } = "";
    public string Part7 { get; set; } = "";
    public List<int> Numbers { get; set; } = [];
    public byte[] Bytes { get; set; } = [];

    public IEnumerable<string> Values => [Part0, Part1, Part2, Part3, Part4, Part5, Part6, Part7];

    public static WideBoundTexts Create(int row, int leaf, int stamp) => new()
    {
        Part0 = ValueAt(0, row, leaf, stamp),
        Part1 = ValueAt(1, row, leaf, stamp),
        Part2 = ValueAt(2, row, leaf, stamp),
        Part3 = ValueAt(3, row, leaf, stamp),
        Part4 = ValueAt(4, row, leaf, stamp),
        Part5 = ValueAt(5, row, leaf, stamp),
        Part6 = ValueAt(6, row, leaf, stamp),
        Part7 = ValueAt(7, row, leaf, stamp),
        Numbers = [row, leaf, stamp, row + leaf + stamp],
        Bytes = [(byte)(row + 11), (byte)(leaf + 19), (byte)(stamp + 23)],
    };

    private static string ValueAt(int slot, int row, int leaf, int stamp) =>
        string.Create(CultureInfo.InvariantCulture, $"{row}-{leaf}-{stamp}-{slot}-é");
}
