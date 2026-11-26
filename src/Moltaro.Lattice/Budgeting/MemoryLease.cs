namespace Moltaro.Lattice.Budgeting;

// The only budget owner that may be abandoned by application code is the write batch builder.
// A lease follows its input through commit and can be collected if the builder never commits.
// Its backing records do not appear in the budget's roots.
internal sealed class MemoryLease(MemoryBudget budget) : IDisposable
{
    private Backing? _first;
    private Backing? _last;

    public MemoryBudget Budget { get; } = budget;

    public T[] Allocate<T>(int length, string owner)
    {
        var array = Budget.Allocate<T>(length, owner);
        try
        {
            Own(() => Budget.Release(array));
            return array;
        }
        catch
        {
            Budget.Release(array);
            throw;
        }
    }

    public T Allocate<T>(long bytes, string owner, Func<T> create, Action<T>? release = null) where T : class
    {
        Budget.Reserve(bytes);
        T? backing = null;
        try
        {
            backing = create();
            Budget.Own(backing, owner);
            Own(() =>
            {
                release?.Invoke(backing);
                Budget.Release(bytes, backing);
            }, backing);
            return backing;
        }
        catch
        {
            if (backing is not null)
            {
                Budget.Forget(backing);
            }

            Budget.Release(bytes);
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
        Allocate(MemoryBudget.ObjectBytes(System.Runtime.CompilerServices.Unsafe.SizeOf<T>()), owner, () => (object)value);

    // The caller reserved space before creating an object from stack-held bytes.
    public void OwnReserved(object item, long capacity, string owner)
    {
        Budget.Own(item, owner);
        try
        {
            Own(() => Budget.Release(capacity, item), item);
        }
        catch
        {
            Budget.Forget(item);
            throw;
        }
    }

    public void Own(Action release) => Own(release, null);

    private void Own(Action release, object? item)
    {
        var backing = new Backing(release, item, _first);
        _first = backing;
        _last ??= backing;
    }

    public void Transfer(object item, MemoryLease destination)
    {
        Backing? previous = null;
        ref var next = ref _first;
        while (next is { } candidate)
        {
            if (ReferenceEquals(candidate.Item, item))
            {
                next = candidate.Next;
                if (ReferenceEquals(candidate, _last))
                {
                    _last = previous;
                }

                candidate.Next = destination._first;
                destination._first = candidate;
                destination._last ??= candidate;
                return;
            }

            previous = candidate;
            next = ref candidate.Next;
        }
    }

    public void AdoptArray<T>(T[] array, string owner)
    {
        var capacity = MemoryBudget.ArrayBytes<T>(array.Length);
        Budget.Reserve(capacity);
        try
        {
            Budget.Own(array, owner);
            // Adopted input may be a privately cloned zero-length array. An allocated
            // empty array would instead be the shared empty value and has no charge.
            Own(() => Budget.Release(capacity, array));
        }
        catch
        {
            Budget.Release(capacity, array);
            throw;
        }
    }

    public void Take(MemoryLease other)
    {
        if (other._last is { } last)
        {
            last.Next = _first;
            _first = other._first;
            _last ??= last;
        }

        other._first = null;
        other._last = null;
        GC.SuppressFinalize(other);
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    ~MemoryLease() => Release();

    private void Release()
    {
        var backing = Interlocked.Exchange(ref _first, null);
        _last = null;
        while (backing is not null)
        {
            backing.Release();
            backing = backing.Next;
        }
    }

    private sealed class Backing(Action release, object? item, Backing? next)
    {
        public Action Release { get; } = release;
        public readonly object? Item = item;
        public Backing? Next = next;
    }
}
