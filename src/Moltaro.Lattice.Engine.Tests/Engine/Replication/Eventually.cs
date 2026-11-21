using System.Diagnostics;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// Waits for what a node does at its own pace: a child may lag behind its master, and a master learns that
// a child has gone only when its connection ends. The tests of Epic 1, sprint S11 wait for a child to be
// at a position without a wait for a position of the engine, which waits on a child are S12's (brief of
// S11, "Out of scope"): the condition is read about once a millisecond, outside every lock of the engine,
// and the test fails with the message given when it does not hold within the bound of these tests.
internal static class Eventually
{
    public static async Task HoldsAsync(Func<bool> condition, string failure)
    {
        var waited = Stopwatch.StartNew();

        while (!condition())
        {
            if (waited.Elapsed > Bound)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(1);
        }
    }

    // The child has applied the position: its own position is that one.
    public static Task AtAsync(ILatticeDatabase child, LatticePosition position) =>
        HoldsAsync(() => child.Position.Equals(position), $"The child did not reach the position {position} within the bound.");
}
