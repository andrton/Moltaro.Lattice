using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

// A 4-byte key and an 8-byte identity, packed to 12 bytes: the identity is read unaligned, which x64
// and ARM64 allow for an ordinary load.
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct Int32Entry : IFixedEntry<Int32Entry, int>
{
    private readonly int _key;

    private readonly ulong _id;

    public Int32Entry(int key, ulong id)
    {
        _key = key;
        _id = id;
    }

    public static string KeyName => "Int32";

    public int Key => _key;

    public ulong Id => _id;

    public static Int32Entry Of(ulong number, ulong id) => new((int)number, id);

    public static Int32Entry With(int key, ulong id) => new(key, id);
}
