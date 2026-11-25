namespace Moltaro.Lattice.Access.Views;

// The original descriptor of a collection, relative to the block that holds it.
// Both the holder and the records found through it live in the caller's read epoch.
internal readonly ref struct ViewCollection(object entryLayout, ReadOnlySpan<byte> holder, int descriptorOffset, int count)
{
    public object EntryLayout { get; } = entryLayout;

    public ReadOnlySpan<byte> Holder { get; } = holder;

    public int DescriptorOffset { get; } = descriptorOffset;

    public int Count { get; } = count;

    public bool MoveNext(ref int ordinal)
    {
        if (ordinal < Count)
        {
            ordinal++;
        }

        return ordinal < Count;
    }

    public void RequireCurrent(int ordinal)
    {
        if ((uint)ordinal >= (uint)Count)
        {
            throw new InvalidOperationException("The enumerator has no current entry.");
        }
    }
}
