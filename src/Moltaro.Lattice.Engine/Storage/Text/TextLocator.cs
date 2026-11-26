using System.Diagnostics;
using System.Numerics;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// An ID selects an immutable leaf mapping, then one acquired address.
// Native epochs protect the address. The managed local keeps that particular
// leaf alive through the load. No retired leaf list holds pages for other readers.
internal sealed class TextLocator(MemoryBudget? budget)
{
    public const uint MostId = int.MaxValue;
    private readonly TextPages<Leaf> _pages = TextPages<Leaf>.Create(budget);
    private long _leafBytes;
    private uint _trimFrom;
    private bool _trimming;

    private static long OwnerBytes => MemoryBudget.ObjectBytes(2L * IntPtr.Size + sizeof(long) + sizeof(uint) + 1);

    public static TextLocator Create(MemoryBudget? budget)
    {
        budget?.Reserve(OwnerBytes);
        try
        {
            var locator = new TextLocator(budget);
            budget?.Own(locator, nameof(TextLocator));
            return locator;
        }
        catch
        {
            budget?.Release(OwnerBytes);
            throw;
        }
    }

    public long Bytes => OwnerBytes + _pages.Bytes + _leafBytes;

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
            Replace(page, leaf, expanded, keepPrevious: budget is { IsPreparing: true });
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
        leaf = RestoreUnused(page, leaf);
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
        directory.Add(new TextAllocationShape(1, (int)(OwnerBytes - 2 * IntPtr.Size), 0, 0));
        _pages.Measure(directory);

        for (var cursor = 0u; _pages.TryNext(cursor, out var page, out var leaf); cursor = page + 1)
        {
            for (var held = leaf; held is not null; held = held.Earlier)
            {
                leaves.Add(new TextAllocationShape(1, Leaf.MetadataFields, 0, 0));
                leaves.Add(new TextAllocationShape(1, 0, 8, held.Capacity));
                leaves.Add(new TextAllocationShape(1, 0, 4, held.Capacity));
            }
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
            new TextLocatorAllocation(leaf, new TextAllocationShape(1, Leaf.MetadataFields, 0, 0)),
            new TextLocatorAllocation(leaf.Addresses, new TextAllocationShape(1, 0, 8, leaf.Capacity)),
            new TextLocatorAllocation(leaf.Ordinals, new TextAllocationShape(1, 0, 4, leaf.Capacity)),
        ];
        return true;
    }

    private void Replace(uint page, Leaf? old, Leaf? current, bool keepPrevious = false)
    {
        try
        {
            SetLeaf(page, current);
        }
        catch
        {
            Release(current);
            throw;
        }

        _leafBytes += current?.Bytes ?? 0;
        if (keepPrevious && old is not null && current is not null)
        {
            current.Earlier = old;
        }
        else
        {
            ReleaseEarlier(old);
        }
    }

    public void Keep(uint id)
    {
        var bit = Bit(id);
        var leaf = _pages.Get(id >> 6)!;
        ref var link = ref leaf.Earlier;
        while (link is { } earlier)
        {
            if ((earlier.Allocated & bit) != 0)
            {
                link = ref earlier.Earlier;
                continue;
            }

            link = earlier.Earlier;
            earlier.Earlier = null;
            _leafBytes -= earlier.Bytes;
            Release(earlier);
        }
    }

    private Leaf RestoreUnused(uint page, Leaf current)
    {
        var restore = current;
        for (var earlier = current.Earlier; earlier is not null; earlier = earlier.Earlier)
        {
            if ((current.Taken & ~earlier.Allocated) == 0)
            {
                restore = earlier;
            }
        }

        if (ReferenceEquals(restore, current))
        {
            return current;
        }

        var removed = restore.Taken & ~current.Taken;
        while (removed != 0)
        {
            var slot = restore.IndexOf((uint)BitOperations.TrailingZeroCount(removed));
            Volatile.Write(ref restore.Addresses[slot], 0);
            restore.Ordinals[slot] = -1;
            removed &= removed - 1;
        }

        restore.Taken = current.Taken;
        CopyEntries(current, restore, restore.Taken);
        SetLeaf(page, restore);
        ReleaseEarlier(current, restore);
        return restore;
    }

    private void ReleaseEarlier(Leaf? first, Leaf? stop = null)
    {
        while (first is not null && !ReferenceEquals(first, stop))
        {
            var released = first;
            first = released.Earlier;
            released.Earlier = null;
            _leafBytes -= released.Bytes;
            Release(released);
        }
    }

    private void SetLeaf(uint page, Leaf? leaf)
    {
        var reserved = page == 0 ? 1UL : 0;
        _pages.Set(page, leaf, leaf is not null && (leaf.Taken | reserved) == ulong.MaxValue);
    }

    private void Release(Leaf? leaf)
    {
        if (leaf is not null && budget is not null)
        {
            budget.Forget(leaf.Addresses);
            budget.Forget(leaf.Ordinals);
            budget.Release(leaf.Bytes, leaf);
        }
    }

    private Leaf Copy(Leaf? old, ulong taken)
    {
        var capacity = CapacityFor(taken);
        var allocated = taken;
        while (BitOperations.PopCount(allocated) < capacity)
        {
            allocated |= 1UL << BitOperations.TrailingZeroCount(~allocated);
        }

        var bytes = Leaf.BytesFor(capacity);
        budget?.Reserve(bytes);
        Leaf? expanded = null;
        try
        {
            expanded = new Leaf(allocated, taken, capacity);
            if (budget is not null)
            {
                budget.Own(expanded, nameof(TextLocator));
                budget.Own(expanded.Addresses, nameof(TextLocator));
                budget.Own(expanded.Ordinals, nameof(TextLocator));
            }
        }
        catch
        {
            if (expanded is not null)
            {
                Release(expanded);
            }
            else
            {
                budget?.Release(bytes);
            }

            throw;
        }

        if (old is not null)
        {
            CopyEntries(old, expanded, taken & old.Taken);
        }

        return expanded;
    }

    private static void CopyEntries(Leaf source, Leaf destination, ulong ids)
    {
        while (ids != 0)
        {
            var low = (uint)BitOperations.TrailingZeroCount(ids);
            var from = source.IndexOf(low);
            var to = destination.IndexOf(low);
            destination.Addresses[to] = source.Addresses[from];
            destination.Ordinals[to] = source.Ordinals[from];
            ids &= ids - 1;
        }
    }

    private static int CapacityFor(ulong taken) => (int)BitOperations.RoundUpToPowerOf2((uint)BitOperations.PopCount(taken));

    private static ulong Bit(uint id) => 1UL << (int)(id & 63);

    private sealed class Leaf(ulong allocated, ulong taken, int capacity)
    {
        public readonly long[] Addresses = new long[capacity];
        public readonly int[] Ordinals = new int[capacity];
        public readonly ulong Allocated = allocated;
        public ulong Taken = taken;
        public Leaf? Earlier;

        public int Capacity => Addresses.Length;

        public long Bytes => BytesFor(Capacity);

        public static int MetadataFields => 3 * IntPtr.Size + 2 * sizeof(ulong);
        public static long BytesFor(int capacity) => MemoryBudget.ObjectBytes(MetadataFields) +
            MemoryBudget.ArrayBytes<long>(capacity) + MemoryBudget.ArrayBytes<int>(capacity);

        public int IndexOf(uint id)
        {
            var bit = Bit(id);
            Debug.Assert((Allocated & bit) != 0);
            return BitOperations.PopCount(Allocated & (bit - 1));
        }
    }
}
