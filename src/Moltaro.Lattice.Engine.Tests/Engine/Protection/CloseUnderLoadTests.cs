using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// A close waits for the reads and writes in progress before it frees the arena, and then every
// operation is ObjectDisposedException (brief of Epic 1, sprint S06, "States"; milestone contract: a
// close cannot free an arena until outstanding protected scopes release it). The database is opened
// and closed in cycles; in each, readers through the class and by name and a writer that inserts,
// replaces and deletes run on threads of their own, each until an operation of it fails, and the
// close comes from another thread once each of them has made one operation. What every operation
// may end with:
// - a read: an entity of its own key, whole and of one written state, or absence; a write: success;
// - or ObjectDisposedException of the database itself. One that names another object comes from a
//   part of the database the close has already freed, which an operation in progress never reaches.
// An operation that begins after the close has completed is ObjectDisposedException. The close
// completes, and every operation ends, within a bound, so a close that never completes or an
// operation that never ends fails the test instead of hanging it. CloseAsync completes once the arena
// has been freed (EngineDatabase); the arena itself is not visible to the test, so that completion is
// what it states. CloseDuringReadsTests of Moltaro.Lattice.Tests states the same for readers alone,
// on both implementations.
public sealed class CloseUnderLoadTests
{
    private const int Cycles = 20;

    private const int Keys = 4;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // At least four readers; at most eight, beside the writer.
    private static int Readers => Math.Clamp(Environment.ProcessorCount, 4, 8);

    [Fact]
    public async Task Close_during_reads_and_writes_waits_for_them_and_every_operation_after_it_is_ObjectDisposedException()
    {
        for (var cycle = 0; cycle < Cycles; cycle++)
        {
            var database = await StampSchema.OpenAsync();
            var stamps = database.Set<StampSchema.Stamp>();
            var records = database.Set("Stamp");

            for (var key = 0; key < Keys; key++)
            {
                await stamps.InsertAsync(StampSchema.StampOf(key, 0));
            }

            using var started = new CountdownEvent(Readers + 1);
            using var closed = new ManualResetEventSlim();
            using var stop = new CancellationTokenSource(Bound);

            var workers = Enumerable.Range(0, Readers + 1)
                .Select(worker => Task.Factory.StartNew(
                    () => UntilClosed(started, closed, stop.Token, worker == Readers ? WriterOf(records) : ReaderOf(worker, stamps, records)),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default))
                .ToArray();

            Assert.True(started.Wait(Bound), "The readers and the writer did not start.");

            await Task.Run(() => database.CloseAsync().AsTask()).WaitAsync(Bound);
            closed.Set();

            await Task.WhenAll(workers).WaitAsync(Bound);

            Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
            ExpectDisposed(() => stamps.Get(1L));
            ExpectDisposed(() => records.Get(1L));
            ExpectDisposed(() => stamps.UpsertAsync(StampSchema.StampOf(1, 1)).AsTask().GetAwaiter().GetResult());
            ExpectDisposed(() => records.DeleteAsync(1L).AsTask().GetAwaiter().GetResult());
        }
    }

    // Repeats the operation until it fails with ObjectDisposedException of the database, and signals
    // after the first. Every other end fails the test.
    private static void UntilClosed(CountdownEvent started, ManualResetEventSlim closed, CancellationToken stop, Action operation)
    {
        var signalled = false;

        while (!stop.IsCancellationRequested)
        {
            var afterClose = closed.IsSet;

            try
            {
                operation();
            }
            catch (ObjectDisposedException exception)
            {
                Assert.Equal(typeof(EngineDatabase).FullName, exception.ObjectName);
                return;
            }
            finally
            {
                if (!signalled)
                {
                    signalled = true;
                    started.Signal();
                }
            }

            Assert.False(afterClose, "An operation that began after the close had completed succeeded.");
        }

        Assert.Fail("The close did not end the operations within the bound.");
    }

    // Reads the keys in turn, through the class or by name.
    private static Action ReaderOf(int reader, ILatticeSet<StampSchema.Stamp> stamps, ILatticeSet records)
    {
        var next = reader;
        var byClass = reader % 2 == 0;

        return () =>
        {
            var key = next++ % Keys;

            _ = byClass ? StampSchema.NumberOf(key, stamps.Get((long)key)) : StampSchema.NumberOf(key, records.Get((long)key));
        };
    }

    // Writes the keys in turn: inserts a key that is absent, deletes one that exists at every third
    // write and replaces it at the others.
    private static Action WriterOf(ILatticeSet records)
    {
        var exists = Enumerable.Repeat(true, Keys).ToArray();
        var write = 0;

        return () =>
        {
            write++;

            var key = write % Keys;
            LatticeWriteResult result;

            if (!exists[key])
            {
                result = records.InsertAsync(StampSchema.RecordOf(records.EntityType, key, write)).AsTask().GetAwaiter().GetResult();
                exists[key] = true;
            }
            else if (write % 3 == 0)
            {
                result = records.DeleteAsync((long)key).AsTask().GetAwaiter().GetResult();
                exists[key] = false;
            }
            else
            {
                result = records.ReplaceAsync(StampSchema.RecordOf(records.EntityType, key, write)).AsTask().GetAwaiter().GetResult();
            }

            Assert.True(result.Changed);
        };
    }

    private static void ExpectDisposed(Action operation)
    {
        var disposed = Assert.Throws<ObjectDisposedException>(operation);

        Assert.Equal(typeof(EngineDatabase).FullName, disposed.ObjectName);
    }
}
