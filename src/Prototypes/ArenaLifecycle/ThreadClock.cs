using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal static class ThreadClock
{
    // Windows diagnostic worker only. CPU times are in 100 ns units. Cycle counts
    // are reported separately and must not be converted to elapsed time.
    public static (long CpuTicks, ulong Cycles) Read()
    {
        var thread = new IntPtr(-2);
        Check.That(GetThreadTimes(thread, out _, out _, out var kernel, out var user), "GetThreadTimes failed.");
        Check.That(QueryThreadCycleTime(thread, out var cycles), "QueryThreadCycleTime failed.");
        return (kernel + user, cycles);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(IntPtr thread, out ulong cycles);
}
