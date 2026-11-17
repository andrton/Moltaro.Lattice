using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// When a write is refused for memory (brief of Epic 1, sprint S09, "When a write is refused for memory:
// the rule after review round 1", and "Cancellation before dispatch"): a write is refused only when it
// does not fit beside the stored data, never because of memory that other writes, earlier or later,
// hold only for a time. Refused twice, a write is resolved alone: it waits for the writes admitted
// before it; it holds back the preparation of later writes, which are admitted and wait, and waits for
// those already prepared or in the commit stage; it waits for the reads then in progress, frees what is
// retired and retries once; then it goes on as any write, or it is WriteRejected with Limit Memory, and
// in both cases the later writes go on at once. Two writes refused at the same time are resolved in the
// order they were admitted, and the token of a write cancels its wait at any step. So, on an arena of
// two segments of 4 MiB (EngineTestInputs, exception X3):
// - The case the first reviewer found: an insert of 3 MiB that waited for its entity behind a delete,
//   and is the oldest write once the delete is published, while a later replace of 3 MiB is prepared
//   and held in the storage step of the test (HeldCommitStorage), completes once the replace is
//   published. The switch of a refused write (RefusedWriteWait) tells that it waits.
// - Without a hook: an insert of 5 MiB into the empty arena beside an insert of 9 MiB that can never fit,
//   both started at once on threads of their own, round after round: the 5 MiB insert always completes
//   and the 9 MiB one is always refused. As built before the fix, the 5 MiB insert was refused in 56 to
//   94 of 200 rounds, whenever it was admitted first and met the arena full of what the 9 MiB insert
//   held while it was prepared; 60 rounds leave that fault less than one chance in a hundred million to
//   pass, and take well under a second.
// - Two replaces of 2.5 MiB, each of which fits beside the two entities stored and which do not fit
//   prepared together, started at once round after round: both always complete. As built before the
//   fix one of them was refused in about half of the rounds.
// - While a refused write is resolved, a later write of another entity is admitted and is not
//   prepared, and once the refused write is refused it completes, and QueuedWrites returns to 0.
// - A write cancelled during the wait of its resolution ends in OperationCanceledException, never in
//   WriteRejected, and the writes it held back, one of another entity and one of its own, go on.
// - Two inserts of 4.5 MiB, each refused while an earlier delete of 4 MiB is held in the storage step:
//   the first admitted is resolved first and completes; the second does not fit beside it and is
//   refused.
// - A write that cannot fit is refused while another caller keeps replacing a small entity, within the
//   time of the test and after the writes in flight alone: from the moment it waits for a replace of
//   that caller held in the storage step, at most three replaces of that caller complete before the
//   write has left the coordinator. As built before the fix it kept retrying while the later replaces
//   retired memory, for hundreds to tens of thousands of them.
// - A later write of the entity of a refused write that waits for the writes admitted before it waits
//   behind it, and runs after it (added by the implementer of S09, part 2).
// Review round 1 of S09, part 2, on writes that waited forever when the write at the head of the chain
// of their entity ended in a wait of a resolution, or when the database faulted during one:
// - A write cancelled while it is held back by a resolution, or while it waits for the writes admitted
//   before it, lets the later write of its entity, which waits behind it, run: that write completes.
// - A fault of the storage step while a write waits in step 2 of its resolution, a later write of
//   another entity is held back and a write of its own entity waits behind it ends each of them, with
//   IndeterminateOutcome for the group in the storage step and Unavailable for the others.
// - A close in the same situation, without a fault, completes once all of them have their outcomes,
//   and the arena has returned every piece of native memory it obtained (CountingNativeAllocator).
// Every wait is bounded; the storage of the test releases every commit when a test ends.
public sealed class RefusalResolutionTests
{
    private const int Mebibyte = 1024 * 1024;

    private const int Large = 3 * Mebibyte;

    // A value of 2.5 MiB: three states of it fit the arena, four do not.
    private const int Replaced = (5 * Mebibyte) / 2;

    // The rounds of the tests without a hook; see the class.
    private const int Rounds = 60;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * new ArenaOptions().SegmentSize };

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Oldest_write_refused_while_a_later_write_holds_prepared_memory_completes_once_that_write_is_published()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            var (insert, replace, inserted, replaced, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: Large);

            Assert.Equal(2, database.Counters.QueuedWrites);

            // The replace is published, and the insert, which goes on as any write once it fits, has a
            // commit of its own: the storage holds no commit from here on.
            storage.ReleaseAll();
            heldReplace.Release();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await insert.WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Equal(4, database.Counters.PublishedSequence);
            PieceTally.Of(replaced, inserted).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Insert_of_5_MiB_beside_an_insert_of_9_MiB_into_an_empty_arena_of_8_MiB_always_completes_and_the_9_MiB_one_is_always_refused()
    {
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());

        try
        {
            var pieces = database.Set<PieceSchema.Piece>();
            var fits = new PieceSchema.Piece { Id = 1, Name = "fits", Data = new byte[5 * Mebibyte] };
            var never = new PieceSchema.Piece { Id = 2, Name = "never", Data = new byte[9 * Mebibyte] };

            for (var round = 0; round < Rounds; round++)
            {
                var (fitting, refused) = StartTogether(() => pieces.InsertAsync(fits), () => pieces.InsertAsync(never));

                var rejection = await Assert.ThrowsAsync<LatticeException>(() => refused.WaitAsync(Bound));
                var result = await Record.ExceptionAsync(() => fitting.WaitAsync(Bound));

                Assert.True(result is null, $"In round {round} the insert of 5 MiB, which fits the empty arena, failed: {result}");
                Assert.Equal(LatticeErrorEnum.WriteRejected, rejection.Error);
                Assert.Equal(LatticeLimitEnum.Memory, rejection.Limit);

                Assert.True((await pieces.DeleteAsync(1L).AsTask().WaitAsync(Bound)).Changed);
                database.DrainRetired();
            }

            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of().ExpectHeldBy(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Two_replaces_that_fit_one_after_the_other_in_either_order_both_complete_when_they_are_made_at_once()
    {
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, TwoSegments, new NativeAllocator());

        try
        {
            var pieces = database.Set<PieceSchema.Piece>();
            var first = new byte[Replaced];
            var second = new byte[Replaced];

            await pieces.InsertAsync(new PieceSchema.Piece { Id = 1, Name = "first", Data = first });
            await pieces.InsertAsync(new PieceSchema.Piece { Id = 2, Name = "second", Data = second });

            for (var round = 0; round < Rounds; round++)
            {
                var name = $"round {round}";
                var (one, other) = StartTogether(
                    () => pieces.ReplaceAsync(new PieceSchema.Piece { Id = 1, Name = name, Data = first }),
                    () => pieces.ReplaceAsync(new PieceSchema.Piece { Id = 2, Name = name, Data = second }));

                var failures = await Task.WhenAll(Record.ExceptionAsync(() => one.WaitAsync(Bound)).AsTask(), Record.ExceptionAsync(() => other.WaitAsync(Bound)).AsTask());

                Assert.True(failures.All(failure => failure is null), $"In round {round} a replace that fits once the other is published failed: {failures.FirstOrDefault(failure => failure is not null)}");

                database.DrainRetired();
            }

            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of(
                new PieceSchema.Piece { Id = 1, Name = $"round {Rounds - 1}", Data = first },
                new PieceSchema.Piece { Id = 2, Name = $"round {Rounds - 1}", Data = second }).ExpectHeldBy(database);
        }
        finally
        {
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Refused_write_holds_back_the_preparation_of_later_writes_and_after_its_refusal_they_complete()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));

            // An insert of 6 MiB, which does not fit beside entity 1 whatever happens.
            var (insert, replace, _, replaced, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: 2 * Large);

            // A later write of another entity is admitted, and waits unprepared while the refused
            // write is resolved.
            var later = PieceSchema.Of(3, name: 10, note: 300);
            var laterWrite = pieces.InsertAsync(later).AsTask();
            var counters = database.Counters;

            Assert.Equal(3, counters.QueuedWrites);
            Assert.Equal(PieceTally.SlotBytesOf(replaced), counters.Arena.PreparedBytes);

            storage.ReleaseAll();
            heldReplace.Release();

            var refusal = await Assert.ThrowsAsync<LatticeException>(() => insert.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.WriteRejected, refusal.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refusal.Limit);
            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await laterWrite.WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Null(pieces.Get(2L));
            PieceTally.Of(replaced, later).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Write_cancelled_during_the_wait_of_its_resolution_ends_in_OperationCanceledException_and_the_writes_it_held_back_go_on()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));

            using var cancellation = new CancellationTokenSource();
            var (insert, replace, _, replaced, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: Large, cancellation.Token);

            // Held back by the resolution: a write of another entity, and one of the entity of the
            // refused write, which waits behind it.
            var other = PieceSchema.Of(3, name: 10, note: 300);
            var otherWrite = pieces.InsertAsync(other).AsTask();
            var own = PieceSchema.Of(2, name: 26);
            var ownWrite = pieces.InsertAsync(own).AsTask();

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => insert.WaitAsync(Bound));
            Assert.False(replace.IsCompleted, "The replace held in the storage step completed before the cancelled insert ended.");

            storage.ReleaseAll();
            heldReplace.Release();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await otherWrite.WaitAsync(Bound)).Changed);
            Assert.True((await ownWrite.WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of(replaced, other, own).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Two_writes_refused_at_the_same_time_are_resolved_in_the_order_they_were_admitted()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: 4 * Mebibyte));
            storage.Hold();

            // While the delete of entity 1 is held, neither insert fits beside it; once it is published
            // either fits alone, and not both.
            var delete = pieces.DeleteAsync(1L).AsTask();
            var heldDelete = await storage.NextAsync();

            // The first is refused and waits before the second is made, so the second is admitted after
            // it and is refused while the first waits.
            var firstPiece = PieceSchema.Of(2, name: 10, data: (9 * Mebibyte) / 2);
            var firstWaits = RefusedWriteWait.Begins(database);
            var first = Task.Run(() => pieces.InsertAsync(firstPiece).AsTask());

            await Task.WhenAny(firstWaits, first).WaitAsync(Bound);
            Assert.True(firstWaits.IsCompleted, $"The first insert did not wait for the delete admitted before it: {Outcome(first)}.");

            var secondWaits = RefusedWriteWait.Begins(database);
            var second = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(3, name: 26, data: (9 * Mebibyte) / 2)).AsTask());

            await Task.WhenAny(secondWaits, second).WaitAsync(Bound);
            Assert.True(secondWaits.IsCompleted, $"The second insert did not wait: {Outcome(second)}.");

            storage.ReleaseAll();
            heldDelete.Release();

            Assert.True((await delete.WaitAsync(Bound)).Changed);

            var refused = await Assert.ThrowsAsync<LatticeException>(() => second.WaitAsync(Bound));

            Assert.True((await first.WaitAsync(Bound)).Changed);
            Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of(firstPiece).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    // Added by the implementer of S09, part 2: a refused write that waits for the writes admitted before
    // it is not prepared, so a later write of its entity has to find it among the writes that wait, not
    // among those prepared. Here the insert of entity 2 waits behind the delete of entity 1, held in the
    // storage step; a replace of entity 2 made after it waits behind it, and runs after it: had it run
    // first, it would have found no entity 2 and been NotFound.
    [Fact]
    public async Task Later_write_of_an_entity_waits_behind_an_earlier_write_of_it_that_waits_for_the_writes_admitted_before_it()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: 4 * Mebibyte));
            storage.Hold();

            var delete = pieces.DeleteAsync(1L).AsTask();
            var heldDelete = await storage.NextAsync();

            var waits = RefusedWriteWait.Begins(database);
            var insert = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: (9 * Mebibyte) / 2)).AsTask());

            await Task.WhenAny(waits, insert).WaitAsync(Bound);
            Assert.True(waits.IsCompleted, $"The insert did not wait for the delete admitted before it: {Outcome(insert)}.");

            var replace = pieces.ReplaceAsync(PieceSchema.Of(2, name: 26)).AsTask();

            Assert.False(replace.IsCompleted, "The later replace of the entity ran before the earlier insert of it.");

            storage.ReleaseAll();
            heldDelete.Release();

            Assert.True((await delete.WaitAsync(Bound)).Changed);

            var inserted = await insert.WaitAsync(Bound);
            var replaced = await replace.WaitAsync(Bound);

            Assert.True(PositionSequence.Of(inserted.Position) < PositionSequence.Of(replaced.Position));
            Assert.Equal(26, pieces.Get(2L)?.Name.Length);
            Assert.Equal(0, database.Counters.QueuedWrites);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Cancelled_write_held_back_by_a_resolution_lets_the_later_write_of_its_entity_run()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            var (insert, replace, inserted, replaced, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: Large);

            // Held back by the resolution, the head of the chain of entity 3, and a write of entity 3
            // behind it.
            using var cancellation = new CancellationTokenSource();
            var head = pieces.InsertAsync(PieceSchema.Of(3, name: 10, note: 300), cancellation.Token).AsTask();
            var later = PieceSchema.Of(3, name: 26);
            var laterWrite = pieces.UpsertAsync(later).AsTask();

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.WaitAsync(Bound));
            Assert.False(laterWrite.IsCompleted, "The later write of entity 3 ran while the resolution held writes back.");

            storage.ReleaseAll();
            heldReplace.Release();

            Assert.True((await replace.WaitAsync(Bound)).Changed);
            Assert.True((await insert.WaitAsync(Bound)).Changed);
            Assert.True((await laterWrite.WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of(replaced, inserted, later).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Cancelled_write_that_waits_for_the_writes_admitted_before_it_lets_the_later_write_of_its_entity_run()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: 4 * Mebibyte));
            storage.Hold();

            var delete = pieces.DeleteAsync(1L).AsTask();
            var heldDelete = await storage.NextAsync();

            // Refused beside entity 1, it waits for the delete admitted before it, at the head of the
            // chain of entity 2; a write of entity 2 waits behind it.
            using var cancellation = new CancellationTokenSource();
            var waits = RefusedWriteWait.Begins(database);
            var head = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: (9 * Mebibyte) / 2), cancellation.Token).AsTask());

            await Task.WhenAny(waits, head).WaitAsync(Bound);
            Assert.True(waits.IsCompleted, $"The insert did not wait for the delete admitted before it: {Outcome(head)}.");

            var later = PieceSchema.Of(2, name: 26);
            var laterWrite = pieces.UpsertAsync(later).AsTask();

            await cancellation.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => head.WaitAsync(Bound));

            storage.ReleaseAll();
            heldDelete.Release();

            Assert.True((await delete.WaitAsync(Bound)).Changed);
            Assert.True((await laterWrite.WaitAsync(Bound)).Changed);
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of(later).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Fault_while_a_write_is_resolved_and_later_writes_are_held_back_ends_every_one_of_them()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            var (insert, replace, _, _, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: Large);

            // Held back, and a write of the entity of the refused write behind it.
            var heldBack = pieces.InsertAsync(PieceSchema.Of(3, name: 10, note: 300)).AsTask();
            var own = pieces.ReplaceAsync(PieceSchema.Of(2, name: 26)).AsTask();

            Assert.Equal(4, database.Counters.QueuedWrites);

            heldReplace.Fail(new IOException("The test failed the storage step: whether it committed is unknown."));

            var indeterminate = await Assert.ThrowsAsync<LatticeException>(() => replace.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, indeterminate.Error);

            foreach (var write in new[] { insert, heldBack, own })
            {
                var unavailable = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
        }
        finally
        {
            storage.ReleaseAll();
            await CloseAsync(database);
        }
    }

    [Fact]
    public async Task Close_while_a_write_is_resolved_and_later_writes_are_held_back_completes_once_each_has_its_outcome()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage, allocator);
        var pieces = database.Set<PieceSchema.Piece>();
        Task? closing = null;

        try
        {
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10, data: Large));
            var (insert, replace, _, _, heldReplace) = await RefusedBesideALaterReplaceAsync(database, storage, insertData: Large);

            // Held back, and a write of the entity of the refused write behind it.
            var heldBack = pieces.InsertAsync(PieceSchema.Of(3, name: 10, note: 300)).AsTask();
            var own = pieces.ReplaceAsync(PieceSchema.Of(2, name: 26)).AsTask();
            Task<LatticeWriteResult>[] waiting = [replace, insert, heldBack, own];

            closing = database.CloseAsync().AsTask();

            Assert.False(closing.IsCompleted, "The close completed while a write was resolved and writes were held back.");
            Assert.DoesNotContain(waiting, write => write.IsCompleted);
            Assert.True(allocator.Outstanding > 0, "The native memory was returned while writes were in progress.");

            storage.ReleaseAll();
            heldReplace.Release();

            var results = await Task.WhenAll(waiting).WaitAsync(Bound);

            Assert.All(results, result => Assert.True(result.Changed));

            await closing.WaitAsync(Bound);

            Assert.Equal(0, allocator.Outstanding);
            Assert.Equal(allocator.Obtained, allocator.Returned);
        }
        finally
        {
            storage.ReleaseAll();
            await (closing ?? database.CloseAsync().AsTask()).WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Write_that_cannot_fit_is_refused_after_the_writes_in_flight_while_another_caller_keeps_replacing_a_small_entity()
    {
        var storage = new HeldCommitStorage(holding: false);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();
        var never = new PieceSchema.Piece { Id = 2, Name = "never", Data = new byte[9 * Mebibyte] };
        var replaced = 0;
        var watching = 0;
        var stopped = 0;

        await pieces.InsertAsync(PieceSchema.Of(1, name: 10));

        // The other caller replaces entity 1 one write after another and counts its writes. Once the
        // test watches, it stops as soon as, between two of its writes, no write is admitted any more:
        // the refused write has left the coordinator. It does not look at the task of that write, whose
        // completion may come later than the end of its resolution.
        var replacer = Task.Factory.StartNew(
            async () =>
            {
                for (var write = 0; Volatile.Read(ref stopped) == 0; write++)
                {
                    if (Volatile.Read(ref watching) != 0 && database.Counters.QueuedWrites == 0)
                    {
                        return;
                    }

                    await pieces.ReplaceAsync(PieceSchema.Of(1, name: 10 + (write % 40))).AsTask().WaitAsync(Bound);
                    Interlocked.Increment(ref replaced);
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        try
        {
            await CountersWait.UntilAsync(database, _ => Volatile.Read(ref replaced) >= 100, "the other caller has replaced its entity 100 times");

            // A replace of the other caller held in the storage step, which the refused write waits for.
            storage.Hold();
            await storage.NextAsync();

            var waits = RefusedWriteWait.Begins(database);
            var refused = Task.Run(() => pieces.InsertAsync(never).AsTask());

            await Task.WhenAny(waits, refused).WaitAsync(Bound);

            Assert.True(waits.IsCompleted, $"The write that cannot fit did not wait for the replace admitted before it: {Outcome(refused)}.");

            // From here on only the writes in flight may complete while the write is resolved: the
            // replace held, and at most one the other caller made before the resolution held writes
            // back; one more for where the count stands when the coordinator lets the write go.
            var before = Volatile.Read(ref replaced);

            Volatile.Write(ref watching, 1);
            storage.ReleaseAll();

            var refusal = await Assert.ThrowsAsync<LatticeException>(() => refused.WaitAsync(Bound));
            await replacer.WaitAsync(Bound);
            var during = Volatile.Read(ref replaced) - before;

            Assert.Equal(LatticeErrorEnum.WriteRejected, refusal.Error);
            Assert.Equal(LatticeLimitEnum.Memory, refusal.Limit);
            Assert.True(during <= 3, $"The other caller completed {during} replaces while the write that cannot fit was resolved; writes admitted after it prolonged its resolution.");
            Assert.Equal(0, database.Counters.QueuedWrites);
            Assert.Null(pieces.Get(2L));
        }
        finally
        {
            Volatile.Write(ref stopped, 1);
            storage.ReleaseAll();
            await replacer.WaitAsync(Bound);
            await CloseAsync(database);
        }
    }

    // The case of the first reviewer, up to the moment the insert waits: entity 1 is stored; a delete of
    // the absent key 2 is held in the storage step; an insert of key 2 waits behind it for its entity; a
    // replace of entity 1 of 3 MiB, admitted after the insert, is prepared. Then the delete is
    // published, the replace is dispatched and held, and the insert, now the oldest write, is refused
    // while the replace holds its prepared memory, and begins to wait. Returns the tasks of the insert
    // and of the replace, what each writes, and the held commit of the replace. The storage holds from
    // here on.
    private static async Task<(Task<LatticeWriteResult> Insert, Task<LatticeWriteResult> Replace, PieceSchema.Piece Inserted, PieceSchema.Piece Replaced, HeldCommit HeldReplace)> RefusedBesideALaterReplaceAsync(
        EngineDatabase database, HeldCommitStorage storage, int insertData, CancellationToken insertToken = default)
    {
        var pieces = database.Set<PieceSchema.Piece>();
        var inserted = PieceSchema.Of(2, name: 10, data: insertData);
        var replaced = PieceSchema.Of(1, name: 11, data: Large);

        storage.Hold();

        var delete = pieces.DeleteAsync(2L).AsTask();
        var heldDelete = await storage.NextAsync();
        var insert = pieces.InsertAsync(inserted, insertToken).AsTask();
        var replace = pieces.ReplaceAsync(replaced).AsTask();

        await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(replaced));

        var waits = RefusedWriteWait.Begins(database);

        heldDelete.Release();
        Assert.False((await delete.WaitAsync(Bound)).Changed);

        var heldReplace = await storage.NextAsync();

        await Task.WhenAny(waits, insert).WaitAsync(Bound);

        Assert.True(waits.IsCompleted, $"The insert, refused while a later replace held prepared memory, did not wait for that replace: {Outcome(insert)}.");
        Assert.False(insert.IsCompleted, $"The insert ended while the replace that holds the memory it needs was in the storage step: {Outcome(insert)}.");

        return (insert, replace, inserted, replaced, heldReplace);
    }

    // A close within the bound: a write a fault of the engine left admitted would hold a close without
    // one for ever.
    private static async Task CloseAsync(EngineDatabase database) =>
        await database.CloseAsync().AsTask().WaitAsync(Bound);

    private static async Task<EngineDatabase> OpenAsync(HeldCommitStorage storage, NativeAllocator? allocator = null) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { Arena = TwoSegments, Allocator = allocator ?? new NativeAllocator(), CommitStorage = storage });

    // Two writes made at once, each on a thread of its own. Both threads spin until the test lets them
    // go together: a wait of the system that wakes one thread after the other would give the first a
    // start of a fraction of a millisecond, enough for its write to be prepared before the other's
    // begins, and the writes would not meet.
    private static (Task<LatticeWriteResult> One, Task<LatticeWriteResult> Other) StartTogether(Func<ValueTask<LatticeWriteResult>> one, Func<ValueTask<LatticeWriteResult>> other)
    {
        var ready = 0;
        var go = 0;

        Task<LatticeWriteResult> Start(Func<ValueTask<LatticeWriteResult>> write) => Task.Factory.StartNew(
            () =>
            {
                var waited = Stopwatch.StartNew();

                Interlocked.Increment(ref ready);

                while (Volatile.Read(ref go) == 0)
                {
                    Assert.True(waited.Elapsed < Bound, "The writes were not let go.");
                }

                return write().AsTask();
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();

        var started = (Start(one), Start(other));
        var waiting = Stopwatch.StartNew();

        while (Volatile.Read(ref ready) < 2)
        {
            Assert.True(waiting.Elapsed < Bound, "The threads of the writes did not start.");
            Thread.Yield();
        }

        Volatile.Write(ref go, 1);

        return started;
    }

    private static string Outcome(Task task) => task.IsCompleted ? task.Exception?.InnerException?.ToString() ?? "it completed" : "it had not ended";
}
