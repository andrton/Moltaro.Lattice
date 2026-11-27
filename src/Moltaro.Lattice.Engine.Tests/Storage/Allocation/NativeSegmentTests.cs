using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Engine.Tests.Storage.Allocation;

// The native segments of the arena (milestone contract, "Native layout and allocation"; Q17,
// "Empty-segment retention and release"; ArenaOptions.SegmentSize). Segments are obtained on
// demand and aligned to 16 bytes, and every size class is a multiple of 16 bytes, so every block
// starts on a 16-byte boundary. A segment is a whole number of regions of MaxBlockSize bytes; the
// smallest it can be is one region. A segment left empty beyond the reserve goes back to the
// native allocator, and later allocations obtain segments again without touching the blocks that
// stay live.
public sealed class NativeSegmentTests
{
    [Fact]
    public void Every_block_starts_on_a_16_byte_boundary()
    {
        using var arena = Arenas.Open();

        foreach (var blockSize in SpecifiedSizeClasses.All)
        {
            for (var block = 0; block < 3; block++)
            {
                var address = Arenas.Allocate(arena, blockSize - SpecifiedSizeClasses.HeaderSize);

                Assert.Equal(0, PositionInMemory(ref arena.Header(address)) % 16);
            }
        }
    }

    [Fact]
    public void Anywhere_in_a_segment_only_the_addresses_of_live_blocks_are_valid()
    {
        using var arena = Arenas.Open();
        var live = new HashSet<ArenaAddress>();
        var given = new List<ArenaAddress>();

        // Payloads of 1,008 and 3,296 bytes take blocks of 1,280 and 3,328 bytes, which do not fill
        // their space exactly and leave room behind their last slot; the blocks of 3,328 bytes are
        // all freed again, which leaves space that served a class and holds no block.
        int[] payloadLengths = [0, 100, 1008, 3296, 30_000, SpecifiedSizeClasses.LargestPayload];

        foreach (var payloadLength in payloadLengths)
        {
            for (var block = 0; block < 40; block++)
            {
                given.Add(Arenas.Allocate(arena, payloadLength));
            }
        }

        for (var index = 0; index < given.Count; index++)
        {
            var freed = payloadLengths[index / 40] == 3296 || index % 3 == 0;

            if (freed)
            {
                arena.Free(given[index]);
            }
            else
            {
                live.Add(given[index]);
            }
        }

        // Every block starts on a 16-byte boundary, so every address that could name one is tried.
        var wrong = 0;

        foreach (var segment in given.Select(address => address.Segment).Distinct())
        {
            for (var offset = 0u; offset < (uint)Arenas.SegmentSize; offset += 16)
            {
                var address = new ArenaAddress(segment, offset);

                if (ArenaBlocks.IsValid(arena, address) != live.Contains(address))
                {
                    wrong++;
                }
            }
        }

        Assert.Equal(0, wrong);
    }

    [Fact]
    public void A_segment_size_that_is_not_a_whole_multiple_of_MaxBlockSize_is_refused()
    {
        int[] sizes = [Arena.MaxBlockSize + 1, Arena.MaxBlockSize * 3 / 2, Arenas.SegmentSize + 16, int.MaxValue];

        foreach (var size in sizes)
        {
            var options = new ArenaOptions { MemoryLimit = 64L * Arenas.SegmentSize, SegmentSize = size };

            Assert.Throws<ArgumentOutOfRangeException>(() => new Arena(options, new MemoryBudget()));
        }
    }

    [Fact]
    public void A_segment_of_MaxBlockSize_bytes_holds_a_block_of_every_size_class()
    {
        var options = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 2L * SpecifiedSizeClasses.All.Count * Arena.MaxBlockSize };
        using var arena = new Arena(options, new MemoryBudget());
        var tally = new ArenaTally();

        foreach (var blockSize in SpecifiedSizeClasses.All)
        {
            var payloadLength = blockSize - SpecifiedSizeClasses.HeaderSize;

            tally.Allocated(Arenas.Allocate(arena, payloadLength), payloadLength, tag: 0);
        }

        Assert.All(tally.Kept, block => Assert.True(ArenaBlocks.IsValid(arena, block.Address)));
        Assert.Equal(tally.LiveBytes, arena.Counters.LiveBytes);
        Assert.Equal(tally.ClassSlackBytes, arena.Counters.ClassSlackBytes);
        Assert.True(arena.Counters.Segments * (long)options.SegmentSize <= options.MemoryLimit);

        while (tally.Kept.Count > 0)
        {
            arena.Free(tally.Remove(0).Address);
        }

        Assert.Equal(
            new ArenaCounters(LiveBytes: 0, ClassSlackBytes: 0, PreparedBytes: 0, RetiredBytes: 0, FreeRegions: 0, ReservedSegments: 1, Segments: 1),
            arena.Counters);
    }

    [Fact]
    public void Segments_freed_beyond_the_reserve_are_obtained_again_and_the_live_blocks_keep_their_bytes()
    {
        // Without a reserve, a segment left empty is freed at once.
        var options = new ArenaOptions { MemoryLimit = 8L * Arenas.SegmentSize, ReservedSegments = 0 };
        using var arena = new Arena(options, new MemoryBudget());
        var blocks = Arenas.AllocateUntilSegments(arena, 6);

        for (var tag = 0; tag < blocks.Count; tag++)
        {
            Mark(arena, blocks[tag], tag);
        }

        // Every block of the second and the fourth segment is freed, and the two segments go back to
        // the native allocator.
        var segments = blocks.Select(block => block.Segment).Distinct().ToList();
        var freedSegments = new HashSet<uint> { segments[1], segments[3] };
        var kept = new List<(ArenaAddress Address, int Tag)>();

        for (var tag = 0; tag < blocks.Count; tag++)
        {
            if (freedSegments.Contains(blocks[tag].Segment))
            {
                arena.Free(blocks[tag]);
            }
            else
            {
                kept.Add((blocks[tag], tag));
            }
        }

        Assert.Equal(4, arena.Counters.Segments);

        // Allocations obtain segments again, up to the limit, and the new blocks take addresses of
        // their own.
        var tagOfNext = blocks.Count;

        while (ArenaBlocks.TryAllocateLive(arena, SpecifiedSizeClasses.LargestPayload, BlockKindEnum.Value, new RootHandle(1, 1), out var address))
        {
            Mark(arena, address, tagOfNext);
            kept.Add((address, tagOfNext));
            tagOfNext++;
        }

        Assert.Equal(8, arena.Counters.Segments);
        Assert.Equal(kept.Count, kept.Select(block => block.Address).Distinct().Count());
        Assert.Equal(kept.Count * (long)Arena.MaxBlockSize, arena.Counters.LiveBytes);

        foreach (var (address, tag) in kept)
        {
            Assert.True(ArenaBlocks.IsValid(arena, address));
            Assert.Equal((uint)tag, arena.Header(address).LayoutVersion);
            Assert.True(arena.Payload(address).IndexOfAnyExcept((byte)tag) < 0, $"The payload of block {tag} changed.");
        }
    }

    // Writes the tag into the header and every byte of the payload.
    private static void Mark(Arena arena, ArenaAddress address, int tag)
    {
        arena.Header(address).LayoutVersion = (uint)tag;
        arena.Payload(address).Fill((byte)tag);
    }

    // The position of the header in memory, as a number: its distance from address zero. Nothing is
    // read through either reference.
    private static nint PositionInMemory(ref BlockHeader header) =>
        Unsafe.ByteOffset(ref Unsafe.NullRef<byte>(), ref Unsafe.As<BlockHeader, byte>(ref header));
}
