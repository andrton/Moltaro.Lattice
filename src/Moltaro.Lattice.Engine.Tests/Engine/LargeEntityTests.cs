using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// By decision O19 an entity has no size limit of its own, and an entity of tens of mebibytes is an
// ordinary case (brief of Epic 1, sprint S06, Acceptance): an entity with a String value of 32 MiB
// and a Binary value of 32 MiB is inserted, read back equal through its class and by name, replaced
// by a small one and deleted, and after that the counters of the arena are what they were before it.
// Until S09 gives the option, the arena of the engine gets half of the memory the runtime reports
// ("The limit before S09"), which holds the entity on every machine the tests run on.
//
// What the arena holds is compared whole. Of its segments, those in use are compared: an emptied
// segment may stay in the reserve of the arena, which then holds one segment more than before
// (S04; brief of S05, "The size of an entity").
public sealed class LargeEntityTests
{
    private const int Size = 32 * 1024 * 1024;

    [Fact]
    public async Task Entity_of_a_String_and_a_Binary_value_of_32_MiB_is_stored_read_replaced_and_deleted_and_leaves_the_arena_as_it_was()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();
        var records = database.Set("Piece");

        // An entity that stays, so that the arena holds something before the large one and after it.
        await pieces.InsertAsync(PieceSchema.Of(1, name: 100, data: 100));
        var before = Held(database.Counters.Arena);

        // Values that do not repeat, so that a chunk stored or read in the wrong place would show: the
        // text is the Base64 of 24 MiB of the bytes, 32 MiB of ASCII, one byte for every character in
        // UTF-8.
        var data = new byte[Size];
        new Random(42).NextBytes(data);
        var large = new PieceSchema.Piece { Id = 2, Name = Convert.ToBase64String(data.AsSpan(0, Size / 4 * 3)), Data = data, Count = 2 };
        Assert.Equal(Size, large.Name.Length);

        await pieces.InsertAsync(large);

        var byClass = pieces.Get(2L);
        Assert.NotNull(byClass);
        Assert.Equal(large.Name, byClass.Name);
        Assert.True(data.AsSpan().SequenceEqual(byClass.Data), "The Binary value read through the class differs.");

        var byName = records.Get(2L);
        Assert.NotNull(byName);
        Assert.Equal(large.Name, byName["Name"]);
        Assert.True(data.AsSpan().SequenceEqual(byName.Get<byte[]>("Data")), "The Binary value read by name differs.");

        await pieces.ReplaceAsync(PieceSchema.Of(2, name: 10));
        Assert.Equal(new string('n', 10), pieces.Get(2L)!.Name);

        await pieces.DeleteAsync(2L);

        Assert.Null(pieces.Get(2L));
        Assert.Equal(before, Held(database.Counters.Arena));
    }

    // What the arena holds, and the segments it uses for it.
    private static (long Live, long Slack, long Prepared, long Retired, int FreeRegions, int SegmentsInUse) Held(ArenaCounters arena) =>
        (arena.LiveBytes, arena.ClassSlackBytes, arena.PreparedBytes, arena.RetiredBytes, arena.FreeRegions, arena.Segments - arena.ReservedSegments);
}
