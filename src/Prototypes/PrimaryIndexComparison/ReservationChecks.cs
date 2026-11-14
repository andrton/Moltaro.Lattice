namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal static class ReservationChecks
{
    public static void Run()
    {
        using var index = new ReservedArcIndex();
        index.Reserve(15);
        for (var key = 0; key < 15; key++)
        {
            index.AddReserved(key, (ulong)key + 1000);
        }

        index.Reserve(8);
        IntegrationChecks.Require(index.Inspect() == (15, 32, 8), "Pre-commit reserve state is incorrect.");
        IntegrationChecks.Require(!index.TryGet(15, out _), "Preparation published an uncommitted key.");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var key = 15; key < 23; key++)
        {
            index.AddReserved(key, (ulong)key + 1000);
        }

        var publicationAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        IntegrationChecks.Require(publicationAllocation == 0, "Prepared publication allocated memory.");

        index.Reserve(3);
        index.Release(3);
        var previous = index.Inspect();
        var rejected = false;
        try
        {
            index.Reserve(4096);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        IntegrationChecks.Require(rejected && index.Inspect() == previous,
            "Rejected preparation changed published data or capacity credits.");

        // Warm the removal path before observing repeated free-slot reuse.
        index.Remove(0);
        index.Reserve(1);
        index.AddReserved(0, 1000);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var round = 0; round < 16; round++)
        {
            for (var key = 0; key < 23; key++)
            {
                IntegrationChecks.Require(index.Remove(key), "Reserved-index removal failed.");
            }

            index.Reserve(23);
            for (var key = 0; key < 23; key++)
            {
                index.AddReserved(key, (ulong)key + 1000);
            }
        }

        var reuseAllocation = GC.GetAllocatedBytesForCurrentThread() - before;
        IntegrationChecks.Require(reuseAllocation == 0 && index.Inspect() == (23, 32, 0),
            "Repeated delete/reinsert did not reuse capacity without allocation.");
        CheckConcurrentReservations();
        Console.WriteLine($"Arc reservation: prepare/reject/cancel/publish/reuse passed; publication {publicationAllocation} B; 16 reuse cycles {reuseAllocation} B.");
    }

    private static void CheckConcurrentReservations()
    {
        using var index = new ReservedArcIndex();
        using var prepared = new CountdownEvent(2);
        using var commit = new ManualResetEventSlim();
        index.Reserve(15);
        for (var key = 0; key < 15; key++)
        {
            index.AddReserved(key, (ulong)key + 1000);
        }

        var writers = new Task[2];
        for (var worker = 0; worker < writers.Length; worker++)
        {
            var firstKey = 15 + worker * 8;
            writers[worker] = Task.Run(() =>
            {
                index.Reserve(8);
                prepared.Signal();
                IntegrationChecks.Require(commit.Wait(TimeSpan.FromSeconds(5)), "Simulated commit timed out.");
                for (var key = firstKey; key < firstKey + 8; key++)
                {
                    index.AddReserved(key, (ulong)key + 1000);
                }
            });
        }

        try
        {
            IntegrationChecks.Require(prepared.Wait(TimeSpan.FromSeconds(5)), "Concurrent preparation timed out.");
            IntegrationChecks.Require(index.Inspect() == (15, 32, 16), "Concurrent credits overbooked capacity.");
            IntegrationChecks.Require(index.TryGet(0, out var existing) && existing == 1000,
                "Reader could not access old state while commit was pending.");
            IntegrationChecks.Require(!index.TryGet(15, out _), "Pending write leaked to reader.");
            commit.Set();
            for (var pass = 0; pass < 100; pass++)
            {
                for (var key = 0; key < 31; key++)
                {
                    if (index.TryGet(key, out var locator))
                    {
                        IntegrationChecks.Require(locator == (ulong)key + 1000, "Concurrent lookup returned wrong locator.");
                    }
                }
            }
        }
        finally
        {
            commit.Set();
            IntegrationChecks.Require(Task.WaitAll(writers, TimeSpan.FromSeconds(5)), "Writers failed to finish.");
        }

        IntegrationChecks.Require(index.Inspect() == (31, 32, 0), "Concurrent publication lost entries or credits.");
        for (var key = 0; key < 31; key++)
        {
            IntegrationChecks.Require(index.TryGet(key, out var value) && value == (ulong)key + 1000,
                "Published locator was lost.");
        }
    }
}
