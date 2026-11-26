namespace Moltaro.Lattice.Budgeting;

// An insertion-only set with explicit backing capacities. The owner can clear and reuse it.
// Lookup probes a table of entry indices; entries are kept in insertion order for overlap checks.
internal sealed class BudgetSet<T>(MemoryBudget? budget, string owner, IEqualityComparer<T> comparer,
    bool restoresPreparation = false) : IDisposable
{
    public static long OwnerBytes => MemoryBudget.ObjectBytes(5L * IntPtr.Size + sizeof(int) + 1);

    private int[] _buckets = [];
    private Entry[] _entries = [];

    public int Count { get; private set; }

    public bool Contains(T value) => Count > 0 && Find(value, Hash(value), out _);

    public bool Add(T value)
    {
        var hash = Hash(value);
        if (Count > 0 && Find(value, hash, out _))
        {
            return false;
        }

        EnsureCapacity(Count + 1);
        _ = Find(value, hash, out var bucket);
        _entries[Count] = new Entry(hash, value);
        _buckets[bucket] = ++Count;
        return true;
    }

    public void EnsureCapacity(int count)
    {
        if (count <= _entries.Length)
        {
            return;
        }

        var previousEntries = _entries;
        var previousBuckets = _buckets;
        var length = BudgetDictionaries.HashCapacity(Math.Max(count, checked(previousEntries.Length * 2)));
        var slots = checked((int)System.Numerics.BitOperations.RoundUpToPowerOf2(checked((uint)length * 2)));
        var charge = MemoryBudget.ArrayBytes<int>(slots) + MemoryBudget.ArrayBytes<Entry>(length);
        budget?.Reserve(charge);

        int[]? buckets = null;
        Entry[]? entries = null;
        try
        {
            buckets = new int[slots];
            entries = new Entry[length];
            previousEntries.AsSpan(0, Count).CopyTo(entries);
            for (var index = 0; index < Count; index++)
            {
                var bucket = Home(entries[index].Hash, buckets.Length);
                while (buckets[bucket] != 0)
                {
                    bucket = Next(bucket, buckets.Length);
                }

                buckets[bucket] = index + 1;
            }

            if (budget is not null)
            {
                budget.Own(buckets, owner);
                budget.Own(entries, owner);
                if (restoresPreparation && budget.IsPreparing)
                {
                    var change = (Set: this, PreviousEntries: previousEntries, PreviousBuckets: previousBuckets, Entries: entries, Buckets: buckets);
                    budget.WhenPreparationEnds(change, static (account, state, keeps) =>
                    {
                        if (keeps)
                        {
                            account.Release(state.PreviousBuckets);
                            account.Release(state.PreviousEntries);
                        }
                        else
                        {
                            state.Set._entries = state.PreviousEntries;
                            state.Set._buckets = state.PreviousBuckets;
                            Array.Clear(state.PreviousEntries);
                            Array.Clear(state.PreviousBuckets);
                            state.Set.Count = 0;
                            account.Release(state.Buckets);
                            account.Release(state.Entries);
                        }
                    });
                }
                else
                {
                    budget.Release(previousBuckets);
                    budget.Release(previousEntries);
                }
            }
        }
        catch
        {
            if (budget is not null)
            {
                if (buckets is not null)
                {
                    budget.Forget(buckets);
                }

                if (entries is not null)
                {
                    budget.Forget(entries);
                }

                budget.Release(charge);
            }

            throw;
        }

        _entries = entries;
        _buckets = buckets;
    }

    public bool Overlaps(BudgetSet<T> other)
    {
        for (var index = 0; index < other.Count; index++)
        {
            if (Contains(other._entries[index].Value))
            {
                return true;
            }
        }

        return false;
    }

    public void Clear()
    {
        Array.Clear(_buckets);
        Array.Clear(_entries, 0, Count);
        Count = 0;
    }

    public void Dispose()
    {
        budget?.Release(_buckets);
        budget?.Release(_entries);
        _buckets = [];
        _entries = [];
        Count = 0;
    }

    private uint Hash(T value) => unchecked((uint)comparer.GetHashCode(value!) * 0x9E3779B9u);
    private static int Home(uint hash, int length) => (int)(hash & (uint)(length - 1));
    private static int Next(int index, int length) => (index + 1) & (length - 1);

    private bool Find(T value, uint hash, out int bucket)
    {
        bucket = Home(hash, _buckets.Length);
        while (_buckets[bucket] is var index && index != 0)
        {
            ref var entry = ref _entries[index - 1];
            if (entry.Hash == hash && comparer.Equals(entry.Value, value))
            {
                return true;
            }

            bucket = Next(bucket, _buckets.Length);
        }

        return false;
    }

    private readonly record struct Entry(uint Hash, T Value);
}
