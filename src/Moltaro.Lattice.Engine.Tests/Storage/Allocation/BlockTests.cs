using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// A block of the arena, as Arena allocates and reaches it. An allocation writes the whole header, also
// in a reused slot: the root, the kind and the payload length as given, every other field zero; it
// checks neither the kind nor the root. Header and Payload reach the block in place, in native memory.
// Payload spans the header's PayloadLength and never goes beyond the capacity of the slot: a length that
// would is refused with InvalidOperationException before any byte of the payload is reached. A new
// payload is not initialized, so these tests read only bytes they wrote.
public sealed class BlockTests
{
    [Fact]
    public void A_new_block_has_the_root_kind_and_payload_length_it_was_given_and_every_other_header_field_zero()
    {
        using var arena = Arenas.Open();
        var root = new RootHandle(3, 17);

        Assert.True(ArenaBlocks.TryAllocateLive(arena, 100, BlockKindEnum.Root, root, out var address));

        Assert.True(ArenaBlocks.IsValid(arena, address));
        AssertNewHeader(arena.Header(address), root, BlockKindEnum.Root, 100);
    }

    [Fact]
    public void A_reused_slot_gets_a_whole_new_header()
    {
        using var arena = Arenas.Open();

        // The region of the 144-byte class, full, with every field of every header written. A region
        // holds several slots of its class (Q17), and the region is the only one of the class.
        var region = Arenas.FillFirstRegion(arena, 100);
        Assert.True(region.Count > 1, "A region of the 144-byte class holds more than one slot.");

        foreach (var block in region)
        {
            Scribble(ref arena.Header(block));
        }

        var freed = region[^1];
        arena.Free(freed);

        // The freed slot is the only free slot of the class, and a payload of 110 bytes takes the
        // same class.
        var root = new RootHandle(5, 9);
        Assert.True(ArenaBlocks.TryAllocateLive(arena, 110, BlockKindEnum.Chunk, root, out var again));

        Assert.Equal(freed, again);
        AssertNewHeader(arena.Header(again), root, BlockKindEnum.Chunk, 110);
    }

    [Fact]
    public void A_block_takes_any_kind_and_any_root_as_given()
    {
        using var arena = Arenas.Open();
        (BlockKindEnum Kind, RootHandle Root)[] cases =
        [
            ((BlockKindEnum)0, default),
            ((BlockKindEnum)99, new RootHandle(uint.MaxValue, uint.MaxValue)),
            ((BlockKindEnum)ushort.MaxValue, new RootHandle(0, 12)),
        ];

        foreach (var (kind, root) in cases)
        {
            Assert.True(ArenaBlocks.TryAllocateLive(arena, 8, kind, root, out var address));

            Assert.Equal(kind, arena.Header(address).Kind);
            Assert.Equal(root, arena.Header(address).Root);
        }
    }

    [Fact]
    public void The_header_of_a_live_block_is_written_and_read_in_place()
    {
        using var arena = Arenas.Open();
        var address = Arenas.Allocate(arena, 100);

        ref var header = ref arena.Header(address);
        header.Parent = new ArenaAddress(2, 4096);
        header.DescriptorOffset = 48;
        header.Flags = 3;
        header.LayoutVersion = 11;

        var read = arena.Header(address);

        Assert.Equal(new ArenaAddress(2, 4096), read.Parent);
        Assert.Equal(48u, read.DescriptorOffset);
        Assert.Equal((ushort)3, read.Flags);
        Assert.Equal(11u, read.LayoutVersion);
        Assert.Equal(100u, read.PayloadLength);
        Assert.True(ArenaBlocks.IsValid(arena, address));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(4064)]
    [InlineData(65_504)]
    public void The_payload_of_a_new_block_spans_its_payload_length(int payloadLength)
    {
        using var arena = Arenas.Open();
        var address = Arenas.Allocate(arena, payloadLength);

        Assert.Equal(payloadLength, arena.Payload(address).Length);
    }

    [Fact]
    public void The_payload_follows_the_length_in_the_header_up_to_the_end_of_the_slot()
    {
        using var arena = Arenas.Open();
        var address = Arenas.Allocate(arena, 100);

        // The 144-byte slot leaves 112 bytes after the header.
        var room = SpecifiedSizeClasses.For(100) - SpecifiedSizeClasses.HeaderSize;
        ref var header = ref arena.Header(address);

        header.PayloadLength = 40;
        Assert.Equal(40, arena.Payload(address).Length);

        header.PayloadLength = (uint)room;
        Assert.Equal(room, arena.Payload(address).Length);
    }

    [Fact]
    public void A_header_length_beyond_the_capacity_of_the_slot_is_refused_before_the_payload_is_reached()
    {
        using var arena = Arenas.Open();
        var address = Arenas.Allocate(arena, 100);
        var room = SpecifiedSizeClasses.For(100) - SpecifiedSizeClasses.HeaderSize;
        uint[] lengths = [(uint)room + 1, Arena.MaxBlockSize, int.MaxValue, (uint)int.MaxValue + 1, uint.MaxValue];

        foreach (var length in lengths)
        {
            arena.Header(address).PayloadLength = length;

            Assert.Throws<InvalidOperationException>(() => arena.Payload(address));
        }

        // The block is still a block of the arena. With the length it was given back in its header,
        // it can be freed.
        Assert.True(ArenaBlocks.IsValid(arena, address));
        arena.Header(address).PayloadLength = 100;
        arena.Free(address);
        Assert.False(ArenaBlocks.IsValid(arena, address));
    }

    [Fact]
    public void A_payload_keeps_what_was_written_while_neighbouring_blocks_are_allocated_and_freed()
    {
        using var arena = Arenas.Open();
        var random = new Random(4_096);
        var blocks = new List<(ArenaAddress Address, int Tag)>();

        for (var tag = 0; tag < 300; tag++)
        {
            blocks.Add((Write(arena, RandomPayloadLength(random), tag), tag));
        }

        // Every other block is freed, and new blocks are allocated in the space between the rest.
        var kept = blocks.Where((_, index) => index % 2 == 0).ToList();

        foreach (var (address, _) in blocks.Where((_, index) => index % 2 == 1))
        {
            arena.Free(address);
        }

        for (var tag = 300; tag < 450; tag++)
        {
            kept.Add((Write(arena, RandomPayloadLength(random), tag), tag));
        }

        foreach (var (address, tag) in kept)
        {
            var payload = arena.Payload(address);

            Assert.Equal(Pattern(payload.Length, tag), payload.ToArray());
            Assert.Equal((uint)tag, arena.Header(address).LayoutVersion);
        }
    }

    private static void AssertNewHeader(BlockHeader header, RootHandle root, BlockKindEnum kind, int payloadLength)
    {
        Assert.Equal(root, header.Root);
        Assert.Equal(kind, header.Kind);
        Assert.Equal((uint)payloadLength, header.PayloadLength);
        Assert.True(header.Parent.IsNull);
        Assert.Equal(0u, header.DescriptorOffset);
        Assert.Equal((ushort)0, header.Flags);
        Assert.Equal(0u, header.LayoutVersion);
    }

    // Writes something other than zero into every field of the header.
    private static void Scribble(ref BlockHeader header)
    {
        header.Root = new RootHandle(0xAAAA, 0xBBBB);
        header.Parent = new ArenaAddress(0xCCCC, 0xDDDD);
        header.DescriptorOffset = 0xEEEE;
        header.PayloadLength = 7;
        header.Kind = BlockKindEnum.SharedText;
        header.Flags = 0xFFFF;
        header.LayoutVersion = 0x1234_5678;
    }

    // Allocates a block, fills its payload with the pattern of its tag and writes the tag into its
    // header.
    private static ArenaAddress Write(Arena arena, int payloadLength, int tag)
    {
        var address = Arenas.Allocate(arena, payloadLength);

        Pattern(payloadLength, tag).CopyTo(arena.Payload(address));
        arena.Header(address).LayoutVersion = (uint)tag;

        return address;
    }

    // Bytes that differ from one tag to the next and along the payload.
    private static byte[] Pattern(int length, int tag)
    {
        var bytes = new byte[length];

        for (var index = 0; index < length; index++)
        {
            bytes[index] = (byte)((tag * 31 + index) % 251);
        }

        return bytes;
    }

    // Half small payloads, half of any length up to the largest.
    private static int RandomPayloadLength(Random random)
    {
        var largest = random.Next(2) == 0 ? 1024 : SpecifiedSizeClasses.LargestPayload;

        return random.Next(largest + 1);
    }
}
