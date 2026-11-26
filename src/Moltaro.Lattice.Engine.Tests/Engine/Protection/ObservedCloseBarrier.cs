using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Protection;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Observes when close has finished waiting for counted operations. The full CloseAsync task can
// also be waiting for the child connection or the lock of the writes.
internal static class ObservedCloseBarrier
{
    public static Task DrainedOf(EngineDatabase database) => DrainSourceOf(BarrierOf(database)).Task;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_barrier")]
    private static extern ref CloseBarrier BarrierOf(EngineDatabase database);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_drained")]
    private static extern ref TaskCompletionSource DrainSourceOf(CloseBarrier barrier);
}
