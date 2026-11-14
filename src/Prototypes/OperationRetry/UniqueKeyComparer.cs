namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal sealed class UniqueKeyComparer(StringComparer textComparer) : IComparer<UniqueKey>
{
    public int Compare(UniqueKey x, UniqueKey y)
    {
        var first = ComparePart(x.First, y.First);
        return first != 0 ? first : ComparePart(x.Second, y.Second);
    }

    private int ComparePart(string? left, string? right)
    {
        if (left is null)
        {
            return right is null ? 0 : 1;
        }

        return right is null ? -1 : textComparer.Compare(left, right);
    }
}
