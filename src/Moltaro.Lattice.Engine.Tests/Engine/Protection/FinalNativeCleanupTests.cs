using System.Numerics;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Close waits for protected reads before freeing native memory (arena-protocol.md,
// "Deferred reclamation"). The allocator accounts for each native segment independently
// of the closed database. A large retire queue reaches a managed allocation during the
// final drain even after every retirement has been reclaimed.
public sealed class FinalNativeCleanupTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Close_releases_native_memory_after_the_reader_leaves_even_if_the_final_drain_fails(
        bool useChild,
        bool failAtTrim)
    {
        var allocator = new CountingNativeAllocator();
        var failure = new OutOfMemoryException("The test failed the final retirement queue trim.");
        var trims = 0;
        var closeWaitingForTheReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inputs = new EngineTestInputs
        {
            Allocator = allocator,
            DisableBackgroundMaintenance = true,
            BeforeCloseReadWait = () => closeWaitingForTheReader.TrySetResult(),
            BeforeCloseRetiredTrim = () =>
            {
                Interlocked.Increment(ref trims);
                if (failAtTrim)
                {
                    throw failure;
                }
            },
        };

        var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema,
            useChild ? new EngineTestInputs { DisableBackgroundMaintenance = true } : inputs);
        await using var otherNode = useChild ? master : null;
        var masterPieces = master.Set<PieceSchema.Piece>();
        await masterPieces.InsertAsync(PieceSchema.Of(1, name: 8));
        await masterPieces.InsertAsync(PieceSchema.Of(2, name: 8));

        var database = useChild
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, inputs)
            : master;
        var pieces = database.Set("Piece");
        Task? closing = null;

        try
        {
            using var pause = new ViewerPause();
            var reading = OwnThread.Run(() =>
            {
                Assert.True(pieces.TryView(1L, pause, static (view, reader) =>
                {
                    reader.Hold();
                    return view["Count"].Get<int>();
                }, out var count));
                return count;
            });
            await pause.EnteredAsync(reading);

            // One replacement retires one root. Cross the documented 64 epoch
            // windows threshold without letting the retired roots be reclaimed.
            var epochSlots = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Environment.ProcessorCount));
            var replacements = 64 * epochSlots + 1;
            var replacement = PieceSchema.Of(2, name: 8);
            for (var version = 0; version < replacements; version++)
            {
                replacement.Count = version + 3;
                await masterPieces.ReplaceAsync(replacement);
            }

            if (useChild)
            {
                await database.WaitForPositionAsync(master.Position, Bound);
            }

            Assert.Equal(replacements + 2, pieces.Get(2L)?.Get<int>("Count"));
            Assert.True(allocator.Outstanding > 0);
            var returned = allocator.Returned;

            closing = database.CloseAsync().AsTask();
            Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
            Assert.Same(closeWaitingForTheReader.Task,
                await Task.WhenAny(closeWaitingForTheReader.Task, closing).WaitAsync(Bound));
            Assert.False(closing.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref trims));
            Assert.Equal(returned, allocator.Returned);

            pause.Release();
            Assert.Equal(1, await reading.WaitAsync(Bound));

            if (failAtTrim)
            {
                var closeFailure = await Assert.ThrowsAsync<OutOfMemoryException>(() => closing.WaitAsync(Bound));
                Assert.Same(failure, closeFailure);

                allocator.ExpectEverythingReturned();

                var disposeFailure = await Assert.ThrowsAsync<OutOfMemoryException>(
                    () => database.DisposeAsync().AsTask().WaitAsync(Bound));
                Assert.Same(failure, disposeFailure);
            }
            else
            {
                await closing.WaitAsync(Bound);
                await database.DisposeAsync().AsTask().WaitAsync(Bound);
            }

            Assert.Equal(1, Volatile.Read(ref trims));
            allocator.ExpectEverythingReturned();
        }
        finally
        {
            if (closing is null)
            {
                await database.CloseAsync().AsTask().WaitAsync(Bound);
            }
        }
    }
}
