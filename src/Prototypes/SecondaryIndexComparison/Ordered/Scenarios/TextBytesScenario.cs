using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// textbytes: the memory of each form of a text key, on the copy-on-write tree, deterministic. Per
// entity: the index's node slots, and the root side, the slots of the root blocks, of the value blocks
// of Plain text longer than 64 bytes and of the shared text entries. A shared entry also costs the
// engine's text table a managed slot of 32 bytes (Storage/Text/TextSlot.cs) and a bucket word of 8 bytes
// in a table kept at 4 entries per 8-word bucket at most, 16 to 32 bytes an entry
// (Storage/Hashing/IndexTable.cs): not modelled, added here as 48 bytes, the low end.
internal sealed class TextBytesScenario : ITextCase
{
    private const int TextTableBytes = 32 + 16;

    private readonly int _count;

    private readonly int _nodeBytes;

    private readonly string _distribution;

    private TextBytesScenario(int count, int nodeBytes, string distribution)
    {
        _count = count;
        _nodeBytes = nodeBytes;
        _distribution = distribution;
    }

    public static void Run(Options options)
    {
        var scenario = new TextBytesScenario(options.Number("count", 200_000), options.Number("node", 1024), options.Word("text", "random"));

        foreach (var length in options.Numbers("lengths", "8,32,100"))
        {
            foreach (var form in options.Words("forms", TextForms.All))
            {
                TextForms.Dispatch(form, length, scenario);
            }
        }
    }

    public unsafe void Run<TEntry, TOrder>(int length)
        where TEntry : unmanaged, ITextForm<TEntry, TOrder>
        where TOrder : struct, IEntryOrder<TEntry, TextProbe>
    {
        var keys = new TextKeys(_distribution, _count, length);

        using var arena = new NodeArena();

        var epochs = new ReadEpochs();
        var (world, entries) = TextForms.Entities<TEntry, TOrder>(arena, keys);

        using var tree = TextForms.Tree<TEntry, TOrder>(arena, epochs, world, _nodeBytes);

        FixedBuild.InsertAll(tree, entries);

        if (tree.Validate().Count != _count)
        {
            throw new InvalidOperationException("The index lost or gained entries.");
        }

        var index = tree.Bytes;
        var rootSide = world.RootBytes + world.ValueBytes + world.TextEntryBytes;
        var textTable = TEntry.SharesText ? (long)TextTableBytes * _count : 0;

        Line.Write(
            "textbytes",
            ("form", TEntry.Name),
            ("length", length),
            ("text", _distribution),
            ("entries", _count),
            ("entry_bytes", sizeof(TEntry)),
            ("index_per_entry", Line.PerEntry(index.NativeBytes, _count)),
            ("root_side_per_entity", Line.PerEntry(rootSide, _count)),
            ("text_table_per_entity", Line.PerEntry(textTable, _count)),
            ("total_per_entity", Line.PerEntry(index.NativeBytes + rootSide + textTable, _count)),
            ("nodes", index.Nodes),
            ("height", index.Height));
    }
}
