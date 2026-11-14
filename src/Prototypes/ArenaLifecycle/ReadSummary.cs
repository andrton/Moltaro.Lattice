namespace Moltaro.Lattice.Prototypes.ArenaLifecycle;

internal readonly record struct ReadSummary(
    int Key, long Number, long Revision, int Seed, int Length, int TextGroup, ulong Checksum);
