using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Entries;
using Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Structures;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Scenarios;

// Builds an index by inserting the entries one at a time, each a prepared write and a publication, as
// the engine maintains an index; and takes entries out the same way.
internal static class FixedBuild
{
    public static void InsertAll<TEntry, TProbe>(IOrderedIndex<TEntry, TProbe> index, ReadOnlySpan<TEntry> entries)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        foreach (var entry in entries)
        {
            index.Prepare(1);
            index.Insert(entry);
            index.AfterPublication();
        }

        index.DrainAll();
    }

    public static void RemoveAll<TEntry, TProbe>(IOrderedIndex<TEntry, TProbe> index, ReadOnlySpan<TEntry> entries)
        where TEntry : unmanaged
        where TProbe : unmanaged
    {
        foreach (var entry in entries)
        {
            index.Prepare(1);

            if (!index.Remove(entry))
            {
                throw new InvalidOperationException("An entry put in was not found.");
            }

            index.AfterPublication();
        }

        index.DrainAll();
    }

    // A random half of the entries, in random order, from the scenario's seed.
    public static TEntry[] RandomHalf<TEntry>(TEntry[] entries)
    {
        var order = (TEntry[])entries.Clone();

        new Random(FixedKeys.Seed + 7).Shuffle(order);

        return order[..(order.Length / 2)];
    }

    // The managed heap after a full, compacting collection.
    public static long ManagedBytes()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);

        return GC.GetTotalMemory(forceFullCollection: false);
    }

    public static void Dispatch(string key, IFixedCase fixedCase)
    {
        switch (key)
        {
            case "int32":
                fixedCase.Run<Int32Entry, int>();
                break;

            case "int64":
                fixedCase.Run<Int64Entry, long>();
                break;

            case "key16":
                fixedCase.Run<Key16Entry, Key16>();
                break;

            default:
                throw new ArgumentException($"No key is named '{key}'; use int32, int64 or key16.");
        }
    }
}
