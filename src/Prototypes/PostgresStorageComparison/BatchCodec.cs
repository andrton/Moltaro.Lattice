using System.Buffers.Binary;

namespace Moltaro.Lattice.Prototypes.PostgresStorageComparison;

internal static class BatchCodec
{
    public const int MutationBytes = 32;

    public static EncodedBatch Encode(ReadOnlySpan<Mutation> mutations)
    {
        var payload = new byte[mutations.Length * MutationBytes];
        for (var index = 0; index < mutations.Length; index++)
        {
            var mutation = mutations[index];
            var target = payload.AsSpan(index * MutationBytes, MutationBytes);
            BinaryPrimitives.WriteInt64LittleEndian(target, mutation.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(target[8..], mutation.Key);
            BinaryPrimitives.WriteInt64LittleEndian(target[16..], mutation.Sequence);
            BinaryPrimitives.WriteInt64LittleEndian(target[24..], ~mutation.Sequence);
        }

        return new EncodedBatch(mutations[0].Sequence, mutations.Length, payload);
    }

    public static void Verify(IEnumerable<EncodedBatch> batches, bool[] acknowledged, NativeRecords records)
    {
        var seen = new bool[acknowledged.Length];
        var finalValues = new long[NativeRecords.RecordCount];
        long previous = 0;
        foreach (var batch in batches)
        {
            if (batch.Count < 1 || batch.Payload.Length != batch.Count * MutationBytes)
            {
                throw new InvalidDataException("Invalid persisted batch size.");
            }

            for (var index = 0; index < batch.Count; index++)
            {
                var bytes = batch.Payload.AsSpan(index * MutationBytes, MutationBytes);
                var sequence = BinaryPrimitives.ReadInt64LittleEndian(bytes);
                var key = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
                var value = BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]);
                var complement = BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]);
                if (sequence <= previous || sequence >= acknowledged.Length
                    || !acknowledged[sequence] || seen[sequence]
                    || key != Mutation.KeyFor(sequence) || value != sequence || complement != ~value
                    || (index == 0 && batch.FirstSequence != sequence))
                {
                    throw new InvalidDataException("Persisted mutation identity, ordering or content mismatch.");
                }

                previous = sequence;
                seen[sequence] = true;
                finalValues[key] = value;
            }
        }

        if (!seen.AsSpan().SequenceEqual(acknowledged))
        {
            throw new InvalidDataException("Acknowledged and recovered mutations differ.");
        }

        for (var key = 0; key < finalValues.Length; key++)
        {
            if (records.Read(key) != finalValues[key])
            {
                throw new InvalidDataException("Recovered and resident records differ.");
            }
        }
    }
}
