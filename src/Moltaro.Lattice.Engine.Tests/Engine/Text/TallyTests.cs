using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Reference counts equal an independent tally after a churn of writes and deletes (brief of Epic 1,
// sprint S13, Acceptance, as restated by the planner's decision after the study, point 9): the tally
// counts the descriptors in live blocks, in blocks retired and not yet freed, and in prepared blocks
// (TextTally, by the one rule of point 4). A deterministic pseudo-random churn of inserts, replaces to the
// same and to other texts, upserts and deletes, through the classes and by name, over a small set of texts
// of every placement — the empty text, texts that differ only in case, a text in chunks — in two fields of
// one entity type and in a field of another; the counts and the arena are compared with the tally at
// points during the churn, after a drain, and at its end, when everything is deleted and nothing is left.
// The same holds while a read is held inside the protection, whose retired roots keep their references,
// and while writes are prepared and not published because the storage step of the test holds the commit
// stage (HeldCommitStorage), whose prepared roots hold theirs; a prepared write that is withdrawn takes its
// references with it.
public sealed class TallyTests
{
    private const int Places = 24;

    private const int Depots = 8;

    // The texts of the churn: the empty text, two spellings of one word, text of every width of a
    // character, a text in a block of its own and one in chunks, in UTF-8 and in UTF-16.
    private static readonly string[] Texts = ["", "North", "north", "South", "Жук", "a😀b", new string('m', 300), new string('c', 70_000)];

    public static IEnumerable<TheoryDataRow<string>> Encodings => TextSchema.Encodings;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Counts_equal_an_independent_tally_during_and_after_a_churn_of_writes_and_deletes(string encoding)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Of(encoding));
        var churn = new Churn(database, new TextTally(encoding), seed: 13);

        await churn.RunAsync(operations: 2_000, checkEvery: 100);

        EntryCounts.ExpectEqual(churn.Tally, database);
        churn.Tally.ExpectHeldBy(database);

        await churn.DeleteEverythingAsync();

        EntryCounts.ExpectEqual(churn.Tally, database);
        churn.Tally.ExpectHeldBy(database);
        Assert.Equal(0, database.Counters.TextEntries);
    }

    [Fact]
    public async Task Counts_equal_the_tally_while_a_held_read_keeps_the_replaced_and_deleted_roots_retired()
    {
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8);

        try
        {
            var churn = new Churn(database, new TextTally("Utf8"), seed: 31);
            var places = database.Set<TextSchema.Place>();

            await churn.RunAsync(operations: 200, checkEvery: 0);
            await churn.EnsurePlaceAsync(0);
            churn.Tally.ExpectHeldBy(database);

            using var held = await HeldRead.StartAsync(database, () => places.Get(0L) is { } place ? place.Number : null);

            churn.Tally.Retaining = true;
            await churn.RunAsync(operations: 400, checkEvery: 50);

            EntryCounts.ExpectEqual(churn.Tally, database);
            churn.Tally.ExpectHeldBy(database);

            await held.ReleaseAsync();
            churn.Tally.ReadLeft();

            EntryCounts.ExpectEqual(churn.Tally, database);
            churn.Tally.ExpectHeldBy(database);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Counts_equal_the_tally_while_writes_are_prepared_and_not_published_and_a_withdrawn_one_takes_its_references_with_it()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs { CommitStorage = storage });

        try
        {
            var places = database.Set<TextSchema.Place>();
            var churn = new Churn(database, new TextTally("Utf8"), seed: 47);

            await churn.RunAsync(operations: 300, checkEvery: 0);

            for (var id = 0L; id < 4; id++)
            {
                await churn.EnsurePlaceAsync(id);
            }

            churn.Tally.ExpectHeldBy(database);
            storage.Hold();

            // A replace held in the storage step, and behind it writes of other entities, prepared: replaces
            // and inserts to texts the database holds and to texts it does not.
            var heldWrite = Prepared(churn, new TextSchema.Place { Id = 0, Region = "held in the storage step", Zone = "North", Label = "label" });
            var held = places.ReplaceAsync(heldWrite).AsTask();
            await storage.NextAsync();

            var queued = new List<(TextSchema.Place Values, Task<LatticeWriteResult> Write)>();
            using var cancellation = new CancellationTokenSource();
            var withdrawn = Prepared(churn, new TextSchema.Place { Id = Places + 1, Region = "withdrawn", Zone = "South", Label = "label" });
            var cancelled = places.InsertAsync(withdrawn, cancellation.Token).AsTask();

            foreach (var values in new[]
            {
                new TextSchema.Place { Id = 1, Region = "North", Zone = "prepared 1", Label = "label" },
                new TextSchema.Place { Id = 2, Number = 7, Region = "South", Zone = null, Label = new string('l', 100) },
                new TextSchema.Place { Id = Places + 2, Region = "prepared 2", Zone = "prepared 2", Label = "label" },
            })
            {
                queued.Add((Prepared(churn, values), places.UpsertAsync(values).AsTask()));
            }

            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == queued.Count + 2, "every write is admitted");

            EntryCounts.ExpectEqual(churn.Tally, database);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            churn.Withdrawn(withdrawn);

            EntryCounts.ExpectEqual(churn.Tally, database);
            Assert.False(EntryCounts.Of(database, "Utf8").ContainsKey("withdrawn"));

            storage.ReleaseAll();
            await held.WaitAsync(Bound);
            await Task.WhenAll(queued.Select(write => write.Write)).WaitAsync(Bound);
            churn.Published();

            EntryCounts.ExpectEqual(churn.Tally, database);
            churn.Tally.ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The values of a write the test is about to submit and hold prepared, told to the tally.
    private static TextSchema.Place Prepared(Churn churn, TextSchema.Place values)
    {
        churn.Prepared(values);

        return values;
    }

    // A deterministic churn of the entities of TextSchema, told to the tally as each write returns.
    private sealed class Churn(EngineDatabase database, TextTally tally, int seed)
    {
        private readonly Random _random = new(seed);

        private readonly Dictionary<long, TextSchema.Place> _places = [];

        private readonly Dictionary<long, TextSchema.Depot> _depots = [];

        private readonly List<TextSchema.Place> _prepared = [];

        private long _number;

        public TextTally Tally => tally;

        // Runs the operations, and compares the counts and the arena with the tally after every so many
        // of them; 0 compares nothing.
        public async Task RunAsync(int operations, int checkEvery)
        {
            for (var operation = 1; operation <= operations; operation++)
            {
                if (_random.Next(4) == 0)
                {
                    await DepotStepAsync();
                }
                else
                {
                    await PlaceStepAsync(_random.Next(Places));
                }

                if (checkEvery > 0 && operation % checkEvery == 0)
                {
                    EntryCounts.ExpectEqual(tally, database);
                    tally.ExpectHeldBy(database);
                }
            }
        }

        // The place of the id exists afterwards.
        public async Task EnsurePlaceAsync(long id)
        {
            if (!_places.ContainsKey(id))
            {
                var values = NewPlace(id);

                await WrittenAsync(database.Set<TextSchema.Place>().InsertAsync(values));
                Written(values);
            }
        }

        public void Prepared(TextSchema.Place values)
        {
            _prepared.Add(values);
            tally.Prepared(values);
        }

        // The prepared write of these values was withdrawn.
        public void Withdrawn(TextSchema.Place values)
        {
            _prepared.Remove(values);
            tally.Withdrawn(values);
        }

        // The prepared writes that were not withdrawn were published, in their order.
        public void Published()
        {
            foreach (var values in _prepared)
            {
                _places[values.Id] = values;
            }

            _prepared.Clear();
            tally.Published();
        }

        public async Task DeleteEverythingAsync()
        {
            foreach (var id in _places.Keys.ToArray())
            {
                await WrittenAsync(database.Set("Place").DeleteAsync(id));
                _places.Remove(id);
                tally.Deleted("Place", id);
            }

            foreach (var id in _depots.Keys.ToArray())
            {
                await WrittenAsync(database.Set<TextSchema.Depot>().DeleteAsync(id));
                _depots.Remove(id);
                tally.Deleted("Depot", id);
            }
        }

        private async Task PlaceStepAsync(long id)
        {
            var places = database.Set<TextSchema.Place>();
            var records = database.Set("Place");
            var byName = _random.Next(2) == 0;
            var choice = _random.Next(10);

            if (!_places.TryGetValue(id, out var current))
            {
                var inserted = NewPlace(id);

                await WrittenAsync(choice < 5
                    ? (byName ? records.InsertAsync(TextSchema.RecordOf(records.EntityType, inserted)) : places.InsertAsync(inserted))
                    : (byName ? records.UpsertAsync(TextSchema.RecordOf(records.EntityType, inserted)) : places.UpsertAsync(inserted)));
                Written(inserted);

                return;
            }

            if (choice < 2)
            {
                await WrittenAsync(byName ? records.DeleteAsync(id) : places.DeleteAsync(id));
                _places.Remove(id);
                tally.Deleted("Place", id);

                return;
            }

            // A replace that keeps the texts, one to other texts, or an upsert to other texts.
            var next = choice < 4
                ? new TextSchema.Place { Id = id, Number = ++_number, Region = current.Region, Zone = current.Zone, Label = current.Label }
                : NewPlace(id);

            await WrittenAsync(choice < 7
                ? (byName ? records.ReplaceAsync(TextSchema.RecordOf(records.EntityType, next)) : places.ReplaceAsync(next))
                : (byName ? records.UpsertAsync(TextSchema.RecordOf(records.EntityType, next)) : places.UpsertAsync(next)));
            Written(next);
        }

        private async Task DepotStepAsync()
        {
            var depots = database.Set<TextSchema.Depot>();
            var id = _random.Next(Depots);

            if (_depots.ContainsKey(id) && _random.Next(3) == 0)
            {
                await WrittenAsync(depots.DeleteAsync((long)id));
                _depots.Remove(id);
                tally.Deleted("Depot", id);

                return;
            }

            var depot = new TextSchema.Depot { Id = id, Area = Texts[_random.Next(Texts.Length)] };

            await WrittenAsync(depots.UpsertAsync(depot));
            _depots[id] = depot;
            tally.Written(depot);
        }

        private TextSchema.Place NewPlace(long id) => new()
        {
            Id = id,
            Number = ++_number,
            Region = Texts[_random.Next(Texts.Length)],
            Zone = _random.Next(4) == 0 ? null : Texts[_random.Next(Texts.Length)],
            Label = _random.Next(3) == 0 ? new string('l', 100) : "label",
        };

        private void Written(TextSchema.Place values)
        {
            _places[values.Id] = values;
            tally.Written(values);
        }

        // A write that has to succeed within the bound: a writer that waited for a held read would not.
        private static async Task WrittenAsync(ValueTask<LatticeWriteResult> write) => await write.AsTask().WaitAsync(Bound);
    }
}
