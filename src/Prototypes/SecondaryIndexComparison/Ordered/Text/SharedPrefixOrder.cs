using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// The order of form 3 with a prefix: the prefixes as unsigned numbers; equal prefixes compare the texts
// of the shared entries; then the identity.
internal readonly unsafe struct SharedPrefixOrder : IEntryOrder<SharedPrefixEntry, TextProbe>
{
    private readonly TextWorld _world;

    public SharedPrefixOrder(TextWorld world) => _world = world;

    public int Compare(in SharedPrefixEntry left, in SharedPrefixEntry right)
    {
        if (left.Prefix != right.Prefix)
        {
            return left.Prefix < right.Prefix ? -1 : 1;
        }

        var leftText = _world.SharedText(left.Text, out var leftLength);
        var rightText = _world.SharedText(right.Text, out var rightLength);
        var text = TextWorld.CompareText(leftText, leftLength, rightText, rightLength);

        return text != 0 ? text : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TextProbe probe, in SharedPrefixEntry entry)
    {
        if (probe.Prefix != entry.Prefix)
        {
            return probe.Prefix < entry.Prefix ? -1 : 1;
        }

        var text = _world.SharedText(entry.Text, out var length);
        var order = TextWorld.CompareText(probe.Text, probe.Length, text, length);

        return order != 0 ? order : probe.Id.CompareTo(entry.Id);
    }

    public bool SameKey(in TextProbe probe, in SharedPrefixEntry entry)
    {
        if (probe.Prefix != entry.Prefix)
        {
            return false;
        }

        var text = _world.SharedText(entry.Text, out var length);

        return TextWorld.CompareText(probe.Text, probe.Length, text, length) == 0;
    }

    public TextProbe ProbeOf(in SharedPrefixEntry entry)
    {
        var text = _world.SharedText(entry.Text, out var length);

        return new TextProbe(text, length, entry.Id);
    }

    public ulong IdOf(in SharedPrefixEntry entry) => entry.Id;
}
