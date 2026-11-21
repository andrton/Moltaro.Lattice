using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.ManagedMemory;

// A write of one entity allocates no managed memory once it is warmed up: the constructor of the
// writer, every Set… member and Complete together, also when a value is chunked or text is encoded
// across a chunk boundary, and so do Abandon and RootStorage.Free of the root. The brief of S05 says
// so in the coordinator's note on managed memory, under "What the members do", and the engine zone
// of AGENTS.md puts memory and speed right after correctness: no allocation per entity on the paths
// that store and free it. The warm-up is what the arena of S04 needs for its segments: the first write obtains the segment, and
// the arena keeps it in its reserve when the root is freed, so the measured writes obtain none; it
// also gives the writer the array it collects the root block in, which it borrows for every write.
// The entity has a field of every type; see EveryValue.
[Collection<ManagedAllocationCollection>]
public sealed class WriteAllocationTests
{
    private const int WarmUpRounds = 3;

    private const int MeasuredRounds = 20;

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Writing_abandoning_and_freeing_a_root_allocate_no_managed_memory_after_warm_up(string encoding)
    {
        using var item = EveryType.Open(encoding);
        var values = new EveryValue(item);

        for (var round = 0; round < WarmUpRounds; round++)
        {
            RootStorage.Free(item.Arena, item.Layout, values.Write());
            values.WriteAndAbandon();
        }

        for (var round = 0; round < MeasuredRounds; round++)
        {
            var start = GC.GetAllocatedBytesForCurrentThread();
            var root = values.Write();
            var written = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            RootStorage.Free(item.Arena, item.Layout, root);
            var freed = GC.GetAllocatedBytesForCurrentThread() - start;

            start = GC.GetAllocatedBytesForCurrentThread();
            values.WriteAndAbandon();
            var abandoned = GC.GetAllocatedBytesForCurrentThread() - start;

            ManagedAllocationAssert.Nothing("A write of the entity", written);
            ManagedAllocationAssert.Nothing("RootStorage.Free", freed);
            ManagedAllocationAssert.Nothing("A write that was abandoned", abandoned);
        }

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
    }
}
