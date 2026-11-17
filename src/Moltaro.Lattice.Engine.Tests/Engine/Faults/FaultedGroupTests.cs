using Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Faults;

// What every write in progress sees when the publication of a group faults (brief of Epic 1, sprint
// S09, the answers of the planner to the contract author, "After a fault"; the remarks of
// LatticeDatabaseStateEnum.Faulted and LatticeErrorEnum.IndeterminateOutcome):
// - A write of the group whose stores were all made before the fault gets its real result: it is
//   published.
// - The write whose publication faulted, and the writes of the group after it, fail with
//   IndeterminateOutcome: the storage step took them and the instance cannot show them. The first of
//   them carries the injected InvalidOperationException, which names the point, as its InnerException.
// - Writes prepared and not yet taken by the commit stage, writes waiting for their identity, a write
//   the arena refused that waits for the writes admitted before it, and a write being prepared fail
//   with Unavailable: they did not run.
// - The instance is Faulted, every later operation is Unavailable, the close works and returns every
//   piece of native memory (CountingNativeAllocator), and with it whatever the writes held.
// The storage step of the test (HeldCommitStorage) holds the group of three replaces while the test
// puts the other writes where it wants them: a replace prepared and waiting for the commit stage; a
// replace of the same entity waiting behind it; an insert of 9 MiB, which an arena of two segments of
// 4 MiB refuses, and which then waits for the writes admitted before it, as the switch of a refused
// write tells (EngineDatabase.HoldNextWaitForReads). The point, named by the test since the enum of the
// engine is internal, is armed so that the first write of the group passes it and the second fails
// there (EngineDatabase.ArmFault with passes to skip); each write passes BeforePublication before its
// own stores. Which write the group holds first is known by its encoded size, which differs from
// every other's. For both points.
//
// A write being prepared when the database faults is held there by the allocator of the test
// (HeldPreparation), at the moment its values need a new segment of the arena; its thread holds the
// lock of the writes meanwhile, so the fault comes from the storage step, which fails the group it
// holds, as a storage step fails that cannot tell whether it committed (brief of S09, the block after
// the implementation: the whole group is IndeterminateOutcome and the database Faulted), rather than
// from a publication, which needs that lock. Every wait is bounded.
public sealed class FaultedGroupTests
{
    private const int Mebibyte = 1024 * 1024;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * new ArenaOptions().SegmentSize };

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Theory]
    [InlineData(nameof(EngineFaultPointEnum.BeforePublication))]
    [InlineData(nameof(EngineFaultPointEnum.DuringPublication))]
    public async Task Fault_in_the_publication_of_a_group_ends_every_write_in_progress_by_how_far_it_had_come(string point)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { Arena = TwoSegments, Allocator = allocator, CommitStorage = storage });

        try
        {
            var pieces = database.Set<PieceSchema.Piece>();

            for (var id = 1; id <= 5; id++)
            {
                await pieces.InsertAsync(StateOf(id, name: 10));
            }

            storage.Hold();

            // The commit stage is busy with a replace of piece 1 while replaces of pieces 2, 3 and 4 are
            // prepared; then those three form one group, which the storage holds.
            var opening = StateOf(1, name: 26);
            var openingWrite = pieces.ReplaceAsync(opening).AsTask();
            var openingCommit = await storage.NextAsync();
            PieceSchema.Piece[] grouped = [StateOf(2, name: 26), StateOf(3, name: 26), StateOf(4, name: 26)];
            var groupWrites = grouped.Select(piece => pieces.ReplaceAsync(piece).AsTask()).ToArray();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(opening) + PieceTally.SlotBytesOf(grouped));
            openingCommit.Release();
            Assert.True((await openingWrite.WaitAsync(Bound)).Changed);

            var group = await storage.NextAsync();

            Assert.Equal(3, group.Count);

            // A replace of piece 5 prepared, one waiting behind it, and an insert the arena refused.
            var prepared = StateOf(5, name: 26);
            var preparedWrite = pieces.ReplaceAsync(prepared).AsTask();

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(grouped) + PieceTally.SlotBytesOf(prepared));

            var waitingWrite = pieces.ReplaceAsync(StateOf(5, name: 42)).AsTask();
            var refusedWaits = RefusedWriteWait.Begins(database);
            var refusedWrite = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(6, name: 10, data: 9 * Mebibyte)).AsTask());

            await Task.WhenAny(refusedWaits, refusedWrite).WaitAsync(Bound);
            Assert.True(refusedWaits.IsCompleted, "The insert that the arena refused did not wait for the writes admitted before it.");

            // The refused insert and the replace waiting for its entity hold nothing.
            var counters = database.Counters;

            Assert.Equal(6, counters.QueuedWrites);
            Assert.Equal(PieceTally.SlotBytesOf(grouped) + PieceTally.SlotBytesOf(prepared), counters.Arena.PreparedBytes);

            database.ArmFault(Enum.Parse<EngineFaultPointEnum>(point), skippedPasses: 1);
            storage.ReleaseAll();

            var order = group.Sizes.Select(size => Array.FindIndex(grouped, piece => PieceTally.SlotBytesOf(piece) == size)).ToArray();

            Assert.Equal(new[] { 0, 1, 2 }, order.Order());

            Assert.True((await groupWrites[order[0]].WaitAsync(Bound)).Changed);

            var faulted = await Assert.ThrowsAsync<LatticeException>(() => groupWrites[order[1]].WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, faulted.Error);
            var cause = Assert.IsType<InvalidOperationException>(faulted.InnerException);
            Assert.Contains(point, cause.Message, StringComparison.Ordinal);

            var after = await Assert.ThrowsAsync<LatticeException>(() => groupWrites[order[2]].WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, after.Error);

            foreach (var write in new[] { preparedWrite, waitingWrite, refusedWrite })
            {
                var unavailable = await Assert.ThrowsAsync<LatticeException>(() => write.WaitAsync(Bound));

                Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
            }

            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);

            var later = await Assert.ThrowsAsync<LatticeException>(() => pieces.InsertAsync(PieceSchema.Of(7, name: 10)).AsTask().WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.Unavailable, later.Error);
            Assert.Equal(LatticeErrorEnum.Unavailable, Assert.Throws<LatticeException>(() => pieces.Get(1L)).Error);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }

        Assert.Equal(0, allocator.Outstanding);
        Assert.Equal(allocator.Obtained, allocator.Returned);
    }

    [Fact]
    public async Task Write_being_prepared_when_the_storage_step_fails_ends_in_Unavailable_and_the_group_is_IndeterminateOutcome()
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs
        {
            Arena = new ArenaOptions { MemoryLimit = 64L * new ArenaOptions().SegmentSize },
            Allocator = allocator,
            CommitStorage = storage,
        });

        try
        {
            var pieces = database.Set<PieceSchema.Piece>();

            // The first segment, which a value of 5 MiB outgrows.
            await pieces.InsertAsync(PieceSchema.Of(1, name: 10));
            storage.Hold();

            var grouped = pieces.ReplaceAsync(PieceSchema.Of(1, name: 26)).AsTask();
            var heldCommit = await storage.NextAsync();

            using var held = HeldPreparation.Arm(allocator);
            var prepared = Task.Run(() => pieces.InsertAsync(PieceSchema.Of(2, name: 10, data: 5 * Mebibyte)).AsTask());

            await held.HeldAsync();
            heldCommit.Fail(new IOException("The test failed the storage step: whether it committed is unknown."));

            var indeterminate = await Assert.ThrowsAsync<LatticeException>(() => grouped.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, indeterminate.Error);
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);

            held.Release();

            var unavailable = await Assert.ThrowsAsync<LatticeException>(() => prepared.WaitAsync(Bound));

            Assert.Equal(LatticeErrorEnum.Unavailable, unavailable.Error);
            Assert.Equal(LatticeErrorEnum.Unavailable, Assert.Throws<LatticeException>(() => pieces.Get(1L)).Error);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }

        Assert.Equal(0, allocator.Outstanding);
        Assert.Equal(allocator.Obtained, allocator.Returned);
    }

    // A small piece whose Note, in a block of its own, makes its encoded size differ from that of the
    // pieces of other ids; the length of the name tells its states apart.
    private static PieceSchema.Piece StateOf(int id, int name) => PieceSchema.Of(id, name, note: 100 + (16 * id));
}
