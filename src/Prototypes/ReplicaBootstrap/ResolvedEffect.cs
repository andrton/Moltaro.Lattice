namespace Moltaro.Lattice.Prototypes.ReplicaBootstrap;

internal sealed record ResolvedEffect(
    MutationKindEnum Kind,
    RootKey Key,
    long BeforeRevision,
    int Number,
    byte[]? FullBlock);
