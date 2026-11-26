using System.Diagnostics;
using System.Numerics;

namespace Moltaro.Lattice.Storage.Text;

// An ID selects an immutable leaf mapping, then one acquired address.
// Native epochs protect the address. The managed local keeps that particular
// leaf alive through the load. No retired leaf list holds pages for other readers.
internal sealed class TextLocator
{
    public const uint MostId = int.MaxValue;
    private readonly TextPages<Leaf> _pages = new();
    private long _leafBytes;
    private uint _trimFrom;
    private bool _trimming;

    public long Bytes => 40 + _pages.Bytes + _leafBytes;

    public ArenaAddress Address(uint id)
    {
        var leaf = _pages.Get(id >> 6)!;
        var packed = (ulong)Volatile.Read(ref leaf.Addresses[leaf.IndexOf(id)]);
        return new ArenaAddress((uint)(packed >> 32), (uint)packed);
    }

    public int Ordinal(uint id)
    {
        if (id is 0 or > MostId)
        {
            return -1;
        }

        var leaf = _pages.Get(id >> 6);
        var bit = Bit(id);
        return leaf is not null && (leaf.Taken & bit) != 0 ? leaf.Ordinals[leaf.IndexOf(id)] : -1;
    }

    public bool TryTake(out uint id)
    {
        id = 0;
        if (_pages.IsFull)
        {
            return false;
        }

        var page = _pages.FirstNonFull();
        var reserved = page == 0 ? 1UL : 0;
        var leaf = _pages.Get(page);
        var taken = leaf?.Taken ?? 0;
        var low = BitOperations.TrailingZeroCount(~(taken | reserved));
        if (!TryIdAt(page, low, out id))
        {
            return false;
        }

        var bit = 1UL << low;
        var next = taken | bit;

        if (leaf is null || (leaf.Allocated & bit) == 0)
        {
            var expanded = Copy(leaf, next);
            Replace(page, leaf, expanded);
            leaf = expanded;
        }
        else
        {
            leaf.Taken = next;
            _pages.Set(page, leaf, (next | reserved) == ulong.MaxValue);
        }

        leaf.Ordinals[leaf.IndexOf(id)] = -1;
        return true;
    }

    public static bool TryIdAt(uint page, int cell, out uint id)
    {
        if (page > (MostId >> 6) || (uint)cell >= 64)
        {
            id = 0;
            return false;
        }

        id = (page << 6) | (uint)cell;
        return id != 0;
    }

    public void SetOrdinal(uint id, int ordinal)
    {
        var leaf = _pages.Get(id >> 6)!;
        leaf.Ordinals[leaf.IndexOf(id)] = ordinal;
    }

    public void SetAddress(uint id, ArenaAddress address)
    {
        var leaf = _pages.Get(id >> 6)!;
        Volatile.Write(ref leaf.Addresses[leaf.IndexOf(id)], (long)address.Value);
    }

    public void Return(uint id)
    {
        var page = id >> 6;
        var leaf = _pages.Get(page)!;
        var slot = leaf.IndexOf(id);
        leaf.Taken &= ~Bit(id);
        leaf.Ordinals[slot] = -1;
        Volatile.Write(ref leaf.Addresses[slot], 0);
        if (leaf.Taken == 0)
        {
            Replace(page, leaf, null);
            return;
        }

        _pages.Set(page, leaf);
        if (leaf.Capacity > CapacityFor(leaf.Taken))
        {
            _trimFrom = _trimming ? Math.Min(_trimFrom, page) : page;
            _trimming = true;
        }
    }

    public bool TrimOne()
    {
        if (!_trimming)
        {
            return false;
        }

        if (!_pages.TryNext(_trimFrom, out var page, out var old))
        {
            _trimming = false;
            return false;
        }

        if (old.Capacity > CapacityFor(old.Taken))
        {
            Replace(page, old, Copy(old, old.Taken));
        }

        _trimFrom = page + 1;
        return true;
    }

    public void Measure(List<TextAllocationShape> directory, List<TextAllocationShape> leaves)
    {
        directory.Add(new TextAllocationShape(1, 24, 0, 0));
        _pages.Measure(directory);

        for (var cursor = 0u; _pages.TryNext(cursor, out var page, out var leaf); cursor = page + 1)
        {
            leaves.Add(new TextAllocationShape(1, 32, 0, 0));
            leaves.Add(new TextAllocationShape(1, 0, 8, leaf.Capacity));
            leaves.Add(new TextAllocationShape(1, 0, 4, leaf.Capacity));
        }
    }

    public bool TryCapture(uint id, out IReadOnlyList<TextLocatorAllocation> objects)
    {
        var leaf = _pages.Get(id >> 6);
        if (leaf is null || (leaf.Taken & Bit(id)) == 0)
        {
            objects = [];
            return false;
        }

        objects = [
            new TextLocatorAllocation(leaf, new TextAllocationShape(1, 32, 0, 0)),
            new TextLocatorAllocation(leaf.Addresses, new TextAllocationShape(1, 0, 8, leaf.Capacity)),
            new TextLocatorAllocation(leaf.Ordinals, new TextAllocationShape(1, 0, 4, leaf.Capacity)),
        ];
        return true;
    }

    private void Replace(uint page, Leaf? old, Leaf? current)
    {
        var reserved = page == 0 ? 1UL : 0;
        _pages.Set(page, current, current is not null && (current.Taken | reserved) == ulong.MaxValue);
        _leafBytes += (current?.Bytes ?? 0) - (old?.Bytes ?? 0);
    }

    private static Leaf Copy(Leaf? old, ulong taken)
    {
        var capacity = CapacityFor(taken);
        var allocated = taken;
        while (BitOperations.PopCount(allocated) < capacity)
        {
            allocated |= 1UL << BitOperations.TrailingZeroCount(~allocated);
        }

        var expanded = new Leaf(allocated, taken, capacity);
        var carried = taken & (old?.Taken ?? 0);
        while (carried != 0)
        {
            var low = (uint)BitOperations.TrailingZeroCount(carried);
            var previous = old!.IndexOf(low);
            var next = expanded.IndexOf(low);
            expanded.Addresses[next] = old.Addresses[previous];
            expanded.Ordinals[next] = old.Ordinals[previous];
            carried &= carried - 1;
        }

        return expanded;
    }

    private static int CapacityFor(ulong taken) => (int)BitOperations.RoundUpToPowerOf2((uint)BitOperations.PopCount(taken));

    private static ulong Bit(uint id) => 1UL << (int)(id & 63);

    private sealed class Leaf(ulong allocated, ulong taken, int capacity)
    {
        public readonly long[] Addresses = new long[capacity];
        public readonly int[] Ordinals = new int[capacity];
        public readonly ulong Allocated = allocated;
        public ulong Taken = taken;

        public int Capacity => Addresses.Length;

        public long Bytes => 96 + (8L * Capacity) + ((4L * Capacity + 7) & ~7L);

        public int IndexOf(uint id)
        {
            var bit = Bit(id);
            Debug.Assert((Allocated & bit) != 0);
            return BitOperations.PopCount(Allocated & (bit - 1));
        }
    }
}
