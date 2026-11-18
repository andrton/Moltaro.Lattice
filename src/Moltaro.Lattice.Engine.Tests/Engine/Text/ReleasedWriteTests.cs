using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A write that takes texts and does not publish them releases what it took (brief of Epic 1, sprint S13,
// point 3 of the block of 17 November; the planner's decision after the study, point 4: a write that is
// withdrawn, cancelled or ended by a fault drops its references where its blocks are freed; decision on
// strings, Q04.4, point 2: failed mutations leave no leaked counts):
// - a write prepared with a new text and an existing one, withdrawn before its dispatch because its token
//   was cancelled while the storage step of the test (HeldCommitStorage) held the commit stage, leaves no
//   entry of the new text and the count of the existing one as before;
// - a write whose token is cancelled while it is being prepared, held there by the allocator of the test
//   at the moment the entry of its large new text needs a new segment (HeldPreparation), is withdrawn
//   when its preparation ends and leaves the same;
// - a write refused by its condition or by the validation of the core leaves no entry either.
// A write ended by a fault leaves an instance that is Faulted and shows no counter; what it leaves is
// stated by CloseTests. A write refused for memory is stated by MemoryTests.
public sealed class ReleasedWriteTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Write_withdrawn_before_its_dispatch_leaves_no_entry_of_its_new_text_and_the_count_of_its_existing_text_as_before()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs { CommitStorage = storage });

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");
            var stored = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };

            await places.InsertAsync(stored);
            tally.Written(stored);
            storage.Hold();

            var first = new TextSchema.Place { Id = 1, Number = 1, Region = "North", Label = "label" };
            var firstWrite = places.ReplaceAsync(first).AsTask();
            await storage.NextAsync();
            tally.Prepared(first);

            using var cancellation = new CancellationTokenSource();
            var withdrawn = new TextSchema.Place { Id = 2, Region = "brand new", Zone = "North", Label = "label" };
            var cancelled = places.InsertAsync(withdrawn, cancellation.Token).AsTask();

            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "both writes are admitted");
            tally.Prepared(withdrawn);

            Assert.Equal(3, EntryCounts.Of(database, "Utf8")["North"]);
            EntryCounts.ExpectEqual(tally, database);

            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            tally.Withdrawn(withdrawn);

            Assert.False(EntryCounts.Of(database, "Utf8").ContainsKey("brand new"));
            Assert.Equal(2, EntryCounts.Of(database, "Utf8")["North"]);
            EntryCounts.ExpectEqual(tally, database);

            storage.ReleaseAll();
            await firstWrite.WaitAsync(Bound);
            tally.Published();

            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_cancelled_while_it_is_being_prepared_leaves_no_entry_of_its_new_text_and_the_count_of_its_existing_text_as_before()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });

        try
        {
            var places = database.Set<TextSchema.Place>();
            var tally = new TextTally("Utf8");

            // The first segment, which a text of 5 MiB outgrows.
            var stored = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };

            await places.InsertAsync(stored);
            tally.Written(stored);
            storage.Hold();

            var earlier = new TextSchema.Place { Id = 2, Region = "North", Label = "label" };
            var earlierWrite = places.InsertAsync(earlier).AsTask();
            await storage.NextAsync();
            tally.Prepared(earlier);

            using var cancellation = new CancellationTokenSource();
            using var held = HeldPreparation.Arm(allocator);
            var withdrawn = new TextSchema.Place { Id = 3, Region = new string('n', 5 * 1024 * 1024), Zone = "North", Label = "label" };
            var cancelled = Task.Run(() => places.InsertAsync(withdrawn, cancellation.Token).AsTask());

            await held.HeldAsync();
            var cancelling = cancellation.CancelAsync();
            held.Release();
            await cancelling.WaitAsync(Bound);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Bound));
            Assert.False(earlierWrite.IsCompleted, "The write held in the storage step completed before the cancelled write ended.");

            Assert.Equal(2, EntryCounts.Of(database, "Utf8")["North"]);
            EntryCounts.ExpectEqual(tally, database);

            storage.ReleaseAll();
            await earlierWrite.WaitAsync(Bound);
            tally.Published();

            EntryCounts.ExpectEqual(tally, database);
            tally.ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_refused_by_its_condition_or_its_validation_leaves_no_entry_of_its_new_text()
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8);
        var places = database.Set<TextSchema.Place>();
        var records = database.Set("Place");
        var tally = new TextTally("Utf8");
        var stored = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };

        await places.InsertAsync(stored);
        tally.Written(stored);
        Assert.True(places.TryGet(1L, out _, out var revision));
        await places.ReplaceAsync(new TextSchema.Place { Id = 1, Number = 1, Region = "North", Label = "label" });
        tally.Written(new TextSchema.Place { Id = 1, Number = 1, Region = "North", Label = "label" });

        await ExpectRefusedAsync(LatticeErrorEnum.DuplicateKey, () => places.InsertAsync(new TextSchema.Place { Id = 1, Region = "duplicate", Label = "label" }));
        await ExpectRefusedAsync(LatticeErrorEnum.NotFound, () => places.ReplaceAsync(new TextSchema.Place { Id = 404, Region = "not found", Label = "label" }));
        await ExpectRefusedAsync(LatticeErrorEnum.RevisionConflict, () => places.ReplaceAsync(new TextSchema.Place { Id = 1, Region = "conflict", Label = "label" }, revision));
        await ExpectRefusedAsync(LatticeErrorEnum.ValidationFailed, () => records.InsertAsync(TextSchema.RecordOf(records.EntityType, new TextSchema.Place { Id = 2, Region = "valid", Zone = "No\uD83Drth", Label = "label" })));

        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);

        async Task ExpectRefusedAsync(LatticeErrorEnum error, Func<ValueTask<LatticeWriteResult>> write)
        {
            var refused = await Assert.ThrowsAsync<LatticeException>(async () => await write());

            Assert.Equal(error, refused.Error);
        }
    }
}
