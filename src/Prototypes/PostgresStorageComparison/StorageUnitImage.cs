using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

// Writer-owned staging image. Concurrent readers use NativeRecords, published after commit.
internal sealed class StorageUnitImage : IDisposable
{
    public const int RecordBytes = 256;
    public const int ImageBytes = NativeRecords.RecordCount * RecordBytes;
    private readonly NativeImage _image = new(ImageBytes);

    public StorageUnitImage(int unitBytes, bool initialize)
    {
        UnitBytes = unitBytes;
        if (initialize)
        {
            var random = new Random(8421);
            random.NextBytes(_image.Slice(0, ImageBytes));
            for (var key = 0; key < NativeRecords.RecordCount; key++)
            {
                SetValue(key, 0, ~0L);
            }
        }
    }

    public int UnitBytes { get; }
    public int UnitCount => ImageBytes / UnitBytes;

    public Span<byte> Unit(int id) => _image.Slice(id * UnitBytes, UnitBytes);

    public int Apply(EncodedBatch batch, Span<int> changedUnits)
    {
        var count = 0;
        for (var index = 0; index < batch.Count; index++)
        {
            var mutation = batch.Payload.AsSpan(index * BatchCodec.MutationBytes);
            var key = checked((int)BinaryPrimitives.ReadInt64LittleEndian(mutation[8..]));
            SetValue(key, BinaryPrimitives.ReadInt64LittleEndian(mutation[16..]),
                BinaryPrimitives.ReadInt64LittleEndian(mutation[24..]));
            var unit = key * RecordBytes / UnitBytes;
            if (!changedUnits[..count].Contains(unit))
            {
                changedUnits[count++] = unit;
            }
        }

        return count;
    }

    public void Verify(bool[] acknowledged, NativeRecords records, StorageUnitImage expectedImage)
    {
        var expected = new long[NativeRecords.RecordCount];
        for (var sequence = 1; sequence < acknowledged.Length; sequence++)
        {
            if (acknowledged[sequence])
            {
                expected[Mutation.KeyFor(sequence)] = sequence;
            }
        }

        for (var key = 0; key < expected.Length; key++)
        {
            var record = _image.Slice(key * RecordBytes, RecordBytes);
            if (BinaryPrimitives.ReadInt64LittleEndian(record) != expected[key]
                || BinaryPrimitives.ReadInt64LittleEndian(record[8..]) != ~expected[key]
                || records.Read(key) != expected[key])
            {
                throw new InvalidDataException("Current-state recovery differs from acknowledged mutations.");
            }
        }

        for (var unit = 0; unit < UnitCount; unit++)
        {
            if (!Unit(unit).SequenceEqual(expectedImage.Unit(unit)))
            {
                throw new InvalidDataException("Persisted unit payload changed outside its expected update.");
            }
        }
    }

    private void SetValue(int key, long value, long complement)
    {
        var target = _image.Slice(key * RecordBytes, 16);
        BinaryPrimitives.WriteInt64LittleEndian(target, value);
        BinaryPrimitives.WriteInt64LittleEndian(target[8..], complement);
    }

    public void Dispose() => _image.Dispose();
}
