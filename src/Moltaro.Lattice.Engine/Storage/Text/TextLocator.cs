using System.Diagnostics;
using System.Numerics;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// An ID selects an immutable leaf mapping, then one acquired address.
// Native epochs protect the address. The managed local keeps that particular
// leaf alive through the load. No retired leaf list holds pages for other readers.
// The two arrays of a leaf are charged to the budget before the leaf is made,
// and released when the leaf is replaced or dropped: a reader that still holds
// the old leaf keeps it through the collector, not through the engine, and the
// headroom holds one such leaf for every slot of reads (HeldByOneRead). A leaf
// emptied by a preparation that was refused or cancelled stays, for the next
// write, until bounded maintenance trims it (docs/design/storage-and-
// maintenance.md, the memory budget).
internal sealed class TextLocator(MemoryBudget budget)
{
    public const uint MostId = int.MaxValue;
    private readonly MemoryBudget _budget = budget;
    private readonly TextPages<Leaf> _pages = new(budget);
    private long _leafBytes;
    private uint _trimFrom;
    private bool _trimming;

    // The fields above: two references, the bytes of the leaves, where trimming goes on and whether it does.
    private static readonly int FieldBytes = 2 * IntPtr.Size + sizeof(long) + sizeof(uint) + sizeof(bool);

    public long Bytes => TextAllocationShape.ObjectBytes(FieldBytes) + _pages.Bytes + _leafBytes;

    // A leaf maps the IDs of one page.
    private const int LeafIds = 64;

    // What one read can hold of the locator after the charge of it was released: the largest leaf, its
    // object and its arrays for 64 IDs, that a change replaced, and the branch of the pages it read that
    // leaf through, which a change dropped (ReplaceCharged, TextPages.Set). 864 and 312 bytes, 1,176
    // together; the headroom holds it once for each slot of the protection of reads (Engine.Headroom).
    public static long HeldByOneRead => TextAllocationShape.ObjectBytes(Leaf.FieldBytes) + LeafArrayBytes(LeafIds) + TextPages<Leaf>.DroppedBranchBytes;

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
            ReplaceCharged(page, leaf, expanded);
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

    // Gives the ID back. A leaf left with no ID is dropped, unless the return unwinds a preparation
    // that was refused or cancelled: then the leaf stays, and maintenance trims it later.
    public void Return(uint id, bool keepsLeaf = false)
    {
        var page = id >> 6;
        var leaf = _pages.Get(page)!;
        var slot = leaf.IndexOf(id);
        leaf.Taken &= ~Bit(id);
        leaf.Ordinals[slot] = -1;
        Volatile.Write(ref leaf.Addresses[slot], 0);
        if (leaf.Taken == 0 && !keepsLeaf)
        {
            ReplaceCharged(page, leaf, null);
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

        if (old.Taken == 0)
        {
            ReplaceCharged(page, old, null);
        }
        else if (old.Capacity > CapacityFor(old.Taken))
        {
            ReplaceCharged(page, old, Copy(old, old.Taken));
        }

        _trimFrom = page + 1;
        return true;
    }

    public void Measure(List<TextAllocationShape> directory, List<TextAllocationShape> leaves)
    {
        directory.Add(new TextAllocationShape(1, FieldBytes, 0, 0));
        _pages.Measure(directory);

        for (var cursor = 0u; _pages.TryNext(cursor, out var page, out var leaf); cursor = page + 1)
        {
            leaves.Add(new TextAllocationShape(1, Leaf.FieldBytes, 0, 0));
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
            new TextLocatorAllocation(leaf, new TextAllocationShape(1, Leaf.FieldBytes, 0, 0)),
            new TextLocatorAllocation(leaf.Addresses, new TextAllocationShape(1, 0, 8, leaf.Capacity)),
            new TextLocatorAllocation(leaf.Ordinals, new TextAllocationShape(1, 0, 4, leaf.Capacity)),
        ];
        return true;
    }

    // Puts the new leaf, charged by Copy, in place of the old one and releases the old one's arrays.
    // A refusal of the branches the change needs releases the new leaf and changes nothing.
    private void ReplaceCharged(uint page, Leaf? old, Leaf? current)
    {
        var reserved = page == 0 ? 1UL : 0;
        try
        {
            _pages.Set(page, current, current is not null && (current.Taken | reserved) == ulong.MaxValue);
        }
        catch
        {
            if (current is not null)
            {
                _budget.Release(LeafArrayBytes(current.Capacity));
            }

            throw;
        }

        _leafBytes += (current?.Bytes ?? 0) - (old?.Bytes ?? 0);

        // Released at once, though a read may still hold the old leaf, unlike a replaced directory
        // (MemoryBudget.ReleaseAfterReads): a leaf is copied whenever its IDs outgrow it or leave it half
        // empty, so a retirement for each would add work to the writes that take or free texts. A read
        // holds at most one replaced leaf and one dropped branch at a time, as locals of its lookup, and
        // the headroom holds one of each for every slot of the protection of reads (HeldByOneRead,
        // docs/design/storage-and-maintenance.md, the memory budget).
        if (old is not null)
        {
            _budget.Release(LeafArrayBytes(old.Capacity));
        }
    }

    public void ReportHeld(List<HeldArray> held)
    {
        for (var cursor = 0u; _pages.TryNext(cursor, out var page, out var leaf); cursor = page + 1)
        {
            held.Add(new HeldArray(nameof(TextTable), typeof(long), leaf.Capacity));
            held.Add(new HeldArray(nameof(TextTable), typeof(int), leaf.Capacity));
        }

        _pages.ReportHeld(held);
    }

    private static long LeafArrayBytes(int capacity) => MemoryBudget.ArrayBytes<long>(capacity) + MemoryBudget.ArrayBytes<int>(capacity);

    // A leaf for the taken IDs, its arrays charged before it is made.
    private Leaf Copy(Leaf? old, ulong taken)
    {
        var capacity = CapacityFor(taken);
        var allocated = taken;
        while (BitOperations.PopCount(allocated) < capacity)
        {
            allocated |= 1UL << BitOperations.TrailingZeroCount(~allocated);
        }

        var bytes = LeafArrayBytes(capacity);
        _budget.Reserve(bytes);
        Leaf expanded;
        try
        {
            expanded = new Leaf(allocated, taken, capacity);
        }
        catch
        {
            _budget.Release(bytes);
            throw;
        }
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

        // The fields above: two references and two masks.
        public static readonly int FieldBytes = 2 * IntPtr.Size + 2 * sizeof(ulong);

        public int Capacity => Addresses.Length;

        public long Bytes => TextAllocationShape.ObjectBytes(FieldBytes) + LeafArrayBytes(Capacity);

        public int IndexOf(uint id)
        {
            var bit = Bit(id);
            Debug.Assert((Allocated & bit) != 0);
            return BitOperations.PopCount(Allocated & (bit - 1));
        }
    }
}
