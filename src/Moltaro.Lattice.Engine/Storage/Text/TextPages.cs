using System.Numerics;
using Moltaro.Lattice.Storage.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Page zero can be held directly. A larger index adds only the radix
// height it needs, published as part of the branch reference. Writers
// change branches under Writes. Readers acquire each published child.
// The array of each branch is charged to the budget before the branches a
// change adds are made, and released when a branch is dropped
// (docs/design/storage-and-maintenance.md, the memory budget); the branch
// object itself is bookkeeping of the headroom.
internal sealed class TextPages<T>(MemoryBudget budget) where T : class
{
    private const int Stride = 32;
    private const int Top = 20;
    private static readonly long BranchBytes = MemoryBudget.ArrayBytes<object?>(Stride);
    private readonly MemoryBudget _budget = budget;
    private object? _root;
    private int _branches;
    private bool _directFull;

    // The fields above: two references, the count of the branches and whether page zero is full.
    private static readonly int FieldBytes = 2 * IntPtr.Size + sizeof(int) + sizeof(bool);

    public long Bytes => TextAllocationShape.ObjectBytes(FieldBytes) + (TextAllocationShape.ObjectBytes(Branch.FieldBytes) + BranchBytes) * _branches;

    // What a read walking the pages can hold of a branch a change dropped: its object and its array.
    public static long DroppedBranchBytes => TextAllocationShape.ObjectBytes(Branch.FieldBytes) + BranchBytes;

    public T? Get(uint index)
    {
        var root = Volatile.Read(ref _root);
        if (root is not Branch branch)
        {
            return index == 0 ? (T?)root : null;
        }

        if (index >> (branch.Shift + 5) != 0)
        {
            return null;
        }

        for (var shift = (int)branch.Shift; shift > 0; shift -= 5)
        {
            if (Volatile.Read(ref branch.Children[(index >> shift) & 31]) is not Branch child)
            {
                return null;
            }

            branch = child;
        }

        return (T?)Volatile.Read(ref branch.Children[index & 31]);
    }

    // Charges the branches the change adds before it makes any, and releases those it drops. A
    // refusal by the budget changes nothing.
    public void Set(uint index, T? page, bool full = false)
    {
        var original = _root;
        if (original is not Branch && index == 0)
        {
            _directFull = full;
            Volatile.Write(ref _root, page);
            return;
        }

        var height = BitOperations.Log2(index) / 5 * 5;
        if (page is null && (original is not Branch top || height > top.Shift))
        {
            return;
        }

        var before = _branches;
        var added = page is null ? 0 : BranchesAdded(original, index, height);
        if (added != 0)
        {
            _budget.Reserve(added * BranchBytes);
        }

        try
        {
            SetBranches(original, index, height, page, full);
        }
        catch
        {
            _budget.Release(added * BranchBytes);
            _branches = before;
            throw;
        }

        // Released at once, though a read walking the pages may still hold a dropped branch: a read holds
        // at most one dropped branch and one replaced leaf at a time, as locals of its lookup, and the
        // headroom holds one of each for every slot of the protection of reads (TextLocator.HeldByOneRead).
        var dropped = before + added - _branches;
        if (dropped != 0)
        {
            _budget.Release(dropped * BranchBytes);
        }
    }

    // The branches a page at the index adds: the wraps that raise the root to the height of the
    // index, and the branches below the first absent child on its path.
    private static int BranchesAdded(object? original, uint index, int height)
    {
        if (original is not Branch branch)
        {
            // An empty root adds a path from the height down; a page held directly is wrapped up to
            // the height first, and the index, beyond page zero, leaves that wrapped path below the top.
            return original is null ? height / 5 + 1 : 2 * (height / 5) + 1;
        }

        if (branch.Shift < height)
        {
            return (height - branch.Shift) / 5 + height / 5;
        }

        for (var shift = (int)branch.Shift; shift > 0; shift -= 5)
        {
            if (branch.Children[(index >> shift) & 31] is not Branch child)
            {
                return shift / 5;
            }

            branch = child;
        }

        return 0;
    }

    public void ReportHeld(List<HeldArray> held)
    {
        for (var branch = 0; branch < _branches; branch++)
        {
            held.Add(new HeldArray(nameof(TextTable), typeof(object), Stride));
        }
    }

    // Releases every branch, when the whole structure is let go of.
    public void ReleaseAll()
    {
        _budget.Release(_branches * BranchBytes);
        _branches = 0;
        _root = null;
        _directFull = false;
    }

    private void SetBranches(object? original, uint index, int height, T? page, bool full)
    {
        var root = original as Branch;
        var wraps = 0;
        if (root is null && original is T direct)
        {
            root = new Branch(0) { Count = 1, Full = _directFull ? 1u : 0 };
            root.Children[0] = direct;
            wraps++;
        }

        while (root is not null && root.Shift < height)
        {
            var outer = new Branch(root.Shift + 5) { Count = 1, Full = root.Full == uint.MaxValue ? 1u : 0 };
            outer.Children[0] = root;
            root = outer;
            wraps++;
        }

        root = Set(root, root?.Shift ?? height, index, page, full);
        _branches += wraps;

        while (root is { Count: 1 } && root.Children[0] is { } first)
        {
            _branches--;
            if (root.Shift == 0)
            {
                _directFull = (root.Full & 1) != 0;
                Volatile.Write(ref _root, first);
                return;
            }

            root = (Branch)first;
        }

        if (root is null)
        {
            _directFull = false;
        }

        Volatile.Write(ref _root, root);
    }

    public uint FirstNonFull()
    {
        if (_root is not Branch first)
        {
            return _directFull ? 1u : 0;
        }

        Branch? branch = first;

        if (branch.Full == uint.MaxValue)
        {
            return 1u << (branch.Shift + 5);
        }

        uint index = 0;
        for (var shift = (int)branch.Shift; shift >= 0 && branch is not null; shift -= 5)
        {
            var slot = BitOperations.TrailingZeroCount(~branch.Full);
            index |= (uint)slot << shift;
            if (shift != 0)
            {
                branch = (Branch?)branch.Children[slot];
            }
        }

        return index;
    }

    public bool IsFull => _root is Branch { Shift: Top, Full: uint.MaxValue };

    public bool TryNext(uint index, out uint number, out T page)
    {
        if (_root is Branch root)
        {
            return Next(root, root.Shift, 0, index, out number, out page);
        }

        number = 0;
        page = (index == 0 ? (T?)_root : null)!;
        return page is not null;
    }

    public void Measure(List<TextAllocationShape> groups)
    {
        groups.Add(new TextAllocationShape(1, FieldBytes, 0, 0));
        if (_branches != 0)
        {
            groups.Add(new TextAllocationShape(_branches, Branch.FieldBytes, 0, 0));
            groups.Add(new TextAllocationShape(_branches, 0, IntPtr.Size, Stride));
        }
    }

    private Branch? Set(Branch? branch, int shift, uint index, T? page, bool full)
    {
        if (branch is null && page is null)
        {
            return null;
        }

        var created = branch is null;
        branch ??= new Branch(shift);
        var slot = (int)((index >> shift) & 31);
        var previous = branch.Children[slot];
        object? child;
        if (shift == 0)
        {
            child = page;
        }
        else
        {
            var nested = Set((Branch?)previous, shift - 5, index, page, full);
            full = nested?.Full == uint.MaxValue;
            child = nested;
        }

        if ((previous is null) != (child is null))
        {
            branch.Count = (byte)(branch.Count + (child is null ? -1 : 1));
        }

        Volatile.Write(ref branch.Children[slot], child);
        var mask = 1u << slot;
        branch.Full = full ? branch.Full | mask : branch.Full & ~mask;
        if (branch.Count == 0)
        {
            _branches--;
            return null;
        }

        if (created)
        {
            _branches++;
        }

        return branch;
    }

    private static bool Next(Branch? branch, int shift, uint prefix, uint index, out uint number, out T page)
    {
        if (branch is not null)
        {
            var first = index > prefix ? (index - prefix) >> shift : 0;
            for (var slot = first; slot < Stride; slot++)
            {
                var start = prefix | (slot << shift);
                if (shift == 0)
                {
                    if (branch.Children[slot] is T found)
                    {
                        number = start;
                        page = found;
                        return true;
                    }
                }
                else if (Next((Branch?)branch.Children[slot], shift - 5, start, index, out number, out page))
                {
                    return true;
                }
            }
        }

        number = 0;
        page = null!;
        return false;
    }

    private sealed class Branch(int shift)
    {
        public readonly object?[] Children = new object?[Stride];
        public uint Full;
        public readonly byte Shift = (byte)shift;
        public byte Count;

        // The fields above: the reference to the children, the mask of the full ones, the shift and the count.
        public static readonly int FieldBytes = IntPtr.Size + sizeof(uint) + 2 * sizeof(byte);
    }
}
