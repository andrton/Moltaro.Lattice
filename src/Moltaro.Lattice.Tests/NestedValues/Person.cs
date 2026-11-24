namespace Moltaro.Lattice.Tests.NestedValues;

// The class of the entity type Person of NestedSchema, whose key Code is a String: every kind of value the
// schema stores inside an entity, an owned value present or absent, a List in each of its two forms and a
// reference.
internal sealed class Person
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public Address? Home { get; set; }

    public Address Work { get; set; } = new();

    public List<string> Tags { get; set; } = [];

    public List<long> Scores { get; set; } = [];

    public List<int?> Marks { get; set; } = [];

    public List<string?> Notes { get; set; } = [];

    public List<Address> Places { get; set; } = [];

    public List<Point> Pins { get; set; } = [];

    public List<List<int>> Grid { get; set; } = [];

    public string? Manager { get; set; }

    public byte[]? Photo { get; set; }

    public List<byte[]> Files { get; set; } = [];
}
