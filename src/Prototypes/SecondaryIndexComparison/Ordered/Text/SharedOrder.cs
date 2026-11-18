using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// The order of form 3: the texts of the shared entries, then the identity.
internal readonly unsafe struct SharedOrder : IEntryOrder<SharedEntry, TextProbe>
{
    private readonly TextWorld _world;

    public SharedOrder(TextWorld world) => _world = world;

    public int Compare(in SharedEntry left, in SharedEntry right)
    {
        var leftText = _world.SharedText(left.Text, out var leftLength);
        var rightText = _world.SharedText(right.Text, out var rightLength);
        var text = TextWorld.CompareText(leftText, leftLength, rightText, rightLength);

        return text != 0 ? text : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TextProbe probe, in SharedEntry entry)
    {
        var text = _world.SharedText(entry.Text, out var length);
        var order = TextWorld.CompareText(probe.Text, probe.Length, text, length);

        return order != 0 ? order : probe.Id.CompareTo(entry.Id);
    }

    public bool SameKey(in TextProbe probe, in SharedEntry entry)
    {
        var text = _world.SharedText(entry.Text, out var length);

        return TextWorld.CompareText(probe.Text, probe.Length, text, length) == 0;
    }

    public TextProbe ProbeOf(in SharedEntry entry)
    {
        var text = _world.SharedText(entry.Text, out var length);

        return new TextProbe(text, length, entry.Id);
    }

    public ulong IdOf(in SharedEntry entry) => entry.Id;
}
