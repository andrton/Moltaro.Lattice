using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Forwarding;

// A write through a child whose connection to its master ends (brief of Epic 1, sprint S12, the planner's
// block of 15 November 2026 and his decisions after the study, points 7 and 12): the child tells by what
// its socket accepted.
// - The connection ends while the child sends the request, before its last frame was accepted: the write
//   was not executed, Unavailable, and the master executes nothing. A link holds what the child sends
//   once a write of 32 MiB has begun (HeldChildLink) and is cut. That a master drops a request whose last
//   frame it did not receive is stated with a child made by hand (IncompleteRequestTests).
// - The connection ends after the master received the whole request and before the reply: the outcome is
//   not known to the child, IndeterminateOutcome, never Unavailable; the request, received whole, runs
//   although its connection ended. The storage step of the master holds it (HeldCommitStorage) while a
//   link between the two (LoopbackLink) is cut; released, the master has executed it.
// - The same, when the connection ends in order without the frame Closed: IndeterminateOutcome too.
// - The connection ends while a write holds its reply and waits for its application: the call ends at
//   once in SynchronizationTimeout with the committed position.
// - A child without a connection to its master refuses a write with Unavailable: here
//   one that stopped following because a transaction it received was not valid, which a relay
//   (FrameRelay) damaged; it ends its connection, and a write through it afterwards changes nothing on the
//   master.
public sealed class LostConnectionTests
{
    [Fact]
    public async Task Connection_that_ends_while_the_child_sends_a_request_gives_Unavailable_and_the_master_executes_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var link = HeldChildLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(link.Address);
        await link.HoldOnceAcknowledgedAsync();

        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8, data: 32 * 1024 * 1024)).AsTask();
        await link.HeldAsync();
        link.Cut();

        var exception = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(ReplicationNodes.Bound));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        await NothingRegistered.ExpectAsync(master, 100);
        Assert.Null(master.Set<PieceSchema.Piece>().Get(1L));
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    [Fact]
    public async Task Connection_that_ends_after_the_master_received_the_whole_request_gives_IndeterminateOutcome_and_the_write_runs()
    {
        var storage = new HeldCommitStorage(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var link = new LoopbackLink(master.ListenUri!, LoopbackLink.CopyAsync);
            await using var child = await ReplicationNodes.OpenChildAsync(link.Address);

            storage.Hold();
            var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            await storage.NextAsync();
            link.Cut();

            var exception = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(ReplicationNodes.Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);

            storage.ReleaseAll();
            await Eventually.HoldsAsync(() => master.Set<PieceSchema.Piece>().Get(1L) is not null, "The master did not execute the request it had received whole.");
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    // An end in order of the connection alone says nothing about execution: a killed process can produce
    // one, so without the frame Closed a write sent whole and not answered stays IndeterminateOutcome, never
    // Unavailable "not executed" (decided at the review of S12, point 2). The link ends its side toward
    // the child in order, the end of the stream without a reset and without Closed, while the storage step
    // of the master holds the write it received whole; released, the master executes it.
    [Fact]
    public async Task Connection_that_ends_in_order_without_Closed_after_the_master_received_the_whole_request_gives_IndeterminateOutcome()
    {
        var storage = new HeldCommitStorage(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { CommitStorage = storage });

        try
        {
            await using var link = new LoopbackLink(master.ListenUri!, LoopbackLink.CopyAsync);
            await using var child = await ReplicationNodes.OpenChildAsync(link.Address);

            storage.Hold();
            var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
            await storage.NextAsync();
            link.EndToChildInOrder();

            var exception = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(ReplicationNodes.Bound));

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);

            storage.ReleaseAll();
            await Eventually.HoldsAsync(() => master.Set<PieceSchema.Piece>().Get(1L) is not null, "The master did not execute the request it had received whole.");
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    // A write that holds its reply and waits for its application when the connection ends: the position
    // cannot arrive any more, so the wait ends at once, with SynchronizationTimeout and the position the
    // master committed the write at, long before the timeout of an hour (settled by the planner with the
    // tests of S12). A relay holds back the transaction of the write, so that the child takes the reply and
    // waits; the next transaction of the master then does not follow the one the child applied last, the
    // child refuses it and ends its connection.
    [Fact]
    public async Task Write_that_has_its_reply_ends_at_once_in_SynchronizationTimeout_with_its_committed_position_when_the_connection_ends()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var dropsTheNextTransaction = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || !Volatile.Read(ref dropsTheNextTransaction))
            {
                return [frame];
            }

            Volatile.Write(ref dropsTheNextTransaction, false);
            return [];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(
            new LatticeDatabaseOptions
            {
                Role = LatticeRoleEnum.Child,
                PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
                MasterUri = relay.Address,
                SynchronizationTimeout = TimeSpan.FromHours(1),
            },
            new EngineTestInputs());

        Volatile.Write(ref dropsTheNextTransaction, true);
        var writing = child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8)).AsTask();
        await Eventually.HoldsAsync(() => master.Set<PieceSchema.Piece>().Get(1L) is not null, "The master did not execute the write.");
        var committed = master.Position;

        await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8));
        await relay.ChildEndedAsync();

        var exception = await Assert.ThrowsAsync<LatticeException>(() => writing.WaitAsync(ReplicationNodes.Bound));

        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, exception.Error);
        Assert.Equal(committed, exception.CommittedPosition);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(1L));
    }

    [Fact]
    public async Task Child_that_stopped_following_refuses_a_write_with_Unavailable_and_the_master_changes_nothing()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var damaged = false;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction || damaged)
            {
                return [frame];
            }

            damaged = true;
            return [frame.WithDamagedCheck()];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, name: 8));
        await relay.ChildEndedAsync();
        var position = master.Position;

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await child.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(2, name: 8)));

        Assert.Equal(LatticeErrorEnum.Unavailable, exception.Error);
        Assert.Equal(position, master.Position);
        Assert.Null(master.Set<PieceSchema.Piece>().Get(2L));
    }
}
