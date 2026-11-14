namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal readonly record struct Mutation(long Sequence, long IntendedTimestamp)
{
    public int Key => KeyFor(Sequence);

    public static int KeyFor(long sequence) => (int)(sequence * 7919 % NativeRecords.RecordCount);
}
