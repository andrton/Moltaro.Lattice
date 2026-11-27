using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// An entity is written again as a new root, and the old root is freed with RootStorage.Free, which
// frees the root block and every block it owns (brief of S05, Acceptance and "RootStorage.Free").
// The counters show it: after the old root is freed the arena holds what the new root holds, and
// after every root is freed it holds nothing. A value crosses the thresholds of its placement in
// both directions, longer and shorter, and reads back equal each time. Free does not touch the
// table of root entries.
public sealed class RewriteTests
{
    // Lengths that climb through every placement and come back down.
    private static readonly int[] Climb =
    [
        0, ValueBlocks.InlineLimit, ValueBlocks.InlineLimit + 1, ValueBlocks.SingleBlockLimit, ValueBlocks.SingleBlockLimit + 1,
        ValueBlocks.ChunkPayload, ValueBlocks.ChunkPayload + 1, 200_000, ValueBlocks.ChunkPayload + 1, ValueBlocks.ChunkPayload,
        ValueBlocks.SingleBlockLimit + 1, ValueBlocks.SingleBlockLimit, ValueBlocks.InlineLimit + 1, ValueBlocks.InlineLimit, 0,
        200_000, 0, ValueBlocks.SingleBlockLimit + 1, ValueBlocks.InlineLimit,
    ];

    [Fact]
    public void A_Binary_value_written_again_longer_and_shorter_reads_back_equal_and_the_old_root_is_freed()
    {
        using var item = TypedItem.OfType("Binary");
        var previous = default(ArenaAddress);

        for (var step = 0; step < Climb.Length; step++)
        {
            var value = Payloads.Bytes(Climb[step], seed: step);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetBinary(item.Field("Value"), value);
            writer.SetBinary(item.Field("Maybe"), value);
            var root = item.Publish(writer.Complete());

            if (!previous.IsNull)
            {
                RootStorage.Free(item.Arena, item.Layout, previous);
            }

            var reader = item.Reader(root);

            Assert.Equal(value, reader.GetBinary(item.Field("Value")));
            Assert.Equal(value, reader.GetBinary(item.Field("Maybe")));
            Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, value.Length, value.Length), StoredBytes.In(item.Arena));
            previous = root;
        }

        RootStorage.Free(item.Arena, item.Layout, previous);

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void A_text_written_again_longer_and_shorter_reads_back_equal_and_the_old_root_is_freed(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);
        var bytesPerCharacter = encoding == "Utf8" ? 1 : 2;
        var previous = default(ArenaAddress);

        for (var step = 0; step < Climb.Length; step++)
        {
            // In UTF-16 the length is rounded up to whole code units, so that it still crosses.
            var value = Payloads.Ascii((Climb[step] + bytesPerCharacter - 1) / bytesPerCharacter, seed: step);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Value"), value);
            var root = item.Publish(writer.Complete());

            if (!previous.IsNull)
            {
                RootStorage.Free(item.Arena, item.Layout, previous);
            }

            Assert.Equal(value, item.Reader(root).GetString(item.Field("Value")));
            Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, value.Length * bytesPerCharacter), StoredBytes.In(item.Arena));
            previous = root;
        }

        RootStorage.Free(item.Arena, item.Layout, previous);

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void After_the_old_root_is_freed_the_arena_holds_what_an_arena_with_the_new_root_alone_holds()
    {
        // Measured, not computed: the same new root written into a fresh arena.
        using var rewritten = TypedItem.OfType("Binary");
        using var fresh = TypedItem.OfType("Binary");
        (int Old, int New)[] cases = [(10, 300_000), (300_000, 10), (100, 5_000), (5_000, 100), (70_000, 64)];

        foreach (var (oldLength, newLength) in cases)
        {
            var old = Write(rewritten, oldLength);
            var root = Write(rewritten, newLength);
            RootStorage.Free(rewritten.Arena, rewritten.Layout, old);
            var alone = Write(fresh, newLength);

            Assert.Equal(StoredBytes.In(fresh.Arena), StoredBytes.In(rewritten.Arena));

            RootStorage.Free(rewritten.Arena, rewritten.Layout, root);
            RootStorage.Free(fresh.Arena, fresh.Layout, alone);
        }

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(rewritten.Arena));
    }

    [Fact]
    public void A_freed_root_is_no_longer_a_block_of_the_arena()
    {
        using var item = TypedItem.OfType("Binary");
        var root = Write(item, 100_000);

        RootStorage.Free(item.Arena, item.Layout, root);

        Assert.False(ArenaBlocks.IsValid(item.Arena, root));
        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void Freeing_a_root_does_not_touch_the_table_of_root_entries()
    {
        using var item = TypedItem.OfType("Binary");
        var table = new RootTable(new MemoryBudget(), pageLimit: null);
        var handle = table.Add((uint)item.EntityType.Id);
        var other = table.Add((uint)item.EntityType.Id);

        var writer = new RootWriter(item.Arena, item.Layout, handle);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), Payloads.Bytes(100_000));
        var root = writer.Complete();

        RootStorage.Free(item.Arena, item.Layout, root);

        // The entry is still the one Add gave out: current, Prepared, with nothing written into it.
        Assert.True(table.IsCurrent(handle));
        Assert.True(table.IsCurrent(other));
        Assert.Equal(2, table.Count);
        Assert.Equal(RootStateEnum.Prepared, table.Entry(handle).State);
        Assert.True(table.Entry(handle).Root.IsNull);
    }

    private static ArenaAddress Write(RootArena item, int length)
    {
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), Payloads.Bytes(length));

        return item.Publish(writer.Complete());
    }
}
