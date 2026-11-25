using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet), the class LightEpoch of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// The protection of the reads of one database by epochs (docs/design/arena-protocol.md, "Deferred
// reclamation"). The database has one epoch, a number that only grows, and a table of slots. A read
// enters by writing the current epoch into a free slot and leaves by clearing it; that slot is the only
// memory it writes, and no other read writes it while it is held. The publishing thread retires what a read
// could still reach, tagged with the current epoch (RetiredMemory), and frees it once every slot is free
// or holds a later epoch. A publication does not wait for a read. Reclamation can wait for
// every older slot to be cleared, including slots held by a protected viewer.
//
// What is taken from LightEpoch: the table of padded slots of one instance, sized as LightEpoch sizes
// it and rounded up to a power of two (below); entering by a compare-and-swap of the current epoch
// into a free slot (TryClaimEntry); one epoch for the instance that only the writer moves on
// (BumpCurrentEpoch); and the safe epoch, one below the oldest epoch a slot holds
// (ComputeNewSafeToReclaimEpoch). What differs, and why:
// - No static or thread-static state, and no limit on the number of instances (AGENTS.md,
//   principles 9 and 10). LightEpoch keeps the slot of a thread in thread-static memory, indexed by
//   an instance id from a static table of 1,024. Here a read starts its search at the slot of the
//   processor it runs on and keeps the slot it took in a local, which it gives to Exit: a read
//   enters and leaves within one call, so it needs no memory of its own between them. Measured: 9 ns
//   for one enter and leave on one thread, 37 to 44 ns on 20; a thread-static start measured 8 to 11
//   and 19 to 24 ns.
// - A read leaves by a store with release semantics, not an interlocked exchange: no reader waits
//   on a semaphore to be woken (LightEpoch's waiters), so nothing has to be read after the store.
// - A full table is waited out by spinning (Enter), not on a semaphore.
// - No read ever runs a deferred action. LightEpoch drains its list from every thread that
//   acquires or releases; here what is retired lives in the arena, the table of root entries and
//   the primary index, which only the thread holding the lock of the writes changes, so only that
//   thread frees (RetiredMemory).
// - The close marks the instance closed and waits for the reads that entered before the mark
//   (Close, WaitForReadsBefore); LightEpoch's Dispose does not wait for protected threads.
//
// Why a read and the writer never miss each other (the .NET memory model, not that of x64). The
// writer first unlinks what it retires, by a store with release semantics (the index, the root
// entry: Entities.ChangePublisher), then moves the epoch on by Interlocked.Increment, a full fence, and
// only then reads the slots (Advance). A read reads the epoch, writes it into a slot by
// Interlocked.CompareExchange, a full fence, and only then reads the index and the roots. Of two
// threads that each write one place and then read the other's, behind full fences, at least one
// sees the other's write: either the writer sees the slot held, with an epoch that keeps what it
// retired from being freed, or the read sees the unlinked state and cannot reach what was retired.
// A read that announced an epoch above the tag of a retired thing read the epoch after the
// increment that followed the unlink, so it sees the unlink as well. A read leaves by Volatile.Write,
// a release: everything it read is read before a writer that reads the slot free, by Volatile.Read,
// frees anything.
//
// The number of slots: the larger of 128 and twice the logical processors, as LightEpoch takes it,
// rounded up to a power of two, so that the number of a processor masked with the slots less one is
// a slot of the table (Enter) without a division on the path of every read.
//
// Memory: 128 bytes a slot, 16 KiB at up to 64 logical processors. Nothing per thread.
internal sealed class ReadEpochs
{
    // A slot that no read holds.
    private const long Free = 0;

    // The object a read refused after the close names, for ObjectDisposedException.
    private readonly object _owner;

    private readonly EpochSlot[] _slots;

    // The slots less one: a number masked with it is a slot of the table.
    private readonly int _mask;

    // The bits of State: the database is closed, the database is faulted.
    private const int Closed = 1;

    private const int Faulted = 2;

    // The current epoch; 1 when the database opens. Only the writer and the close move it on.
    private long _current = 1;

    // Closed once the database is closed, and Faulted once a publication failed (Close, Fault): a read
    // that finds either leaves at once. 0 while the database serves reads, which is all a read loads
    // of it on its ordinary path. A bit, once set, stays set.
    private int _state;

    // An action at the next read cut under protection (EngineDatabase.ArmReadSwitch).
    // The cuts are before reading a root found by key or search, and before confirming a
    // protected view's candidate miss. A read only loads a null field when the switch is unarmed.
    private Action<RootHandle>? _readSwitch;

    public ReadEpochs(object owner)
    {
        _owner = owner;
        _slots = new EpochSlot[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Environment.ProcessorCount))];
        _mask = _slots.Length - 1;
    }

    // The number of slots: how many reads can be inside at once without waiting.
    public int SlotCount => _slots.Length;

    // The current epoch: the tag of what the writer retires now. Read by the writer, which is the
    // only one that moves it on besides the close.
    public long Current => Volatile.Read(ref _current);

    // Enters a read and returns the slot it holds, which the read gives to Exit. A read takes no lock
    // and writes only its slot. ObjectDisposedException of the database after the close, and
    // LatticeException with Unavailable once the database is faulted.
    //
    // When every slot is held it waits for one to be left, spinning and then yielding its processor.
    // An owned read holds its slot for one engine step. A protected view holds its slot while the
    // application callback runs. The caller keeps that callback short because all slots can be held
    // by viewers (exception X6 and docs/design/arena-protocol.md, "Protected views").
    // The token of an asynchronous read ends the wait with OperationCanceledException. Enter
    // checks it only after finding every slot held.
    public int Enter(CancellationToken cancellationToken = default)
    {
        if (!TryEnter(cancellationToken, out var slot))
        {
            ThrowForState();
        }

        return slot;
    }

    // Enters a read as Enter does, but returns false, holding no slot, where Enter refuses the read after
    // the close or once the database is faulted, so that the caller can let go of what it holds before it
    // reports the refusal (ThrowForState).
    public bool TryEnter(CancellationToken cancellationToken, out int slot)
    {
        var start = Thread.GetCurrentProcessorId();
        var spinner = default(SpinWait);

        while (true)
        {
            for (var probe = 0; probe < _slots.Length; probe++)
            {
                slot = (start + probe) & _mask;

                if (!TryClaim(slot))
                {
                    continue;
                }

                if (Volatile.Read(ref _state) != 0)
                {
                    Exit(slot);

                    return false;
                }

                return true;
            }

            if (Volatile.Read(ref _state) != 0)
            {
                slot = 0;

                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spinner.SpinOnce();
        }
    }

    // The mark of a fault, visible to the database state, a child about to forward a write and a read
    // retrying while a publication is incomplete (Entities.EntityTable). The publication will never
    // finish, so the read gives up. The ordinary path of a read does not call this.
    public bool IsFaulted => (Volatile.Read(ref _state) & Faulted) != 0;

    // Whether a read that entered now would be refused: the database is closed or faulted (ThrowForState).
    public bool Refuses => Volatile.Read(ref _state) != 0;

    // The refusal of a read that found the state set: ObjectDisposedException after the close, which
    // comes first, Unavailable after a fault.
    [DoesNotReturn]
    public void ThrowForState()
    {
        ObjectDisposedException.ThrowIf((Volatile.Read(ref _state) & Closed) != 0, _owner);

        throw FaultedRefusal();
    }

    // The refusal of an operation of a database that faulted: Unavailable, for good.
    public static LatticeException FaultedRefusal() =>
        new(LatticeErrorEnum.Unavailable, "The database is Faulted: a publication failed, and it serves nothing until a new instance is opened.");

    // Leaves the slot a read took. The release orders everything the read read before the slot is
    // seen free (see the class).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Exit(int slot) => Volatile.Write(ref _slots[slot].Epoch, Free);

    // For the writer: moves the epoch on and returns the safe epoch, the latest epoch whose retired
    // things no read in progress can reach: one below the oldest epoch a slot holds, or the epoch
    // before the new one when no slot is held.
    public long Advance()
    {
        // The full fence of the increment: what the writer unlinked before is seen by every read that
        // reads the new epoch, and the slots are read after it.
        var current = Interlocked.Increment(ref _current);

        return OldestHeld(current) - 1;
    }

    // Whether a read that entered with an epoch below this one is still inside.
    public bool HasReadsBefore(long epoch) => OldestHeld(epoch) < epoch;

    // Returns once no read that entered with an epoch below this one is inside: for the close, and
    // for a write under memory pressure, which waits for the reads that hold retired memory. The wait
    // ends for the reason Enter gives; a read that enters meanwhile announces a later epoch, because
    // the caller moved the epoch on before. It spins, then yields its processor, then sleeps. The
    // token cancels it, with OperationCanceledException.
    public void WaitForReadsBefore(long epoch, CancellationToken cancellationToken)
    {
        var spinner = default(SpinWait);

        while (HasReadsBefore(epoch))
        {
            cancellationToken.ThrowIfCancellationRequested();
            spinner.SpinOnce();
        }
    }

    // Marks the database closed and returns the epoch the reads after the mark announce at the
    // least. A read that entered before the mark goes on; one that enters after it sees the mark and
    // leaves at once, and the close waits for the others with WaitForReadsBefore. The interlocked or
    // and the increment are full fences: either a read sees the mark, or the close sees its slot.
    public long Close()
    {
        Interlocked.Or(ref _state, Closed);

        return Interlocked.Increment(ref _current);
    }

    // Marks the database faulted: a read that enters from now on reports Unavailable, and a read inside
    // that retries on the mark of the write whose publication failed gives up (IsFaulted). Returns
    // whether this call set the mark, so a test can hold the publishing thread once at that point.
    public bool Fault() => (Interlocked.Or(ref _state, Faulted) & Faulted) == 0;

    // A slot taken by a compare-and-swap of the current epoch into it, when it is free. The full fence
    // of the exchange orders the read of the state and of everything the read reads after it (see the
    // class).
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryClaim(int slot)
    {
        ref var epoch = ref _slots[slot].Epoch;

        return Volatile.Read(ref epoch) == Free && Interlocked.CompareExchange(ref epoch, Volatile.Read(ref _current), Free) == Free;
    }

    // Arms the read switch of the tests: the next read by key that finds its root, or the next root a
    // step of a search takes, runs the action once.
    public void ArmReadSwitch(Action<RootHandle> action) => Volatile.Write(ref _readSwitch, action);

    // Runs the read switch of the tests when it is armed, inside the protection, with the handle of the
    // root entry the read found; otherwise a read pays one load of a field nothing writes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OfferReadSwitch(RootHandle handle)
    {
        if (_readSwitch is not null)
        {
            RunReadSwitch(handle);
        }
    }

    private void RunReadSwitch(RootHandle handle) => Interlocked.Exchange(ref _readSwitch, null)?.Invoke(handle);

    // The oldest epoch a slot holds, or the given one when no slot holds an older one.
    private long OldestHeld(long epoch)
    {
        var oldest = epoch;

        foreach (ref var slot in _slots.AsSpan())
        {
            var held = Volatile.Read(ref slot.Epoch);

            if (held != Free && held < oldest)
            {
                oldest = held;
            }
        }

        return oldest;
    }
}
