namespace Moltaro.Lattice.TestSupport.PartialUpdates;

internal sealed class UpdateItem
{
    public string Id { get; set; } = "";

    public string Login { get; set; } = "";

    public UpdateHome? Home { get; set; }

    public List<int>? Scores { get; set; }

    public Dictionary<string, UpdateHome?>? Options { get; set; }

    public HashSet<string>? Tags { get; set; }

    public byte[] Data { get; set; } = [];

    public string Label { get; set; } = "";
}
