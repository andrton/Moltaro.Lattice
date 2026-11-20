using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.TestSupport.Diagnostics;

// What the system had left when a test failed in a way that a shortage of the machine may explain: a
// socket the system refused (Replication.ListenFailure), a child that did not apply a write in time. A
// helper of the tests writes Describe into the message of the failure it rethrows, so that the output
// kept of a failed run says it. For diagnosis only: nothing is retried.
//
// The state is read when the failure has reached the test, not at the moment it happened, and it covers
// the whole machine where the system reports it: the commit charge and its limit, the physical memory
// available and the TCP listeners and connections, beside the handles and threads of this process.
internal static class SystemState
{
    private const long Mebibyte = 1024 * 1024;

    // One sentence of every part of the state. It is read when the system may be short of something, so
    // a failure to read it is written in its place: the capture must never take the place of the failure
    // it explains.
    public static string Describe()
    {
        try
        {
            var (commitCharge, commitLimit, availablePhysical) = Memory();
            using var process = Process.GetCurrentProcess();
            var tcp = IPGlobalProperties.GetIPGlobalProperties();

            return "The state of the system after the failure: "
                + $"commit charge {commitCharge / Mebibyte} of {commitLimit / Mebibyte} MiB; available physical memory {availablePhysical / Mebibyte} MiB; "
                + $"this process holds {process.HandleCount} handles and {process.Threads.Count} threads; "
                + $"the machine has {tcp.GetActiveTcpListeners().Length} TCP listeners and {tcp.GetActiveTcpConnections().Length} TCP connections.";
        }
        catch (Exception exception)
        {
            return $"The state of the system after the failure could not be read: {exception.GetType().Name}: {exception.Message}";
        }
    }

    // The commit charge of the machine, its limit and the physical memory available, in bytes. Windows
    // reports them through GlobalMemoryStatusEx, whose page file total is the commit limit; Linux in
    // /proc/meminfo, in kibibytes.
    private static (long CommitCharge, long CommitLimit, long AvailablePhysical) Memory()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };

            if (!GlobalMemoryStatusEx(ref status))
            {
                throw new InvalidOperationException($"GlobalMemoryStatusEx failed with error {Marshal.GetLastPInvokeError()}.");
            }

            return ((long)(status.TotalPageFile - status.AvailablePageFile), (long)status.TotalPageFile, (long)status.AvailablePhysical);
        }

        var kibibytes = File.ReadLines("/proc/meminfo")
            .Select(line => line.Split(':', 2))
            .ToDictionary(parts => parts[0], parts => long.Parse(parts[1].Trim().Split(' ')[0], CultureInfo.InvariantCulture));

        return (kibibytes["Committed_AS"] * 1024, kibibytes["CommitLimit"] * 1024, kibibytes["MemAvailable"] * 1024);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    // MEMORYSTATUSEX of the Windows API, field for field.
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
