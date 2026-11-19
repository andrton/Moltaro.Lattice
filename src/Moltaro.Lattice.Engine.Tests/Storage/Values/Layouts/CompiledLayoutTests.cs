using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// What a compiled layout says of itself (brief of S05, "Types and members" and "What Compile does
// not lay out yet"): the entity type it was compiled for, the layout version, which is 2 for every
// layout — 1 was the form of a root before a field that is part of an index stored a value longer than
// 64 bytes as a text entry (docs/decisions/index-structures.md, "Search-key ownership"), and two builds
// that store roots in other forms refuse each other by this number —, and the thresholds by which a String or Binary value is placed, those of the
// milestone contract ("Native layout and allocation"): inline up to 64 bytes, one block up to
// 4 KiB, then chunks of the largest payload a block holds.
public sealed class CompiledLayoutTests
{
    [Fact]
    public void A_layout_is_the_layout_of_the_entity_type_it_was_compiled_for()
    {
        var schema = EveryType.Compile();

        foreach (var entityType in schema.EntityTypes)
        {
            Assert.Same(entityType, RootLayout.Compile(entityType, schema).EntityType);
        }
    }

    [Fact]
    public void Every_layout_has_the_layout_version_2()
    {
        string[] fields =
        [
            "",
            "{ 'Id': 2, 'Name': 'Count', 'Type': 'Int32', 'Nullable': true }",
            "{ 'Id': 2, 'Name': 'Login', 'Type': 'String' }, { 'Id': 3, 'Name': 'Photo', 'Type': 'Binary' }",
        ];

        foreach (var declaration in fields)
        {
            foreach (var encoding in new[] { "Utf8", "Utf16" })
            {
                using var item = RootArena.Of(declaration, encoding);

                Assert.Equal(2u, item.Layout.LayoutVersion);
            }
        }
    }

    [Fact]
    public void The_thresholds_of_placement_are_those_of_the_specification()
    {
        Assert.Equal(64, RootLayout.InlineLimit);
        Assert.Equal(4 * 1024, RootLayout.SingleBlockLimit);

        // The largest payload of a block: the largest size class less the 32-byte header.
        Assert.Equal(65_504, RootLayout.ChunkPayload);
        Assert.Equal(Arena.MaxBlockSize - 32, RootLayout.ChunkPayload);
    }
}
