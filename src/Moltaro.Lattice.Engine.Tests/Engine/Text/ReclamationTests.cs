using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// An entry is freed after the last reference and the last protected reader are gone, and not before
// (brief of Epic 1, sprint S13, Acceptance; decision on strings, Q04.4; the planner's decision after the
// study, points 4 and 5). There is no retirement of an entry on its own and no generation: a replaced or
// deleted root is retired as before, its references go when the drain frees it, and an entry that reaches
// zero leaves the table and is freed in the same pass. So:
// - an entry with two referrers survives the delete of one;
// - an entry whose last referrer is replaced or deleted is freed by the next drain with no read in
//   progress, and the arena holds what it held before the text was written;
// - while a read is held inside the protection (HeldRead), the entry of a text whose last referrer was
//   replaced or deleted stays in the table with the reference of the retired root, live in the arena,
//   through a drain; once the read has left, a drain frees it;
// - a text is never rebound: while such a read is held, new texts of the same size class are written and
//   take memory of their own; the held read, and every read after it, returns one written text;
// - a text written again while its old referrer waits for a held read takes that same entry, which is
//   never revived once it has left the table; a text written after its entry was freed makes a new one.
// The held read finds its root and stops before it copies it; it copies whatever root its entity has when
// it is released, so it returns the text of the state before the write or of the one after it.
public sealed class ReclamationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    public static IEnumerable<TheoryDataRow<string>> Encodings => TextSchema.Encodings;

    public static IEnumerable<TheoryDataRow<string>> LastReferrerEnds => [new("replace"), new("delete")];

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Entry_with_two_referrers_survives_the_delete_of_one(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally(encoding);

        foreach (var id in new[] { 1L, 2L })
        {
            var place = new TextSchema.Place { Id = id, Region = "North", Label = "label" };

            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.True((await places.DeleteAsync(1L)).Changed);
        tally.Deleted("Place", 1);

        Assert.Equal(1, EntryCounts.Of(database, encoding)["North"]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal("North", places.Get(2L)?.Region);
    }

    [Theory]
    [MemberData(nameof(LastReferrerEnds))]
    public async Task Entry_whose_last_referrer_ends_is_freed_by_the_next_drain_and_the_arena_holds_what_it_held_before(string end)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8);
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally("Utf8");
        var kept = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };

        await places.InsertAsync(kept);
        tally.Written(kept);
        tally.ExpectHeldBy(database);

        var before = database.Counters.Arena;
        var lonely = new TextSchema.Place { Id = 2, Region = "Lonely", Zone = "Lonelier", Label = "label" };

        await places.InsertAsync(lonely);
        tally.Written(lonely);

        if (end == "replace")
        {
            var replaced = new TextSchema.Place { Id = 2, Region = "North", Label = "label" };

            await places.ReplaceAsync(replaced);
            tally.Written(replaced);
        }
        else
        {
            await places.DeleteAsync(2L);
            tally.Deleted("Place", 2);
        }

        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.False(EntryCounts.Of(database, "Utf8").ContainsKey("Lonely"));

        if (end == "delete")
        {
            var after = database.Counters.Arena;

            Assert.Equal((before.LiveBytes, before.ClassSlackBytes), (after.LiveBytes, after.ClassSlackBytes));
        }
    }

    [Theory]
    [MemberData(nameof(LastReferrerEnds))]
    public async Task Entry_whose_last_referrer_ends_while_a_read_is_held_stays_until_the_read_left_and_a_drain(string end)
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");
            var first = new TextSchema.Place { Id = 1, Region = "North", Zone = "Zone", Label = "label" };

            await places.InsertAsync(first);
            tally.Written(first);
            tally.ExpectHeldBy(database);

            using var held = await HeldRead.StartAsync(database, () => RegionOf(places.Get(1L), "North", "South"));

            tally.Retaining = true;

            if (end == "replace")
            {
                var replaced = new TextSchema.Place { Id = 1, Region = "South", Zone = "Zone", Label = "label" };

                await WrittenAsync(() => places.ReplaceAsync(replaced));
                tally.Written(replaced);
            }
            else
            {
                await WrittenAsync(() => places.DeleteAsync(1L));
                tally.Deleted("Place", 1);
            }

            // North keeps the reference of the retired root, and its entry stays live, through a drain.
            Assert.Equal(1, EntryCounts.Of(database, "Utf8")["North"]);
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);

            long?[] allowed = end == "replace" ? [0, 1] : [0, null];

            Assert.Contains(await held.ReleaseAsync(), allowed);

            tally.ReadLeft();
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
            Assert.False(EntryCounts.Of(database, "Utf8").ContainsKey("North"));
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Held_read_returns_a_written_text_while_texts_of_the_same_size_class_are_written_after_the_last_referrer_of_its_text_was_replaced()
    {
        const int Others = 64;

        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");

            // Every text has ten bytes, so every entry takes a slot of the same size class.
            var first = new TextSchema.Place { Id = 0, Region = "North 0000", Label = "label" };

            await places.InsertAsync(first);
            tally.Written(first);
            tally.ExpectHeldBy(database);

            using var held = await HeldRead.StartAsync(database, () => RegionOf(places.Get(0L), "North 0000", "South 0000"));

            tally.Retaining = true;

            var replaced = new TextSchema.Place { Id = 0, Region = "South 0000", Label = "label" };

            await WrittenAsync(() => places.ReplaceAsync(replaced));
            tally.Written(replaced);

            for (var id = 1; id <= Others; id++)
            {
                var other = new TextSchema.Place { Id = id, Region = $"Other {id:D4}", Label = "label" };

                await WrittenAsync(() => places.InsertAsync(other));
                tally.Written(other);
            }

            // The entry of North 0000 is still there, and each new text has an entry of its own.
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            tally.ReadLeft();
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
            Assert.Equal("South 0000", places.Get(0L)?.Region);

            for (var id = 1; id <= Others; id++)
            {
                Assert.Equal($"Other {id:D4}", places.Get((long)id)?.Region);
            }
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Text_written_again_while_its_last_referrer_waits_for_a_held_read_takes_that_entry_and_keeps_it_after_the_drain()
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");
            var first = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };

            await places.InsertAsync(first);
            tally.Written(first);
            tally.ExpectHeldBy(database);

            using var held = await HeldRead.StartAsync(database, () => RegionOf(places.Get(1L), "North", "South"));

            tally.Retaining = true;

            var replaced = new TextSchema.Place { Id = 1, Region = "South", Label = "label" };
            var again = new TextSchema.Place { Id = 2, Region = "North", Label = "label" };

            await WrittenAsync(() => places.ReplaceAsync(replaced));
            await WrittenAsync(() => places.InsertAsync(again));
            tally.Written(replaced);
            tally.Written(again);

            // One entry of North, with the reference of the retired root and of the new one.
            Assert.Equal(2, EntryCounts.Of(database, "Utf8")["North"]);
            Assert.Equal(2, database.Counters.TextEntries);
            tally.ExpectHeldBy(database);

            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });

            tally.ReadLeft();
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
            Assert.Equal("North", places.Get(2L)?.Region);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Text_written_after_its_entry_was_freed_makes_a_new_entry_that_reads_back_as_written(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally(encoding);

        await places.InsertAsync(new TextSchema.Place { Id = 1, Region = "North", Label = "label" });
        await places.DeleteAsync(1L);
        database.DrainRetired();

        Assert.Equal(0, database.Counters.TextEntries);
        tally.ExpectHeldBy(database);

        var again = new TextSchema.Place { Id = 2, Region = "North", Zone = "North", Label = "label" };

        await places.InsertAsync(again);
        tally.Written(again);

        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal(("North", "North"), (places.Get(2L)!.Region, places.Get(2L)!.Zone));
    }

    public static IEnumerable<TheoryDataRow<int>> EntryLengths =>
    [
        new(100),
        new(20_000),
    ];

    // A reference never resolves to other text, read through the very root a read took (Storage.Text.TextTable:
    // an entry is freed only after its last reference and the last protected reader that could reach it are
    // gone). A read enters the protection of reads and takes the root of its entity; then the entity is
    // replaced, its old root retired, texts of the same length, which take blocks of the same size class,
    // are written and deleted, and the retired roots are drained; the read reads the text of its entity
    // through the root it took, and finds the text that root was written with. Once the read has left, a
    // drain frees the old root and the entry of its text. The test works the parts themselves — the arena
    // with its text table, the epochs of the protection of reads and the retired list
    // (Protection.RetiredMemory) — with an entry of one block and one in chunks.
    [Theory]
    [MemberData(nameof(EntryLengths))]
    public void Read_that_took_a_root_reads_its_text_through_it_after_a_replace_texts_of_the_same_size_written_and_deleted_and_a_drain(int length)
    {
        using var item = new RootArena(ItemSchema.Compile("{ 'Id': 2, 'Name': 'Region', 'Type': 'String', 'StringStorage': 'Dictionary' }"));
        var epochs = new ReadEpochs(new object());
        var retired = new RetiredMemory(epochs, item.Arena, new RootTable());
        var written = new string('N', length);
        var root = Write(item, 1, written);

        var protection = epochs.Enter();
        var taken = root;

        try
        {
            var replacement = Write(item, 1, new string('S', length));

            retired.Reserve();
            retired.RetireRoot(item.Layout, root);

            for (var other = 'a'; other <= 'p'; other++)
            {
                RootStorage.Free(item.Arena, item.Layout, Write(item, 2, new string(other, length)));
            }

            retired.Drain();

            Assert.Equal(written, item.Reader(taken).GetString(item.Field("Region")));
            Assert.Equal(2, item.Arena.Texts.Count);
            Assert.Equal(new string('S', length), item.Reader(replacement).GetString(item.Field("Region")));
        }
        finally
        {
            epochs.Exit(protection);
        }

        retired.Drain();

        Assert.Equal(0, item.Arena.Counters.RetiredBytes);
        Assert.Equal(1, item.Arena.Texts.Count);
    }

    // A live root of Item with the key and the Region given.
    private static ArenaAddress Write(RootArena item, long id, string region)
    {
        var writer = item.Writer();

        writer.SetInteger(item.Field("Id"), id);
        writer.SetString(item.Field("Region"), region);

        return writer.Complete();
    }

    // The number of the text the read returned among the texts given, after the read; null when it found
    // no entity, and a failure when the text is none of them.
    private static long? RegionOf(TextSchema.Place? place, params string[] texts)
    {
        if (place is null)
        {
            return null;
        }

        var index = Array.IndexOf(texts, place.Region);

        Assert.True(index >= 0, $"A read returned the text {place.Region}, which no write wrote.");

        return index;
    }

    // A write on another thread that has to return within the bound: a writer that waited for the held
    // read would not.
    private static async Task WrittenAsync(Func<ValueTask<LatticeWriteResult>> write)
    {
        var result = await Task.Run(() => write().AsTask()).WaitAsync(Bound);

        Assert.True(result.Changed);
    }
}
