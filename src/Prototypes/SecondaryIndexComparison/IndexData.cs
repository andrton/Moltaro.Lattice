namespace Moltaro.Lattice.Prototypes.SecondaryIndexComparison;

internal sealed class IndexData
{
    public const int Seed = 20261113;

    public IndexData(int count, DistributionEnum distribution)
    {
        GroupCount = distribution == DistributionEnum.Unique ? count : 100;
        OriginalKeys = new SearchKey[count];
        CurrentKeys = new SearchKey[count];
        Order = Enumerable.Range(0, count).ToArray();
        for (var index = 0; index < count; index++)
        {
            OriginalKeys[index] = SearchKey.FromRank(index % GroupCount);
            CurrentKeys[index] = OriginalKeys[index];
        }

        new Random(Seed).Shuffle(Order);
        PointQueries = Enumerable.Range(0, 4096)
            .Select(index => SearchKey.FromRank(Order[index % count] % GroupCount)).ToArray();
        Ranges = Enumerable.Range(0, 64).Select(index =>
        {
            var firstRank = Order[index % count] % (GroupCount - 8);
            var sum = Enumerable.Range(0, count)
                .Where(id => id % GroupCount >= firstRank && id % GroupCount < firstRank + 8)
                .Sum(id => (long)id + 1);
            return (Lower: SearchKey.FromRank(firstRank), Upper: SearchKey.FromRank(firstRank + 7), ExpectedSum: (ulong)sum);
        }).ToArray();
    }

    public int Count => OriginalKeys.Length;
    public int GroupCount { get; }
    public SearchKey[] OriginalKeys { get; }
    public SearchKey[] CurrentKeys { get; }
    public int[] Order { get; }
    public SearchKey[] PointQueries { get; }
    public (SearchKey Lower, SearchKey Upper, ulong ExpectedSum)[] Ranges { get; }
}
