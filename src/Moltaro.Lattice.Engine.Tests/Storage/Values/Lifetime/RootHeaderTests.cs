using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Lifetime;

// The header of the root block that Complete returns (brief of S05, "Types and members" and "What
// Compile does not lay out yet"; milestone contract, "Native layout and allocation"): its kind is
// Root, it carries the handle the writer was given, it has no parent and the descriptor offset 0,
// and it carries the layout version of the layout. The header is read through the arena, the only
// block of a root a test can reach.
public sealed class RootHeaderTests
{
    [Fact]
    public void The_root_block_is_of_kind_Root_with_the_handle_of_the_writer_no_parent_and_the_layout_version()
    {
        using var item = RootArena.Of("{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }");
        RootHandle[] handles = [new(1, 0), new(7, 4_096), new(uint.MaxValue, 12)];

        foreach (var handle in handles)
        {
            var writer = new RootWriter(item.Arena, item.Layout, handle);
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Text"), new string('t', 100_000));
            var root = writer.Complete();

            var header = item.Arena.Header(root);

            Assert.Equal(BlockKindEnum.Root, header.Kind);
            Assert.Equal(handle, header.Root);
            Assert.True(header.Parent.IsNull);
            Assert.Equal(0u, header.DescriptorOffset);
            Assert.Equal(item.Layout.LayoutVersion, header.LayoutVersion);
            Assert.Equal(1u, header.LayoutVersion);
        }
    }
}
