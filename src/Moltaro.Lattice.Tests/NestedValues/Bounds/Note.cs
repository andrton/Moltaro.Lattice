namespace Moltaro.Lattice.Tests.NestedValues.Bounds;

// The class of the entity type Note of DepthBoundTests.
internal sealed class Note
{
    public string Code { get; set; } = "";

    public Attachment Body { get; set; } = new();

    public List<string> Captions { get; set; } = [];

    public List<byte[]> Blobs { get; set; } = [];
}
