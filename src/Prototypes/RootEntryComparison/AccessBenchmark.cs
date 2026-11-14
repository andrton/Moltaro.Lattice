using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Prototypes.RootEntryComparison;

internal static class AccessBenchmark
{
    public const int Warmups = 2;
    public const int Samples = 7;
    public const int TargetOperations = 262144;
    private static ulong _observedChecksum;

    public static void Warmup<TAccess>(TAccess access, int[] trace) where TAccess : struct, IRootAccess
    {
        foreach (var operation in Enum.GetValues<OperationKindEnum>())
        {
            _observedChecksum = Execute(access, trace, 1, operation);
        }
    }

    public static OperationResult Measure<TAccess>(TAccess access, int[] trace, OperationKindEnum operation)
        where TAccess : struct, IRootAccess
    {
        var passes = Math.Max(1, TargetOperations / trace.Length);
        ulong expectedReadChecksum = 0;
        for (var index = 0; index < trace.Length; index++)
        {
            var entry = EntryData.Create(index);
            expectedReadChecksum += entry.Location + (ulong)entry.Revision;
        }
        expectedReadChecksum *= (ulong)passes;
        for (var warmup = 0; warmup < Warmups; warmup++)
        {
            _observedChecksum = Execute(access, trace, passes, operation);
        }

        var samples = new double[Samples];
        var counts = new[] { GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2) };
        var allocationBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var sample = 0; sample < samples.Length; sample++)
        {
            var started = Stopwatch.GetTimestamp();
            _observedChecksum = Execute(access, trace, passes, operation);
            var elapsed = Stopwatch.GetTimestamp() - started;
            samples[sample] = elapsed * (1_000_000_000.0 / Stopwatch.Frequency) / (passes * trace.Length);
            if (operation != OperationKindEnum.RandomUpdate && _observedChecksum != expectedReadChecksum)
            {
                throw new InvalidOperationException("Read checksum mismatch.");
            }
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocationBefore;
        var sorted = samples.Order().ToArray();
        return new OperationResult(operation.ToString(), passes * trace.Length, sorted[Samples / 2],
            samples, allocated, GC.CollectionCount(0) - counts[0], GC.CollectionCount(1) - counts[1],
            GC.CollectionCount(2) - counts[2]);
    }

    // Value-type accessors generate specialized loops, avoiding interface dispatch per entry.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong Execute<TAccess>(TAccess access, int[] trace, int passes, OperationKindEnum operation)
        where TAccess : struct, IRootAccess
    {
        ulong checksum = 0;
        for (var pass = 0; pass < passes; pass++)
        {
            switch (operation)
            {
                case OperationKindEnum.SequentialRead:
                    for (var index = 0; index < trace.Length; index++)
                    {
                        ref var entry = ref access.Get(index);
                        checksum += entry.Location + (ulong)entry.Revision;
                    }
                    break;
                case OperationKindEnum.RandomRead:
                    foreach (var index in trace)
                    {
                        ref var entry = ref access.Get(index);
                        checksum += entry.Location + (ulong)entry.Revision;
                    }
                    break;
                case OperationKindEnum.RandomUpdate:
                    foreach (var index in trace)
                    {
                        ref var entry = ref access.Get(index);
                        entry.Location ^= EntryData.UpdateMask;
                        entry.Revision++;
                        checksum += (ulong)entry.Revision;
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        return checksum;
    }
}
