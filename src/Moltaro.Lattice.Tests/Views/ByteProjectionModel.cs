namespace Moltaro.Lattice.Tests.Views;

internal sealed class ByteProjectionModel
{
    public int Id { get; set; }

    public int? Level { get; set; }

    public byte[] Data { get; set; } = [];

    public List<int>? Numbers { get; set; }
}
