using System.Runtime.CompilerServices;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Text;

// The order of form 2: the copied bytes, then the identity. Nothing outside the node is read.
internal readonly unsafe struct CopyOrder<TBytes> : IEntryOrder<CopyEntry<TBytes>, TextProbe>
    where TBytes : unmanaged
{
    public int Compare(in CopyEntry<TBytes> left, in CopyEntry<TBytes> right)
    {
        var text = left.Text.SequenceCompareTo(right.Text);

        return text != 0 ? text : left.Id.CompareTo(right.Id);
    }

    public int CompareProbe(in TextProbe probe, in CopyEntry<TBytes> entry)
    {
        var text = new ReadOnlySpan<byte>(probe.Text, probe.Length).SequenceCompareTo(entry.Text);

        return text != 0 ? text : probe.Id.CompareTo(entry.Id);
    }

    public bool SameKey(in TextProbe probe, in CopyEntry<TBytes> entry) =>
        new ReadOnlySpan<byte>(probe.Text, probe.Length).SequenceEqual(entry.Text);

    // The probe points into the entry, which the structures pass by reference to its place in a node.
    public TextProbe ProbeOf(in CopyEntry<TBytes> entry) =>
        new((byte*)Unsafe.AsPointer(ref Unsafe.AsRef(in entry)) + CopyEntry<TBytes>.TextOffset, entry.Length, entry.Id);

    public ulong IdOf(in CopyEntry<TBytes> entry) => entry.Id;
}
