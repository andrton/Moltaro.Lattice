using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The entries of a measurement of fixed keys, in the order they are inserted, from one seed: the same
// on every run. Identities are 1 to count.
// - random: keys drawn from the whole 64-bit range, cut to the width of the key;
// - ascending: keys 0, 1, 2, ... inserted in that order;
// - hotkey: one key for every entry, its identities inserted in random order.
internal static class FixedKeys
{
    public const int Seed = 20261118;

    public const string Distributions = "random,ascending,hotkey";

    public static (TEntry[] Entries, ulong[] Numbers) Make<TEntry, TKey>(string distribution, int count)
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey>
    {
        var random = new Random(Seed);
        var entries = new TEntry[count];
        var numbers = new ulong[count];
        var ids = Enumerable.Range(1, count).Select(id => (ulong)id).ToArray();

        if (distribution == "hotkey")
        {
            random.Shuffle(ids);
        }

        for (var index = 0; index < count; index++)
        {
            numbers[index] = distribution switch
            {
                "random" => (ulong)random.NextInt64(long.MinValue, long.MaxValue),
                "ascending" => (ulong)index,
                "hotkey" => 42,
                _ => throw new ArgumentException($"No distribution is named '{distribution}'; use {Distributions}."),
            };

            entries[index] = TEntry.Of(numbers[index], ids[index]);
        }

        return (entries, numbers);
    }

    // The probe of a search for the key of a number: the key with identity 0, before every identity.
    public static TEntry Probe<TEntry, TKey>(ulong number)
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey> => TEntry.Of(number, 0);
}
