namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// An 8-byte key and an 8-byte identity: 16 bytes.
internal readonly struct Int64Entry : IFixedEntry<Int64Entry, long>
{
    private readonly long _key;

    private readonly ulong _id;

    public Int64Entry(long key, ulong id)
    {
        _key = key;
        _id = id;
    }

    public static string KeyName => "Int64";

    public long Key => _key;

    public ulong Id => _id;

    public static Int64Entry Of(ulong number, ulong id) => new((long)number, id);

    public static Int64Entry With(long key, ulong id) => new(key, id);
}
