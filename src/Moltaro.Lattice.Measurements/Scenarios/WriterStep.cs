using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Measurements.Scenarios;

// A step of a window (StepWindow) that calls a writer given as a delegate, whose closure keeps what the
// writer changes from one write to the next; the call through the delegate is small beside a write.
// Counts the writes the call made.
internal readonly struct WriterStep(Func<int> write) : IWindowStep
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int Run(bool counting, long counted) => write();
}
