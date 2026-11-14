namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal sealed record RecoveryState(SavedRoot[] Roots, long Position, long Revision, StoredBatch[] Tail)
{
    public static RecoveryState Initial(BatchMutation[] mutations) => new(
        mutations.Select((mutation, index) => new SavedRoot(mutation, index + 1)).ToArray(),
        0, mutations.Length, []);
}
