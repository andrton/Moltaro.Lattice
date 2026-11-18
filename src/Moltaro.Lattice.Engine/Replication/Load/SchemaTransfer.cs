using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Replication.Load;

// The first part of a load: the schema of the master and the number of roots of each of its entity
// types (brief of Epic 1, sprint S10; the planner's decision of 17 November 2026 after the study, points
// 3 and 6). The bytes are the compiled schema as LatticeSchema.Save writes it, with its own digest, and
// then one UInt64 for each entity type, in the order of LatticeSchema.EntityTypes, little-endian: the
// count a child sizes the primary index of the type from; LoadEnd still verifies the roots it received.
// They are cut into Schema frames by the rule of a root: every frame full but the last, which carries
// Protocol.EndsABody.
internal static class SchemaTransfer
{
    // The schema in its compiled form, once for every child of a master.
    public static byte[] Saved(LatticeSchema schema)
    {
        using var stream = new MemoryStream();

        schema.Save(stream);

        return stream.ToArray();
    }

    // Adds the Schema frames of the saved schema and of the counts to the connection.
    public static void Add(FrameConnection connection, byte[] savedSchema, ReadOnlySpan<long> counts)
    {
        var bytes = new byte[savedSchema.Length + (counts.Length * sizeof(ulong))];

        savedSchema.CopyTo(bytes, 0);

        for (var index = 0; index < counts.Length; index++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(savedSchema.Length + (index * sizeof(ulong))), (ulong)counts[index]);
        }

        var unsent = bytes.AsSpan();

        do
        {
            var part = Math.Min(unsent.Length, Protocol.MaxDataPayload);

            unsent = unsent[part..];
            connection.Add(FrameKindEnum.Schema, unsent.IsEmpty ? Protocol.EndsABody : 0, bytes.AsSpan(bytes.Length - unsent.Length - part, part));
        }
        while (!unsent.IsEmpty);
    }

    // Reads what the Schema frames of a load carried: the schema, and the count of roots of each of its
    // entity types, in their order. ValidationFailed when the counts are not one UInt64 for each entity
    // type after the schema, or one of them is beyond what a count can be; InvalidSchema from
    // LatticeSchema.Load when the schema is not one.
    public static (LatticeSchema Schema, long[] Counts) Read(ReadOnlySequence<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        var schema = LatticeSchema.Load(stream);
        var rest = stream.Length - stream.Position;
        var types = schema.EntityTypes.Count;

        if (rest != (long)types * sizeof(ulong))
        {
            throw Invalid($"The schema of the master is followed by {rest} bytes, not by a count for each of its {types} entity types.");
        }

        var counts = new long[types];
        Span<byte> count = stackalloc byte[sizeof(ulong)];

        for (var index = 0; index < types; index++)
        {
            stream.ReadExactly(count);

            var value = BinaryPrimitives.ReadUInt64LittleEndian(count);

            if (value > int.MaxValue)
            {
                throw Invalid($"The master announces {value} roots of {schema.EntityTypes[index].Name}, more than a database holds.");
            }

            counts[index] = (long)value;
        }

        return (schema, counts);
    }

    private static LatticeException Invalid(string message) => new(LatticeErrorEnum.ValidationFailed, message);
}
