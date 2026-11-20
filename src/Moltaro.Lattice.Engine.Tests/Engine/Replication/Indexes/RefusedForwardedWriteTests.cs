using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Refusals;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Indexes;

// A write a child forwards is executed by its master as its own (docs/design/ordered-index.md, "A child"):
// one whose index nodes the arena of the master cannot hold is refused as a write the memory budget cannot
// hold is, before its commit and with no effect (decision on write admission, Q14), and the call on the
// child ends in that refusal as the master gave it, WriteRejected with Limit Memory (Forwarding.Forwarder).
// The master has an arena full but for the room of a root and its texts, whose fill ends by a refusal of a
// filler and leaves no free slot of the class of the nodes (FullArena); its child has the default memory
// limit. Second, inserted through the child, is refused for memory: neither node holds
// anything of it, each holds what it held (HeldState), a search on each finds nothing of Second, and both
// stay Ready. Once the master has freed memory, the same insert through the child succeeds; it returns
// once the child has applied it, and every index of both nodes finds it.
public sealed class RefusedForwardedWriteTests
{
    [Fact]
    public async Task Write_forwarded_through_a_child_whose_index_nodes_the_master_cannot_hold_is_refused_for_memory_on_the_child_and_succeeds_once_the_master_has_freed_memory()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(FullArena.Schema, new EngineTestInputs { Arena = FullArena.Options });
        await FullArena.FillAsync(master);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var ranked = child.Set<IndexSchema.Ranked>();

        FullArena.ExpectFull(master);
        var heldByMaster = HeldState.Of(master);
        var heldByChild = HeldState.Of(child);

        await FullArena.ExpectRefusedAsync(ranked.InsertAsync(FullArena.Second).AsTask());

        foreach (var (node, held) in new[] { (master, heldByMaster), (child, heldByChild) })
        {
            Assert.Equal(LatticeDatabaseStateEnum.Ready, node.State);
            held.ExpectHeldBy(node);
            Assert.Null(node.Set<IndexSchema.Ranked>().Get(FullArena.Second.Id));
            RankedSearch.ExpectFound(node, FullArena.Second, []);
            RankedSearch.ExpectFound(node, FullArena.First, [1L]);
        }

        await FullArena.FreeAsync(master, 1, 10);
        await ranked.InsertAsync(FullArena.Second);

        foreach (var node in new[] { master, child })
        {
            RankedSearch.ExpectFound(node, FullArena.Second, [2L]);
            RankedSearch.ExpectFound(node, FullArena.First, [1L]);
        }
    }
}
