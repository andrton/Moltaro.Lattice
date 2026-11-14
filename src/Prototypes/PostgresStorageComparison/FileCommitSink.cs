using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class FileCommitSink : ICommitSink
{
    private readonly string _path;
    private readonly FileStream _stream;

    public FileCommitSink(string path)
    {
        _path = path;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
            4096, FileOptions.SequentialScan);
    }

    public void Commit(EncodedBatch batch)
    {
        Span<byte> header = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(header, batch.FirstSequence);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], batch.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], batch.Payload.Length);
        _stream.Write(header);
        _stream.Write(batch.Payload);
        _stream.Flush(flushToDisk: true);
    }

    public void Verify(bool[] acknowledged, NativeRecords records)
    {
        BatchCodec.Verify(ReadPersisted(), acknowledged, records);
    }

    private IEnumerable<EncodedBatch> ReadPersisted()
    {
        using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream);
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            var first = reader.ReadInt64();
            var count = reader.ReadInt32();
            var length = reader.ReadInt32();
            if (length != count * BatchCodec.MutationBytes || count is < 1 or > 64)
            {
                throw new InvalidDataException("Invalid file batch header.");
            }

            var payload = reader.ReadBytes(length);
            if (payload.Length != length)
            {
                throw new EndOfStreamException();
            }

            yield return new EncodedBatch(first, count, payload);
        }
    }

    public void Dispose() => _stream.Dispose();
}
