using System.Buffers;
using Moltaro.Lattice.Engine.Child.Loading;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// Capture supplies all of its roots before replay. A sparse effect missing its
// root can be explained by a later Delete in the same catch-up interval.
// A full Write cannot clear that outstanding obligation (replication-and-hosting.md).
public sealed class PartialCatchUpTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData("deleted without a local root")]
    [InlineData("deleted after a full write")]
    [InlineData("still marked after a full write")]
    [InlineData("malformed absent-root effect")]
    public async Task A_child_must_resolve_a_missing_partial_target_by_a_delete_at_or_before_readiness(string tail)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));
        var start = PositionSequence.Of(master.Position);
        await using var witnessLink = FrameRelay.Start(master.ListenUri!);
        await using var witness = await ReplicationNodes.OpenChildAsync(witnessLink.Address);

        var update = items.CreateUpdate("ada");
        update.Assign("Login", "sparse");
        var sparse = await update.CommitAsync();
        var whole = UpdateSchema.Item("ada");
        whole.Login = "replacement";
        var wrote = await items.ReplaceAsync(whole);
        var deleted = await items.DeleteAsync("ADA");
        await witness.WaitForPositionAsync(deleted.Position, Bound);

        var first = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(witnessLink, sparse.Position));
        byte[] second = tail == "deleted without a local root"
            ? [TransactionPayload.Unchanged]
            : TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(witnessLink, wrote.Position));
        byte[] third = tail == "still marked after a full write"
            ? [TransactionPayload.Unchanged]
            : TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(witnessLink, deleted.Position));
        if (tail == "malformed absent-root effect")
        {
            // Keep the declared sparse body length and change only its last UTF-8 byte.
            first[^1] = byte.MaxValue;
        }

        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.LoadEnd)
            {
                return [frame];
            }

            var end = LoadEndPayload.Of(frame);
            Assert.Equal(start + 3, end.End);
            Assert.Equal(0UL, end.Roots);
            return
            [
                frame.WithPayload((end with { Start = start }).ToBytes()),
                TailFrame(frame, start + 1, 1, first),
                TailFrame(frame, start + 2, 2, second),
                TailFrame(frame, start + 3, 3, third),
            ];
        });
        await using var child = LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));
        if (tail is "still marked after a full write" or "malformed absent-root effect")
        {
            var error = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
            Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
            Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.Applied);
            return;
        }

        await child.OpenAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(deleted.Position, child.Position);
        Assert.Null(child.Set("Item").Get("ada"));
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData("covered")]
    [InlineData("malformed covered effect")]
    [InlineData("covered key named twice")]
    [InlineData("wrong older base")]
    public async Task Catch_up_checks_the_body_and_key_once_even_when_the_capture_covers_the_effect(string scenario)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        var baseline = UpdateSchema.Item("ada");
        var inserted = await items.InsertAsync(baseline);
        var start = PositionSequence.Of(inserted.Position);
        await using var witnessLink = FrameRelay.Start(master.ListenUri!);
        await using var witness = await ReplicationNodes.OpenChildAsync(witnessLink.Address);
        var oldCopy = Assert.Single(witnessLink.FromMaster, frame => frame.Kind == FrameKindEnum.Blocks).Payload;

        baseline.Login = "predecessor";
        var full = await items.ReplaceAsync(baseline);
        var update = items.CreateUpdate("ada");
        update.Assign("Login", "sparse");
        var sparse = await update.CommitAsync();
        await witness.WaitForPositionAsync(sparse.Position, Bound);

        var fullEffect = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(witnessLink, full.Position));
        var sparseEffect = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(witnessLink, sparse.Position));
        if (scenario == "malformed covered effect")
        {
            sparseEffect[^1] = byte.MaxValue;
        }
        else if (scenario == "covered key named twice")
        {
            sparseEffect = [.. sparseEffect, .. sparseEffect];
        }

        var older = scenario == "wrong older base";
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (older && frame.Kind == FrameKindEnum.Blocks)
            {
                return [frame.WithPayload(oldCopy)];
            }

            if (frame.Kind != FrameKindEnum.LoadEnd)
            {
                return [frame];
            }

            var end = LoadEndPayload.Of(frame);
            Assert.Equal(start + 2, end.End);
            Assert.Equal(1UL, end.Roots);
            return
            [
                frame.WithPayload((end with { Start = start }).ToBytes()),
                TailFrame(frame, start + 1, 1, older ? [TransactionPayload.Unchanged] : fullEffect),
                TailFrame(frame, start + 2, 2, sparseEffect),
            ];
        });
        await using var child = LatticeDatabase.Create(ReplicationNodes.ChildOptions(relay.Address, ReplicationNodes.BeyondTheBound));
        if (scenario != "covered")
        {
            var error = await Assert.ThrowsAsync<LatticeException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));
            Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
            Assert.DoesNotContain(relay.FromChild, frame => frame.Kind == FrameKindEnum.Applied);
            return;
        }

        await child.OpenAsync().AsTask().WaitAsync(Bound);
        Assert.Equal("sparse", child.Set<UpdateItem>().Get("ada")!.Login);
        Assert.Equal(sparse.Position, child.Position);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_catch_up_transaction_can_be_retried_at_the_same_sequence_without_leaking_marker_edits(bool clearing)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        var inserted = await items.InsertAsync(UpdateSchema.Item("ada"));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var witness = await ReplicationNodes.OpenChildAsync(relay.Address);
        var update = items.CreateUpdate("ada");
        update.Assign("Login", "sparse");
        var changed = await update.CommitAsync();
        var deleted = await items.DeleteAsync("ada");
        await witness.WaitForPositionAsync(deleted.Position, Bound);
        var sparse = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(relay, changed.Position));
        var delete = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(relay, deleted.Position));

        using var arena = new Arena(new ArenaOptions { MemoryLimit = 4L * new ArenaOptions().SegmentSize });
        var (received, roots, clock) = Receiver(arena);
        var start = PositionSequence.Of(inserted.Position);
        received.EndLoad(start);
        if (clearing)
        {
            received.Apply(start + 1, new ReadOnlySequence<byte>(sparse), CancellationToken.None);
        }

        var attemptedSequence = clock.Sequence + 1;
        byte[] rejected = [.. (clearing ? delete : sparse), byte.MaxValue];
        var refused = Assert.Throws<LatticeException>(() => received.Apply(attemptedSequence, new ReadOnlySequence<byte>(rejected), CancellationToken.None));
        Assert.Equal(LatticeErrorEnum.ValidationFailed, refused.Error);
        Assert.Equal(attemptedSequence - 1, clock.Sequence);
        Assert.Equal(0, roots.Count);

        received.Apply(attemptedSequence, new ReadOnlySequence<byte>(new byte[] { TransactionPayload.Unchanged }), CancellationToken.None);
        Assert.Equal(attemptedSequence, clock.Sequence);
        if (clearing)
        {
            Assert.Throws<LatticeException>(received.ValidateUniqueIndexes);
            received.Apply(attemptedSequence + 1, new ReadOnlySequence<byte>(delete), CancellationToken.None);
        }

        received.ValidateUniqueIndexes();
        Assert.Equal(0, roots.Count);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Retained_missing_target_keys_have_a_load_budget_separate_from_arena_blocks()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var witness = await ReplicationNodes.OpenChildAsync(relay.Address);
        var update = items.CreateUpdate("ada");
        update.Assign("Login", "sparse");
        var changed = await update.CommitAsync();
        await witness.WaitForPositionAsync(changed.Position, Bound);
        var sparse = TransactionPayload.EffectsOf(TransactionPayload.SingleFrameAt(relay, changed.Position));

        var arenaOptions = new ArenaOptions();
        using var arena = new Arena(new ArenaOptions { MemoryLimit = arenaOptions.SegmentSize });
        var (received, roots, clock) = Receiver(arena);
        received.EndLoad(captureStart: 1);
        var random = new Random(42);
        var key = new byte[32 * 1024];
        var largestAccepted = (ulong)arenaOptions.SegmentSize / (ulong)key.Length;
        var refusedAt = 0UL;
        for (var sequence = 2UL; sequence <= largestAccepted + 2; sequence++)
        {
            for (var index = 0; index < key.Length; index++)
            {
                key[index] = (byte)('a' + random.Next(26));
            }

            var effects = new ReadOnlySequence<byte>(SparseUpdatePayload.WithStringKey(sparse, key));
            try
            {
                received.Apply(sequence, effects, CancellationToken.None);
            }
            catch (LatticeException error)
            {
                Assert.Equal(LatticeErrorEnum.Unavailable, error.Error);
                refusedAt = sequence;
                break;
            }
        }

        Assert.InRange(refusedAt, 3UL, largestAccepted + 2);
        Assert.Equal(refusedAt - 1, clock.Sequence);
        Assert.Equal(0, roots.Count);
        Assert.Equal(0, arena.Counters.LiveBytes);
        Assert.Equal(0, arena.Counters.PreparedBytes);
    }

    private static (ReceivedChanges Received, RootTable Roots, PositionClock Clock) Receiver(Arena arena)
    {
        var owner = new object();
        var roots = new RootTable();
        var epochs = new ReadEpochs(owner);
        var retired = new RetiredMemory(epochs, arena, roots);
        var clock = new PositionClock(1, owner);
        var type = Assert.Single(UpdateSchema.Schema.EntityTypes);
        var table = EntityTable.Open(type, UpdateSchema.Schema, arena, roots, epochs, retired, clock, seed: 0);
        var tables = new EntityTables([table]);
        var publisher = new ChangePublisher(arena, roots, retired, clock, tables);
        var received = new ReceivedChanges(arena, roots, retired, clock, epochs, tables, publisher, new Lock(), new CloseBarrier(owner),
            () => Assert.Fail("No publication fault was expected."));
        return (received, roots, clock);
    }

    private static WireFrame TailFrame(WireFrame end, ulong sequence, ulong added, byte[] effects) =>
        WireFrame.Make(FrameKindEnum.Transaction, Protocol.EndsABody, correlationId: 0, end.StreamSequence + added,
            TransactionPayload.Make(sequence, effects));
}
