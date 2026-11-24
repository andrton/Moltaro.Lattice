using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Ordered;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// The audit of the ownership of blocks (docs/design/arena-protocol.md, "Required bounded checks and
// measurements": complete native ownership and reference-count audits): every block of the arena is owned by
// exactly one live entity, is a node of a secondary index or held by its pool, or belongs to an entry of the
// text table. It walks every live root (EngineDatabase.ForEachLiveRoot, StructureWalk) and checks what it
// reaches against things the walk does not decide:
// - the header of each block: the root that reached it, the parent and the offset of the descriptor that
//   names it, its kind (AuditVisitor); and no block is reached twice, from one root or from two;
// - the counters of the arena: once retired memory is drained and no write is in progress, nothing is
//   retired, what is prepared is the nodes the pools of the indexes hold (EngineDatabase.ReservedNodeBytes),
//   and the bytes of the slots of the live blocks, LiveBytes and ClassSlackBytes together, are those of the
//   blocks the walk reached, the root blocks, of the live nodes of every index, a block of the size of a
//   node of its index each (EngineDatabase.IndexNodes), and of the blocks of every entry of the text table a
//   root or a node refers to, each entry once. A block that no live root and no index reaches, leaked, makes
//   the counters larger than the walk; a block freed while a root still names it fails its header or the
//   arena.
// And the text table: it holds exactly the entries the roots and the nodes refer to, each with as many
// references as Text descriptors name it and as the entries and separators of live nodes hold it: an item of
// a node holds one for each String part longer than IndexLayout.TextCopyLimit bytes, which it shares with
// the entry its root names, or named when the item was copied (docs/design/ordered-index.md, "Text
// references"). The entries are told apart by their exact stored bytes, which the walk reads from the arena
// for the entries the roots name and which the encoding of the database gives for the keys of the nodes.
internal static class OwnershipAudit
{
    public static void Expect(EngineDatabase database)
    {
        database.DrainRetired();

        var blocks = new HashSet<ulong>();
        var texts = new Dictionary<ulong, (uint Length, long References)>();
        var textOf = new Dictionary<ulong, string>();
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

            foreach (var (address, (length, _)) in texts)
            {
                if (!textOf.ContainsKey(address))
                {
                    textOf[address] = StoredBytesOf(arena, address, length);
                }
            }
        });

        // The entries the items of the nodes of the indexes refer to, by their stored bytes: one the roots refer
        // to as well, or one that only nodes refer to.
        var addressOf = textOf.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);
        var onlyNodes = new Dictionary<string, long>(StringComparer.Ordinal);
        var encoding = database.Schema.StringEncoding == LatticeStringEncodingEnum.Utf8 ? Encoding.UTF8 : Encoding.Unicode;

        foreach (var entityType in database.Schema.EntityTypes)
        {
            var rootLayout = RootLayout.Compile(entityType, database.Schema);

            foreach (var index in entityType.Indexes)
            {
                var layout = IndexLayout.Compile(index, rootLayout);
                var contents = IndexContents.Of(database, entityType.Name, index.Name);

                slotBytes += (long)contents.Nodes * layout.NodeBytes;

                foreach (var key in contents.KeysInNodes)
                {
                    for (var place = 0; place < key.Length; place++)
                    {
                        if (!layout.Parts[place].IsText || key[place] is not string text || encoding.GetByteCount(text) <= IndexLayout.TextCopyLimit)
                        {
                            continue;
                        }

                        var stored = Encoding.Latin1.GetString(encoding.GetBytes(text));

                        if (addressOf.TryGetValue(stored, out var address))
                        {
                            texts[address] = (texts[address].Length, texts[address].References + 1);
                        }
                        else
                        {
                            onlyNodes[stored] = onlyNodes.GetValueOrDefault(stored) + 1;
                        }
                    }
                }
            }
        }

        foreach (var (_, (length, _)) in texts)
        {
            slotBytes += ValueBlocks.SlotBytesOf((int)length);
        }

        foreach (var (stored, _) in onlyNodes)
        {
            slotBytes += ValueBlocks.SlotBytesOf(stored.Length);
        }

        var counters = database.Counters;

        Assert.Equal(database.ReservedNodeBytes(), counters.Arena.PreparedBytes);
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(slotBytes, counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes);
        Assert.Equal(roots, counters.RootEntries);
        Assert.Equal(texts.Count + onlyNodes.Count, counters.TextEntries);
        Assert.Equal(
            texts.Values.Select(text => (text.Length, text.References)).Concat(onlyNodes.Select(text => ((uint)text.Key.Length, text.Value))).Order(),
            database.TextReferences().Select(entry => ((uint)entry.Text.Length, entry.References)).Order());

        // And the very bytes of each entry, which tell apart entries of one length.
        Assert.Equal(
            textOf.Select(text => (text.Value, texts[text.Key].References)).Concat(onlyNodes.Select(text => (text.Key, text.Value))).OrderBy(text => text.Item1, StringComparer.Ordinal),
            database.TextReferences().Select(entry => (Encoding.Latin1.GetString(entry.Text), entry.References)).OrderBy(text => text.Item1, StringComparer.Ordinal));
    }

    // The bytes the entry of the text table at the address holds, length of them, one char of Latin-1 for
    // each byte, so that two entries compare as their bytes do.
    private static string StoredBytesOf(Arena arena, ulong address, uint length)
    {
        var bytes = new byte[length];
        var descriptor = new ValueDescriptor { Kind = DescriptorKindEnum.Text, Length = length, Location = address };

        ValueBlocks.CopyTo(arena, descriptor, default, bytes);

        return Encoding.Latin1.GetString(bytes);
    }
}
