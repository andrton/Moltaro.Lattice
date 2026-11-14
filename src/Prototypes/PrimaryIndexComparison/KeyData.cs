using System.Globalization;

namespace Moltaro.Lattice.Prototypes.PrimaryIndexComparison;

internal sealed class KeyData<TKey> where TKey : notnull, IEquatable<TKey>
{
    public const int Seed = 20261113;

    public KeyData(int count)
    {
        Keys = new TKey[count];
        EqualKeys = new TKey[count];
        MissingKeys = new TKey[count];
        Order = new int[count];
        for (var index = 0; index < count; index++)
        {
            Keys[index] = Create(index);
            EqualKeys[index] = Create(index);
            MissingKeys[index] = Create(index + count);
            Order[index] = index;
        }

        new Random(Seed).Shuffle(Order);
    }

    public TKey[] Keys { get; }
    public TKey[] EqualKeys { get; }
    public TKey[] MissingKeys { get; }
    public int[] Order { get; }
    public int Count => Keys.Length;

    public static ulong Value(int index, int version = 0) =>
        (ulong)(index + 1) ^ ((ulong)(uint)version << 32);

    private static TKey Create(int index)
    {
        // A bijective 64-bit finalizer gives reproducible distinct keys.
        var bits = Mix((ulong)index + Seed);
        if (typeof(TKey) == typeof(long))
        {
            return (TKey)(object)unchecked((long)bits);
        }

        if (typeof(TKey) == typeof(Guid))
        {
            Span<byte> bytes = stackalloc byte[16];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes, bits);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], Mix(bits));
            return (TKey)(object)new Guid(bytes);
        }

        if (typeof(TKey) == typeof(string))
        {
            // Equal text probes are distinct objects, avoiding reference-only hits.
            return (TKey)(object)("key-" + bits.ToString("x16", CultureInfo.InvariantCulture));
        }

        throw new NotSupportedException(typeof(TKey).Name);
    }

    private static ulong Mix(ulong value)
    {
        value = (value ^ (value >> 30)) * 0xbf58476d1ce4e5b9UL;
        value = (value ^ (value >> 27)) * 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }
}
