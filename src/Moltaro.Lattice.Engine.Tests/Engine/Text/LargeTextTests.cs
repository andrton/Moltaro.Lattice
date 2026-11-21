using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// An entry above 4 KiB lies in chunks, a directory and its chunks, by the placement rule of a Plain value
// (brief of Epic 1, sprint S13, the planner's decision after the study, point 2), and is shared, read back
// and freed like a small one: two entities and two fields that hold one large text have one entry, which
// the arena holds once, chunks and directory, beside their roots; it reads back as written, its characters
// across the boundaries of the chunks included; it survives the delete of one referrer, stays while a read
// is held across the replace of the last one, and is freed by the drain after the read has left. Texts of
// 100,000 bytes, two chunks, and of 1 MiB, seventeen.
public sealed class LargeTextTests
{
    public static IEnumerable<TheoryDataRow<int>> Lengths => [new(100_000), new(1024 * 1024)];

    [Theory]
    [MemberData(nameof(Lengths))]
    public async Task Large_text_is_one_entry_that_two_entities_share_and_reads_back_as_written(int bytes)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8);
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally("Utf8");
        var large = LargeText(bytes);

        foreach (var place in new[]
        {
            new TextSchema.Place { Id = 1, Region = large, Label = "label" },
            new TextSchema.Place { Id = 2, Region = large, Zone = large, Label = "label" },
        })
        {
            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(3, EntryCounts.Of(database, "Utf8")[large]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.True(large == places.Get(1L)?.Region, "The large text of 1 did not read back as written.");
        Assert.True(large == (string?)database.Set("Place").Get(2L)?["Zone"], "The large text of 2 did not read back as written by name.");
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public async Task Large_text_survives_the_delete_of_one_referrer_stays_while_a_read_is_held_and_is_freed_after_it(int bytes)
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");
            var large = LargeText(bytes);

            foreach (var id in new[] { 1L, 2L })
            {
                var place = new TextSchema.Place { Id = id, Region = large, Label = "label" };

                await places.InsertAsync(place);
                tally.Written(place);
            }

            await places.DeleteAsync(1L);
            tally.Deleted("Place", 1);
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);

            using var held = await HeldRead.StartAsync(database, () => places.Get(2L) is { } place ? place.Region.Length : null);

            tally.Retaining = true;

            var replaced = new TextSchema.Place { Id = 2, Region = "small", Label = "label" };

            await Task.Run(() => places.ReplaceAsync(replaced).AsTask()).WaitAsync(Bound);
            tally.Written(replaced);

            Assert.Equal(1, EntryCounts.Of(database, "Utf8")[large]);
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);

            Assert.Contains(await held.ReleaseAsync(), new long?[] { large.Length, "small".Length });

            tally.ReadLeft();
            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
            Assert.False(EntryCounts.Of(database, "Utf8").ContainsKey(large));
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // A text of this many bytes of UTF-8 whose characters of two and three bytes lie across the
    // boundaries of the chunks, 65,504 bytes each, after a first character of one byte.
    private static string LargeText(int bytes)
    {
        var text = new StringBuilder("a");
        var length = 1;

        // é takes two bytes, € three.
        for (var next = 0; length <= bytes - 3; next++)
        {
            text.Append(next % 2 == 0 ? 'é' : '€');
            length += next % 2 == 0 ? 2 : 3;
        }

        text.Append('z', bytes - length);

        var large = text.ToString();

        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(large));

        return large;
    }
}
