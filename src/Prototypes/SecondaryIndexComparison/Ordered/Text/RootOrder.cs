using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// The order of form 1: the text of each root, then the identity.
internal readonly unsafe struct RootOrder : IEntryOrder<RootEntry, TextProbe>
{
    private readonly TextWorld _world;

    public RootOrder(TextWorld world) => _world = world;

    public int Compare(in RootEntry left, in RootEntry right)
    {
        var leftText = _world.TextOf(left.Id, out var leftLength);
        var rightText = _world.TextOf(right.Id, out var rightLength);
        var text = TextWorld.CompareText(leftText, leftLength, rightText, rightLength);

        return text != 0 ? text : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TextProbe probe, in RootEntry entry)
    {
        var text = _world.TextOf(entry.Id, out var length);
        var order = TextWorld.CompareText(probe.Text, probe.Length, text, length);

        return order != 0 ? order : probe.Id.CompareTo(entry.Id);
    }

    public bool SameKey(in TextProbe probe, in RootEntry entry)
    {
        var text = _world.TextOf(entry.Id, out var length);

        return TextWorld.CompareText(probe.Text, probe.Length, text, length) == 0;
    }

    public TextProbe ProbeOf(in RootEntry entry)
    {
        var text = _world.TextOf(entry.Id, out var length);

        return new TextProbe(text, length, entry.Id);
    }

    public ulong IdOf(in RootEntry entry) => entry.Id;
}
