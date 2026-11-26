namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class WideBoundGuids
{
    public Guid Part0 { get; set; }
    public Guid Part1 { get; set; }
    public Guid Part2 { get; set; }
    public Guid Part3 { get; set; }
    public Guid Part4 { get; set; }
    public Guid Part5 { get; set; }
    public Guid Part6 { get; set; }
    public Guid Part7 { get; set; }

    public IEnumerable<Guid> Values => [Part0, Part1, Part2, Part3, Part4, Part5, Part6, Part7];

    public static WideBoundGuids Create(int row, int leaf, int stamp) => new()
    {
        Part0 = ValueAt(0, row, leaf, stamp),
        Part1 = ValueAt(1, row, leaf, stamp),
        Part2 = ValueAt(2, row, leaf, stamp),
        Part3 = ValueAt(3, row, leaf, stamp),
        Part4 = ValueAt(4, row, leaf, stamp),
        Part5 = ValueAt(5, row, leaf, stamp),
        Part6 = ValueAt(6, row, leaf, stamp),
        Part7 = ValueAt(7, row, leaf, stamp),
    };

    private static Guid ValueAt(int slot, int row, int leaf, int stamp) =>
        new(row, (short)leaf, (short)stamp, (byte)slot, 1, 2, 3, 4, 5, 6, 7);
}
