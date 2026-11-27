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
// is taken only by the waiting child and by a release that finds the child's flag set. No lock is
// held across an allocation, I/O or a wait. The narrative is in docs/design/storage-and-maintenance.md.
internal sealed class MemoryBudget
{
    // The header of an array on a 64-bit runtime: the object header, the method table and the
    // length, 8 bytes each. The elements follow, and the whole is rounded up to 8 bytes.
    private const int ArrayHeaderBytes = 24;

    // An unlimited budget by default; Open sets the limit of an instance once it is known.
    public MemoryBudget(long limit = long.MaxValue) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public long Limit => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // What is reserved now: native grants with their metadata, held arrays, the headroom and the
    // estimates in flight.
    public long Charged => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The largest charge since the budget was made or since ResetPeak.
    public long Peak => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The bytes of small fixed bookkeeping reserved once by ReserveHeadroom.
    public long Headroom => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The bytes of a T[length]: the header and the elements, rounded up to 8 bytes.
    public static long ArrayBytes<T>(int length) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public void Open(long limit) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    public void ResetPeak() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Adds the bytes unless the charge would pass the limit or the budget is closed; nothing is
    // written when it refuses.
    public bool TryReserve(long bytes) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // TryReserve that refuses with BudgetRefusedException and the limit Memory.
    public void Reserve(long bytes) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Subtracts the bytes, then wakes the waiting child if one is marked and the release does not
    // come from its own attempt. After close it changes nothing.
    public void Release(long bytes) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Reserves the bytes of a T[length], then allocates it.
    public T[] Allocate<T>(int length) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Releases the bytes of an array the caller drops.
    public void Release<T>(T[] array) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // For a structure no reader holds: reserves the new array of the length given, allocates it,
    // copies the old elements, publishes it in the reference and releases the old one.
    public void Grow<T>(ref T[] array, int length) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Reserves the capacity List<T> takes next for count elements, sets List<T>.Capacity and
    // releases the old capacity.
    public void EnsureCapacity<T>(List<T> list, int count) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Reserves the headroom once and makes it the figure Headroom.
    public void ReserveHeadroom(long bytes) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // The wait of the one child of the instance. Sets the waiter's flag and the thread of its next
    // attempt with an interlocked write and returns a ticket, the count of frees so far.
    public long BeginRetry() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Clears the thread of the attempt; called in a finally after the attempt.
    public void EndRetry() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Completes at the first free counted after the ticket, or at close.
    public Task WhenFreedAfter(long ticket) => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Clears the waiter's flag; called in a finally when the wait ends.
    public void EndWait() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Called where a native slot or a root slot becomes reusable without a release of bytes.
    public void NoteReusable() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");

    // Ends admission, completes the wait and clears the charge.
    public void Close() => throw new NotSupportedException("Charging memory by structure is not implemented yet.");
}
