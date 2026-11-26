using System.Numerics;
using Moltaro.Lattice.Budgeting;

namespace Moltaro.Lattice.Storage.Text;

// Page zero can be held directly. A larger index adds only the radix
// height it needs, published as part of the branch reference. Writers
// change branches under Writes. Readers acquire each published child.
internal sealed class TextPages<T> where T : class
{
    private const int Stride = 32;
    private const int Top = 20;
    private readonly MemoryBudget? _budget;
    private object? _root;
    private int _branches;
    private bool _directFull;

    private static long OwnerBytes => MemoryBudget.ObjectBytes(2L * IntPtr.Size + sizeof(int) + 1);
    private static long BranchBytes => MemoryBudget.ObjectBytes(IntPtr.Size + sizeof(uint) + 2) +
        MemoryBudget.ArrayBytes<object?>(Stride);

    private TextPages(MemoryBudget? budget) => _budget = budget;

    public static TextPages<T> Create(MemoryBudget? budget)
    {
        budget?.Reserve(OwnerBytes);
        try
        {
            var pages = new TextPages<T>(budget);
            budget?.Own(pages, nameof(TextPages<T>));
            return pages;
        }
        catch
        {
            budget?.Release(OwnerBytes);
            throw;
        }
    }

    public long Bytes => OwnerBytes + (BranchBytes * _branches);

    public void ReleaseBacking() => _budget?.Release(OwnerBytes, this);

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

    public void Set(uint index, T? page, bool full = false)
    {
        var original = _root;
        if (original is not Branch && index == 0)
        {
            _directFull = full;
            Volatile.Write(ref _root, page);
            return;
        }

        var root = original as Branch;
        var wraps = 0;
        var height = BitOperations.Log2(index) / 5 * 5;
        if (page is null && (root is null || height > root.Shift))
        {
            return;
        }

        try
        {
            if (root is null && original is T direct)
            {
                root = NewBranch(0);
                root.Count = 1;
                root.Full = _directFull ? 1u : 0;
                root.Children[0] = direct;
                wraps++;
            }

            while (root is not null && root.Shift < height)
            {
                var outer = NewBranch(root.Shift + 5);
                outer.Count = 1;
                outer.Full = root.Full == uint.MaxValue ? 1u : 0;
                outer.Children[0] = root;
                root = outer;
                wraps++;
            }

            root = Set(root, root?.Shift ?? height, index, page, full);
        }
        catch
        {
            while (wraps-- > 0)
            {
                var previous = root!;
                root = previous.Children[0] as Branch;
                ReleaseBranch(previous);
            }

            throw;
        }

        _branches += wraps;

        while (root is { Count: 1 } && root.Children[0] is { } first)
        {
            _branches--;
            ReleaseBranch(root);
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
        groups.Add(new TextAllocationShape(1, (int)(OwnerBytes - 2 * IntPtr.Size), 0, 0));
        if (_branches != 0)
        {
            groups.Add(new TextAllocationShape(_branches, 16, 0, 0));
            groups.Add(new TextAllocationShape(_branches, 0, 8, Stride));
        }
    }

    private Branch? Set(Branch? branch, int shift, uint index, T? page, bool full)
    {
        if (branch is null && page is null)
        {
            return null;
        }

        var created = branch is null;
        branch ??= NewBranch(shift);
        var slot = (int)((index >> shift) & 31);
        var previous = branch.Children[slot];
        object? child;
        if (shift == 0)
        {
            child = page;
        }
        else
        {
            try
            {
                var nested = Set((Branch?)previous, shift - 5, index, page, full);
                full = nested?.Full == uint.MaxValue;
                child = nested;
            }
            catch
            {
                if (created)
                {
                    ReleaseBranch(branch);
                }

                throw;
            }
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
            ReleaseBranch(branch);
            return null;
        }

        if (created)
        {
            _branches++;
        }

        return branch;
    }

    private Branch NewBranch(int shift)
    {
        var budget = _budget;
        budget?.Reserve(BranchBytes);
        try
        {
            var branch = new Branch(shift);
            if (budget is not null)
            {
                budget.Own(branch, nameof(TextPages<T>));
                budget.Own(branch.Children, nameof(TextPages<T>));
            }

            return branch;
        }
        catch
        {
            budget?.Release(BranchBytes);
            throw;
        }
    }

    private void ReleaseBranch(Branch branch)
    {
        if (_budget is { } budget)
        {
            budget.Forget(branch.Children);
            budget.Release(BranchBytes, branch);
        }
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
    }
}
