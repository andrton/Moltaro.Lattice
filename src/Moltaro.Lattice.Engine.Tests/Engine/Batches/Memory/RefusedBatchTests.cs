using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Memory;

// A write batch that the memory budget cannot hold is refused with WriteRejected and Limit Memory before
// anything is written, and the database is as it was (docs/design/ordered-index.md, "The reservation"): no
// entity, no entry of an index, no root entry and no position of it is left, and the arena holds no prepared
// byte of it. Every pool of an index gives back what the batch took from the arena when the batch is
// withdrawn, and the standing reserve with it, keeping the lesser bound of the writes still prepared
// (IndexReserve); before a refusal every index gives back what its pool holds above that bound, so that the
// reserves are never the reason of a refusal, and the prepared bytes are then at most what they were. The
// same holds for a batch that waited behind a write of one of its entities and does not fit once that write
// is published, and for a batch whose token is cancelled at the wait of the resolution of its refusal, where
// the limit is looked at last.
// UniqueSchema, whose Account holds a Data of the size each test gives, on an arena of four segments of
// 4 MiB, or two, given through the inputs of a test (exception X3); every wait is bounded.
public sealed class RefusedBatchTests
{
    private const int Mebibyte = 1024 * 1024;

    private static readonly ArenaOptions FourSegments = new() { MemoryLimit = 4L * new ArenaOptions().SegmentSize };

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * new ArenaOptions().SegmentSize };

    [Fact]
    public async Task Batch_the_arena_cannot_hold_leaves_the_entities_the_indexes_the_root_entries_and_the_position_as_they_were()
    {
        await using var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = FourSegments });
        var accounts = database.Set<UniqueSchema.Account>();
        await accounts.InsertAsync(UniqueSchema.Of(1, "a", data: Mebibyte));
        database.DrainRetired();
        var before = database.Counters;
        var entries = IndexContents.Of(database, "Account", "ByLogin").Entries;

        var batch = database.CreateWriteBatch();
        batch.Replace(UniqueSchema.Of(1, "moved", note: "batch"));

        for (var id = 2L; id <= 6; id++)
        {
            batch.Insert(UniqueSchema.Of(id, $"new {id}", data: 4 * Mebibyte));
        }

        var refused = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);

        database.DrainRetired();
        ExpectAsBefore(database, before, entries);
        Assert.True(database.Counters.Arena.PreparedBytes <= before.Arena.PreparedBytes);
        Assert.Equal("a", accounts.Get(1L)!.Login);

        Assert.True((await accounts.InsertAsync(UniqueSchema.Of(2, "after", data: Mebibyte))).Changed);
    }

    [Fact(Skip = SkipReason.WriteBatch)]
    public async Task Batch_withdrawn_before_its_dispatch_gives_back_exactly_what_it_took()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = FourSegments, Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            storage.Hold();

            var first = accounts.InsertAsync(UniqueSchema.Of(10, "first")).AsTask();
            await storage.NextAsync();
            var before = database.Counters;
            var entries = IndexContents.Of(database, "Account", "ByLogin").Entries;

            using var cancellation = new CancellationTokenSource();
            var batch = database.CreateWriteBatch();

            for (var id = 2L; id <= 200; id++)
            {
                batch.Insert(UniqueSchema.Of(id, $"new {id}"));
            }

            batch.Replace(UniqueSchema.Of(1, "moved"));

            var batched = batch.CommitAsync(cancellation.Token).AsTask();

            Assert.True(database.Counters.Arena.PreparedBytes > before.Arena.PreparedBytes, "The batch prepared nothing.");

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batched.WaitAsync(Bound));

            var after = database.Counters;
            var reserveAboveTheInsert = IndexReserve.BytesOf(database, "Account") - IndexReserve.LesserBoundBytesOf(database, "Account", inserts: 1, removes: 0);

            Assert.Equal(
                (before.Arena.LiveBytes, before.Arena.ClassSlackBytes, before.Arena.PreparedBytes - reserveAboveTheInsert, before.Arena.RetiredBytes),
                BytesOf(after.Arena));
            Assert.Equal(before.RootEntries, after.RootEntries);
            Assert.Equal(EntriesOf(entries), EntriesOf(IndexContents.Of(database, "Account", "ByLogin").Entries));

            storage.ReleaseAll();
            await first.WaitAsync(Bound);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The batch replaces the entity the held write inserts, so it waits for it; once that write is published,
    // the batch does not fit beside it.
    [Fact]
    public async Task Batch_that_waited_behind_a_write_of_its_entity_and_does_not_fit_after_it_changes_nothing()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = FourSegments, Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
            storage.Hold();

            var holder = accounts.InsertAsync(UniqueSchema.Of(2, "b", data: 10 * Mebibyte)).AsTask();
            await storage.NextAsync();

            var batch = database.CreateWriteBatch();
            batch.Replace(UniqueSchema.Of(2, "b", data: 8 * Mebibyte));
            batch.Insert(UniqueSchema.Of(3, "c"));
            var batched = batch.CommitAsync().AsTask();

            Assert.False(batched.IsCompleted);

            storage.ReleaseAll();

            var published = await holder.WaitAsync(Bound);
            var refused = await Assert.ThrowsAsync<LatticeException>(() => batched.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(published.Position, database.Position);
            Assert.Equal(10 * Mebibyte, accounts.Get(2L)!.Data!.Length);
            Assert.Null(accounts.Get(3L));

            database.DrainRetired();
            Assert.Equal(2, database.Counters.RootEntries);
            Assert.Equal([1L, 2L], IndexContents.Of(database, "Account", "ByLogin").Entries.Select(entry => entry.Entity).Order());
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // A replace of 3 MiB is held in the persistence step, so that the arena holds both states of the entity
    // for a time; the batch does not fit beside them, is refused twice and waits in its resolution, where its
    // token is cancelled.
    [Fact]
    public async Task Batch_cancelled_at_the_wait_of_the_resolution_of_its_refusal_ends_cancelled_and_changes_nothing()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = TwoSegments, Persistence = storage });
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            await accounts.InsertAsync(UniqueSchema.Of(1, "a", data: 3 * Mebibyte));
            storage.Hold();

            var replace = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", data: 3 * Mebibyte)).AsTask();
            await storage.NextAsync();
            var waits = RefusedWriteWait.Begins(database);

            using var cancellation = new CancellationTokenSource();
            var batch = database.CreateWriteBatch();
            batch.Insert(UniqueSchema.Of(2, "b", data: 3 * Mebibyte));
            batch.Insert(UniqueSchema.Of(3, "c"));
            var batched = batch.CommitAsync(cancellation.Token).AsTask();

            await waits.WaitAsync(Bound);
            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => batched.WaitAsync(Bound));

            storage.ReleaseAll();
            await replace.WaitAsync(Bound);

            Assert.Null(accounts.Get(2L));
            Assert.Null(accounts.Get(3L));
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static void ExpectAsBefore(EngineDatabase database, EngineCounters before, List<(object?[] Key, long Entity)> entries)
    {
        var after = database.Counters;

        Assert.Equal(before.Arena.LiveBytes, after.Arena.LiveBytes);
        Assert.Equal(before.RootEntries, after.RootEntries);
        Assert.Equal(before.PublishedSequence, after.PublishedSequence);
        Assert.Equal(0, after.Arena.RetiredBytes);
        Assert.Equal(EntriesOf(entries), EntriesOf(IndexContents.Of(database, "Account", "ByLogin").Entries));
    }

    // The bytes of the blocks of the arena, by their state; the segments it keeps are not the batch's.
    private static (long Live, long Slack, long Prepared, long Retired) BytesOf(ArenaCounters arena) =>
        (arena.LiveBytes, arena.ClassSlackBytes, arena.PreparedBytes, arena.RetiredBytes);

    // The entries of ByLogin as the login and the entity of each, in the order of the index.
    private static List<(string Login, long Entity)> EntriesOf(List<(object?[] Key, long Entity)> entries) =>
        [.. entries.Select(entry => ((string)entry.Key[0]!, entry.Entity))];
}
