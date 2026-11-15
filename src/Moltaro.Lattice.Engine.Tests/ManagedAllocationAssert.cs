namespace Moltaro.Lattice.Engine.Tests;

// The assertion of the tests of ManagedAllocationCollection that a measured call allocated no
// managed memory. It names the call when it did, and allocates nothing while the call allocated
// nothing: xunit's assertions over a sequence of items allocate for every item, and a test that
// measures allocation over thousands of calls should not be the largest allocator of the suite.
internal static class ManagedAllocationAssert
{
    public static void Nothing<TCall>(TCall call, long managedBytes)
    {
        if (managedBytes != 0)
        {
            Assert.Fail($"{call} allocated {managedBytes} bytes of managed memory.");
        }
    }
}
