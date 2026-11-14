namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct CompositeIndexKey(NativeTextKey Text, long Number) : IComparable<CompositeIndexKey>
{
    public int CompareTo(CompositeIndexKey other)
    {
        var textOrder = Text.CompareTo(other.Text);
        return textOrder != 0 ? textOrder : Number.CompareTo(other.Number);
    }
}
