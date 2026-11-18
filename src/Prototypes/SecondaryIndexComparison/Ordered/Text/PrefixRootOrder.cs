using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// The order of the bounded form 2: the prefixes as unsigned numbers; equal prefixes compare the texts
// of the roots; then the identity.
internal readonly unsafe struct PrefixRootOrder : IEntryOrder<PrefixRootEntry, TextProbe>
{
    private readonly TextWorld _world;

    public PrefixRootOrder(TextWorld world) => _world = world;

    public int Compare(in PrefixRootEntry left, in PrefixRootEntry right)
    {
        if (left.Prefix != right.Prefix)
        {
            return left.Prefix < right.Prefix ? -1 : 1;
        }

        var leftText = _world.TextOf(left.Id, out var leftLength);
        var rightText = _world.TextOf(right.Id, out var rightLength);
        var text = TextWorld.CompareText(leftText, leftLength, rightText, rightLength);

        return text != 0 ? text : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TextProbe probe, in PrefixRootEntry entry)
    {
        if (probe.Prefix != entry.Prefix)
        {
            return probe.Prefix < entry.Prefix ? -1 : 1;
        }

        var text = _world.TextOf(entry.Id, out var length);
        var order = TextWorld.CompareText(probe.Text, probe.Length, text, length);

        return order != 0 ? order : probe.Id.CompareTo(entry.Id);
    }

    public bool SameKey(in TextProbe probe, in PrefixRootEntry entry)
    {
        if (probe.Prefix != entry.Prefix)
        {
            return false;
        }

        var text = _world.TextOf(entry.Id, out var length);

        return TextWorld.CompareText(probe.Text, probe.Length, text, length) == 0;
    }

    public TextProbe ProbeOf(in PrefixRootEntry entry)
    {
        var text = _world.TextOf(entry.Id, out var length);

        return new TextProbe(text, length, entry.Id);
    }

    public ulong IdOf(in PrefixRootEntry entry) => entry.Id;
}
