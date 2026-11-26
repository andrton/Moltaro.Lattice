using System.Collections.ObjectModel;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Storage;

// The native allocator of an arena under test (exception X3: a fault point), for the tests of the
// arena and of the values stored in it. It obtains and returns memory through the native allocator,
// as the arena's default does, and keeps account of it: how many requests it served, how many
// returns it took and which memory is still outstanding. Memory returned that it did not give out,
// or that came back already, fails the test, and is not handed to the native allocator. When
// FailNextAllocation is set, the next request fails as the native allocator fails, with
// OutOfMemoryException, obtains nothing and clears the setting; when FailAt is set, the request of that
// number fails so, counted from 1 over every request, failed ones included (Requests), and Failed tells
// whether it came.
//
// HoldNextAllocation runs an action once, at the next request, on the thread that makes it and
// before anything is obtained: a test of the engine holds a write there while it is being prepared,
// since the arena obtains a segment only for a block a preparation allocates. That thread holds the
// lock of the writes of the engine meanwhile, so the action waits for nothing that needs it.
internal sealed class CountingNativeAllocator : NativeAllocator
{
    private readonly HashSet<nint> _outstanding = [];

    private Action? _hold;
    private Action? _holdLastFree;

    public CountingNativeAllocator()
    {
        OutstandingAddresses = new ReadOnlySet<nint>(_outstanding);
    }

    // How many times memory was obtained.
    public int Obtained { get; private set; }

    // How many times memory was returned.
    public int Returned { get; private set; }

    // How many pieces of memory were obtained and not returned.
    public int Outstanding => _outstanding.Count;

    public IReadOnlyCollection<nint> OutstandingAddresses { get; }

    public bool FailNextAllocation { get; set; }

    // How many requests were made, failed ones included.
    public int Requests { get; private set; }

    // The number of the request that fails, from 1; 0 for none.
    public int FailAt { get; set; }

    // Whether the request FailAt names came and failed.
    public bool Failed { get; private set; }

    // Arms the action that the next request runs once.
    public void HoldNextAllocation(Action hold) => Volatile.Write(ref _hold, hold);

    // Runs once when Free is about to return the allocator's last outstanding piece.
    public void HoldLastFree(Action hold) => Volatile.Write(ref _holdLastFree, hold);

    // Fails the test unless every piece of memory obtained has been returned, exactly once: what a
    // test states once a database has closed or its open has failed.
    public void ExpectEverythingReturned()
    {
        Assert.Equal(0, Outstanding);
        Assert.Equal(Obtained, Returned);
    }

    public override nint Allocate(int size)
    {
        Interlocked.Exchange(ref _hold, null)?.Invoke();

        Requests++;

        if (Requests == FailAt)
        {
            Failed = true;

            throw new OutOfMemoryException("The test failed this native allocation.");
        }

        if (FailNextAllocation)
        {
            FailNextAllocation = false;

            throw new OutOfMemoryException("The test failed this native allocation.");
        }

        var memory = base.Allocate(size);

        _outstanding.Add(memory);
        Obtained++;

        return memory;
    }

    public override void Free(nint memory)
    {
        if (_outstanding.Count == 1)
        {
            Interlocked.Exchange(ref _holdLastFree, null)?.Invoke();
        }

        Assert.True(_outstanding.Remove(memory), "The arena returned memory that was not outstanding.");

        Returned++;
        base.Free(memory);
    }
}
