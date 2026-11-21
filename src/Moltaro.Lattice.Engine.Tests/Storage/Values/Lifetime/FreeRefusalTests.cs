using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// What RootStorage.Free refuses, with ArgumentException and before it frees anything (the comment of
// RootStorage): it recognizes a root as RootReader does, so a block whose kind is not Root and a
// root of another layout version are refused (brief of S05, "A reader"). Free reaches the blocks of
// a root through the descriptors in its root block, so a block that is taken for a root by mistake
// would have them freed. The block of the first test therefore holds a copy of a real root block:
// its descriptors name that root's value block, which must survive.
public sealed class FreeRefusalTests
{
    private const string Fields = "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }";

    [Fact]
    public void A_block_that_is_not_a_root_is_refused_and_nothing_is_freed()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item);
        var payload = item.Arena.Payload(root);
        Assert.True(ArenaBlocks.TryAllocateLive(item.Arena, payload.Length, BlockKindEnum.Value, RootArena.Handle, out var copy));
        payload.CopyTo(item.Arena.Payload(copy));
        item.Arena.Header(copy).LayoutVersion = item.Layout.LayoutVersion;
        var before = item.Arena.Counters;

        Assert.Throws<ArgumentException>(() => RootStorage.Free(item.Arena, item.Layout, copy));

        Assert.Equal(before, item.Arena.Counters);
        Assert.True(ArenaBlocks.IsValid(item.Arena, copy));
        Assert.Equal(new string('t', 1_000), item.Reader(root).GetString(item.Field("Text")));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("an older one")]
    [InlineData("a newer one")]
    public void A_root_of_another_layout_version_is_refused_and_nothing_is_freed(string version)
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item);
        item.Arena.Header(root).LayoutVersion = OtherLayoutVersion.Of(item.Layout, version);
        var before = item.Arena.Counters;

        Assert.Throws<ArgumentException>(() => RootStorage.Free(item.Arena, item.Layout, root));

        Assert.Equal(before, item.Arena.Counters);
        Assert.True(ArenaBlocks.IsValid(item.Arena, root));
    }

    // A root whose text has a value block of its own.
    private static ArenaAddress Write(RootArena item)
    {
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Text"), new string('t', 1_000));

        return writer.Complete();
    }
}
