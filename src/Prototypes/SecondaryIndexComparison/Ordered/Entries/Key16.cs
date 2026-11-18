namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// A 16-byte key, ordered as two unsigned words, the high one first: the shape of a Guid or of a
// composite key of two Int64 fields.
internal readonly struct Key16 : IComparable<Key16>
{
    public Key16(ulong high, ulong low)
    {
        High = high;
        Low = low;
    }

    public ulong High { get; }

    public ulong Low { get; }

    public int CompareTo(Key16 other)
    {
        var high = High.CompareTo(other.High);

        return high != 0 ? high : Low.CompareTo(other.Low);
    }
}
