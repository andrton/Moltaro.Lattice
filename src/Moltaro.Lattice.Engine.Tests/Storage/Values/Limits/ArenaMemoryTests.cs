using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// What the writer does when the arena has no room for the entity, or a value needs more chunks than
// one directory block holds (brief of S05, "Where a value goes" and "The size of an entity"):
// Complete is WriteRejected, no Set… member fails for it, and everything the writer allocated is
// freed, so LiveBytes and ClassSlackBytes are as they were before the writer. Segments and
// ReservedSegments may differ and are not read. An OutOfMemoryException of the native allocator
// passes through whichever member meets it; met inside Complete it is a failed Complete, met inside
// a Set… member the caller calls Abandon, and either way nothing the writer allocated is left. The
// native allocator is failed through the fault point of S04 (exception X3).
public sealed class ArenaMemoryTests
{
    private const string Fields = "{ 'Id': 2, 'Name': 'Payload', 'Type': 'Binary' }";

    [Fact(Skip = NotImplemented.RootLayout)]
    public void An_arena_with_no_room_left_refuses_the_entity_at_Complete()
    {
        // One segment, filled with blocks of the largest class until the arena refuses another.
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: RootArena.SegmentSize);

        while (item.Arena.TryAllocate(Arena.MaxBlockSize - 32, BlockKindEnum.Value, default, out _))
        {
        }

        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), Filled(100));

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_value_larger_than_the_arena_refuses_the_entity_at_Complete_and_frees_what_was_allocated()
    {
        // An arena of one 4 MiB segment and a value of 5 MiB under a limit of 8 MiB: only the memory
        // of the arena stands in the way.
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: RootArena.SegmentSize);
        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer(maxEntitySize: 8 * 1024 * 1024);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), Filled(5 * 1024 * 1024));

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_value_that_needs_more_chunks_than_one_directory_block_holds_is_WriteRejected_by_Complete()
    {
        // A directory block holds ChunkPayload / 16 = 4,094 descriptors. One byte more than 4,094
        // full chunks needs a 4,095th. Neither the limit of the entity nor the memory of the arena
        // stands in the way.
        var mostChunks = RootLayout.ChunkPayload / 16;
        var length = mostChunks * RootLayout.ChunkPayload + 1;
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: 80L * RootArena.SegmentSize);
        var value = GC.AllocateUninitializedArray<byte>(length);
        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer(maxEntitySize: int.MaxValue);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), value);

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_failure_of_the_native_allocator_passes_through_and_leaves_nothing_the_writer_allocated()
    {
        // The arena holds one empty segment, in its reserve, and its next native allocation fails.
        // A value of 5 MiB needs more than one segment, so the writer meets the failure after it has
        // allocated in the first: in Complete, or in a Set… member if it allocates there.
        var allocator = new CountingNativeAllocator();
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: 8L * RootArena.SegmentSize, allocator: allocator);
        Assert.True(item.Arena.TryAllocate(0, BlockKindEnum.Value, default, out var first));
        item.Arena.Free(first);
        allocator.FailNextAllocation = true;
        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer(maxEntitySize: 8 * 1024 * 1024);
        WriterAction[] calls =
        [
            (ref RootWriter w) => w.SetInteger(item.Field("Id"), 1),
            (ref RootWriter w) => w.SetBinary(item.Field("Payload"), Filled(5 * 1024 * 1024)),
            static (ref RootWriter w) => w.Complete(),
        ];
        Exception? thrown = null;
        var index = 0;

        for (; index < calls.Length; index++)
        {
            thrown = WriterAssert.Catch(ref writer, calls[index]);

            if (thrown is not null)
            {
                break;
            }
        }

        // The failure of the native allocator, not another exception in its place.
        Assert.IsType<OutOfMemoryException>(thrown);

        if (index == calls.Length - 1)
        {
            // Met inside Complete, it is a failed Complete: everything is freed already, Abandon does
            // nothing, and the writer takes nothing more.
            Assert.Equal(before, StoredBytes.In(item.Arena));
            WriterAssert.Throws<InvalidOperationException>(ref writer, static (ref RootWriter w) => w.Complete());
        }

        // Met inside a Set… member, Abandon frees everything.
        writer.Abandon();

        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    private static byte[] Filled(int length)
    {
        var bytes = new byte[length];
        bytes.AsSpan().Fill(0xA5);

        return bytes;
    }
}
