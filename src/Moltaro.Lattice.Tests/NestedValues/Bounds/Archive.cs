namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// The class of the entity type Archive of ListBoundAtDepthTests: a List of Lists of String, an owned Shelf and
// a List of owned Shelves.
internal sealed class Archive
{
    public string Code { get; set; } = "";

    public List<List<string>> Rows { get; set; } = [];

    public Shelf Shelf { get; set; } = new();

    public List<Shelf> Shelves { get; set; } = [];
}
