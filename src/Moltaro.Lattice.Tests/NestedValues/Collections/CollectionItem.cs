namespace Moltaro.Lattice.Tests.NestedValues.Collections;

internal sealed class CollectionItem
{
    public long Id { get; set; }

    public Dictionary<string, CollectionDetails?>? Options { get; set; }

    public HashSet<string?>? Rights { get; set; }

    public Dictionary<byte, ushort> Numbers { get; set; } = [];

    public HashSet<byte> Bytes { get; set; } = [];

    public HashSet<long> Scores { get; set; } = [];
}
