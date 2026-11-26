using System.Reflection;
using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Budgeting;

// The .NET 10 Dictionary chooses a prime for its requested capacity. Reserve those two arrays
// before EnsureCapacity, and inventory the arrays it actually installed.
internal static class BudgetDictionaries
{
    public static long OwnerBytes => MemoryBudget.ObjectBytes(5L * IntPtr.Size + sizeof(ulong) + 4 * sizeof(int));

    public static Dictionary<TKey, TValue> Create<TKey, TValue>(MemoryBudget? budget, string owner)
        where TKey : notnull
    {
        budget?.Reserve(OwnerBytes);
        try
        {
            var dictionary = new Dictionary<TKey, TValue>();
            budget?.Own(dictionary, owner);
            return dictionary;
        }
        catch
        {
            budget?.Release(OwnerBytes);
            throw;
        }
    }

    public static void EnsureCapacity<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, int count,
        MemoryBudget? budget, string owner) where TKey : notnull
    {
        if (budget is null)
        {
            dictionary.EnsureCapacity(count);
            return;
        }

        var previous = dictionary.EnsureCapacity(0);
        if (count <= previous)
        {
            return;
        }

        var requested = BudgetCollections.CapacityFor(previous, count);
        var capacity = GetPrime(null, requested);
        var bytes = CapacityBytes<TKey, TValue>(capacity);
        budget.Reserve(bytes);
        try
        {
            var actual = dictionary.EnsureCapacity(requested);
            System.Diagnostics.Debug.Assert(actual == capacity);
            budget.ObserveContainer(dictionary, owner, DictionaryBacking<TKey, TValue>.Visit);
        }
        catch
        {
            budget.Release(bytes);
            throw;
        }

        if (previous != 0)
        {
            budget.Release(CapacityBytes<TKey, TValue>(previous));
        }
    }

    public static void ReleaseDictionary<TKey, TValue>(this MemoryBudget budget, Dictionary<TKey, TValue> dictionary,
        bool includesOwner = false) where TKey : notnull
    {
        var capacity = dictionary.EnsureCapacity(0);
        if (capacity != 0)
        {
            budget.ForgetContainer(dictionary);
            budget.Release(CapacityBytes<TKey, TValue>(capacity));
        }

        if (includesOwner)
        {
            budget.Release(OwnerBytes, dictionary);
        }
    }

    private static long CapacityBytes<TKey, TValue>(int length) =>
        MemoryBudget.ArrayBytes<int>(length) + MemoryBudget.ArrayBytes<Entry<TKey, TValue>>(length);

    [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "GetPrime")]
    private static extern int GetPrime([UnsafeAccessorType("System.Collections.HashHelpers, System.Private.CoreLib")] object? owner, int count);

    public static int HashCapacity(int count) => GetPrime(null, count);

    // Only an explicit test inventory walks the actual references. Normal admission uses the
    // capacity of the dictionary and never inspects its fields.
    private static class DictionaryBacking<TKey, TValue> where TKey : notnull
    {
        private static readonly FieldInfo Buckets = Field("_buckets");
        private static readonly FieldInfo Entries = Field("_entries");

        private static FieldInfo Field(string name) => typeof(Dictionary<TKey, TValue>)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

        public static void Visit(object dictionary, string owner, Action<string, object> visit)
        {
            if (Buckets.GetValue(dictionary) is Array buckets)
            {
                visit(owner, buckets);
                visit(owner, Entries.GetValue(dictionary)!);
            }
        }
    }

    private readonly record struct Entry<TKey, TValue>(uint Hash, int Next, TKey Key, TValue Value);
}
