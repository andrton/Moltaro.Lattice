using System.Diagnostics;
using System.Numerics;

namespace Moltaro.Lattice.Storage.Allocation;

// One native segment of the arena and the allocator metadata of its regions. The segment is
// divided into regions of RegionSize bytes; each region serves one size class at a time and is
// divided into slots of that class. Which slots hold a block is kept here, one bit per slot, so
// that an address is checked against this metadata and never against the memory it points to
// (milestone contract: slot class, capacity and state live in allocator metadata).
//
// The segment owns its native memory from its construction, which obtains it from the arena's
// NativeAllocator, until Release, which returns it there. Only the arena calls Release, once per
// segment: when the segment is left empty and the reserve has no room for it, and when the arena
// is disposed. Like the arena, it takes no lock: one thread at a time changes it, and readers of a
// block only read the bits of the block's own slot (Arena, "Who uses the arena at the same time").
internal sealed unsafe class ArenaSegment
{
    // The bytes of one region: the largest block, so that a region of every class holds at least
    // one slot.
    public const int RegionSize = Arena.MaxBlockSize;

    // The alignment of a segment's memory (milestone contract). Regions start at multiples of
    // RegionSize and every class is a multiple of 16 bytes, so every block starts on a 16-byte
    // boundary too, and the 8-byte fields of its header are aligned.
    public const int Alignment = 16;

    // The words of slot bits each region has: enough for the most slots a region can have, 2,048
    // of the smallest class. Every region has as many, whatever class it serves, so that a region
    // takes another class without metadata of another size.
    public const int SlotWordsPerRegion = RegionSize / SizeClasses.Smallest / 64;

    // One bit per slot, SlotWordsPerRegion words for each region in the order of the regions: set
    // while the slot holds an allocated block. Every bit of a region that serves no class is clear,
    // because each block clears its bit when it is freed.
    private readonly ulong[] _slotBits;

    // One bit per slot, in the same order: set while the block in the slot is prepared, allocated
    // for a write that has not published it yet (Arena.TryAllocatePrepared). It is clear for every
    // free slot, because a block clears it when it is published or freed.
    private readonly ulong[] _preparedBits;

    // One bit per slot, in the same order: set while the block in the slot is retired, detached from
    // its owner but still readable by the reads that could reach it (Arena.Retire). It is clear for
    // every free slot, because a block clears it when it is freed.
    private readonly ulong[] _retiredBits;

    public ArenaSegment(int size, NativeAllocator allocator)
    {
        Regions = new ArenaRegion[size / RegionSize];
        _slotBits = new ulong[Regions.Length * SlotWordsPerRegion];
        _preparedBits = new ulong[_slotBits.Length];
        _retiredBits = new ulong[_slotBits.Length];

        // The native memory comes last. If the metadata cannot be allocated, nothing native is held
        // yet; if the native allocation fails, the metadata is left to the collector.
        Memory = (byte*)allocator.Allocate(size);
    }

    // The start of the segment's memory, null after Release. The memory is not initialized: the
    // arena writes the header of a block when it allocates it, and its owner writes the payload.
    public byte* Memory { get; private set; }

    // The metadata of each region, by its index in the segment.
    public ArenaRegion[] Regions { get; }

    // How many regions serve a size class. At zero the segment is empty.
    public int RegionsInUse { get; set; }

    // Marks the lowest free slot among the first slotCount slots of the region as allocated and
    // returns its index. The arena calls this only for a region that has a free slot; a region of
    // the smallest class searches at most SlotWordsPerRegion words, one of 1 KiB or more one word.
    //
    // The bits beyond the last slot of the class are clear too, but they are higher than every slot,
    // so the lowest clear bit is the lowest free slot as long as the region has one.
    public int TakeSlot(int region, int slotCount)
    {
        var words = _slotBits.AsSpan(region * SlotWordsPerRegion, (slotCount + 63) / 64);

        for (var index = 0; index < words.Length; index++)
        {
            var free = ~words[index];

            if (free != 0)
            {
                var bit = BitOperations.TrailingZeroCount(free);

                words[index] |= 1UL << bit;
                Regions[region].Allocated++;

                return index * 64 + bit;
            }
        }

        throw new UnreachableException("The arena took a slot of a region that has no free slot.");
    }

    // Marks an allocated slot of the region as free.
    public void ReturnSlot(int region, int slot)
    {
        _slotBits[region * SlotWordsPerRegion + slot / 64] &= ~(1UL << (slot % 64));
        Regions[region].Allocated--;
    }

    // Whether the slot of the region holds an allocated block.
    public bool IsAllocated(int region, int slot) =>
        (_slotBits[region * SlotWordsPerRegion + slot / 64] & (1UL << (slot % 64))) != 0;

    // Whether the block in the slot of the region is prepared.
    public bool IsPrepared(int region, int slot) =>
        (_preparedBits[region * SlotWordsPerRegion + slot / 64] & (1UL << (slot % 64))) != 0;

    // Marks the block in the slot of the region as prepared, or as no longer prepared.
    public void SetPrepared(int region, int slot, bool prepared)
    {
        ref var word = ref _preparedBits[region * SlotWordsPerRegion + slot / 64];
        var bit = 1UL << (slot % 64);

        word = prepared ? word | bit : word & ~bit;
    }

    // Whether the block in the slot of the region is retired.
    public bool IsRetired(int region, int slot) =>
        (_retiredBits[region * SlotWordsPerRegion + slot / 64] & (1UL << (slot % 64))) != 0;

    // Marks the block in the slot of the region as retired, or as no longer retired.
    public void SetRetired(int region, int slot, bool retired)
    {
        ref var word = ref _retiredBits[region * SlotWordsPerRegion + slot / 64];
        var bit = 1UL << (slot % 64);

        word = retired ? word | bit : word & ~bit;
    }

    // Returns the native memory to the allocator it came from. Every address into it is then refused
    // by the arena, which no longer holds this segment.
    public void Release(NativeAllocator allocator)
    {
        allocator.Free((nint)Memory);
        Memory = null;
    }
}
