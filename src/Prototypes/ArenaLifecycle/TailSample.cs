namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct TailSample(string Operation, int Key, long Start, long End,
    long CpuTicks, ulong Cycles, int Gen0, int Gen1, int Gen2, long GcPauseTicks,
    long AcquireTicks, long ContendedAcquireTicks, int ContendedGates)
{
    public long ElapsedTicks => End - Start;
}
