namespace Moltaro.Lattice.Prototypes.OperationRetry;

internal readonly record struct UniqueKey(string? First, string? Second)
{
    public bool ContainsNull => First is null || Second is null;
}
