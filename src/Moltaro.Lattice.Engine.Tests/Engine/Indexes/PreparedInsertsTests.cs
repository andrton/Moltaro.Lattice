using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Inserts prepared at once each reserve room in the primary index beside the others, so that every
// one finds its room when it is published, in whatever order, and publication obtains nothing (brief
// of Epic 1, sprint S09, "Who may touch what": an insert makes room in the index while it is prepared,
// which may grow it, and publication only installs what was prepared; review round 1 of S09). The keys
// all share one hash under the seed 0 (CollidingKeys), so they fall on one chain of buckets of the
// index: beyond the seven entries a bucket holds, every seven of them need an overflow bucket, which
// the reservations have to provide for all of them together, and the main table, which grows by the
// count, grows while they are prepared. The storage step of the test (HeldCommitStorage) holds a
// replace while 50 inserts, four replaces and three deletes of keys of that chain are prepared; ten of
// the inserts are withdrawn before their dispatch, which gives back what they reserved. Then:
// - every write that was not withdrawn completes, and the database stays Ready;
// - from the moment they are all prepared to the moment the last has returned the primary index does
//   not grow and the arena obtains no segment;
// - after a drain the counters equal what the entities hold, and every key reads as its last write
//   left it.
public sealed class PreparedInsertsTests
{
    private const int Existing = 10;

    private const int Inserts = 50;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // The bytes of the slot of one entity of Clash: a root of its fixed part alone, the header and two
    // Int64 fields.
    private static readonly StoredBytes OneClash = StoredBytes.Of(32 + 8 + 8);

    [Fact]
    public async Task Inserts_of_one_chain_prepared_at_once_find_their_room_and_their_publication_obtains_nothing()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(ClashSchema.Schema, new EngineTestInputs { IndexSeed = 0, CommitStorage = storage });
        var clashes = database.Set<ClashSchema.Clash>();
        var keys = CollidingKeys.Of(Existing + Inserts);

        try
        {
            for (var index = 0; index < Existing; index++)
            {
                await clashes.InsertAsync(ClashSchema.Of(keys[index]));
            }

            var grownFrom = database.Counters.PrimaryIndexBytes;

            storage.Hold();

            var held = clashes.ReplaceAsync(new ClashSchema.Clash { Id = keys[0], Number = -1 }).AsTask();
            await storage.NextAsync();

            // Every fifth insert has a token, and is withdrawn.
            var tokens = new List<CancellationTokenSource>();
            var inserts = new List<Task<LatticeWriteResult>>();
            var withdrawn = new List<Task<LatticeWriteResult>>();

            for (var index = Existing; index < Existing + Inserts; index++)
            {
                if (index % 5 == 0)
                {
                    var cancellation = new CancellationTokenSource();

                    tokens.Add(cancellation);
                    withdrawn.Add(clashes.InsertAsync(ClashSchema.Of(keys[index]), cancellation.Token).AsTask());
                }
                else
                {
                    inserts.Add(clashes.InsertAsync(ClashSchema.Of(keys[index])).AsTask());
                }
            }

            var replaces = Enumerable.Range(1, 4).Select(index => clashes.ReplaceAsync(new ClashSchema.Clash { Id = keys[index], Number = -1 }).AsTask()).ToList();
            var deletes = Enumerable.Range(5, 3).Select(index => clashes.DeleteAsync(keys[index]).AsTask()).ToList();

            var prepared = await CountersWait.PreparedAsync(database, SlotBytes(1 + Inserts + replaces.Count));

            Assert.True(prepared.PrimaryIndexBytes > grownFrom, "The primary index did not grow while the inserts were prepared.");

            foreach (var cancellation in tokens)
            {
                await cancellation.CancelAsync();
            }

            foreach (var write in withdrawn)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Bound));
            }

            Assert.False(held.IsCompleted, "The replace held in the storage step completed before the withdrawn inserts ended.");

            var before = database.Counters;

            Assert.Equal(SlotBytes(1 + Inserts - withdrawn.Count + replaces.Count), before.Arena.PreparedBytes);

            storage.ReleaseAll();

            var results = await Task.WhenAll([held, .. inserts, .. replaces, .. deletes]).WaitAsync(Bound);
            var after = database.Counters;

            Assert.All(results, result => Assert.True(result.Changed));
            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            Assert.Equal(before.PrimaryIndexBytes, after.PrimaryIndexBytes);
            Assert.True(after.Arena.Segments <= before.Arena.Segments, "A publication obtained a segment.");

            foreach (var cancellation in tokens)
            {
                cancellation.Dispose();
            }

            // What the keys hold now: the first ten replaced or deleted, the inserts that ran.
            for (var index = 0; index < Existing + Inserts; index++)
            {
                long? expected = index switch
                {
                    < 5 => -1,
                    < 8 => null,
                    < Existing => keys[index],
                    _ when index % 5 == 0 => null,
                    _ => keys[index],
                };

                Assert.Equal(expected, clashes.Get(keys[index])?.Number);
            }

            var live = Existing - deletes.Count + inserts.Count;

            database.DrainRetired();
            var drained = database.Counters;

            Assert.Equal(live, drained.RootEntries);
            Assert.Equal(live * OneClash.LiveBytes, drained.Arena.LiveBytes);
            Assert.Equal(live * OneClash.ClassSlackBytes, drained.Arena.ClassSlackBytes);
            Assert.Equal(0, drained.Arena.PreparedBytes);
            Assert.Equal(0, drained.Arena.RetiredBytes);
            Assert.Equal(0, drained.RetiredIndexBytes);
            Assert.Equal(0, drained.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static long SlotBytes(int entities) => entities * (OneClash.LiveBytes + OneClash.ClassSlackBytes);
}
