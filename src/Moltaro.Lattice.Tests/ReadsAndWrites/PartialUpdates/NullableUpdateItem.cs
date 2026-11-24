namespace Moltaro.Lattice.Tests.ReadsAndWrites.PartialUpdates;

internal sealed class NullableUpdateItem
{
    public string Id { get; set; } = "";

    public int? Rank { get; set; }

    public List<int>? Scores { get; set; }
}
