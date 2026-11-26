using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Budgeting;

// The capacity shared by storage, write preparation, and replication of one instance. Reservations
// enter the total before allocating and stay there until the last library owner lets the capacity
// go. The gate protects the counters, outstanding reservation and optional backing inventory. It
// never protects storage or network state and is never held across allocation, I/O or a wait.
internal sealed class MemoryBudget(bool observesBackings)
{
    private readonly Lock _gate = new();
    private readonly BudgetBackings? _backings = observesBackings ? new() : null;
    private readonly ConditionalWeakTable<object, Container>? _containers = observesBackings ? new() : null;
    private long _charged;
    private long _peak;
    private long _native;
    private long _change;
    private TaskCompletionSource? _changed;
    private bool _closed;

    // Only the publishing thread uses the journal, in the lock of the writes. Network and
    // batch builders reserve from the same instance without joining that preparation.
    private int _preparingThread;
    private CapacityChange? _changes;

    public long Limit { get; private set; }

    public void Open(long limit) => Limit = limit;

    public bool IsPreparing => _preparingThread == Environment.CurrentManagedThreadId;

    public void BeginPreparation()
    {
        System.Diagnostics.Debug.Assert(_preparingThread == 0);
        _preparingThread = Environment.CurrentManagedThreadId;
    }

    public void EndPreparation(bool keepsCapacity)
    {
        System.Diagnostics.Debug.Assert(IsPreparing);
        var changes = _changes;
        _changes = null;
        _preparingThread = 0;
        for (var change = changes; change is not null; change = change.Previous)
        {
            change.Finish(keepsCapacity);
        }
    }

    public void WhenPreparationEnds(Action<bool> finish)
    {
        System.Diagnostics.Debug.Assert(IsPreparing);
        _changes = new CapacityChange(finish, _changes);
    }

    public void Replace<T>(T[] previous, T[] current, Action<T[]> restore)
    {
        if (IsPreparing)
        {
            WhenPreparationEnds(keeps =>
            {
                if (keeps)
                {
                    Release(previous);
                }
                else
                {
                    restore(previous);
                    Release(current);
                }
            });
        }
        else
        {
            Release(previous);
        }
    }

    private sealed record CapacityChange(Action<bool> Finish, CapacityChange? Previous);

    public static long ArrayBytes<T>(int length) => AlignedBytes(3L * IntPtr.Size + (long)Unsafe.SizeOf<T>() * length);

    public static long ObjectBytes(long fields) => AlignedBytes(2L * IntPtr.Size + fields);

    private static long AlignedBytes(long bytes) => (bytes + IntPtr.Size - 1) & -IntPtr.Size;

    public bool TryReserve(long bytes)
    {
        lock (_gate)
        {
            if (_closed || bytes > Limit - _charged)
            {
                return false;
            }

            _charged += bytes;
            _peak = Math.Max(_peak, _charged);
            return true;
        }
    }

    public void Reserve(long bytes)
    {
        if (!TryReserve(bytes))
        {
            throw new BudgetRefusedException(LatticeLimitEnum.Memory);
        }
    }

    // Called after a reservation produced an actual backing. Weak keys allow a builder to be
    // collected without the inventory extending the lifetime of the builder's allocations.
    public void Own(object backing, string owner)
    {
        if (_backings is not null)
        {
            lock (_gate)
            {
                _backings.Add(backing, owner);
            }
        }
    }

    public void Forget(object backing)
    {
        if (_backings is not null)
        {
            lock (_gate)
            {
                _backings.Remove(backing);
            }
        }
    }

    public void ObserveContainer(object container, string owner, Action<object, string, Action<string, object>> visit)
    {
        if (_containers is not null)
        {
            lock (_gate)
            {
                _containers.TryAdd(container, new Container(owner, visit));
            }
        }
    }

    public void ForgetContainer(object container)
    {
        if (_containers is not null)
        {
            lock (_gate)
            {
                _containers.Remove(container);
            }
        }
    }

    private sealed record Container(string Owner, Action<object, string, Action<string, object>> Visit);

    public T[] Allocate<T>(int length, string owner)
    {
        if (length == 0)
        {
            return [];
        }

        var bytes = ArrayBytes<T>(length);
        Reserve(bytes);

        try
        {
            var backing = new T[length];
            Own(backing, owner);
            return backing;
        }
        catch
        {
            Release(bytes);
            throw;
        }
    }

    public void Release<T>(T[] backing)
    {
        if (backing.Length > 0)
        {
            Release(ArrayBytes<T>(backing.Length), backing);
        }
    }

    public void Release(long bytes, object? backing = null)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            if (backing is not null)
            {
                _backings?.Remove(backing);
            }

            _charged -= bytes;
            Changed();
        }
    }

    public void GrantedNative(int bytes)
    {
        lock (_gate)
        {
            _native += bytes;
        }
    }

    public void ReleasedNative(int bytes, long capacity)
    {
        lock (_gate)
        {
            _native -= bytes;
            _charged -= capacity;
            Changed();
        }
    }

    public void ReusableSpace()
    {
        lock (_gate)
        {
            Changed();
        }
    }

    private void Changed()
    {
        _change++;
        _changed?.TrySetResult();
        _changed = null;
    }

    public long Change
    {
        get
        {
            lock (_gate)
            {
                return _change;
            }
        }
    }

    public Task WhenChanged(long change)
    {
        lock (_gate)
        {
            if (_closed || _change != change)
            {
                return Task.CompletedTask;
            }

            _changed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _changed.Task;
        }
    }

    public (long Charged, long Peak, long Native) Inspect(Action<string, object>? visit)
    {
        lock (_gate)
        {
            if (visit is not null && _backings is not null)
            {
                _backings.Visit(visit);

                foreach (var container in _containers!)
                {
                    container.Value.Visit(container.Key, container.Value.Owner, visit);
                }
            }

            return (_charged, _peak, _native);
        }
    }

    public void ResetPeak()
    {
        lock (_gate)
        {
            _peak = _charged;
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _charged = 0;
            _backings?.Clear();
            _containers?.Clear();
            Changed();
        }
    }
}
