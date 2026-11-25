using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// Every block of a root says whose it is and where its descriptor lies (brief of S05, "Where a value
// goes" and the comment of RootWriter; the planner's answer to Q30 on DescriptorOffset): it carries
// the handle of the root and the layout version, the parent of a value block and of a chunk
// directory is the root block, the parent of a chunk is its directory, and DescriptorOffset is the
// offset of the descriptor that names the block, from the start of the parent block, its header
// included. That is what lets a later sprint move a block and repair the one descriptor that refers
// to it. A descriptor names its value by the kinds of DescriptorKindEnum, and a null value has a
// descriptor of zero bytes.
//
// The tests find the descriptors by the formula of the fixed part (brief of S05, "The fixed part"):
// the entity type has the key Id, an Int64, and three nullable fields, so the bitmap takes one byte
// and the descriptors of Text, Data and Note, in the order of their identities, lie at 41, 57 and
// 73 from the start of the root block.
public sealed class OwnershipTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Text', 'Type': 'String', 'Nullable': true },
        { 'Id': 3, 'Name': 'Data', 'Type': 'Binary', 'Nullable': true },
        { 'Id': 4, 'Name': 'Note', 'Type': 'String', 'Nullable': true }
        """;

    private const int TextDescriptor = 32 + 1 + 8;

    private const int DataDescriptor = TextDescriptor + 16;

    private const int NoteDescriptor = DataDescriptor + 16;

    private static readonly RootHandle Handle = new(9, 77);

    [Fact]
    public void A_chunked_value_has_a_directory_owned_by_the_root_and_chunks_owned_by_the_directory()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item, text: new string('t', 3 * ValueBlocks.ChunkPayload + 5), data: null, note: null);

        var descriptor = DescriptorAt(item.Arena, root, TextDescriptor);
        var directory = AddressOf(descriptor.Location);

        Assert.Equal(DescriptorKindEnum.Chunked, descriptor.Kind);
        Assert.Equal(3u * ValueBlocks.ChunkPayload + 5, descriptor.Length);
        AssertOwned(item.Arena.Header(directory), BlockKindEnum.ChunkDirectory, parent: root, TextDescriptor);
        Assert.Equal(4 * 16u, item.Arena.Header(directory).PayloadLength);

        var entries = item.Arena.Payload(directory);

        for (var chunk = 0; chunk < 4; chunk++)
        {
            var entry = ResidentDescriptor.Read(entries[(16 * chunk)..]);
            var header = item.Arena.Header(AddressOf(entry.Location));

            AssertOwned(header, BlockKindEnum.Chunk, parent: directory, 32 + 16 * chunk);
            Assert.Equal(chunk < 3 ? (uint)ValueBlocks.ChunkPayload : 5u, entry.Length);
            Assert.Equal(entry.Length, header.PayloadLength);
        }
    }

    [Fact]
    public void A_value_in_a_block_of_its_own_is_owned_by_the_root_and_an_inline_value_lies_after_the_fixed_part()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item, text: "inline", data: new byte[1_000], note: null);

        var inline = DescriptorAt(item.Arena, root, TextDescriptor);

        Assert.Equal(DescriptorKindEnum.Inline, inline.Kind);
        Assert.Equal(6u, inline.Length);
        Assert.Equal((ulong)item.Layout.FixedSize, inline.Location);

        var block = DescriptorAt(item.Arena, root, DataDescriptor);
        var header = item.Arena.Header(AddressOf(block.Location));

        Assert.Equal(DescriptorKindEnum.Block, block.Kind);
        Assert.Equal(1_000u, block.Length);
        AssertOwned(header, BlockKindEnum.Value, parent: root, DataDescriptor);
        Assert.Equal(1_000u, header.PayloadLength);
    }

    [Fact]
    public void The_descriptor_of_a_null_value_is_all_zero()
    {
        using var item = RootArena.Of(Fields);
        var root = Write(item, text: null, data: null, note: null);
        var payload = item.Arena.Payload(root);

        foreach (var offset in new[] { TextDescriptor, DataDescriptor, NoteDescriptor })
        {
            Assert.True(payload.Slice(offset - 32, 16).IndexOfAnyExcept((byte)0) < 0, $"The descriptor at {offset} is not zero.");
        }
    }

    private static ArenaAddress Write(RootArena item, string? text, byte[]? data, string? note)
    {
        var writer = new RootWriter(item.Arena, item.Layout, Handle);
        writer.SetInteger(item.Field("Id"), 1);

        if (text is not null)
        {
            writer.SetString(item.Field("Text"), text);
        }

        if (data is not null)
        {
            writer.SetBinary(item.Field("Data"), data);
        }

        if (note is not null)
        {
            writer.SetString(item.Field("Note"), note);
        }

        return writer.Complete();
    }

    // The descriptor at an offset counted from the start of the root block, its header included.
    private static ValueDescriptor DescriptorAt(Arena arena, ArenaAddress root, int offset) =>
        ResidentDescriptor.Read(arena.Payload(root)[(offset - 32)..]);

    private static ArenaAddress AddressOf(ulong location) => new((uint)(location >> 32), (uint)location);

    private static void AssertOwned(BlockHeader header, BlockKindEnum kind, ArenaAddress parent, int descriptorOffset)
    {
        Assert.Equal(kind, header.Kind);
        Assert.Equal(Handle, header.Root);
        Assert.Equal(parent, header.Parent);
        Assert.Equal((uint)descriptorOffset, header.DescriptorOffset);
        Assert.Equal(2u, header.LayoutVersion);
    }
}
