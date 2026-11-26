using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal static class MeasurementEnvironment
{
    public static async Task<Snapshot> CaptureAsync(CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The development-5 measurement uses Windows counters.");
        }

        using var process = Process.GetCurrentProcess();
        var processor = (string?)Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString", null);
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        ChurnAudit.Require(GlobalMemoryStatusEx(ref memory), "Windows did not report available physical memory.");
        ChurnAudit.Require(GetSystemTimes(out var idle, out var kernel, out var user), "Windows did not report system activity.");
        await Task.Delay(TimeSpan.FromSeconds(1), token);
        ChurnAudit.Require(GetSystemTimes(out var afterIdle, out var afterKernel, out var afterUser), "Windows did not report system activity.");
        ulong total = afterKernel - kernel + afterUser - user;
        ChurnAudit.Require(total > 0, "The Windows idle sample had no elapsed CPU ticks.");
        return new Snapshot(processor?.Trim(), RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
            RuntimeInformation.ProcessArchitecture.ToString(), (long)process.ProcessorAffinity, Environment.ProcessorCount,
            memory.TotalPhysical, memory.AvailablePhysical, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            (double)(afterIdle - idle) / total);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint Load;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    internal sealed record Snapshot(string? Processor, string OperatingSystem, string Runtime, string Architecture,
        long Affinity, int LogicalProcessors, ulong PhysicalMemory, ulong AvailablePhysicalMemory,
        long GcAvailableMemory, double OneSecondMachineIdleFraction);
}
