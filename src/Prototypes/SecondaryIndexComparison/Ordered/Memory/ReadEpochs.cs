using System.Numerics;
using System.Runtime.InteropServices;

namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison.Ordered.Memory;

// The protection of reads by epochs, reduced from the engine's
// (src/Moltaro.Lattice.Engine/Engine/Protection/ReadEpochs.cs), which is adapted from Garnet/Tsavorite
// (https://github.com/microsoft/garnet), the class LightEpoch of
// libs/storage/Tsavorite/cs/src/core/Epochs/LightEpoch.cs at commit
// 3d104a76ec410499545bc7fb39d0e3168f8ee709; MIT licence, Copyright (c) Microsoft Corporation.
//
// A read enters by a compare-and-swap of the current epoch into a free slot, starting at the slot of
// the processor it runs on, and leaves by a store with release semantics; that slot is the only
// memory it writes. The writer moves the epoch on by an interlocked increment, a full fence, and
// reads every slot: what it retired with a tag at or below the safe epoch no read in progress can
// reach. The close, the fault and the switch of the tests of the engine are left out.
internal sealed class ReadEpochs
{
    private const long Free = 0;

    private readonly Slot[] _slots;

    private readonly int _mask;

    private long _current = 1;

    public ReadEpochs()
    {
        _slots = new Slot[(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(128, 2 * Environment.ProcessorCount))];
        _mask = _slots.Length - 1;
    }

    public int SlotCount => _slots.Length;

    public long Current => Volatile.Read(ref _current);

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

                if (Volatile.Read(ref epoch) == Free && Interlocked.CompareExchange(ref epoch, Volatile.Read(ref _current), Free) == Free)
                {
                    return slot;
                }
            }

            spinner.SpinOnce();
        }
    }

    public void Exit(int slot) => Volatile.Write(ref _slots[slot].Epoch, Free);

    // For the writer: moves the epoch on and returns the safe epoch.
    public long Advance()
    {
        var current = Interlocked.Increment(ref _current);
        var oldest = current;

        foreach (ref var slot in _slots.AsSpan())
        {
            var held = Volatile.Read(ref slot.Epoch);

            if (held != Free && held < oldest)
            {
                oldest = held;
            }
        }

        return oldest - 1;
    }

    // One slot on lines of its own, as the engine's EpochSlot.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct Slot
    {
        [FieldOffset(0)]
        public long Epoch;
    }
}
