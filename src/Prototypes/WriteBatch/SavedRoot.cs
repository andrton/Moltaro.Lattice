namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct SavedRoot(BatchMutation Mutation, long Revision);
