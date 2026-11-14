namespace Moltaro.Lattice.Tests;

// The catalog version of a schema is a whole number from 1: the descriptor declares it as
// SchemaVersion, or it is 1 when the descriptor does not.
public sealed class CatalogVersionTests
{
    private const string Json = """
        {
          "SchemaVersion": 4,
          "EntityTypes": [ { "Id": 1, "Name": "Note", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ]
        }
        """;

    [Fact]
    public void Catalog_version_is_the_declared_one_or_1()
    {
        Assert.Equal(4, LatticeSchema.Compile(Json).Version);
        Assert.Equal(1, LatticeSchema.Compile(Descriptor.With(Json, "'SchemaVersion': 4,", "'SchemaVersion': 1,")).Version);
        Assert.Equal(int.MaxValue, LatticeSchema.Compile(Descriptor.With(Json, "'SchemaVersion': 4,", "'SchemaVersion': 2147483647,")).Version);

        // A descriptor without the property has version 1.
        Assert.Equal(1, LatticeSchema.Compile(Descriptor.With(Json, "'SchemaVersion': 4,", "")).Version);
    }

    [Theory(Skip = NotImplemented.SchemaValues)]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-2147483648")]
    public void Catalog_version_below_1_is_refused_with_the_property_named(string version)
    {
        var exception = Descriptor.ExpectRefused(Json, "'SchemaVersion': 4,", $"'SchemaVersion': {version},");

        Assert.Contains("SchemaVersion", exception.Message);
    }
}
