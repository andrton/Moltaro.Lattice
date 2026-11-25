namespace Moltaro.Lattice.Storage.Text;

// The test observation of one writer entry and its current native location.
internal readonly record struct TextEntrySnapshot(
    uint Id, ArenaAddress Address, int WriterOrdinal, TextBucketSetEnum BucketSet, int Bucket, int BucketDepth, long References);
