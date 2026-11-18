namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// A 16-byte key and an 8-byte identity: 24 bytes.
internal readonly struct Key16Entry : IFixedEntry<Key16Entry, Key16>
{
    // 2^64 divided by the golden ratio: spreads the low word of a key made from one number.
    private const ulong Spread = 0x9E3779B97F4A7C15;

    private readonly Key16 _key;

    private readonly ulong _id;

    public Key16Entry(Key16 key, ulong id)
    {
        _key = key;
        _id = id;
    }

    public static string KeyName => "Key16";

    public Key16 Key => _key;

    public ulong Id => _id;

    public static Key16Entry Of(ulong number, ulong id) => new(new Key16(number, number * Spread), id);

    public static Key16Entry With(Key16 key, ulong id) => new(key, id);
}
