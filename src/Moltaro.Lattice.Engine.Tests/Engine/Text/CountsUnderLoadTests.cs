using System.Globalization;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A count is changed only by the thread that holds the lock of the writes, on every path that frees a
// block, the drain of retired memory among them (brief of Epic 1, sprint S13, the planner's decision after
// the study, point 6, the P1 of the sprint). A count changed outside that lock loses or doubles an update
// when two such changes meet, and the counts then differ from any tally. So writers on threads of their
// own replace, delete and insert entities whose texts change with every write — each state has a text of
// its own in Region, which its next write leaves to be freed, and one of four shared texts in Zone, whose
// counts every write changes — while a thread of its own drains retired memory without a pause and
// readers on threads of their own read the entities (OwnThread). Every read is checked on its own: the
// texts it returns are those of the state whose number it returns, so a read never returns text that was
// freed and given to another state. When the writes are done, the counts and the arena equal the tally of
// the last states; once everything is deleted, nothing is left. Nothing sleeps.
public sealed class CountsUnderLoadTests
{
    private const int Keys = 16;

    private const int Writers = 2;

    // Per key, in cycles of four: replace, replace, delete, insert; a multiple of four.
    private const int WritesPerKey = 500;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    private static readonly string[] Shared = ["North", "South", "Жук", "a😀b"];

    private static int Readers => Math.Clamp(Environment.ProcessorCount - 2, 2, 6);

    [Fact(Skip = SkipReason.BeforeSprint13)]
    public async Task Counts_equal_the_tally_after_writes_drains_and_reads_on_threads_of_their_own()
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var places = database.Set<TextSchema.Place>();
            var records = database.Set("Place");

            for (var key = 0; key < Keys; key++)
            {
                await places.InsertAsync(StateOf(key, 0));
            }

            // The last reader is the thread that drains.
            await ReadersAndWriter.RunAsync(
                Readers + 1,
                reader =>
                {
                    if (reader == Readers)
                    {
                        return database.DrainRetired;
                    }

                    var next = reader;
                    var byClass = reader % 2 == 0;

                    return () =>
                    {
                        var key = next++ % Keys;

                        byClass = !byClass;
                        ExpectWhole(key, byClass ? places.Get((long)key) : PlaceOf(records.Get((long)key)));
                    };
                },
                () => Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => OwnThread.Run(() => Write(places, records, writer)))))
                .WaitAsync(Bound);

            // The writes per key are a multiple of four, so the last write of every key inserts it again.
            var tally = TextTally.Of("Utf8", Enumerable.Range(0, Keys).Select(key => StateOf(key, WritesPerKey)));

            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);

            for (var key = 0; key < Keys; key++)
            {
                await places.DeleteAsync((long)key);
                tally.Deleted("Place", key);
            }

            tally.ExpectHeldBy(database);
            Assert.Equal(0, database.Counters.TextEntries);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The state with the number of the key: a Region of its own and a shared Zone.
    private static TextSchema.Place StateOf(int key, long number) => new()
    {
        Id = key,
        Number = number,
        Region = RegionOf(key, number),
        Zone = Shared[(int)((key + number) % Shared.Length)],
        Label = "label",
    };

    private static string RegionOf(int key, long number) =>
        string.Create(CultureInfo.InvariantCulture, $"key {key:D2} state {number:D6}");

    private static TextSchema.Place? PlaceOf(LatticeRecord? record) => record is null
        ? null
        : new TextSchema.Place
        {
            Id = record.Get<long>("Id"),
            Number = record.Get<long>("Number"),
            Region = record.Get<string>("Region")!,
            Zone = record.Get<string>("Zone"),
            Label = record.Get<string>("Label")!,
        };

    // A read returned absence, or the texts of the state whose number it returned.
    private static void ExpectWhole(int key, TextSchema.Place? place)
    {
        if (place is null)
        {
            return;
        }

        var expected = StateOf(key, place.Number);

        Assert.Equal(key, place.Id);
        Assert.Equal(expected.Region, place.Region);
        Assert.Equal(expected.Zone, place.Zone);
        Assert.Equal(expected.Label, place.Label);
    }

    // Write w of a key replaces it, deletes it or inserts it again by w alone, with the state number w;
    // the writer writes the keys whose number leaves its own when divided by the number of writers.
    private static void Write(ILatticeSet<TextSchema.Place> places, ILatticeSet records, int writer)
    {
        for (var write = 1; write <= WritesPerKey; write++)
        {
            for (var key = writer; key < Keys; key += Writers)
            {
                var state = StateOf(key, write);
                var result = (write % 4) switch
                {
                    0 => OwnThread.Wait(places.InsertAsync(state)),
                    3 => OwnThread.Wait(records.DeleteAsync((long)key)),
                    1 => OwnThread.Wait(records.ReplaceAsync(TextSchema.RecordOf(records.EntityType, state))),
                    _ => OwnThread.Wait(places.ReplaceAsync(state)),
                };

                Assert.True(result.Changed);
            }
        }
    }
}
