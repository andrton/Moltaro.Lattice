using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The check's keys as entries of a text form: the key of number n is the numbered text n, whose order
// is the order of the numbers, and an entry is read back by its identity.
internal sealed unsafe class TextCheckKeys<TEntry, TOrder> : ICheckKeys<TEntry, TextProbe>
    where TEntry : unmanaged, ITextForm<TEntry, TOrder>
    where TOrder : struct, IEntryOrder<TEntry, TextProbe>
{
    private readonly CheckModel _model;

    private readonly TextKeys _keys;

    private readonly TEntry[] _entries;

    private readonly TOrder _order;

    public TextCheckKeys(CheckModel model, TextKeys keys, TextWorld world)
    {
        _model = model;
        _keys = keys;
        _order = TEntry.OrderFor(world);
        _entries = new TEntry[model.IdCount + 1];

        for (var id = 1; id <= model.IdCount; id++)
        {
            var key = keys.Key(model.KeyOf[id]);
            var text = world.Add((ulong)id, key, TEntry.SharesText);

            _entries[id] = TEntry.Make((ulong)id, key, text);
        }
    }

    public TEntry EntryOf(ulong id) => _entries[id];

    public TextProbe ProbeAt(int keyNumber, ulong id) => new(_keys.Pointer(keyNumber), _keys.Length, id);

    public int KeyNumberOf(in TEntry entry) => _model.KeyOf[_order.IdOf(entry)];

    public ulong IdOf(in TEntry entry) => _order.IdOf(entry);
}
