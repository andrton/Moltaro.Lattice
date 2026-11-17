using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Measurements.Scenarios;

// How many processors the whole machine kept busy in a window, every process together, so that a series
// can show whether its load is below what the machine gives (WriteCallersScenario.Series). On Windows
// from GetSystemTimes, whose kernel time includes the idle time; elsewhere not read.
internal static class MachineProcessors
{
    // The busy time of every processor of the machine so far, in ticks of 100 ns, or null where it is
    // not read.
    public static long? BusyTicks()
    {
        if (!OperatingSystem.IsWindows() || !GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return null;
        }

        return kernel + user - idle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
}
