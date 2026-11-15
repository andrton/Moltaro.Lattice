using System.Numerics;

namespace Moltaro.Lattice.Engine.Protection;

// The gate every read of one database passes (brief of S08, "The scaling of reads"; arena protocol,
// "Lock and lifetime order"). A read holds the gate from its lookup in the index to the end of its
// copy of the root, and holds nothing else: it is the protection of the index, of the root entries,
// of the blocks of the roots and of the arena against the close, in one. Holding the gate writes one
// slot that no other read writes, so reads on different processors do not slow each other down.
//
// The mechanism is not new; each part is that of an established system:
// - The slots. A read takes a slot of an array that belongs to the database, starting at the slot of
//   the processor it runs on and taking the next free one when that is held, by one compare-and-swap,
//   as the deferred readers of folly's SharedMutex do and as .NET's shared array pool picks its
//   per-core stack (Thread.GetCurrentProcessorId). Two reads on one processor, or a thread that moved
//   to another, only take another slot: a slot is held by one read at a time.
// - Exclusion. A write that changes the index in place, an insert, a delete or a growth of an index,
//   sets the mark Excluding and then waits until no slot is held, as a writer of Linux's
//   percpu-rwsem sets its block flag and waits for the readers' counts to drain. A read that took a
//   slot and then finds the mark leaves the slot and waits until the mark is gone. The write changes
//   the index alone, and AdmitReads lets the reads in again.
// - Waiting for the reads. A replace does not exclude reads: it installs the new root, then waits
//   until every read that held a slot at that moment has left it, and only then frees the old root,
//   as synchronize_rcu of liburcu waits for the readers it saw inside a read-side section. A read that
//   takes a slot after the install finds the new root. The write frees within its own call, so no
//   block is ever retired and none waits for a later moment: once the write has returned, what it
//   freed is free.
// - A write that waits long. A write first spins on the slot it waits for; when the read that holds
//   it takes longer, most often because the system has taken its processor away, the write names the
//   slot and sleeps until the read that leaves it wakes it, as synchronize_rcu sleeps on its futex
//   and the reader's unlock wakes it. So a writer gives its processor to that read instead of
//   spinning against it while more threads run than there are processors.
// - The close. The close sets the mark Closed and waits until no slot is held; a read that finds the
//   mark leaves its slot and fails with ObjectDisposedException of the database.
//
// Why a read and a write never both miss each other. A read takes its slot by an interlocked
// compare-and-swap, a full fence, and only then reads the state and the index; a write sets its mark
// by an interlocked operation, or makes a full fence after its change, and only then reads the
// slots. Of two threads that each write one place and then read the other's, behind full fences, at
// least one sees the other's write: either the write sees the slot held and waits for it, or the read
// sees the mark, or the new root. A close that read the slots first and set its mark afterwards would
// miss a read that took its slot in between; so the mark comes first, by Interlocked.Or. The wake-up
// is the same pair the other way round: a read leaves its slot by an interlocked exchange and then
// reads which slot a write sleeps on; the write names the slot by an interlocked exchange and then
// reads the slot once more before it sleeps. Either the write sees the slot left and does not sleep,
// or the read sees its slot named and wakes the write.
//
// Why the waits end. A read holds a slot only for synchronous work in memory, which waits for
// nothing a writer holds: no code of the application runs while it is held (IRootCopy), and the only
// wait inside it is for a replace of the same root entry to finish its few stores (EntityTable). A
// write waits for reads only while it holds the lock of the writes, which no read takes.
//
// The order. A write enters the close barrier, takes the lock of the writes, and then either
// excludes the reads or waits for them; nothing is taken after that. A read takes the gate alone and
// nothing inside it. The close sets its mark here before it closes the barrier of the writes
// (EngineDatabase), and neither is held while the other is waited for.
//
// Memory: the slots are 128 bytes each, twice as many as the logical processors rounded up to a power
// of two, 64 slots and 8 KiB on a machine of 20 logical processors; the values a wait for the reads
// saw, 8 bytes a slot; and one event a writer sleeps on. A thread holds nothing of its own.
internal sealed class ReadGate
{
    // A write that changes the index has excluded the reads, until AdmitReads.
    private const int Excluding = 1;

    // The database is closed: no read passes any more.
    private const int Closed = 2;

    // No write sleeps on a slot.
    private const int NoSlot = -1;

    // The object a read refused after the close is disposed of, for its message.
    private readonly object _owner;

    private readonly ReaderSlot[] _slots;

    // The slots less one: a slot number masked with it is a slot of the array.
    private readonly int _mask;

    // The sequences of the slots that WaitForReads saw, kept here so that a wait allocates nothing.
    // Only the thread that executes writes waits for the reads, so one array serves every wait.
    private readonly long[] _seen;

    // What a write that sleeps on a slot waits for: the read that leaves the slot sets it. It is not
    // disposed: it holds a wait handle only once a write slept on it, which the collector releases,
    // and disposing it at a close, while a late read might still leave its slot, would only trade an
    // ObjectDisposedException of the database for one of the event.
    private readonly ManualResetEventSlim _slotLeft = new(initialState: false, spinCount: 1);

    // Excluding and Closed. Written by interlocked operations only.
    private int _state;

    // The slot a write sleeps on, or NoSlot. Only the thread that executes writes sleeps, so there is
    // one at most.
    private int _awaitedSlot = NoSlot;

    public ReadGate(object owner)
    {
        _owner = owner;
        _slots = new ReaderSlot[(int)BitOperations.RoundUpToPowerOf2((uint)(2 * Environment.ProcessorCount))];
        _mask = _slots.Length - 1;
        _seen = new long[_slots.Length];
    }

    // Lets the read in and returns the slot it holds, which it gives to Exit. While a write excludes
    // the reads it waits until the write admits them again. ObjectDisposedException of the database
    // after the close.
    public int Enter()
    {
        var spinner = default(SpinWait);

        while (true)
        {
            if (!TryTake(out var slot))
            {
                // Every slot is held, by more reads at once than twice the processors.
                spinner.SpinOnce();
                continue;
            }

            // After the full fence of the take: the state a write or the close set before it read the
            // slots, or a state from before the write, which then sees this slot held.
            var state = Volatile.Read(ref _state);

            if (state == 0)
            {
                return slot;
            }

            Exit(slot);
            ObjectDisposedException.ThrowIf((state & Closed) != 0, _owner);

            // A write changes an index in place: some hundred nanoseconds for an insert or a delete,
            // longer while an index grows, when the read lets its processor go.
            var excluded = default(SpinWait);

            while (Volatile.Read(ref _state) == Excluding)
            {
                excluded.SpinOnce();
            }
        }
    }

    // Leaves the slot a read took, and wakes the write that sleeps on it.
    public void Exit(int slot)
    {
        ref var sequence = ref _slots[slot].Sequence;

        // Only the read that holds a slot changes its odd sequence, so the plain read finds its own.
        // The exchange is a full fence: everything the read read is read before, and the slot a write
        // sleeps on is read after (see the class).
        Interlocked.Exchange(ref sequence, sequence + 1);

        if (Volatile.Read(ref _awaitedSlot) == slot)
        {
            _slotLeft.Set();
        }
    }

    // Lets no read pass until AdmitReads, and returns once no read is inside any more. For a write
    // that changes an index in place. Called by the thread that executes writes, under the lock of
    // the writes.
    public void ExcludeReads()
    {
        Interlocked.Or(ref _state, Excluding);

        for (var slot = 0; slot < _slots.Length; slot++)
        {
            // A read that takes the slot from now on finds the mark and leaves it again at once.
            long held;

            while (((held = Volatile.Read(ref _slots[slot].Sequence)) & 1) != 0)
            {
                WaitWhileHeld(slot, held);
            }
        }
    }

    // Lets the reads pass again. Interlocked: what the write changed is seen by every read that passes
    // after it.
    public void AdmitReads() => Interlocked.And(ref _state, ~Excluding);

    // Returns once every read that was inside when it was called has left. The caller made its change
    // before, by stores that a read that comes later sees; what the reads that were inside could still
    // reach, it frees after. Called by the thread that executes writes, under the lock of the writes.
    public void WaitForReads()
    {
        // The change of the caller is made before the slots are read (see the class).
        Interlocked.MemoryBarrier();

        // The sequences first, then the waits: a wait for the reads seen at one moment, not for a
        // read that came later on a slot that is looked at later.
        for (var slot = 0; slot < _slots.Length; slot++)
        {
            _seen[slot] = Volatile.Read(ref _slots[slot].Sequence);
        }

        for (var slot = 0; slot < _slots.Length; slot++)
        {
            if ((_seen[slot] & 1) != 0)
            {
                WaitWhileHeld(slot, _seen[slot]);
            }
        }
    }

    // Lets no read pass any more, and returns once no read is inside. Called once, by the close,
    // perhaps while a write waits for the reads as well; so it does not sleep on a slot as a write
    // does, but lets its processor go by the thread's own sleep when a read takes long.
    public void Close()
    {
        Interlocked.Or(ref _state, Closed);

        var spinner = default(SpinWait);

        for (var slot = 0; slot < _slots.Length; slot++)
        {
            while ((Volatile.Read(ref _slots[slot].Sequence) & 1) != 0)
            {
                spinner.SpinOnce();
            }
        }
    }

    // Takes a free slot, from the slot of the processor on: false when every slot is held.
    private bool TryTake(out int slot)
    {
        var first = Thread.GetCurrentProcessorId();

        for (var probe = 0; probe < _slots.Length; probe++)
        {
            slot = (first + probe) & _mask;

            ref var sequence = ref _slots[slot].Sequence;
            var free = Volatile.Read(ref sequence);

            if ((free & 1) == 0 && Interlocked.CompareExchange(ref sequence, free + 1, free) == free)
            {
                return true;
            }
        }

        slot = 0;

        return false;
    }

    // For the thread that executes writes: returns once the sequence of the slot is no longer the one
    // that was seen, that is, once the read that held it has left. It spins while that is likely to
    // come soon, then sleeps until the read wakes it.
    private void WaitWhileHeld(int slot, long seen)
    {
        ref var sequence = ref _slots[slot].Sequence;
        var spinner = default(SpinWait);

        while (Volatile.Read(ref sequence) == seen)
        {
            if (!spinner.NextSpinWillYield)
            {
                spinner.SpinOnce();
                continue;
            }

            // Reset before the slot is named: a read that sees its slot named sets the event after
            // this, so the wait below does not miss it.
            _slotLeft.Reset();
            Interlocked.Exchange(ref _awaitedSlot, slot);

            if (Volatile.Read(ref sequence) == seen)
            {
                _slotLeft.Wait();
            }

            Volatile.Write(ref _awaitedSlot, NoSlot);
        }
    }
}
