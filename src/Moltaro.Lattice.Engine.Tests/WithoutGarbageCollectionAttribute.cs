using System.Reflection;
using System.Runtime;
using Xunit.v3;

namespace Moltaro.Lattice.Engine.Tests;

// Runs each test it applies to inside a region without garbage collection (GC.TryStartNoGCRegion):
// from Before to After the runtime starts no garbage collection, neither a blocking nor a
// background one, while the whole process allocates less than Budget. Starting the region runs one
// garbage collection before the test and waits for one already under way. After ends the region;
// when the process went beyond the budget, a garbage collection ran during the test, and After
// fails the test, because its measurement of managed allocation was not protected. Nothing is
// retried.
//
// What the region needs from the host: starting it raised the private memory of the test process
// by 131 MiB, about twice the budget (Windows, .NET 10; another measurement found about 122 MiB).
// With a heap limit (DOTNET_GCHeapHardLimit) of 144 MiB or less the region cannot start, and the
// tests fail in Before with its message; a limit of 152 MiB was enough.
[AttributeUsage(AttributeTargets.Class)]
public sealed class WithoutGarbageCollectionAttribute : BeforeAfterTestAttribute
{
    // Between Before and After of one test the whole process allocated at most 4.3 MiB, the test
    // runner's allocations included (RootTableAllocationTests, GC.GetTotalAllocatedBytes, Windows,
    // .NET 10): the budget is about fifteen times that.
    private const long Budget = 64L * 1024 * 1024;

    public override void Before(MethodInfo methodUnderTest, IXunitTest test)
    {
        if (!GC.TryStartNoGCRegion(Budget))
        {
            throw new InvalidOperationException("The runtime could not set up a region without garbage collection for the test.");
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
