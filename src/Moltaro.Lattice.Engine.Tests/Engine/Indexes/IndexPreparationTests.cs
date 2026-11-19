using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Index nodes are prepared before the commit, and nothing grows inside a publication (docs/design/
// ordered-index.md, "The reservation"; milestone contract, "Index preparation and traversal"): in the hold
// that prepares a write, each index it changes reserves the nodes its publication can take as prepared
// blocks of the arena; a prepared, unpublished write holds them while the storage step is busy, and no
// reader can reach them; a write withdrawn before its publication gives them back. While p inserts and q
// removes wait, an index holds at most p · (2H + 1) + q · (H + 1) nodes, H = h + 1 + (p - 1) / I, h the
// height of the tree and I the separators its inner node holds before it is full.
// - The storage step of the test (HeldCommitStorage) holds a replace of an entity in no index while
//   writes of Counted wait behind it: what the arena holds prepared beyond what the same writes of
//   Uncounted hold, which has no index, is the pool of ByNumber: whole nodes of 1 KiB, more than none and
//   no more than the bound, for 10 entities in one leaf (h = 1) and 40 separators to an inner node of
//   16-byte entries. A search finds none of the waiting inserts. Once the storage step lets them go, they
//   are published without the arena obtaining memory, and nothing is left prepared.
// - A write withdrawn while it waits, because its token was cancelled, leaves the prepared bytes as the
//   writes before it held them, and nothing of it is found or left in the arena.
// - A write cancelled while it is being prepared, held there by the allocator of the test when its large
//   value needs a new segment (HeldPreparation), leaves the same, its new text entries included.
// A faulted write is stated by IndexCloseTests, a write the arena cannot hold by RefusedReservationTests.
public sealed class IndexPreparationTests
{
    private const int Existing = 10;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    public static IEnumerable<TheoryDataRow<int, int>> Waiting =>
    [
        new(1, 0),
        new(5, 3),
    ];

    [Theory(Skip = SkipReason.BeforeSprint14)]
    [MemberData(nameof(Waiting))]
    public async Task Prepared_unpublished_writes_hold_whole_nodes_within_the_bound_no_search_reaches_and_their_publication_obtains_nothing(int inserts, int deletes)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });

        try
        {
            var counted = database.Set<IndexSchema.Counted>();
            var uncounted = database.Set<IndexSchema.Uncounted>();

            for (var id = 1L; id <= Existing; id++)
            {
                await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
                await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = id, Number = id });
            }

            // The writes on the entity type without an index, then the same ones with it.
            var withoutIndex = await PreparedBytesOfAsync(database, storage, Writes(uncounted, id => new IndexSchema.Uncounted { Id = id, Number = id }, inserts, deletes));
            var pending = await HoldAsync(database, storage, Writes(counted, id => new IndexSchema.Counted { Id = id, Number = id }, inserts, deletes));
            var pool = database.Counters.Arena.PreparedBytes - withoutIndex;
            var height = 1;
            var separators = 40;
            var reach = height + 1 + ((inserts - 1) / separators);
            var bound = (inserts * ((2 * reach) + 1)) + (deletes * (reach + 1));

            Assert.True(pool > 0, "The waiting writes reserved no node of the index.");
            Assert.Equal(0, pool % IndexSchema.NodeBytes);
            Assert.True(pool <= bound * IndexSchema.NodeBytes, $"The index reserved {pool / IndexSchema.NodeBytes} nodes for {inserts} inserts and {deletes} deletes, beyond the bound of {bound}.");

            for (var id = 101L; id < 101 + inserts; id++)
            {
                Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(id)));
                Assert.Null(counted.Get(id));
            }

            var obtained = allocator.Obtained;

            storage.ReleaseAll();
            await Task.WhenAll(pending).WaitAsync(Bound);

            Assert.Equal(obtained, allocator.Obtained);
            Assert.Equal(0, database.Counters.Arena.PreparedBytes);

            for (var id = 101L; id < 101 + inserts; id++)
            {
                Assert.Equal(id, Assert.Single(counted.Index("ByNumber").Find(IndexKey.Equal(id))).Id);
            }

            for (var id = 1L; id <= deletes; id++)
            {
                Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(id)));
            }
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Write_withdrawn_while_it_waits_gives_back_every_node_it_reserved_and_leaves_nothing()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs { CommitStorage = storage });

        try
        {
            var counted = database.Set<IndexSchema.Counted>();
            var uncounted = database.Set<IndexSchema.Uncounted>();

            for (var id = 1L; id <= Existing; id++)
            {
                await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
            }

            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = 1, Number = 1 });
            database.DrainRetired();
            var before = database.Counters;

            storage.Hold();
            var first = uncounted.ReplaceAsync(new IndexSchema.Uncounted { Id = 1, Number = 2 }).AsTask();
            await storage.NextAsync();
            var alone = (await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 1, "the first write is admitted")).Arena.PreparedBytes;

            using var cancellation = new CancellationTokenSource();
            var withdrawn = counted.InsertAsync(new IndexSchema.Counted { Id = 100, Number = 100 }, cancellation.Token).AsTask();
            var both = await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "both writes are admitted");

            Assert.True(both.Arena.PreparedBytes > alone + IndexSchema.CountedFixedSize, "The waiting insert reserved no node of the index.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => withdrawn.WaitAsync(Bound));

            Assert.Equal(alone, database.Counters.Arena.PreparedBytes);

            storage.ReleaseAll();
            await first.WaitAsync(Bound);
            database.DrainRetired();
            var after = database.Counters;

            Assert.Equal(0, after.Arena.PreparedBytes);
            Assert.Equal((before.Arena.LiveBytes, before.Arena.ClassSlackBytes, before.RootEntries), (after.Arena.LiveBytes, after.Arena.ClassSlackBytes, after.RootEntries));
            Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(100L)));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact(Skip = SkipReason.BeforeSprint14)]
    public async Task Write_cancelled_while_it_is_being_prepared_gives_back_every_node_and_text_it_took()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });

        try
        {
            var ranked = database.Set<IndexSchema.Ranked>();
            var uncounted = database.Set<IndexSchema.Uncounted>();

            for (var id = 1L; id <= Existing; id++)
            {
                await ranked.InsertAsync(new IndexSchema.Ranked { Id = id, Number = id, Login = $"login {id}", Region = "North" });
            }

            await uncounted.InsertAsync(new IndexSchema.Uncounted { Id = 1, Number = 1 });
            database.DrainRetired();
            var before = database.Counters;
            var texts = database.TextReferences().Count;

            storage.Hold();
            var first = uncounted.ReplaceAsync(new IndexSchema.Uncounted { Id = 1, Number = 2 }).AsTask();
            await storage.NextAsync();
            var alone = (await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 1, "the first write is admitted")).Arena.PreparedBytes;

            // A Note of 5 MiB outgrows the segment the database has; a long Login and a new Region bring
            // text entries, which are shared with the index.
            using var cancellation = new CancellationTokenSource();
            using var held = HeldPreparation.Arm(allocator);
            var withdrawn = new IndexSchema.Ranked { Id = 100, Number = 100, Login = new string('l', 100), Region = new string('r', 100), Note = new string('n', 5 * 1024 * 1024) };
            var cancelled = Task.Run(() => ranked.InsertAsync(withdrawn, cancellation.Token).AsTask());

            await held.HeldAsync();
            var cancelling = cancellation.CancelAsync();
            held.Release();
            await cancelling.WaitAsync(Bound);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));

            Assert.Equal(alone, database.Counters.Arena.PreparedBytes);
            Assert.Equal(texts, database.TextReferences().Count);

            storage.ReleaseAll();
            await first.WaitAsync(Bound);
            database.DrainRetired();
            var after = database.Counters;

            Assert.Equal(0, after.Arena.PreparedBytes);
            Assert.Equal((before.Arena.LiveBytes, before.Arena.ClassSlackBytes, before.RootEntries, before.TextEntries), (after.Arena.LiveBytes, after.Arena.ClassSlackBytes, after.RootEntries, after.TextEntries));
            Assert.Empty(ranked.Index("ByNumber").Find(IndexKey.Equal(100L)));
            Assert.Empty(ranked.Index("ByLogin").Find(IndexKey.Equal(new string('l', 100))));
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The inserts of ids from 101 on and the deletes of ids from 1 on, of the set given.
    private static Func<IEnumerable<Task>> Writes<TEntity>(ILatticeSet<TEntity> set, Func<long, TEntity> entityOf, int inserts, int deletes)
        where TEntity : class
    {
        return () =>
        {
            var writes = new List<Task>();

            for (var id = 101L; id < 101 + inserts; id++)
            {
                writes.Add(set.InsertAsync(entityOf(id)).AsTask());
            }

            for (var id = 1L; id <= deletes; id++)
            {
                writes.Add(set.DeleteAsync(id).AsTask());
            }

            return writes;
        };
    }

    // Holds the storage step with a replace of an entity in no index, submits the writes and returns once
    // they are all admitted and prepared: the writes, and the replace that holds them.
    private static async Task<Task[]> HoldAsync(EngineDatabase database, HeldCommitStorage storage, Func<IEnumerable<Task>> writes)
    {
        storage.Hold();

        var first = database.Set<IndexSchema.Uncounted>().ReplaceAsync(new IndexSchema.Uncounted { Id = Existing, Number = -1 }).AsTask();

        await storage.NextAsync();

        Task[] pending = [first, .. writes()];

        await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == pending.Length, "every write is admitted");

        return pending;
    }

    // What the arena holds prepared while the writes wait behind the held storage step; the writes are then
    // let go and complete.
    private static async Task<long> PreparedBytesOfAsync(EngineDatabase database, HeldCommitStorage storage, Func<IEnumerable<Task>> writes)
    {
        var pending = await HoldAsync(database, storage, writes);
        var prepared = database.Counters.Arena.PreparedBytes;

        storage.ReleaseAll();
        await Task.WhenAll(pending).WaitAsync(Bound);

        return prepared;
    }
}
