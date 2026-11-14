namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

// Only the experiment shares this interface; it is not a database provider contract.
internal interface ICommitSink : IDisposable
{
    void Commit(EncodedBatch batch);

    void Verify(bool[] acknowledged, NativeRecords records);
}
