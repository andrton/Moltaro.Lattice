using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// The audit of the ownership of blocks (docs/design/arena-protocol.md, "Required bounded checks and
// measurements": complete native ownership and reference-count audits): every block of the arena is owned by
// exactly one live entity. It walks every live root (EngineDatabase.ForEachLiveRoot, StructureWalk) and checks
// what it reaches against two things the walk does not decide:
// - the header of each block: the root that reached it, the parent and the offset of the descriptor that
//   names it, its kind (AuditVisitor); and no block is reached twice, from one root or from two;
// - the counters of the arena: once retired memory is drained and no write is in progress, nothing is
//   prepared or retired, and the bytes of the slots of the live blocks, LiveBytes and ClassSlackBytes
//   together, are those of the blocks the walk reached, the root blocks and the blocks of every entry of the
//   text table a root refers to, each entry once. A block that no live root reaches, leaked, makes the
//   counters larger than the walk; a block freed while a root still names it fails its header or the arena.
// And the text table: it holds exactly the entries the roots refer to, each with as many references as Text
// descriptors name it. The schemas of the audit declare no secondary index, whose nodes and standing reserve
// are blocks no root owns.
internal static class OwnershipAudit
{
    public static void Expect(EngineDatabase database)
    {
        database.DrainRetired();

        var blocks = new HashSet<ulong>();
        var texts = new Dictionary<ulong, (uint Length, long References)>();
        var slotBytes = 0L;
        var roots = 0;

        database.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            Assert.True(blocks.Add(root.Value), $"The root block {root} is reached twice.");

            ref var header = ref arena.Header(root);

            Assert.Equal((handle, default(ArenaAddress), 0u, BlockKindEnum.Root), (header.Root, header.Parent, header.DescriptorOffset, header.Kind));

            var visitor = new AuditVisitor(handle, blocks, texts);

            StructureWalk.Walk(arena, layout, root, ref visitor);

            slotBytes += Arena.BlockSizeFor((int)header.PayloadLength) + visitor.SlotBytes;
            roots++;
        });

        foreach (var (_, (length, _)) in texts)
        {
            slotBytes += ValueBlocks.SlotBytesOf((int)length);
        }

        var counters = database.Counters;

        Assert.Equal(0, counters.Arena.PreparedBytes);
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(slotBytes, counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes);
        Assert.Equal(roots, counters.RootEntries);
        Assert.Equal(texts.Count, counters.TextEntries);
        Assert.Equal(
            texts.Values.Select(text => (text.Length, text.References)).Order(),
            database.TextReferences().Select(entry => ((uint)entry.Text.Length, entry.References)).Order());
    }
}
