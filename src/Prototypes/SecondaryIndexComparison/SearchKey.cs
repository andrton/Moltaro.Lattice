namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal readonly record struct SearchKey(long Group, long Score) : IComparable<SearchKey>
{
    public int CompareTo(SearchKey other)
    {
        var groupOrder = Group.CompareTo(other.Group);
        return groupOrder != 0 ? groupOrder : Score.CompareTo(other.Score);
    }

    public static SearchKey FromRank(int rank) => new(rank / 100, rank % 100);
}
