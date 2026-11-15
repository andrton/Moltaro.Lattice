using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Storage;

// The native allocator of an arena under test (exception X3: a fault point), for the tests of the
// arena and of the values stored in it. It obtains and returns memory through the native allocator,
// as the arena's default does, and keeps account of it: how many requests it served, how many
// returns it took and which memory is still outstanding. Memory returned that it did not give out,
// or that came back already, fails the test, and is not handed to the native allocator. When
// FailNextAllocation is set, the next request fails as the native allocator fails, with
// OutOfMemoryException, obtains nothing and clears the setting.
internal sealed class CountingNativeAllocator : NativeAllocator
{
    private readonly HashSet<nint> _outstanding = [];

    // How many times memory was obtained.
    public int Obtained { get; private set; }

    // How many times memory was returned.
    public int Returned { get; private set; }

    // How many pieces of memory were obtained and not returned.
    public int Outstanding => _outstanding.Count;

    public bool FailNextAllocation { get; set; }

    public override nint Allocate(int size)
    {
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
        Assert.True(_outstanding.Remove(memory), "The arena returned memory that was not outstanding.");

        Returned++;
        base.Free(memory);
    }
}
