using System.Buffers.Binary;
using Moltaro.Lattice.Prototypes.ArenaLifecycle;

namespace Moltaro.Lattice.Prototypes.WriteBatch;

internal static class UnitCodec
{
    public const int Bytes = 256;
    public const int MaximumParts = 10;
    private const int HeaderBytes = 12;

    public static SavedUnit[] Encode(SavedRoot root)
    {
        var encoded = BatchCodec.Encode([root.Mutation]);
        var count = (HeaderBytes + encoded.Length + Bytes - 1) / Bytes;
        Check.That(count <= MaximumParts && root.Revision > 0, "Saved root exceeds fixture format.");
        var padded = new byte[count * Bytes];
        BinaryPrimitives.WriteInt32LittleEndian(padded, encoded.Length);
        BinaryPrimitives.WriteInt64LittleEndian(padded.AsSpan(4), root.Revision);
        encoded.CopyTo(padded, HeaderBytes);
        return Enumerable.Range(0, count).Select(part => new SavedUnit(root.Mutation.Identity.Slot,
            part, padded.AsSpan(part * Bytes, Bytes).ToArray())).ToArray();
    }

    public static SavedRoot Decode(int slot, IReadOnlyList<SavedUnit> units)
    {
        Check.That(units.Count is > 0 and <= MaximumParts, "Invalid saved root part count.");
        var padded = new byte[units.Count * Bytes];
        for (var index = 0; index < units.Count; index++)
        {
            var unit = units[index];
            Check.That(unit.Slot == slot && unit.Part == index && unit.Payload.Length == Bytes, "Saved unit gap or size mismatch.");
            unit.Payload.CopyTo(padded, index * Bytes);
        }

        var length = BinaryPrimitives.ReadInt32LittleEndian(padded);
        var revision = BinaryPrimitives.ReadInt64LittleEndian(padded.AsSpan(4));
        Check.That(length > 0 && length <= padded.Length - HeaderBytes && revision > 0 &&
            (length + HeaderBytes + Bytes - 1) / Bytes == units.Count, "Invalid saved root framing.");
        Check.That(padded.AsSpan(HeaderBytes + length).IndexOfAnyExcept((byte)0) < 0, "Invalid saved root padding.");
        var mutations = BatchCodec.Decode(padded.AsSpan(HeaderBytes, length).ToArray());
        Check.That(mutations.Length == 1 && mutations[0].Kind == MutationKindEnum.Upsert &&
            mutations[0].Identity.Slot == slot, "Saved root identity mismatch.");
        return new SavedRoot(mutations[0], revision);
    }
}
