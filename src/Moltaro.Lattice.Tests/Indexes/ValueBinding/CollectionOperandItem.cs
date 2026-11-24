namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

internal sealed class CollectionOperandItem<T>
    where T : notnull
{
    public long Id { get; set; }

    public Dictionary<T, bool> Keys { get; set; } = [];

    public Dictionary<int, T> Values { get; set; } = [];

    public HashSet<T> Members { get; set; } = [];

    public List<T> Elements { get; set; } = [];
}
