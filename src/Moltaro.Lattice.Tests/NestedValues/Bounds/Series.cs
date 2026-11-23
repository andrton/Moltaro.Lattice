namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// The class of the entity type Series of ListBoundTests.
internal sealed class Series
{
    public string Code { get; set; } = "";

    public List<long> Values { get; set; } = [];

    public List<string> Labels { get; set; } = [];
}
