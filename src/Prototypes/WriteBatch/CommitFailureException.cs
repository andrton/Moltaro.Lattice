namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed class CommitFailureException(bool uncertain, Exception inner)
    : Exception(uncertain ? "Storage commit outcome requires recovery." : "Storage transaction rolled back.", inner)
{
    public bool Uncertain { get; } = uncertain;
}
