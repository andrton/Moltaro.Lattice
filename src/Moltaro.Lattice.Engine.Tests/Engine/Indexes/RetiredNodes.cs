using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// How many nodes of its indexes a write retires: what tells a write that leaves an entry byte for byte as it
// was, and copies no node of its index, from one that rewrites or moves the entry, which copies at least the
// leaf that holds it and retires the old one (docs/design/ordered-index.md, "Writes", "The retired node").
// The write runs while a read is held inside the protection of reads (HeldRead), so that nothing it retires
// is freed before it is counted; the retired bytes then are the blocks of the root it replaced and the
// nodes it replaced. The read is let go and what was retired drained afterwards. Retired nodes are not
// prepared memory, so the count does not depend on how many nodes the pool of an index keeps.
internal static class RetiredNodes
{
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // The nodes the write retires; the root it replaces is of the entity type given and holds inline the
    // String values of the lengths given, as StoredBytes places them.
    public static async Task<long> OfAsync(EngineDatabase database, Func<long?> heldRead, Func<Task> write, string replacedType, params int[] inlineLengths)
    {
        Assert.True(database.Schema.TryGetEntityType(replacedType, out var type));

        var layout = RootLayout.Compile(type, database.Schema);
        var root = StoredBytes.Of(layout.FixedSize, inlineLengths);
        long retired;

        database.DrainRetired();

        using (var held = await HeldRead.StartAsync(database, heldRead))
        {
            await Task.Run(write).WaitAsync(Bound);
            retired = database.Counters.Arena.RetiredBytes;
            await held.ReleaseAsync();
        }

        database.DrainRetired();

        var nodeBytes = retired - (root.LiveBytes + root.ClassSlackBytes);

        Assert.True(nodeBytes >= 0 && nodeBytes % IndexSchema.NodeBytes == 0, $"The write retired {retired} bytes, its replaced root {root.LiveBytes + root.ClassSlackBytes}: no whole number of nodes beside it.");

        return nodeBytes / IndexSchema.NodeBytes;
    }
}
