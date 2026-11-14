using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal sealed class ReplayLayout
{
    public ReplayLayout(int recordBytes = 2048, int recordCount = NativeRecords.RecordCount)
    {
        RecordBytes = recordBytes;
        RecordCount = recordCount;
    }

    public int RecordBytes { get; }
    public int RecordCount { get; }
    public int ImageBytes => RecordCount * RecordBytes;
    public const int CommandBytes = 32;
    public const int WriterBatch = 64;

    public int KeyFor(long sequence, ReplayPatternEnum pattern)
    {
        if (pattern == ReplayPatternEnum.Hot && sequence % 10 != 0)
        {
            // Ninety percent of writes visit 256 keys scattered across the full image.
            var ordinal = sequence - sequence / 10 - 1;
            return (int)((ordinal % 256) * 251);
        }

        var ordinalSequence = pattern == ReplayPatternEnum.Hot ? sequence / 10 : sequence;
        return (int)(ordinalSequence * 7919 % RecordCount);
    }

    public void Initialize(NativeImage image)
    {
        new Random(20261112).NextBytes(image.Slice(0, ImageBytes));
        for (var key = 0; key < RecordCount; key++)
        {
            SetValue(image.Slice(key * RecordBytes, 16), 0);
        }
    }

    public static void SetValue(Span<byte> record, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(record, value);
        BinaryPrimitives.WriteInt64LittleEndian(record[8..], ~value);
    }

    public static EncodedBatch Encode(ReadOnlySpan<ReplayChange> changes)
    {
        var payload = new byte[changes.Length * CommandBytes];
        for (var index = 0; index < changes.Length; index++)
        {
            var change = changes[index];
            var bytes = payload.AsSpan(index * CommandBytes, CommandBytes);
            BinaryPrimitives.WriteInt64LittleEndian(bytes, change.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], change.Key);
            SetValue(bytes[16..], change.Sequence);
        }

        return new EncodedBatch(changes[0].Sequence, changes.Length, payload);
    }

    public ReplayChange Decode(ReadOnlySpan<byte> bytes, ReplayPatternEnum pattern)
    {
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var key = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]));
        if (sequence <= 0 || key != KeyFor(sequence, pattern)
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]) != sequence
            || BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]) != ~sequence)
        {
            throw new InvalidDataException("Invalid replay command.");
        }

        return new ReplayChange(sequence, key, 0);
    }
}
