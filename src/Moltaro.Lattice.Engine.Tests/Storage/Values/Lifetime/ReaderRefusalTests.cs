using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// What the constructor of RootReader refuses, with ArgumentException (brief of S05, "A reader"): an
// address that is not a valid block of the arena, a block whose kind is not Root, and a block
// whose layout version is not the layout's. The arena decides which addresses are valid blocks
// without reading through them (S04).
public sealed class ReaderRefusalTests
{
    private const string Fields = "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }";

    [Fact(Skip = NotImplemented.RootLayout)]
    public void An_address_that_is_not_a_valid_block_is_refused()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item);
        ArenaAddress[] addresses =
        [
            default,
            new ArenaAddress(root.Segment, root.Offset + 1),
            new ArenaAddress(root.Segment + 100, root.Offset),
            new ArenaAddress(uint.MaxValue, uint.MaxValue),
        ];

        foreach (var address in addresses)
        {
            Assert.Throws<ArgumentException>(() => item.Reader(address));
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_freed_root_is_refused()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item);

        RootStorage.Free(item.Arena, item.Layout, root);

        Assert.Throws<ArgumentException>(() => item.Reader(root));
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_block_that_is_not_a_root_is_refused()
    {
        using var item = RootArena.Of(Fields);

        foreach (var kind in new[] { BlockKindEnum.Value, BlockKindEnum.Chunk, BlockKindEnum.ChunkDirectory, BlockKindEnum.SharedText })
        {
            // A block as large as a root of the layout, with the layout version of the layout, so
            // that only its kind is wrong.
            Assert.True(item.Arena.TryAllocate(item.Layout.FixedSize - 32, kind, RootArena.Handle, out var block));
            item.Arena.Header(block).LayoutVersion = item.Layout.LayoutVersion;

            Assert.Throws<ArgumentException>(() => item.Reader(block));
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void A_root_of_another_layout_version_is_refused(uint layoutVersion)
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item);

        item.Arena.Header(root).LayoutVersion = layoutVersion;

        Assert.Throws<ArgumentException>(() => item.Reader(root));
    }

    private static ArenaAddress Write(RootArena item)
    {
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Text"), "text");

        return writer.Complete();
    }
}
