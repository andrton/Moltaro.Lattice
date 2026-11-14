namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal static class Check
{
    public static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void Contents<TIndex>(TIndex map, IndexData data)
        where TIndex : struct, ISecondaryIndex<TIndex>
    {
        var entries = map.Entries().ToArray();
        Require(map.Count == data.Count && entries.Length == data.Count, "Index count mismatch.");
        for (var index = 1; index < entries.Length; index++)
        {
            Require(entries[index - 1].Key.CompareTo(entries[index].Key) <= 0, "Index is not ordered.");
        }

        var byLocator = entries.OrderBy(entry => entry.Value).ToArray();
        for (var index = 0; index < data.Count; index++)
        {
            Require(byLocator[index].Value == (ulong)index + 1 && byLocator[index].Key == data.CurrentKeys[index],
                "Index lost or changed a key/locator pair.");
        }
    }

    public static long Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }
}
