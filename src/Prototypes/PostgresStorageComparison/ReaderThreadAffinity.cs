using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

// Optional local measurement control; only the calling dedicated reader thread is changed.
internal sealed class ReaderThreadAffinity : IDisposable
{
    private readonly nuint _previous;

    public ReaderThreadAffinity()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The pinned-reader comparison currently targets Windows.");
        }

        _previous = SetThreadAffinityMask(GetCurrentThread(), 1);
        if (_previous == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Dispose()
    {
        if (SetThreadAffinityMask(GetCurrentThread(), _previous) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
}
