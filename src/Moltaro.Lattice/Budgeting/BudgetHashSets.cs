using System.Reflection;

namespace Moltaro.Lattice.Budgeting;

// A schema Set keeps the ordinary HashSet representation. Its replacement buckets and
// entries must both fit before the old backings can be returned.
internal static class BudgetHashSets
{
    public static long OwnerBytes => MemoryBudget.ObjectBytes(3L * IntPtr.Size + sizeof(ulong) + 4 * sizeof(int));

    public static void EnsureCapacity<T>(this HashSet<T> set, int count, MemoryBudget budget, string owner)
    {
        var previous = set.EnsureCapacity(0);
        if (count <= previous)
        {
            return;
        }

        var requested = BudgetCollections.CapacityFor(previous, count);
        var capacity = BudgetDictionaries.HashCapacity(requested);
        var bytes = CapacityBytes<T>(capacity);
        budget.Reserve(bytes);
        try
        {
            var actual = set.EnsureCapacity(requested);
            System.Diagnostics.Debug.Assert(actual == capacity);
            budget.ObserveContainer(set, owner, Backing<T>.Visit);
        }
        catch
        {
            budget.Release(bytes);
            throw;
        }

        if (previous != 0)
        {
            budget.Release(CapacityBytes<T>(previous));
        }
    }

    public static void ReleaseSet<T>(this MemoryBudget budget, HashSet<T> set)
    {
        var capacity = set.EnsureCapacity(0);
        if (capacity != 0)
        {
            budget.ForgetContainer(set);
            budget.Release(CapacityBytes<T>(capacity));
        }
    }

    private static long CapacityBytes<T>(int length) =>
        MemoryBudget.ArrayBytes<int>(length) + MemoryBudget.ArrayBytes<Entry<T>>(length);

    private static class Backing<T>
    {
        private static readonly FieldInfo Buckets = Field("_buckets");
        private static readonly FieldInfo Entries = Field("_entries");

        private static FieldInfo Field(string name) => typeof(HashSet<T>)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        public static void Visit(object set, string owner, Action<string, object> visit)
        {
            if (Buckets.GetValue(set) is Array buckets)
            {
                visit(owner, buckets);
                visit(owner, Entries.GetValue(set)!);
            }
        }
    }

    private readonly record struct Entry<T>(int Hash, int Next, T Value);
}
