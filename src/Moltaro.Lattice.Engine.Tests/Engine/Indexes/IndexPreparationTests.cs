using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// Index nodes are prepared before the commit, and nothing grows inside a publication (docs/design/
// ordered-index.md, "The reservation"; milestone contract, "Index preparation and traversal"): in the hold
// that prepares a write, each index it changes reserves the nodes its publication can take as prepared
// blocks of the arena; a prepared, unpublished write holds them while the storage step is busy, and no
// reader can reach them; a write withdrawn before its publication gives them back. While p inserts and q
// removes wait, an index holds at most p · (2Hi + 1) + q · (Hr + 1) nodes, with Hi = h + ⌈(p − 1) / I⌉ and
// Hr = h + ⌈p / I⌉, h the height of the tree and I the separators its inner node holds before it is full.
// Here 10 entities lie in one leaf (h = 1), and an inner node holds 40 separators of 16-byte entries.
// - The storage step of the test (HeldCommitStorage) holds a replace of an entity in no index while
//   writes of Counted wait behind it, from one insert to so many that the tree gains a level when they
//   are published: what the arena holds prepared beyond what the same writes of Uncounted hold, which has
//   no index, is the pool of ByNumber, whole nodes of 1 KiB, more than none and no more than the bound. A
//   search finds none of the waiting inserts. Then the arena is filled to its last slot of 1 KiB, the size
//   of a node, by entities whose fixed part takes such a slot, until it refuses one; only then does the
//   storage step let the writes go. Every one of them is published with no memory left to take, the arena
//   obtains nothing, nothing is left prepared, and the entries are those of the writes.
// - Inserts that wait while some of them, every third, are withdrawn: the pool stays within the bound of
//   the writes still waiting after the withdrawals, and once the rest are published, the tree has grown and
//   its entries are those of the inserts that ran.
// - A write withdrawn while it waits, because its token was cancelled, leaves the prepared bytes as the
//   writes before it held them, and nothing of it is found or left in the arena.
// - A write cancelled while it is being prepared, held there by the allocator of the test when its large
//   value needs a new segment (HeldPreparation), leaves the same, its new text entries included.
// A faulted write is stated by IndexCloseTests, a write the arena cannot hold by RefusedReservationTests.
public sealed class IndexPreparationTests
{
    private const int Existing = 10;

    // The separators of 16-byte entries an inner node of 1 KiB holds before it is full: after the block
    // header and the node header of 8 bytes, a child of 8 bytes for each separator and one more
    // (docs/design/ordered-index.md, "Nodes in the arena").
    private const int Separators = (IndexSchema.NodeBytes - BlockHeader.Size - 8 - 8) / (16 + 8);

    // The entries of 16 bytes a leaf of 1 KiB holds.
    private const int FullLeaf = (IndexSchema.NodeBytes - BlockHeader.Size - 8) / 16;

    // The arena of the test that fills it: regions of 64 KiB, each a segment of its own.
    private const int Regions = 48;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    public static IEnumerable<TheoryDataRow<int, int>> Waiting =>
    [
        new(1, 0),
        new(5, 3),
        new(150, 3),
    ];

    [Theory]
    [MemberData(nameof(Waiting))]
    public async Task Prepared_unpublished_writes_hold_whole_nodes_within_the_bound_and_are_published_with_no_memory_left_to_take(int inserts, int deletes)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(FillerSchema, new EngineTestInputs
        {
            Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = (long)Regions * Arena.MaxBlockSize },
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
            var bound = BoundOf(inserts, deletes, height: 1);

            Assert.True(pool > 0, "The waiting writes reserved no node of the index.");
            Assert.Equal(0, pool % IndexSchema.NodeBytes);
            Assert.True(pool <= bound * IndexSchema.NodeBytes, $"The index reserved {pool / IndexSchema.NodeBytes} nodes for {inserts} inserts and {deletes} deletes, beyond the bound of {bound}.");

            for (var id = 101L; id < 101 + inserts; id++)
            {
                Assert.Empty(counted.Index("ByNumber").Find(IndexKey.Equal(id)));
                Assert.Null(counted.Get(id));
            }

            var fillers = await FillAsync(database);
            var full = database.Counters;

            // The premise: the arena holds all it may, and no region is free.
            Assert.Equal((Regions, 0), (full.Arena.Segments, full.Arena.FreeRegions));

            var obtained = allocator.Obtained;

            storage.ReleaseAll();
            await Task.WhenAll(pending).WaitAsync(Bound);
            await ExpectPublishedOrRefusedForMemoryAsync(fillers);

            Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);
            Assert.Equal(obtained, allocator.Obtained);
            Assert.Equal(0, database.Counters.Arena.PreparedBytes);

            var live = Enumerable.Range(1 + deletes, Existing - deletes).Concat(Enumerable.Range(101, inserts)).Select(id => ((object?)(long)id, (long)id));

            IndexTally.ExpectEntries(database, "Counted", "ByNumber", live, IndexTally.ByNumber);

            if (Existing - deletes + inserts > FullLeaf)
            {
                Assert.True(IndexContents.Of(database, "Counted", "ByNumber").Height > 1, "The published inserts left the tree one leaf.");
            }
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Tree_that_grows_while_inserts_wait_keeps_its_reservation_within_the_bound_as_some_are_withdrawn_and_the_rest_published()
    {
        const int Inserts = 200;

        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs { CommitStorage = storage });

        try
        {
            var counted = database.Set<IndexSchema.Counted>();
            var root = Arena.BlockSizeFor(IndexSchema.CountedFixedSize - BlockHeader.Size);

            for (var id = 1L; id <= Existing; id++)
            {
                await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
            }

            await database.Set<IndexSchema.Uncounted>().InsertAsync(new IndexSchema.Uncounted { Id = Existing, Number = Existing });

            // Every third insert has a token of its own and is withdrawn while the others wait.
            var tokens = new List<CancellationTokenSource>();
            var withdrawn = new List<Task>();
            var kept = new List<long>();
            var pending = await HoldAsync(database, storage, () =>
            {
                var writes = new List<Task>();

                for (var id = 101L; id < 101 + Inserts; id++)
                {
                    if (id % 3 == 0)
                    {
                        var cancellation = new CancellationTokenSource();

                        tokens.Add(cancellation);
                        withdrawn.Add(counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id }, cancellation.Token).AsTask());
                    }
                    else
                    {
                        kept.Add(id);
                        writes.Add(counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id }).AsTask());
                    }
                }

                return [.. writes, .. withdrawn];
            });

            var all = database.Counters.Arena.PreparedBytes - ((1L + Inserts) * root);

            Assert.True(all > 0 && all <= BoundOf(Inserts, 0, height: 1) * IndexSchema.NodeBytes, $"{Inserts} waiting inserts reserved {all} bytes of nodes.");

            foreach (var cancellation in tokens)
            {
                await cancellation.CancelAsync();
            }

            foreach (var write in withdrawn)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(Bound));
            }

            var left = database.Counters.Arena.PreparedBytes - ((1L + kept.Count) * root);

            Assert.True(left > 0 && left <= BoundOf(kept.Count, 0, height: 1) * IndexSchema.NodeBytes, $"{kept.Count} inserts still waiting hold {left} bytes of nodes.");

            storage.ReleaseAll();
            await Task.WhenAll(pending.Except(withdrawn)).WaitAsync(Bound);

            Assert.Equal(0, database.Counters.Arena.PreparedBytes);
            Assert.True(IndexContents.Of(database, "Counted", "ByNumber").Height > 1, "The published inserts left the tree one leaf.");
            IndexTally.ExpectEntries(database, "Counted", "ByNumber", Enumerable.Range(1, Existing).Select(id => (long)id).Concat(kept).Select(id => ((object?)id, id)), IndexTally.ByNumber);

            foreach (var cancellation in tokens)
            {
                cancellation.Dispose();
            }
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
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

    [Fact]
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

    // The bound of docs/design/ordered-index.md, "The reservation", in nodes, for p inserts and q removes
    // waiting on a tree of the height given.
    private static long BoundOf(int inserts, int removes, int height)
    {
        var insertHeight = height + CeilingOf(inserts - 1, Separators);
        var removeHeight = height + CeilingOf(inserts, Separators);

        return ((long)inserts * ((2 * insertHeight) + 1)) + ((long)removes * (removeHeight + 1));
    }

    private static int CeilingOf(int dividend, int divisor) => dividend <= 0 ? 0 : (dividend + divisor - 1) / divisor;

    // Fills the arena while the storage step holds the writes: inserts of Filler, whose fixed part takes a
    // slot of 1 KiB, the size of a node, each prepared and waiting behind the held writes, until the arena
    // refuses one and that write begins to wait for memory (EngineDatabase.HoldNextRefusedWriteWait). The
    // fillers, the refused one among them.
    private static async Task<List<Task>> FillAsync(EngineDatabase database)
    {
        var fillers = database.Set("Filler");
        var refused = RefusedWriteWait.Begins(database);
        var writes = new List<Task>();

        for (var id = 1L; id <= 4_000 && !refused.IsCompleted; id++)
        {
            var filler = new LatticeRecord(fillers.EntityType) { ["Id"] = id };

            foreach (var field in fillers.EntityType.Fields.Where(field => field.Name != "Id"))
            {
                filler[field.Name] = 0m;
            }

            writes.Add(fillers.InsertAsync(filler).AsTask());
        }

        await refused.WaitAsync(Bound);

        return writes;
    }

    // Each filler was published, or refused for memory once the writes before it had been published.
    private static async Task ExpectPublishedOrRefusedForMemoryAsync(List<Task> fillers)
    {
        foreach (var filler in fillers)
        {
            var refusal = await Record.ExceptionAsync(() => filler.WaitAsync(Bound));

            if (refusal is not null)
            {
                Assert.Equal(LatticeErrorEnum.WriteRejected, Assert.IsType<LatticeException>(refusal).Error);
            }
        }
    }

    // The schema of IndexSchema with the entity type Filler: the key Id and 61 Decimals, a fixed part of
    // 32 + 8 + 61 · 16 = 1,016 bytes, which takes a slot of 1 KiB.
    private static LatticeSchema FillerSchema { get; } = IndexSchema.With("Utf8", FillerType());

    private static string FillerType()
    {
        var decimals = string.Concat(Enumerable.Range(0, 61).Select(index => $$""", { "Id": {{100 + index}}, "Name": "Decimal{{index}}", "Type": "Decimal" }"""));

        return $$"""{ "Id": 10, "Name": "Filler", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }{{decimals}} ], "PrimaryKey": [1] },""";
    }
}
