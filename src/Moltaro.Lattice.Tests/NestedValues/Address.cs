namespace Moltaro.Lattice.Tests.NestedValues;

// The class of the owned type Address of NestedSchema: a String, an Int32, a List of String and a nullable
// owned Point.
internal sealed class Address
{
    public string City { get; set; } = "";

    public int Zip { get; set; }

    public List<string> Lines { get; set; } = [];

    public Point? Geo { get; set; }
}
