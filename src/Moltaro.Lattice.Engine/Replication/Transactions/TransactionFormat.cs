using System.Buffers.Binary;

namespace Moltaro.Lattice.Replication.Transactions;

// How a committed transaction travels from a master to its children after LoadEnd (brief of Epic 1,
// sprint S11; the planner's decision of 17 November 2026 after the study, points 5 to 8). Every number
// outside a root is little-endian.
//
// A transaction is its sequence (UInt64), the length of its effects (UInt64), and its effects, one after
// another until that length:
// - Write: its kind (EffectKindEnum, one byte), the length of its root (UInt64) and the root as a load
//   sends it — type id (UInt32), last mutation sequence (UInt64), then what Load.BlockExport writes:
//   layout version, root payload, values out of line, in the byte order the Hello has checked. The last
//   mutation of the root is the sequence of its transaction.
// - Delete: its kind, the type id of the entity (UInt32), then the key, in the form of the entity type
//   of the key (Engine.Entities.EntityTable.WriteKey).
// - Unchanged: its kind and nothing more.
// It travels in Transaction frames (FrameKindEnum.Transaction) cut from those bytes in their order: a
// transaction begins a frame of its own, each frame holds at most Protocol.MaxDataPayload bytes of it,
// and its last frame carries Protocol.EndsARoot. Its frames are its check: the check of each frame, the
// announced length, which the frames end exactly, and its sequence, which follows the one before; no
// digest of its own is built (decided after the study of S11, point 6).
//
// Applied (FrameKindEnum.Applied), from a child, carries the sequence up to which it has applied all it
// has received (UInt64).
internal static class TransactionFormat
{
    // The sequence and the length of the effects at the start of a transaction.
    public const int HeaderSize = 2 * sizeof(ulong);

    // The kind and the length of the root at the start of a Write.
    public const int WriteEnvelopeSize = 1 + sizeof(ulong);

    // The type id and the last mutation sequence at the start of the root of a Write, as at the start of
    // a root of a load.
    public const int RootEnvelopeSize = sizeof(uint) + sizeof(ulong);

    // The kind and the type id at the start of a Delete.
    public const int DeleteEnvelopeSize = 1 + sizeof(uint);

    // An Unchanged: its kind.
    public const int UnchangedSize = 1;

    public static void WriteHeader(Span<byte> bytes, ulong sequence, long effectsLength)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[sizeof(ulong)..], (ulong)effectsLength);
    }

    public static void WriteApplied(Span<byte> payload, ulong sequence) => BinaryPrimitives.WriteUInt64LittleEndian(payload, sequence);

    // ValidationFailed when the payload is not Protocol.AppliedPayloadSize bytes long.
    public static ulong ReadApplied(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Protocol.AppliedPayloadSize)
        {
            throw new LatticeException(LatticeErrorEnum.ValidationFailed, $"The payload of an Applied has {Protocol.AppliedPayloadSize} bytes, not {payload.Length}.");
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(payload);
    }
}
