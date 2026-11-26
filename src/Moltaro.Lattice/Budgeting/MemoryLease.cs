using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Budgeting;

// The lease follows owned input through preparation, transfer and commit.
// The batch builder can also be abandoned, so the inventory uses weak keys
// and the lease returns its capacity when finalized. A lease has one
// active owner and its entry blocks move without duplicating payload charges.
internal sealed class MemoryLease : IDisposable
{
    private static long OwnerBytes => MemoryBudget.ObjectBytes(3L * IntPtr.Size);

    private MemoryBudget? _budget;
    private Entries? _first;
    private Entries? _last;

    private MemoryLease(MemoryBudget budget) => _budget = budget;

    public MemoryBudget Budget => _budget!;

    public static MemoryLease Create(MemoryBudget budget)
    {
        budget.Reserve(OwnerBytes);
        MemoryLease? lease = null;
        try
        {
            lease = new MemoryLease(budget);
            budget.Own(lease, nameof(MemoryLease));
            return lease;
        }
        catch
        {
            if (lease is null)
            {
                budget.Release(OwnerBytes);
            }
            else
            {
                lease.Dispose();
            }

            throw;
        }
    }

    public T[] Allocate<T>(int length, string owner)
    {
        if (length == 0)
        {
            return [];
        }

        var entries = Room();
        var array = Budget.Allocate<T>(length, owner);
        entries.Add(new Entry(array, MemoryBudget.ArrayBytes<T>(length)));
        return array;
    }

    public T Allocate<T>(long bytes, string owner, Func<T> create, Action<MemoryBudget, T>? release = null) where T : class
    {
        var entries = Room();
        var budget = Budget;
        var total = bytes + (release is null ? 0 : Cleanup<T>.Bytes);
        budget.Reserve(total);
        T? backing = null;
        Cleanup<T>? cleanup = null;
        try
        {
            // A factory for an owned value can record its fields in this same lease.
            ref var entry = ref entries.Reserve();
            backing = create();
            budget.Own(backing, owner);
            if (release is null)
            {
                entry = new Entry(backing, bytes);
            }
            else
            {
                cleanup = new Cleanup<T>(backing, bytes, release);
                budget.Own(cleanup, nameof(MemoryLease));
                entry = new Entry(cleanup, Cleanup<T>.Bytes);
            }

            return backing;
        }
        catch
        {
            try
            {
                if (backing is not null)
                {
                    release?.Invoke(budget, backing);
                }
            }
            finally
            {
                if (backing is not null)
                {
                    budget.Forget(backing);
                }

                if (cleanup is not null)
                {
                    budget.Forget(cleanup);
                }

                budget.Release(total);
            }

            throw;
        }
    }

    public byte[] Copy(byte[] original, string owner)
    {
        var copy = Allocate<byte>(original.Length, owner);
        original.CopyTo(copy, 0);
        return copy;
    }

    public object Box<T>(T value, string owner) where T : struct =>
        Allocate(MemoryBudget.ObjectBytes(Unsafe.SizeOf<T>()), owner, () => (object)value);

    // The caller reserved the item's bytes and still owns that reservation if recording it fails.
    public void OwnReserved(object item, long bytes, string owner)
    {
        var entries = Room();
        Budget.Own(item, owner);
        entries.Add(new Entry(item, bytes));
    }

    // Cleanup state is owned by the lease. The callback is shared by the operation that records it.
    public void Own<T>(T state, Action<MemoryBudget, T> release)
    {
        var entries = Room();
        var budget = Budget;
        var bytes = Cleanup<T>.Bytes;
        budget.Reserve(bytes);
        try
        {
            var cleanup = new Cleanup<T>(state, 0, release);
            budget.Own(cleanup, nameof(MemoryLease));
            entries.Add(new Entry(cleanup, bytes));
        }
        catch
        {
            budget.Release(bytes);
            throw;
        }
    }

    // Keeps the capacity of an already registered array as its previous owner lets go.
    public void TakeArray<T>(T[] backing)
    {
        if (backing.Length != 0)
        {
            Room().Add(new Entry(backing, MemoryBudget.ArrayBytes<T>(backing.Length)));
        }
    }

    public void Transfer(object item, MemoryLease destination)
    {
        for (var entries = _first; entries is not null; entries = entries.Earlier)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                ref var entry = ref entries.At(index);
                if (ReferenceEquals(entry.Backing, item))
                {
                    destination.Room().Add(entry);
                    entry = default;
                    return;
                }
            }
        }
    }

    public void AdoptArray<T>(T[] array, string owner)
    {
        // A privately cloned empty array has its own header. Allocate returns a shared empty array.
        var bytes = MemoryBudget.ArrayBytes<T>(array.Length);
        Budget.Reserve(bytes);
        try
        {
            OwnReserved(array, bytes, owner);
        }
        catch
        {
            Budget.Release(bytes, array);
            throw;
        }
    }

    public void Take(MemoryLease other)
    {
        if (other._last is { } last)
        {
            if (_last is null)
            {
                _first = other._first;
            }
            else
            {
                _last.Earlier = other._first;
            }

            _last = last;
        }

        other._first = null;
        other._last = null;
        other.Dispose();
    }

    private Entries Room()
    {
        if (_first is { Count: < Entries.Capacity } available)
        {
            return available;
        }

        var budget = Budget;
        budget.Reserve(Entries.Bytes);
        try
        {
            var entries = new Entries(_first);
            budget.Own(entries, nameof(MemoryLease));
            _first = entries;
            _last ??= entries;
            return entries;
        }
        catch
        {
            budget.Release(Entries.Bytes);
            throw;
        }
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    ~MemoryLease() => Release();

    private void Release()
    {
        if (Interlocked.Exchange(ref _budget, null) is not { } budget)
        {
            return;
        }

        var entries = _first;
        _first = null;
        _last = null;
        while (entries is not null)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var entry = entries.At(index);
                if (entry.Backing is { } backing)
                {
                    if (backing is Cleanup cleanup)
                    {
                        cleanup.Release(budget);
                    }

                    budget.Release(entry.Bytes, backing);
                }
            }

            budget.Release(Entries.Bytes, entries);
            entries = entries.Earlier;
        }

        budget.Release(OwnerBytes, this);
    }

    private readonly record struct Entry(object? Backing, long Bytes);

    // Three slots cover the command array, path and input of a single update
    // with one allocation and no retained delegates for ordinary backings.
    private sealed class Entries(Entries? earlier)
    {
        public const int Capacity = 3;

        public static long Bytes => MemoryBudget.ObjectBytes(IntPtr.Size + Capacity * Unsafe.SizeOf<Entry>() + sizeof(int));

        private Entry _first;
        private Entry _second;
        private Entry _third;

        public Entries? Earlier = earlier;

        public int Count { get; private set; }

        public void Add(Entry entry) => Reserve() = entry;

        public ref Entry Reserve() => ref At(Count++);

        public ref Entry At(int index)
        {
            if (index == 0)
            {
                return ref _first;
            }

            if (index == 1)
            {
                return ref _second;
            }

            return ref _third;
        }
    }

    private abstract class Cleanup
    {
        public abstract void Release(MemoryBudget budget);
    }

    private sealed class Cleanup<T>(T state, long bytes, Action<MemoryBudget, T> release) : Cleanup
    {
        public static long Bytes => MemoryBudget.ObjectBytes(Unsafe.SizeOf<T>() + sizeof(long) + IntPtr.Size);

        public override void Release(MemoryBudget budget)
        {
            release(budget, state);
            if (bytes != 0)
            {
                budget.Release(bytes, state);
            }
        }
    }
}
