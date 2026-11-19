using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The check's keys as Int64 entries: the key is the key number.
internal sealed class FixedCheckKeys : ICheckKeys<Int64Entry, Int64Entry>
{
    private readonly CheckModel _model;

    public FixedCheckKeys(CheckModel model) => _model = model;

    public Int64Entry EntryOf(ulong id) => new(_model.KeyOf[id], id);

    public Int64Entry ProbeAt(int keyNumber, ulong id) => new(keyNumber, id);

    public int KeyNumberOf(in Int64Entry entry) => (int)entry.Key;

    public ulong IdOf(in Int64Entry entry) => entry.Id;
}
