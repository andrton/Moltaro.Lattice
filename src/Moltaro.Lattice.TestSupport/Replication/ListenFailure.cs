using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.TestSupport.Replication;

// What a test says when the system refuses the socket of a master or of a relay it opens. A system short
// of memory, buffers or handles refuses a socket with a message such as "the system lacked sufficient
// buffer space or a queue was full", which alone does not say what was short. The helpers that open a
// listening master or a relay rethrow such a failure with the state of the system in its message
// (Describe) and the original as its inner exception, so that the output kept of a failed run explains
// it. For diagnosis only: nothing is retried, and the code of the failure stays what the engine reported.
//
// The state is read when the failure has happened, not at the moment the system refused the socket, and
// it covers the whole machine where the system reports it: the commit charge and its limit, the physical
// memory available and the TCP listeners and connections, beside the handles and threads of this process.
internal static class ListenFailure
{
    private const long Mebibyte = 1024 * 1024;

    // Awaits the open of a master that listens. An open that fails because the system refused its socket,
    // a LatticeException whose inner exception is a SocketException, is rethrown as a LatticeException with
    // the same code, the message followed by the state of the system, and the original as its inner
    // exception. Every other outcome passes unchanged.
    public static async Task ExplainAsync(Task open)
    {
        try
        {
            await open;
        }
        catch (LatticeException exception) when (exception.InnerException is SocketException refused)
        {
            throw new LatticeException(exception.Error, $"{exception.Message} {Describe(refused)}", exception);
        }
    }

    // One line: the error the system reported for the socket and what the system had left. The line is
    // read when the system is short of something, so a failure to read it is written into the line in
    // its place: the capture must never take the place of the failure it explains.
    public static string Describe(SocketException refused)
    {
        try
        {
            var (commitCharge, commitLimit, availablePhysical) = Memory();
            using var process = Process.GetCurrentProcess();
            var tcp = IPGlobalProperties.GetIPGlobalProperties();

            return $"The state of the system after the failure: socket error {refused.SocketErrorCode} (native {refused.NativeErrorCode}); "
                + $"commit charge {commitCharge / Mebibyte} of {commitLimit / Mebibyte} MiB; available physical memory {availablePhysical / Mebibyte} MiB; "
                + $"this process holds {process.HandleCount} handles and {process.Threads.Count} threads; "
                + $"the machine has {tcp.GetActiveTcpListeners().Length} TCP listeners and {tcp.GetActiveTcpConnections().Length} TCP connections.";
        }
        catch (Exception exception)
        {
            return $"The state of the system after the failure could not be read: socket error {refused.SocketErrorCode} (native {refused.NativeErrorCode}); {exception.GetType().Name}: {exception.Message}";
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
