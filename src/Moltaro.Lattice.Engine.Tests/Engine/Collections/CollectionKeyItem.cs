namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

internal sealed class CollectionKeyItem<TKey>
    where TKey : notnull
{
    public long Id { get; set; }

    public Dictionary<TKey, int> Map { get; set; } = [];

    public HashSet<TKey> Set { get; set; } = [];
}
