using System.Buffers;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Memory;

// A write batch kept after its commit keeps no root alive (the batch of the core, Access.Batches.WriteBatch,
// lets go of its operations when it is committed, and the write path of the record of the batch): the arrays
// of the roots encoded when its members were added are collected while the application still holds the
// batch, for a batch published in the hold of its call, in MemoryOnly, and for one that waited behind a
// held write of its entity (HeldPersistence), once the thread that gave it its outcome has left the hold
// that published it. One member is small and one is held in pieces. The arrays are
// found through the operations of the batch of the core, which has no public surface for them, before the
// commit. Account of UniqueSchema; every wait is bounded.
public sealed class KeptBatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_kept_after_its_commit_keeps_no_root_alive(bool waits)
    {
        var storage = new HeldPersistence(holding: false);
        var database = waits ? await HeldMaster.OpenAsync(storage) : await EngineMaster.OpenAsync(UniqueSchema.Schema);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            Task<LatticeWriteResult>? held = null;

            if (waits)
            {
                await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
                storage.Hold();
                held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "held")).AsTask();
                await storage.NextAsync();
            }

            var batch = database.CreateWriteBatch();
            var roots = AddAndWatch(batch, waits);

            await CommitAsync(batch, storage, held);

            // The thread that gave a batch that waited its outcome may still be leaving the hold that
            // published it, with the record of the batch in a local of its frame, which a Debug build keeps
            // to the end of the method; nothing the test can wait for marks that end, so the collection is
            // looked at again, within the bound, until the roots are gone.
            var watch = Stopwatch.StartNew();

            while (Collect(roots) > 0 && watch.Elapsed < Bound)
            {
                await Task.Delay(10);
            }

            Assert.NotEmpty(roots);
            Assert.Equal(0, Collect(roots));
            GC.KeepAlive(batch);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The roots still alive after a full collection.
    private static int Collect(List<WeakReference> roots)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        return roots.Count(root => root.IsAlive);
    }

    // The commit, apart from the test, so that no task of it stays on the stack of the test; a batch that
    // waits behind the held write ends once that write is published.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CommitAsync(ILatticeWriteBatch batch, HeldPersistence storage, Task<LatticeWriteResult>? held)
    {
        var committed = batch.CommitAsync().AsTask();

        if (held is not null)
        {
            Assert.False(committed.IsCompleted, "The batch did not wait behind the held write of its entity.");
            storage.ReleaseAll();
            await held.WaitAsync(Bound);
        }

        await committed.WaitAsync(Bound);
    }

    // Adds a small member and one held in pieces, of the entity of the held write when the batch waits, and
    // returns a weak reference to every array of their roots.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> AddAndWatch(ILatticeWriteBatch batch, bool waits)
    {
        if (waits)
        {
            batch.Replace(UniqueSchema.Of(1, "a", note: "batch"));
        }

        batch.Insert(UniqueSchema.Of(2, "b", data: 1_000));
        batch.Insert(UniqueSchema.Of(3, "c", data: 200_000));

        var watched = new List<WeakReference>();

        foreach (var root in RootsOf(batch))
        {
            foreach (var memory in root)
            {
                Assert.True(MemoryMarshal.TryGetArray(memory, out var segment));
                watched.Add(new WeakReference(segment.Array));
            }
        }

        return watched;
    }

    // The roots the batch of the core holds, read from its list of operations, which has no public surface.
    private static List<ReadOnlySequence<byte>> RootsOf(ILatticeWriteBatch batch)
    {
        var operations = (IList)batch.GetType().GetField("_operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(batch)!;
        var roots = new List<ReadOnlySequence<byte>>();

        foreach (var operation in operations)
        {
            roots.Add((ReadOnlySequence<byte>)operation.GetType().GetProperty("Root")!.GetValue(operation)!);
        }

        return roots;
    }
}
