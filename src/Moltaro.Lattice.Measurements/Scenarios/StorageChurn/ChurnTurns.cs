using System.Threading.Channels;

namespace Moltaro.Lattice.Measurements.Scenarios.StorageChurn;

internal sealed class ChurnTurns
{
    // The producer is optional and must run beyond the actual admitted engine turn.
    // It never waits on disk. Full buffer or file fails evidence; it is not sampling.
    public const int RecordLimit = 1_000_000;
    public const int TraceVersion = 2;
    public const int BytesPerSample = 82;
    private readonly Channel<Sample> _buffer = Channel.CreateBounded<Sample>(8192);
    private int _offered;
    private int _lost;

    public int Offered => Volatile.Read(ref _offered);
    public int Lost => Volatile.Read(ref _lost);

    public void Offer(Sample sample)
    {
        if (Interlocked.Increment(ref _offered) > RecordLimit || !_buffer.Writer.TryWrite(sample))
        {
            Interlocked.Increment(ref _lost);
        }
    }

    public async Task WriteAsync(string file)
    {
        await using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);
        using var writer = new BinaryWriter(stream);
        await foreach (var sample in _buffer.Reader.ReadAllAsync())
        {
            writer.Write(sample.ObservedAt);
            writer.Write(sample.Elapsed);
            writer.Write(sample.Admitted);
            writer.Write(sample.Progress);
            writer.Write(sample.CandidatesExamined);
            writer.Write(sample.SelectionWork);
            writer.Write(sample.Old);
            writer.Write(sample.New);
            writer.Write(sample.MovedSlotBytes);
            writer.Write(sample.ReclaimedCount);
            writer.Write(sample.ReturnedSegments);
            writer.Write(sample.ReturnedBytes);
            writer.Write(sample.ChargedBytes);
            writer.Write(sample.SourceKind);
            writer.Write(sample.SourceFlags);
            writer.Write(sample.SourceDescriptorKind);
            writer.Write(sample.TextMetadataWork);
        }
    }

    // Close both databases before completing these queues, so no admitted turn publishes after completion.
    public void Complete() => _buffer.Writer.Complete();

    internal readonly record struct Sample(long ObservedAt, long Elapsed, bool Admitted, bool Progress, int CandidatesExamined, int SelectionWork,
        ulong Old, ulong New, int MovedSlotBytes, int ReclaimedCount, int ReturnedSegments, long ReturnedBytes,
        long ChargedBytes, ushort SourceKind, ushort SourceFlags, uint SourceDescriptorKind, int TextMetadataWork);
}
