using System.Reflection;
using System.Runtime;
using Xunit.v3;

namespace Moltaro.Lattice.Engine.Tests;

// Runs each test it applies to inside a region without garbage collections (GC.TryStartNoGCRegion):
// from Before to After the runtime starts no collection, neither a blocking nor a background one,
// while the whole process allocates less than Budget. Starting the region collects once, before
// the test, and waits for a collection already under way. After ends the region; when the process
// went beyond the budget, a collection ran during the test, and After fails it, because its
// measurement of managed allocation was not protected. Nothing is retried.
[AttributeUsage(AttributeTargets.Class)]
public sealed class WithoutCollectionsAttribute : BeforeAfterTestAttribute
{
    // Far more than a test of the collection allocates, a few MiB at most, with the test runner's
    // own allocations while it runs.
    private const long Budget = 64L * 1024 * 1024;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!GC.TryStartNoGCRegion(Budget))
        {
            throw new InvalidOperationException("The runtime could not set up a region without garbage collections for the test.");
        }
    }

    public override void After(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (GCSettings.LatencyMode != GCLatencyMode.NoGCRegion)
        {
            throw new InvalidOperationException("A garbage collection ran during the test, so its measurement of managed allocation was not protected.");
        }

        GC.EndNoGCRegion();
    }
}
