using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Positions;

// A test captures the clock while the node is Ready. It can then verify the last
// published position even after the node can no longer serve that public read.
internal static class ObservedClock
{
    public static PositionClock Of(EngineDatabase database) => PartsOf(database)!.Clock;

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_parts")]
    private static extern ref DatabaseParts? PartsOf(EngineDatabase database);
}
