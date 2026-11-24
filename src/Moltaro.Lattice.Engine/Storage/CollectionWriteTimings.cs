#if LATTICE_COLLECTION_TIMINGS
using System.Diagnostics;

namespace Moltaro.Lattice.Storage;

// Timing cells for an isolated measurement build. The scenario makes one synchronous
// write at a time and reads the cells on the caller that took the write lock.
internal static class CollectionWriteTimings
{
    [ThreadStatic]
    private static long _admit;

    [ThreadStatic]
    private static long _allocate;

    [ThreadStatic]
    private static long _populate;

    public static long Start() => Stopwatch.GetTimestamp();

    public static void EndAdmission(long started) => _admit += Stopwatch.GetTimestamp() - started;

    public static void EndAllocation(long started) => _allocate += Stopwatch.GetTimestamp() - started;

    public static void EndPopulation(long started) => _populate += Stopwatch.GetTimestamp() - started;

    public static (long Admit, long Allocate, long Populate) Take()
    {
        var result = (_admit, _allocate, _populate);
        _admit = 0;
        _allocate = 0;
        _populate = 0;
        return result;
    }
}
#endif
