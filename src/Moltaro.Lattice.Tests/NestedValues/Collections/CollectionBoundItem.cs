namespace Moltaro.Lattice.Tests.NestedValues.Collections;

internal sealed class CollectionBoundItem
{
    public long Id { get; set; }

    public Dictionary<string, byte[]> Map { get; set; } = [];

    public HashSet<string> Set { get; set; } = [];
}
