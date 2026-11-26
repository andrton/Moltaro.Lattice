using System.Numerics;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Reclaiming retired roots is part of the storage protocol. Shrinking the empty
// managed queue is optional upkeep (arena-protocol.md, "Deferred reclamation").
// A refusal of that allocation after a coherent publication cannot fault a master
// or stop a child from applying and forwarding subsequent writes.
public sealed class PublishedRetirementTrimTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task A_failed_optional_trim_after_publication_leaves_the_node_able_to_write(
        bool useChild,
        bool failAtTrim)
    {
        var allocator = new CountingNativeAllocator();
        var trim = new TaskCompletionSource<(int Count, int Reserved, int Capacity)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trims = 0;
        var inputs = new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforePublishedRetiredTrim = (count, reserved, capacity) =>
            {
                Interlocked.Increment(ref trims);
                if (trim.TrySetResult((count, reserved, capacity)) && failAtTrim)
                {
                    throw new OutOfMemoryException("The test refused the empty retirement queue's replacement backing.");
                }
            },
        };

        var master = useChild
            ? await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema, new EngineTestInputs { DisableBackgroundMaintenance = true })
            : await EngineMaster.OpenAsync(PieceSchema.Schema, inputs);
        await using var masterLifetime = master;
        var masterPieces = master.Set<PieceSchema.Piece>();
        await masterPieces.InsertAsync(PieceSchema.Of(1, name: 8));
        await masterPieces.InsertAsync(PieceSchema.Of(2, name: 8));

        var child = useChild ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs) : null;
        await using var childLifetime = child;
        var subject = child ?? master;
        var pieces = subject.Set<PieceSchema.Piece>();
        var views = subject.Set("Piece");

        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(views.TryView(1L, pause, static (view, reader) =>
            {
                reader.Hold();
                return view["Count"].Get<int>();
            }, out var count));
            return count;
        });
        await pause.EnteredAsync(reading);

        // Each replacement retires one root. Cross the retained capacity threshold
        // and stop one write before the next synchronous look at the protected queue.
        var epochSlots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Environment.ProcessorCount));
        var highWater = 64 * epochSlots;
        var replacements = highWater + epochSlots - 1;
        var replacement = PieceSchema.Of(2, name: 8);
        for (var version = 0; version < replacements; version++)
        {
            replacement.Count = version + 3;
            await masterPieces.ReplaceAsync(replacement);
        }

        if (child is not null)
        {
            await child.WaitForPositionAsync(master.Position, Bound);
        }

        Assert.True(subject.Counters.Arena.RetiredBytes > 0);
        Assert.Equal(0, Volatile.Read(ref trims));
        Assert.True(pieces.TryGet(2L, out var before, out var oldRevision));
        Assert.Equal(replacements + 2, before.Count);
        var beforePosition = subject.Position;

        pause.Release();
        Assert.Equal(1, await reading.WaitAsync(Bound));

        replacement.Count++;
        var writing = pieces.ReplaceAsync(replacement, oldRevision).AsTask();
        if (await Task.WhenAny(trim.Task, writing).WaitAsync(Bound) == writing && !trim.Task.IsCompleted)
        {
            // Explain a refusal before the trim instead of waiting for an event it cannot reach.
            await writing;
        }

        var queue = await trim.Task.WaitAsync(Bound);
        Assert.Equal((0, 0), (queue.Count, queue.Reserved));
        Assert.True(queue.Capacity > highWater);
        Assert.Equal(1, Volatile.Read(ref trims));

        var applied = await writing.WaitAsync(Bound);
        Assert.True(applied.Changed);
        Assert.Equal(beforePosition.Sequence + 1, applied.Position.Sequence);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, subject.State);
        if (child is not null)
        {
            await child.WaitForPositionAsync(applied.Position, Bound);
        }

        Assert.Equal(applied.Position, subject.Position);
        Assert.True(pieces.TryGet(2L, out var visible, out var revision));
        Assert.Equal(replacement.Count, visible.Count);
        Assert.NotEqual(oldRevision, revision);
        var accounted = subject.Counters.Arena;
        Assert.Equal((0L, 0L), (accounted.RetiredBytes, accounted.PreparedBytes));

        // Reuse the published revision in a real new write, including forwarding on a child.
        replacement.Count++;
        var later = await pieces.ReplaceAsync(replacement, revision).AsTask().WaitAsync(Bound);
        Assert.True(later.Changed);
        Assert.Equal(applied.Position.Sequence + 1, later.Position.Sequence);
        Assert.Equal(later.Position, subject.Position);
        Assert.Equal(replacement.Count, pieces.Get(2L)?.Count);

        await subject.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }
}
