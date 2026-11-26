using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Loading;

// A child applies a transaction whole or not at all (docs/decisions/replication-and-hosting.md).
// An exception during preparation must leave no root blocks or root lifetimes belonging to that
// attempt. The first loaded root and a live full-root transaction share the preparation.
public sealed class ReceivedRootRollbackTests
{
    private const int LargeName = 512;
    private const int DataBytes = 200;

    [Fact]
    public async Task A_child_returns_everything_prepared_when_its_first_loaded_root_cannot_be_recorded()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var committed = await master.Set<PieceSchema.Piece>().InsertAsync(PieceSchema.Of(1, LargeName, data: DataBytes));
        var allocationFailure = new OutOfMemoryException("The list of prepared roots could not grow.");
        var allocator = new CountingNativeAllocator();
        (Arena Arena, RootTable Roots)? receiving = null;
        (int RootEntries, long PreparedBytes)? beforeDispose = null;

        await using var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(master.ListenUri!), new EngineTestInputs
        {
            Allocator = allocator,
            BeforeReceivedRootListGrowth = (count, _, arena, roots) =>
            {
                Assert.Equal(0, count);
                receiving = (arena, roots);
                throw allocationFailure;
            },
            AfterCloseStatePublication = () =>
            {
                if (receiving is { } parts)
                {
                    beforeDispose = (parts.Roots.Count, parts.Arena.Counters.PreparedBytes);
                }
            },
        });

        var failure = await Assert.ThrowsAsync<OutOfMemoryException>(() => child.OpenAsync().AsTask().WaitAsync(Bound));
        Assert.Same(allocationFailure, failure);
        Assert.Equal(LatticeDatabaseStateEnum.Closed, child.State);
        allocator.ExpectEverythingReturned();

        await using var witness = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(committed.Position, witness.Position);
        var loaded = Assert.IsType<PieceSchema.Piece>(witness.Set<PieceSchema.Piece>().Get(1L));
        Assert.Equal(LargeName, loaded.Name.Length);
        Assert.Equal(DataBytes, loaded.Data?.Length);

        Assert.Equal((0, 0L), beforeDispose);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_child_keeps_its_applied_prefix_and_returns_prepared_roots_when_the_fifth_full_write_cannot_be_recorded(bool replaces)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var source = master.Set<PieceSchema.Piece>();
        var initial = await source.InsertAsync(PieceSchema.Of(1, name: 8));
        var allocationFailure = new OutOfMemoryException("The list of prepared roots could not grow.");
        var loadGrowths = 0;
        (int Count, uint Member)? refusedGrowth = null;

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            DisableBackgroundMaintenance = true,
            BeforeReceivedRootListGrowth = (count, member, _, _) =>
            {
                if (count == 0)
                {
                    Interlocked.Increment(ref loadGrowths);
                    return;
                }

                refusedGrowth = (count, member);
                throw allocationFailure;
            },
        });
        await using var witness = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(initial.Position, child.Position);

        var fits = master.CreateWriteBatch();
        for (var key = 10L; key < 14; key++)
        {
            fits.Insert(PieceSchema.Of(key, LargeName, data: DataBytes));
        }

        var control = await fits.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(control.Position, Bound);
        var childPieces = child.Set<PieceSchema.Piece>();
        long[] prefix = [1, 10, 11, 12, 13];
        Assert.Equal(prefix, childPieces.Enumerate().Select(piece => piece.Id).Order());
        Assert.Equal(1, Volatile.Read(ref loadGrowths));
        Assert.Null(refusedGrowth);
        var before = child.Counters;
        Assert.Equal((prefix.Length, 0L), (before.RootEntries, before.Arena.PreparedBytes));
        OwnershipAudit.Expect(child);

        var fails = master.CreateWriteBatch();
        for (var key = 20L; key < 24; key++)
        {
            fails.Insert(PieceSchema.Of(key, LargeName, data: DataBytes));
        }

        if (replaces)
        {
            fails.Replace(PieceSchema.Of(1, LargeName, data: DataBytes));
        }
        else
        {
            fails.Insert(PieceSchema.Of(24, LargeName, data: DataBytes));
        }

        var committed = await fails.CommitAsync().AsTask().WaitAsync(Bound);
        await witness.WaitForPositionAsync(committed.Position, Bound);
        var witnessPieces = witness.Set<PieceSchema.Piece>();
        var last = Assert.IsType<PieceSchema.Piece>(witnessPieces.Get(replaces ? 1L : 24L));
        Assert.Equal(LargeName, last.Name.Length);
        Assert.Equal(DataBytes, last.Data?.Length);
        Assert.Equal(LargeName, witnessPieces.Get(20L)?.Name.Length);
        var expectedKeys = prefix.Concat([20L, 21L, 22L, 23L]);
        if (!replaces)
        {
            expectedKeys = expectedKeys.Append(24L);
        }

        Assert.Equal(expectedKeys, witnessPieces.Enumerate().Select(piece => piece.Id).Order());
        long[] changedKeys = [20, 21, 22, 23, replaces ? 1L : 24L];
        foreach (var key in changedKeys)
        {
            var expected = PieceSchema.Of(key, LargeName, data: DataBytes);
            var actual = Assert.IsType<PieceSchema.Piece>(witnessPieces.Get(key));
            Assert.Equal((expected.Id, expected.Name, expected.Note, expected.Count), (actual.Id, actual.Name, actual.Note, actual.Count));
            Assert.Equal(expected.Data, actual.Data);
        }

        OwnershipAudit.Expect(witness);

        // The child can no longer reach the commit. The position wait distinguishes loss of the
        // sender from a wait that could still succeed, using a timer beyond the test's own bound.
        var waiting = child.WaitForPositionAsync(committed.Position, ReplicationNodes.BeyondTheBound).AsTask();
        var unavailable = await Assert.ThrowsAsync<LatticeException>(() => waiting.WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.SynchronizationTimeout, unavailable.Error);
        Assert.Equal((4, 5u), refusedGrowth);
        Assert.Equal(control.Position, child.Position);
        Assert.Equal(prefix, childPieces.Enumerate().Select(piece => piece.Id).Order());
        Assert.Equal(8, childPieces.Get(1L)?.Name.Length);

        var after = child.Counters;
        Assert.Equal(
            (before.RootEntries, before.Arena.PreparedBytes, before.Arena.LiveBytes, before.Arena.RetiredBytes),
            (after.RootEntries, after.Arena.PreparedBytes, after.Arena.LiveBytes, after.Arena.RetiredBytes));
        OwnershipAudit.Expect(child);
    }
}
