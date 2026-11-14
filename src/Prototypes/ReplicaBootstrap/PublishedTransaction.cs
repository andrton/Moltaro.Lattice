namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

// One sequence position covers the entire explicit batch, never an individual member.
internal sealed record PublishedTransaction(long Position, ResolvedEffect[] Effects)
{
    public int EncodedBytes => 16 + Effects.Sum(effect => 32 + (effect.FullBlock?.Length ?? 0));
}
