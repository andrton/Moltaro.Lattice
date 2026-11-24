namespace Moltaro.Lattice.Tests.NestedValues.Collections;

internal sealed class CollectionDetails
{
    public string[] Lines { get; set; } = [];

    public byte[] Data { get; set; } = [];

    public List<Dictionary<byte, ushort>> Layers { get; set; } = [];

    public HashSet<byte> Flags { get; set; } = [];
}
