using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class QueuedWriteCodec
{
    public static void Fill(long sequence, Span<byte> command)
    {
        BinaryPrimitives.WriteInt64LittleEndian(command, sequence);
        BinaryPrimitives.WriteInt64LittleEndian(command[8..], Mutation.KeyFor(sequence));
        BinaryPrimitives.WriteInt64LittleEndian(command[16..], sequence);
        BinaryPrimitives.WriteInt64LittleEndian(command[24..], ~sequence);
        var random = (uint)sequence ^ 0x9e3779b9;
        for (var index = 32; index < command.Length; index++)
        {
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            command[index] = (byte)random;
        }
    }

    public static long Validate(ReadOnlySpan<byte> command)
    {
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(command);
        Span<byte> expected = stackalloc byte[command.Length];
        Fill(sequence, expected);
        if (!command.SequenceEqual(expected))
        {
            throw new InvalidDataException("Queued command content changed during persistence.");
        }

        return sequence;
    }

    public static EncodedBatch Encode(PendingWrite?[] writes, int count)
    {
        var commandBytes = writes[0]!.Command.Length;
        var payload = new byte[count * commandBytes];
        for (var index = 0; index < count; index++)
        {
            writes[index]!.Command.CopyTo(payload, index * commandBytes);
        }

        return new EncodedBatch(writes[0]!.Mutation.Sequence, count, payload);
    }

    public static void Apply(Mutation mutation, NativeImage image, NativeRecords records)
    {
        records.Apply(mutation);
        var target = image.Slice(mutation.Key * 16, 16);
        BinaryPrimitives.WriteInt64LittleEndian(target, mutation.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(target[8..], ~mutation.Sequence);
    }
}
