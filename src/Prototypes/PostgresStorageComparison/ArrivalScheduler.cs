using System.Diagnostics;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class ArrivalScheduler
{
    public static void WaitUntil(long timestamp, CancellationToken cancellationToken)
    {
        while (Stopwatch.GetTimestamp() < timestamp)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timestamp - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 1000)
            {
                Thread.Sleep(1);
            }
            else
            {
                Thread.SpinWait(32);
            }
        }
    }
}
