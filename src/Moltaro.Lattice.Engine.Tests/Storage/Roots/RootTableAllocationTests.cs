using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Roots;

// The root table allocates managed memory only for its pages (Q16, "Managed arrays of structs for
// control entries": the table grows by adding arrays). Release allocates nothing, and Add
// allocates only when it adds a page, also when it gives out a released slot again. Each call is
// measured alone, with GC.GetAllocatedBytesForCurrentThread around it; the same steps run first on
// another table, so that every path is taken once before it is measured, and the test runs in the
// test collection that keeps garbage collection out of the measured windows.
[Collection<ManagedAllocationCollection>]
public sealed class RootTableAllocationTests
{
    private const int Lifetimes = 3 * RootTable.EntriesPerPage;

    [Fact]
    public void Release_allocates_no_managed_memory_and_Add_allocates_only_when_it_adds_a_page()
    {
        Run();
        var calls = Run();

        // Every Release, and every Add that added no page.
        foreach (var call in calls)
        {
            if (!call.Add || call.PagesAfter == call.PagesBefore)
            {
                ManagedAllocationAssert.Nothing(call, call.ManagedBytes);
            }
        }

        // Some of the measured lifetimes took a released slot again.
        Assert.Contains(calls, call => call.Add && call.Handle.Generation > 1);
    }

    // Adds three pages of lifetimes, releases every other one, adds as many again, which takes the
    // released slots, and releases them all. Returns every call with what it allocated.
    private static List<Call> Run()
    {
        var table = new RootTable();
        var handles = new RootHandle[Lifetimes];
        var calls = new List<Call>(4 * Lifetimes);

        for (var index = 0; index < Lifetimes; index++)
        {
            handles[index] = Add(table, calls);
        }

        for (var index = 0; index < Lifetimes; index += 2)
        {
            Release(table, handles[index], calls);
        }

        for (var index = 0; index < Lifetimes; index += 2)
        {
            handles[index] = Add(table, calls);
        }

        foreach (var handle in handles)
        {
            Release(table, handle, calls);
        }

        return calls;
    }

    private static RootHandle Add(RootTable table, List<Call> calls)
    {
        var pages = table.Pages;
        var start = GC.GetAllocatedBytesForCurrentThread();

        var handle = table.Add(7);

        var managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        calls.Add(new Call(Add: true, handle, managedBytes, pages, table.Pages));

        return handle;
    }

    private static void Release(RootTable table, RootHandle handle, List<Call> calls)
    {
        var start = GC.GetAllocatedBytesForCurrentThread();

        table.Release(handle);

        var managedBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        calls.Add(new Call(Add: false, handle, managedBytes, table.Pages, table.Pages));
    }

    // One call of Add or Release.
    private readonly record struct Call(bool Add, RootHandle Handle, long ManagedBytes, int PagesBefore, int PagesAfter);
}
