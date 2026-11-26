using System.Diagnostics;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal static class ChurnPacing
{
    // Absolute per-process deadlines. Short Linux sleeps avoid occupying the reader CPUs
    // while waiting for their next burst; finish the wait against the monotonic clock.
    public static void WaitUntil(long due, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            long remaining = due - Stopwatch.GetTimestamp();
            if (remaining <= 0)
            {
                return;
            }

            if (OperatingSystem.IsLinux() && remaining > 3 * Stopwatch.Frequency / 1000)
            {
                Thread.Sleep(1);
            }
            else if (remaining > Stopwatch.Frequency / 1000)
            {
                Thread.Yield();
            }
            else
            {
                Thread.SpinWait(32);
            }
        }
    }
}
