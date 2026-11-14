namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal readonly record struct UnitApplySample(long From, long Through, int Batches, int Mutations,
    int RootsWritten, int UnitsWritten, int JournalPayloadBytes, int UnitPayloadBytes,
    long ElapsedTicks, long AllocatedBytes);
