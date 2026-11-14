using System.Text;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class BatchCodec
{
    private const int Magic = 0x3142544c;

    // Independent fixture format: version + typed identities + values/text/payload.
    // No native address or index image is persisted. The reader verifies every
    // actual byte against this fixture's deterministic schema before reconstruction.
    public static byte[] Encode(ReadOnlySpan<BatchMutation> mutations)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(Magic);
        writer.Write(mutations.Length);
        foreach (var mutation in mutations)
        {
            writer.Write((byte)mutation.Identity.Type);
            writer.Write(mutation.Identity.Key);
            writer.Write((byte)mutation.Kind);
            if (mutation.Kind == MutationKindEnum.Delete)
            {
                continue;
            }

            var value = mutation.Value;
            writer.Write(value.Number);
            writer.Write(value.Seed);
            writer.Write(value.TextGroup);
            writer.Write(TextTable.Expected(value.TextGroup));
            writer.Write(value.Length);
            for (var index = 0; index < value.Length; index++)
            {
                writer.Write(RootCodec.Pattern(value.Seed, index));
            }
        }

        return stream.ToArray();
    }

    public static BatchMutation[] Decode(byte[] payload)
    {
        using var stream = new MemoryStream(payload, false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        Check.That(reader.ReadInt32() == Magic, "Unsupported fixture format.");
        var count = reader.ReadInt32();
        Check.That(count is > 0 and <= 512, "Fixture persisted count limit.");
        var result = new BatchMutation[count];
        for (var index = 0; index < count; index++)
        {
            var identity = new EntityKey((EntityTypeEnum)reader.ReadByte(), reader.ReadInt32());
            var kind = (MutationKindEnum)reader.ReadByte();
            if (kind == MutationKindEnum.Delete)
            {
                result[index] = BatchMutation.Delete(identity);
                continue;
            }

            Check.That(kind == MutationKindEnum.Upsert, "Unsupported persisted mutation.");
            var number = reader.ReadInt64();
            var seed = reader.ReadInt32();
            var group = reader.ReadInt32();
            Check.That(group is >= 0 and < TextTable.Count && reader.ReadString() == TextTable.Expected(group),
                "Persisted text differs from fixture schema.");
            var length = reader.ReadInt32();
            Check.That(length is > 0 and <= RootCodec.MaximumChunks * RootCodec.ChunkBytes, "Persisted payload limit.");
            for (var offset = 0; offset < length; offset++)
            {
                Check.That(reader.ReadByte() == RootCodec.Pattern(seed, offset), "Persisted payload differs.");
            }

            result[index] = new BatchMutation(identity, kind, new ValueSpec(identity.Slot, number, seed, length, group));
        }

        Check.That(stream.Position == stream.Length, "Unexpected persisted tail.");
        return result;
    }
}
