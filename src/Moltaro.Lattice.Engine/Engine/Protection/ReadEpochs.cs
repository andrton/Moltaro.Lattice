using System.Numerics;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Protection;

// Adapted from Garnet/Tsavorite (https://github.com/microsoft/garnet), the class LightEpoch of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation (see
// THIRD-PARTY-NOTICES.md).
//
// The protection of the reads of one database by epochs (the owner's decision of 16 November 2026;
// arena protocol, "Deferred reclamation, 16 November 2026"). The database has one epoch, a number
// that only grows, and a table of slots. A read enters by writing the current epoch into a free slot
// and leaves by clearing it; that slot is the only memory it writes, and no other read writes it
// while it is held. The one writer retires what a read could still reach, tagged with the current
// epoch (RetiredMemory), and frees it once every slot is free or holds a later epoch: then every
// read that entered before it was retired has left. The writer never waits for a read; a read never
// waits for the writer.
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
//   enters and leaves within one call, so it needs no memory of its own between them. Measured in
//   phase 1 of S08: 9 ns for one enter and leave on one thread, 37 to 44 ns on 20; a thread-static
//   start measured 8 to 11 and 19 to 24 ns.
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
// entry: Writes.WriteExecutor), then moves the epoch on by Interlocked.Increment, a full fence, and
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

    // The current epoch; 1 when the database opens. Only the writer and the close move it on.
    private long _current = 1;

    // 1 once the database is closed: a read that finds it leaves at once.
    private int _closed;

    // What the next read by key that finds its root runs inside the protection, for the tests of
    // the engine (EngineDatabase.HoldNextRead); null when no test armed it, which is all a read pays.
    private Action<RootHandle>? _hold;

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
    // and writes only its slot. ObjectDisposedException of the database after the close.
    //
    // When every slot is held it waits for one to be left, spinning and then yielding its processor.
    // The wait ends: a read holds its slot only while it looks up and copies, which waits for nothing
    // and runs no code of the application (Entities.IRootCopy), so every held slot is left within one
    // step of a read; a test that holds reads through HoldNextRead holds each for a bounded time. A
    // waiting read takes part in no refusal and never fails for the wait.
    public int Enter()
    {
        var start = Thread.GetCurrentProcessorId();
        var spinner = default(SpinWait);

        while (true)
        {
            for (var probe = 0; probe < _slots.Length; probe++)
            {
                var slot = (start + probe) & _mask;
                ref var epoch = ref _slots[slot].Epoch;

                if (Volatile.Read(ref epoch) != Free)
                {
                    continue;
                }

                // The full fence of the exchange orders the read of the mark and of everything the read
                // reads after it (see the class).
                if (Interlocked.CompareExchange(ref epoch, Volatile.Read(ref _current), Free) != Free)
                {
                    continue;
                }

                if (Volatile.Read(ref _closed) != 0)
                {
                    Exit(slot);
                    ObjectDisposedException.ThrowIf(true, _owner);
                }

                return slot;
            }

            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, _owner);
            spinner.SpinOnce();
        }
    }

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
    // leaves at once, and the close waits for the others with WaitForReadsBefore. The exchange and
    // the increment are full fences: either a read sees the mark, or the close sees its slot.
    public long Close()
    {
        Interlocked.Exchange(ref _closed, 1);

        return Interlocked.Increment(ref _current);
    }

    // Arms the switch of the tests: the next read by key that finds its root runs hold once.
    public void HoldNext(Action<RootHandle> hold) => Volatile.Write(ref _hold, hold);

    // Runs the switch of the tests when it is armed, inside the protection, with the handle of the
    // root entry the read found; otherwise a read pays one load of a field nothing writes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OfferHold(RootHandle handle)
    {
        if (_hold is not null)
        {
            RunHold(handle);
        }
    }

    private void RunHold(RootHandle handle) => Interlocked.Exchange(ref _hold, null)?.Invoke(handle);

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
