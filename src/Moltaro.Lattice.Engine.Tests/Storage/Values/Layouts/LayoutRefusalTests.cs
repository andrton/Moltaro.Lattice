using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// A fixed part that does not fit one block, Arena.MaxBlockSize bytes with its header, is refused
// during layout compilation with InvalidSchema and the entity type named, before any data is admitted
// (docs/design/arena-protocol.md, "Stored structures and root transfer"). An entity type of a
// different schema is refused with ArgumentException.
public sealed class LayoutRefusalTests
{
    [Fact]
    public void The_largest_fixed_part_that_fits_one_block_compiles()
    {
        // 32 + 8 + 16 × 4,093 + 8 = 65,536 bytes.
        var schema = WideSchema.Compile("Widest", decimals: 4_093, bytes: 8);
        Assert.True(schema.TryGetEntityType("Widest", out var widest));

        var layout = RootLayout.Compile(widest, schema);

        Assert.Equal(Arena.MaxBlockSize, layout.FixedSize);
    }

    [Theory]
    [InlineData(4_093, 9)]
    [InlineData(5_000, 0)]
    public void A_fixed_part_above_one_block_is_InvalidSchema_with_the_entity_type_named(int decimals, int bytes)
    {
        var schema = WideSchema.Compile("Oversized", decimals, bytes);
        Assert.True(schema.TryGetEntityType("Oversized", out var oversized));

        var exception = Assert.Throws<LatticeException>(() => RootLayout.Compile(oversized, schema));

        Assert.Equal(LatticeErrorEnum.InvalidSchema, exception.Error);
        Assert.Contains("Oversized", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_type_of_another_schema_is_refused()
    {
        var schema = ItemSchema.Compile("{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32' }");
        var other = LatticeSchema.Compile("""
            { "EntityTypes": [ { "Id": 1, "Name": "Other", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }
            """);
        Assert.True(other.TryGetEntityType("Other", out var foreign));

        Assert.Throws<ArgumentException>(() => RootLayout.Compile(foreign, schema));
    }

    [Fact]
    public void An_entity_type_belongs_to_the_schema_that_compiled_it_and_not_to_a_copy()
    {
        // The same descriptor compiled twice gives two schemas, each with its own entity types.
        var json = ItemSchema.Json("{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32' }");
        var schema = LatticeSchema.Compile(json);
        var copy = LatticeSchema.Compile(json);
        Assert.True(copy.TryGetEntityType(ItemSchema.EntityTypeName, out var itemOfCopy));

        Assert.Throws<ArgumentException>(() => RootLayout.Compile(itemOfCopy, schema));
    }
}
