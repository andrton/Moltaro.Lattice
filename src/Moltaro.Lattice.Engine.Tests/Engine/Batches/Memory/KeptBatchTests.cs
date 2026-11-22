using System.Buffers;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Batches.Memory;

// A write batch kept after its commit keeps no root alive (the batch of the core, Access.Batches.WriteBatch,
// lets go of its operations when it is committed, and the record of the batch in the engine lets go of its
// members when the batch has its outcome, Writes.PendingWrite.LetGoOfInputs): the arrays of the roots encoded
// when its members were added are collected at the first collection after the commit, while the application
// still holds the batch, for a batch published in the hold of its call, in MemoryOnly, and for one that
// waited behind a held write of its entity (HeldPersistence). The record of a batch that waited is the
// source of the task of its commit, and whatever still holds that task holds the record: a caller that keeps
// the task, and the frame that completes a task made from it (ValueTask.AsTask), inside which the
// continuation of the caller can run. One member is small and one is held in pieces. The arrays are found
// through the operations of the batch of the core, which has no public surface for them, before the
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
                held = await HoldWriteOfFirstAccountAsync(accounts, storage);
            }

            var batch = database.CreateWriteBatch();
            var roots = AddAndWatch(batch, waits);

            await CommitAsync(batch, storage, held);

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

    // The caller keeps the task of the commit in a local across an await, as an application does that starts
    // the commit, awaits something else and then the commit, and still holds it at the collection.
    [Fact]
    public async Task Task_of_a_commit_kept_after_its_completion_keeps_no_root_alive()
    {
        var storage = new HeldPersistence(holding: false);
        var database = await HeldMaster.OpenAsync(storage);
        var accounts = database.Set<UniqueSchema.Account>();

        try
        {
            var held = await HoldWriteOfFirstAccountAsync(accounts, storage);
            var batch = database.CreateWriteBatch();
            var roots = AddAndWatch(batch, waits: true);
            var commit = batch.CommitAsync();

            Assert.False(commit.IsCompleted, "The batch did not wait behind the held write of its entity.");
            storage.ReleaseAll();
            await held.WaitAsync(Bound);
            await commit.AsTask().WaitAsync(Bound);

            Assert.NotEmpty(roots);
            Assert.Equal(0, Collect(roots));
            GC.KeepAlive(commit);
            GC.KeepAlive(batch);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // A replace of the first account whose commit the storage holds, so that a batch of that entity waits
    // behind it; its task completes once the test releases the storage.
    private static async Task<Task<LatticeWriteResult>> HoldWriteOfFirstAccountAsync(ILatticeSet<UniqueSchema.Account> accounts, HeldPersistence storage)
    {
        await accounts.InsertAsync(UniqueSchema.Of(1, "a"));
        storage.Hold();

        var held = accounts.ReplaceAsync(UniqueSchema.Of(1, "a", note: "held")).AsTask();

        await storage.NextAsync();

        return held;
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
