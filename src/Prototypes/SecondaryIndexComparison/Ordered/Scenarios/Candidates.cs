using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// The candidates by the names the command line uses.
internal static class Candidates
{
    public const string All = "cow,optimistic,skiplist,arc";

    public static IOrderedIndex<TEntry, TEntry> Fixed<TEntry, TKey>(string name, NodeArena arena, ReadEpochs epochs, int nodeBytes)
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey> => name switch
        {
            "cow" => new CopyOnWriteTree<TEntry, TEntry, FixedOrder<TEntry, TKey>>(arena, epochs, default, nodeBytes),
            "optimistic" => new OptimisticTree<TEntry, FixedOrder<TEntry, TKey>>(arena, epochs, default, nodeBytes),
            "skiplist" => new SkipList<TEntry, TEntry, FixedOrder<TEntry, TKey>>(arena, epochs, default),
            "arc" => new LockedMultiMap<TEntry, TKey>(),
            _ => throw new ArgumentException($"No candidate is named '{name}'; use {All}."),
        };

    // The restarts of the optimistic tree's reads on the calling thread; 0 for the others.
    public static long Restarts<TEntry, TKey>(string name)
        where TEntry : unmanaged, IFixedEntry<TEntry, TKey>
        where TKey : IComparable<TKey> =>
        name == "optimistic" ? OptimisticTree<TEntry, FixedOrder<TEntry, TKey>>.RestartsOnThisThread : 0;
}
