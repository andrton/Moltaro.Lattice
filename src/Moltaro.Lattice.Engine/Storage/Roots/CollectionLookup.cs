using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Storage.Roots;

// The derived lookup of a Map or Set. An entry ordinal names an entry in input order. A slot
// holds a hash and ordinal plus one. Slot blocks never contain entry addresses. The lookup
// owns its entries through its first descriptor and, if needed, owns chunks of slots through
// the subsequent addresses. The publishing thread builds it before publishing its root.
// Reads require protection of the root. See docs/design/arena-protocol.md.
internal readonly ref struct CollectionLookup : ICollectionSlots
{
    public const int MostCompactEntries = 8;

    private const int CompactAfterRemove = 4;

    public const int EntriesOffset = BlockHeader.Size;

    public const int FirstChunkOffset = EntriesOffset + ValueBlocks.DescriptorSize;

    private const int SlotBytes = sizeof(ulong);

    private const int SlotsPerChunk = ValueBlocks.ChunkPayload / SlotBytes;

    private const int ContiguousSlots = (ValueBlocks.ChunkPayload - ValueBlocks.DescriptorSize) / SlotBytes;

    private const int ContiguousEntries = ContiguousSlots * 7 / 10;

    public const int MostEntries = ValueBlocks.MostChunks * SlotsPerChunk * 7 / 10;

    private readonly Arena _arena;

    private readonly Span<ulong> _slots;

    private readonly ReadOnlySpan<ArenaAddress> _chunks;

    public CollectionLookup(Arena arena, scoped in ValueDescriptor descriptor)
        : this(arena, descriptor, OpenPayload(arena, descriptor))
    {
    }

    private CollectionLookup(Arena arena, scoped in ValueDescriptor descriptor, Span<byte> lookupPayload)
    {
        _arena = arena;
        if (descriptor.Kind != DescriptorKindEnum.Lookup)
        {
            return;
        }

        if (IsChunked(descriptor))
        {
            _chunks = ChunkAddresses(lookupPayload);
            Capacity = (_chunks.Length - 1) * SlotsPerChunk + arena.Payload(ReadChunk(_chunks, _chunks.Length - 1)).Length / SlotBytes;
        }
        else
        {
            _slots = MemoryMarshal.Cast<byte, ulong>(lookupPayload[ValueBlocks.DescriptorSize..]);
            Capacity = _slots.Length;
        }
    }

    public int Capacity { get; }

    public static bool IsCompact(int count) => count <= MostCompactEntries;

    public static bool IsCompactAfterEdit(int count, bool hashed) =>
        count <= (hashed ? CompactAfterRemove : MostCompactEntries);

    public static bool IsChunked(in ValueDescriptor descriptor) => descriptor.Length > ContiguousEntries;

    public static ValueDescriptor Entries(Arena arena, in ValueDescriptor descriptor) =>
        ResidentDescriptor.ReadAligned(arena.Payload(ValueBlocks.AddressOf(descriptor)));

    // A probe is bound to the declaration of the key. The holder is the payload
    // that contains the original descriptor, including when the collection is inline.
    // Under root protection, the slots and entry keys use the same lookup payload.
    public static int Find(Arena arena, StructureLayout entry, in ValueDescriptor descriptor, ReadOnlySpan<byte> holder,
        LatticeStringEncodingEnum encoding, in IndexValue key)
    {
        var lookupPayload = OpenPayload(arena, descriptor);
        var keys = new CollectionKeys(arena, entry, descriptor, holder, encoding, lookupPayload);
        return new CollectionLookup(arena, descriptor, lookupPayload).Find(keys, key);
    }

    public static Span<byte> OpenPayload(Arena arena, scoped in ValueDescriptor descriptor) =>
        descriptor.Kind == DescriptorKindEnum.Lookup ? arena.Payload(ValueBlocks.AddressOf(descriptor)) : default;

    public static Span<ArenaAddress> Chunks(Arena arena, ArenaAddress address) =>
        ChunkAddresses(arena.Payload(address));

    public static Span<ArenaAddress> ChunkAddresses(Span<byte> lookupPayload) =>
        MemoryMarshal.Cast<byte, ArenaAddress>(lookupPayload[ValueBlocks.DescriptorSize..]);

    public static int ChunkOffset(int index) => FirstChunkOffset + index * SlotBytes;

    public static int ChunkIndexAtOffset(int offset)
    {
        var delta = offset - FirstChunkOffset;
        return delta >= 0 && delta % SlotBytes == 0 ? delta / SlotBytes : -1;
    }

    // These words begin at aligned block offset 48. Protection of the lookup
    // keeps a slot block alive after this acquisition, even if its address changes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ArenaAddress ReadChunk(ReadOnlySpan<ArenaAddress> chunks, int index)
    {
        ref var word = ref Unsafe.As<ArenaAddress, ulong>(ref Unsafe.AsRef(in chunks[index]));
        return Unsafe.BitCast<ulong, ArenaAddress>(Volatile.Read(ref word));
    }

    public static void PublishChunk(Span<ArenaAddress> chunks, int index, ArenaAddress replacement)
    {
        ref var word = ref Unsafe.As<ArenaAddress, ulong>(ref chunks[index]);
        Volatile.Write(ref word, replacement.Value);
    }

    public static int ChunkCountForSlots(int slots) =>
        slots > ContiguousSlots ? (slots + SlotsPerChunk - 1) / SlotsPerChunk : 0;

    public static int PayloadForSlots(int slots, int chunks)
    {
        if (chunks != 0)
        {
            return ValueBlocks.DescriptorSize + chunks * SlotBytes;
        }

        // The last class step can hold more than twice the required probing
        // range. Keep the class, with the excess charged as class slack.
        var wanted = ValueBlocks.DescriptorSize + slots * SlotBytes;
        return Math.Min(SlotPayload(wanted), wanted + slots * SlotBytes);
    }

    public static int ChunkPayloadForSlots(int slots, int ordinal) =>
        SlotPayload(Math.Min(slots - ordinal * SlotsPerChunk, SlotsPerChunk) * SlotBytes);

    // Installs the descriptor only after its lookup payload is zeroed. Each initialized chunk
    // then becomes reachable from that descriptor. The caller owns cleanup on a false return.
    public static bool TryPrepare(ref ValueHolder holder, int offset, int count, out ValueHolder entries, bool reuseTail)
    {
        entries = default;
        var wanted = SlotsNeeded(count);
        var chunks = ChunkCountForSlots(wanted);
        var length = PayloadForSlots(wanted, chunks);

        if (!holder.TryAllocate(length, BlockKindEnum.Lookup, holder.Address, offset, out var address))
        {
            return false;
        }

        var payload = holder.Arena.Payload(address);
        payload.Clear();
        holder.SetDescriptor(offset, new ValueDescriptor
        {
            Kind = DescriptorKindEnum.Lookup,
            Length = (uint)count,
            Location = address.Value,
        });

        if (chunks != 0)
        {
            var addresses = Chunks(holder.Arena, address);
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var bytes = ChunkPayloadForSlots(wanted, chunk);
                var link = ChunkOffset(chunk);
                ArenaAddress block;
                if (reuseTail && holder.Arena.CollectionTails!.Borrow(holder.Root, offset, address, (uint)link,
                    holder.Layout.LayoutVersion, bytes, out block))
                {
                    holder.SlotBytes += Arena.BlockSizeFor(bytes);
                }
                else if (!holder.TryAllocate(bytes, BlockKindEnum.Chunk, address, link, out block))
                {
                    return false;
                }

                holder.Arena.Header(block).Flags = BlockHeader.LookupSlotsFlag;
                holder.Arena.Payload(block).Clear();
                addresses[chunk] = block;
            }
        }

        entries = new ValueHolder(holder.Arena, holder.Layout, holder.Root, payload, BlockHeader.Size, address, BlockHeader.Size + payload.Length);
        return true;
    }

    private static int SlotPayload(int bytes) => Arena.BlockSizeFor(bytes) - BlockHeader.Size;

    public static int SlotsNeeded(int count) => (count * 10 + 6) / 7;

    public ref ulong Slot(int index)
    {
        if (_chunks.IsEmpty)
        {
            return ref _slots[index];
        }

        var chunk = index / SlotsPerChunk;
        var slots = MemoryMarshal.Cast<byte, ulong>(_arena.Payload(ReadChunk(_chunks, chunk)));
        return ref slots[index - chunk * SlotsPerChunk];
    }

    // One uniqueness check. Compact entries compare with at most seven earlier entries.
    // Hashed entries build the lookup and compare only when a cached hash matches.
    public static int AddEntries<TKeys, TSlots>(ref TKeys keys, ref TSlots slots, int count)
        where TKeys : ICollectionKeySource, allows ref struct
        where TSlots : ICollectionSlots, allows ref struct
    {
        var capacity = slots.Capacity;
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            if (capacity == 0)
            {
                for (var prior = 0; prior < ordinal; prior++)
                {
                    if (keys.Equal(ordinal, prior))
                    {
                        return ordinal;
                    }
                }

                continue;
            }

            var hash = keys.Hash(ordinal);
            var index = Home(hash, capacity);
            while (true)
            {
                ref var slot = ref slots.Slot(index);
                if (slot == 0)
                {
                    slot = ((ulong)hash << 32) | ((uint)ordinal + 1);
                    break;
                }

                if ((uint)(slot >> 32) == hash && keys.Equal(ordinal, (int)(uint)slot - 1))
                {
                    return ordinal;
                }

                index = Next(index, capacity);
            }
        }

        return -1;
    }

    private int Find(CollectionKeys keys, in IndexValue probe)
    {
        Span<byte> bytes = stackalloc byte[16];
        var fixedProbe = bytes[..keys.Width];
        keys.Encode(probe, fixedProbe);
        var capacity = Capacity;
        if (capacity == 0)
        {
            for (var ordinal = 0; ordinal < keys.Count; ordinal++)
            {
                if (keys.Equal(ordinal, probe, fixedProbe))
                {
                    return ordinal;
                }
            }

            return -1;
        }

        var hash = keys.Hash(probe, fixedProbe);
        var index = Home(hash, capacity);
        while (true)
        {
            var slot = Slot(index);
            if (slot == 0)
            {
                return -1;
            }

            var ordinal = (int)(uint)slot - 1;
            if ((uint)(slot >> 32) == hash && keys.Equal(ordinal, probe, fixedProbe))
            {
                return ordinal;
            }

            index = Next(index, capacity);
        }
    }

    private static int Home(uint hash, int capacity) => (int)(((ulong)hash * (uint)capacity) >> 32);

    private static int Next(int index, int capacity) => index + 1 == capacity ? 0 : index + 1;
}
