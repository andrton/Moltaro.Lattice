namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed record QueueMemoryMeasurement(int CommandBytes, int RetainedCount,
    long EmptyQueueManagedBytes, long ItemsManagedBytes, double ManagedBytesPerItem)
{
    public double Estimate(int count) => EmptyQueueManagedBytes + count * ManagedBytesPerItem;

    public static QueueMemoryMeasurement Measure(int commandBytes)
    {
        // Warm lazy runtime state before measuring retention; no workload threads run here.
        _ = new PendingWrite(new Mutation(1, 0), commandBytes).Completion.Task;
        var before = GC.GetTotalMemory(forceFullCollection: true);
        var queue = new PendingWriteQueue();
        var empty = GC.GetTotalMemory(forceFullCollection: true);
        var count = 0;
        while (queue.TryAdd(new Mutation(count + 1L, 0), commandBytes))
        {
            count++;
        }

        var retained = GC.GetTotalMemory(forceFullCollection: true);
        var result = new QueueMemoryMeasurement(commandBytes, count, empty - before,
            retained - empty, (retained - empty) / (double)count);
        GC.KeepAlive(queue);
        return result;
    }
}
