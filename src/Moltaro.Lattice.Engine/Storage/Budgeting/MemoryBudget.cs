using System.Runtime.CompilerServices;

namespace Moltaro.Lattice.Storage.Budgeting;

// The memory budget of one instance, charged by structure (docs/decisions/write-admission.md, Q14;
// milestone contract, "Initial bounded settings"). Every instance has one, never null; a structure
// made alone in a test takes an unlimited one.
//
// What it charges. A native segment at its grant with its managed metadata, from before the grant
// to its return. A managed array that grows with the data, by its bytes, reserved before the array
// is allocated and released when the array is dropped or, for a generation a reader may still hold,
// when its retirement is freed. Small fixed bookkeeping by one headroom, reserved once and reported
// as its own figure. The input of a batch and of a write unit by an estimate.
//
// What protects its state. The charge is one counter changed by interlocked operations: a
// reservation is a compare-and-swap that never passes the limit, a release a subtraction. The lock
// is taken only by the waiting child and by a release that finds the child's flag set, and it
// protects the count of frees and the signal of the wait. No lock is held across an allocation, I/O
// or a wait. The narrative is in docs/design/storage-and-maintenance.md, "The memory budget".
internal sealed class MemoryBudget(long limit = long.MaxValue)
{
    // The header of an array on a 64-bit runtime: the object header, the method table and the
    // length, 8 bytes each. The elements follow, and the whole is rounded up to 8 bytes. Measured with
    // GC.GetAllocatedBytesForCurrentThread on .NET 10, x64: a byte[0] takes 24 bytes, a byte[1] 32.
    private const int ArrayHeaderBytes = 24;

    private readonly Lock _gate = new();

    private long _limit = limit;

    private long _charged;

    private long _peak;

    private volatile bool _closed;

    // The wait of the one child of the instance (see the class). The flag is set while the
    // child retries or waits, the thread while its own attempt runs, so that the attempt's rollback
    // does not count as a free. Both are written by interlocked operations and read without the lock.
    private int _waiting;

    private int _attemptThread;

    // Under _gate: the frees counted while the flag was set, and the signal of the wait.
    private long _frees;

    private TaskCompletionSource? _freed;

    public long Limit => Volatile.Read(ref _limit);

    // What is reserved now: native grants with their metadata, held arrays, the headroom and the
    // estimates in flight.
    public long Charged => Volatile.Read(ref _charged);

    // The largest charge since the budget was made or since ResetPeak.
    public long Peak => Volatile.Read(ref _peak);

    // The bytes of small fixed bookkeeping reserved once by ReserveHeadroom.
    public long Headroom { get; private set; }

    // The bytes of a T[length]: the header and the elements, rounded up to 8 bytes.
    public static long ArrayBytes<T>(int length) => (ArrayHeaderBytes + (long)Unsafe.SizeOf<T>() * length + 7) & ~7L;

    // Sets the limit of an instance once its options are known.
    public void Open(long limit) => Volatile.Write(ref _limit, limit);

    public void ResetPeak() => Volatile.Write(ref _peak, Charged);

    // Adds the bytes unless the charge would pass the limit or the budget is closed; nothing is
    // written when it refuses.
    public bool TryReserve(long bytes)
    {
        var charged = Volatile.Read(ref _charged);
        while (true)
        {
            if (_closed || bytes > Volatile.Read(ref _limit) - charged)
            {
                return false;
            }

            var next = charged + bytes;
            var seen = Interlocked.CompareExchange(ref _charged, next, charged);
            if (seen == charged)
            {
                RaisePeak(next);
                return true;
            }

            charged = seen;
        }
    }

    // TryReserve that refuses with BudgetRefusedException and the limit Memory.
    public void Reserve(long bytes)
    {
        if (!TryReserve(bytes))
        {
            throw new BudgetRefusedException(LatticeLimitEnum.Memory);
        }
    }

    // Subtracts the bytes, then wakes the waiting child if one is marked and the release does not
    // come from its own attempt. After close it changes nothing: close cleared the charge, and no
    // decision reads it afterwards.
    public void Release(long bytes)
    {
        if (_closed)
        {
            return;
        }

        // The subtraction is a full fence, so the flag is read after the bytes are free: a child that
        // marks itself and then tries again sees either these bytes or this release's count.
        Interlocked.Add(ref _charged, -bytes);
        NoteReusable();
    }

    // Reserves the bytes of a T[length], then allocates it. An empty array is the shared one and is
    // not charged.
    public T[] Allocate<T>(int length)
    {
        if (length == 0)
        {
            return [];
        }

        var bytes = ArrayBytes<T>(length);
        Reserve(bytes);
        try
        {
            return new T[length];
        }
        catch
        {
            Release(bytes);
            throw;
        }
    }

    // Releases the bytes of an array the caller drops.
    public void Release<T>(T[] array)
    {
        if (array.Length != 0)
        {
            Release(ArrayBytes<T>(array.Length));
        }
    }

    // For a structure no reader holds: reserves the new array of the length given, allocates it,
    // copies the old elements, publishes it in the reference and releases the old one. A refusal
    // leaves the array and the charge as they were.
    public void Grow<T>(ref T[] array, int length)
    {
        var grown = Allocate<T>(length);
        var previous = array;
        previous.AsSpan(0, Math.Min(previous.Length, length)).CopyTo(grown);
        Volatile.Write(ref array, grown);
        Release(previous);
    }

    // Reserves the capacity List<T> takes next for count elements, sets List<T>.Capacity and
    // releases the old capacity. List<T>.Capacity is the length of its one array, and its setter
    // allocates exactly that length (.NET 10).
    public void EnsureCapacity<T>(List<T> list, int count)
    {
        var capacity = list.Capacity;
        if (count <= capacity)
        {
            return;
        }

        var next = Math.Max(count, capacity == 0 ? 4 : (int)Math.Min(2L * capacity, Array.MaxLength));
        var bytes = ArrayBytes<T>(next);
        Reserve(bytes);
        try
        {
            list.Capacity = next;
        }
        catch
        {
            Release(bytes);
            throw;
        }

        if (capacity != 0)
        {
            Release(ArrayBytes<T>(capacity));
        }
    }

    // Reserves the headroom once and makes it the figure Headroom.
    public void ReserveHeadroom(long bytes)
    {
        Reserve(bytes);
        Headroom = bytes;
    }

    // The wait of the one child of the instance. Sets the waiter's flag and the thread of its next
    // attempt with an interlocked write and returns a ticket, the count of frees so far.
    public long BeginRetry()
    {
        Interlocked.Exchange(ref _attemptThread, Environment.CurrentManagedThreadId);
        Interlocked.Exchange(ref _waiting, 1);
        lock (_gate)
        {
            return _frees;
        }
    }

    // Clears the thread of the attempt; called in a finally after the attempt.
    public void EndRetry() => Interlocked.Exchange(ref _attemptThread, 0);

    // Completes at the first free counted after the ticket, or at close.
    public Task WhenFreedAfter(long ticket)
    {
        lock (_gate)
        {
            if (_closed || _frees > ticket)
            {
                return Task.CompletedTask;
            }

            _freed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _freed.Task;
        }
    }

    // Clears the waiter's flag; called in a finally when the wait ends.
    public void EndWait() => Interlocked.Exchange(ref _waiting, 0);

    // Called where a native slot or a root slot becomes reusable without a release of bytes, and by
    // every release: counts a free for the waiting child, unless no child waits or the call comes
    // from the child's own attempt.
    public void NoteReusable()
    {
        if (Volatile.Read(ref _waiting) == 0 || Volatile.Read(ref _attemptThread) == Environment.CurrentManagedThreadId)
        {
            return;
        }

        lock (_gate)
        {
            _frees++;
            _freed?.TrySetResult();
            _freed = null;
        }
    }

    // Ends admission, completes the wait and clears the charge.
    public void Close()
    {
        _closed = true;
        Interlocked.Exchange(ref _charged, 0);
        lock (_gate)
        {
            _freed?.TrySetResult();
            _freed = null;
        }
    }

    private void RaisePeak(long charged)
    {
        var peak = Volatile.Read(ref _peak);
        while (charged > peak)
        {
            var seen = Interlocked.CompareExchange(ref _peak, charged, peak);
            if (seen == peak)
            {
                return;
            }

            peak = seen;
        }
    }
}
