using System.Buffers;
using System.Buffers.Binary;
using Moltaro.Lattice.Replication.Load;

namespace Moltaro.Lattice.Replication.Transactions;

// How a committed transaction travels from a master to its children after LoadEnd
// (docs/design/milestone-1-engine-contract.md, "Replication transport and initial loading"). Every
// number outside a root is little-endian.
//
// A transaction is its sequence (UInt64), the length of its effects (UInt64), the number of its effects
// (UInt32), from which a child reserves the estimate of the transaction before it reads them, and its
// effects, one after another until that length; a child that reads another number of effects than the
// header states refuses the transaction as malformed:
// - Write: its kind (EffectKindEnum, one byte), the length of its root (UInt64) and the root as a load
//   sends it — type id (UInt32), last mutation sequence (UInt64), the number of its member of a write
//   batch, 0 for a single write (UInt32) (Load.RootEnvelope), then what Load.BlockExport writes: layout
//   version, root payload, values out of line, in the byte order the Hello has checked. The last mutation
//   of the root is the sequence of its transaction.
// - Delete: its kind, the type id of the entity (UInt32), then the key, in the form of the entity type
//   of the key (Engine.Entities.PrimaryKeys.WriteKey).
// - Unchanged: its kind and nothing more.
// - Update: its kind, the length of its body (UInt64), the type id (UInt32),
//   original member (UInt32), predecessor sequence (UInt64), key and effective commands.
// It travels in Transaction frames (FrameKindEnum.Transaction) cut from those bytes in their order: a
// transaction begins a frame of its own, each frame holds at most Protocol.MaxDataPayload bytes of it,
// and its last frame carries Protocol.EndsABody. Its frames are its check: the check of each frame, the
// announced length, which the frames end exactly, and its sequence, which follows the one before; no
// digest of its own is built.
//
// Applied (FrameKindEnum.Applied), from a child, carries the sequence up to which it has applied all it
// has received (UInt64).
internal static class TransactionFormat
{
    // The sequence, the length of the effects and their number at the start of a transaction.
    public const int HeaderSize = (2 * sizeof(ulong)) + sizeof(uint);

    // The kind and the length of the root at the start of a Write.
    public const int WriteEnvelopeSize = 1 + sizeof(ulong);

    // The kind and the type id at the start of a Delete.
    public const int DeleteEnvelopeSize = 1 + sizeof(uint);

    public const int UpdateHeadSize = (2 * sizeof(uint)) + sizeof(ulong);

    public const int UpdateEnvelopeSize = 1 + sizeof(ulong) + UpdateHeadSize;

    // An Unchanged: its kind.
    public const int UnchangedSize = 1;

    public static void WriteHeader(Span<byte> bytes, ulong sequence, long effectsLength, uint effectCount)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[sizeof(ulong)..], (ulong)effectsLength);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[(2 * sizeof(ulong))..], effectCount);
    }

    // The number of the effects a header states.
    public static uint ReadEffectCount(ReadOnlySpan<byte> header) => BinaryPrimitives.ReadUInt32LittleEndian(header[(2 * sizeof(ulong))..]);

    // The envelope of a Write, WriteEnvelopeSize bytes: its kind and the length of its root.
    public static void WriteEnvelopeOfWrite(Span<byte> bytes, long rootLength)
    {
        bytes[0] = (byte)EffectKindEnum.Write;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[1..], (ulong)rootLength);
    }

    // The envelope of a Delete, DeleteEnvelopeSize bytes: its kind and the type id of the entity.
    public static void WriteEnvelopeOfDelete(Span<byte> bytes, uint typeId)
    {
        bytes[0] = (byte)EffectKindEnum.Delete;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[1..], typeId);
    }

    public static void WriteEnvelopeOfUpdate(Span<byte> bytes, long bodyLength, uint typeId, uint member, ulong baseSequence)
    {
        bytes[0] = (byte)EffectKindEnum.Update;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[1..], (ulong)bodyLength);
        var body = bytes[(1 + sizeof(ulong))..];
        BinaryPrimitives.WriteUInt32LittleEndian(body, typeId);
        BinaryPrimitives.WriteUInt32LittleEndian(body[sizeof(uint)..], member);
        BinaryPrimitives.WriteUInt64LittleEndian(body[(2 * sizeof(uint))..], baseSequence);
    }

    // An Unchanged, UnchangedSize bytes: its kind.
    public static void WriteUnchanged(Span<byte> bytes) => bytes[0] = (byte)EffectKindEnum.Unchanged;

    // Reads the envelope of the next effect from the effects of the transaction of the sequence and returns
    // its kind: for a Write the root, whose type id and last mutation are read, with the effects advanced
    // past it; for a Delete its type id, with the effects left at its key; for an Unchanged nothing more.
    // ValidationFailed for a Write whose bytes do not hold the root it announces, a Delete that ends before
    // its type id, and a kind that is none.
    public static EffectKindEnum ReadEffect(ref SequenceReader<byte> effects, ulong sequence, out uint typeId, out ulong lastMutation, out uint member, out SequenceReader<byte> root)
    {
        typeId = 0;
        lastMutation = 0;
        member = 0;
        root = default;

        effects.TryRead(out var kind);

        switch ((EffectKindEnum)kind)
        {
            case EffectKindEnum.Write:
                if (!effects.TryReadLittleEndian(out long rootLength) || rootLength < RootAssembler.EnvelopeSize || rootLength > effects.Remaining)
                {
                    throw Protocol.Invalid($"A Write of the transaction {sequence} announces a root its bytes do not hold.");
                }

                root = new SequenceReader<byte>(effects.UnreadSequence.Slice(0, rootLength));
                effects.Advance(rootLength);
                RootEnvelope.TryRead(ref root, out typeId, out lastMutation, out member);

                return EffectKindEnum.Write;

            case EffectKindEnum.Update:
                if (!effects.TryReadLittleEndian(out long updateLength) || updateLength < UpdateHeadSize || updateLength > effects.Remaining)
                {
                    throw Protocol.Invalid($"An Update of the transaction {sequence} announces a body its bytes do not hold.");
                }

                root = new SequenceReader<byte>(effects.UnreadSequence.Slice(0, updateLength));
                effects.Advance(updateLength);
                root.TryReadLittleEndian(out int updatedId);
                root.TryReadLittleEndian(out int updateMember);
                root.TryReadLittleEndian(out long predecessor);
                typeId = (uint)updatedId;
                member = (uint)updateMember;
                lastMutation = (ulong)predecessor;
                if (lastMutation == 0 || lastMutation >= sequence)
                {
                    throw Protocol.Invalid($"An Update of the transaction {sequence} has the invalid predecessor {lastMutation}.");
                }

                return EffectKindEnum.Update;

            case EffectKindEnum.Delete:
                if (!effects.TryReadLittleEndian(out int deletedId))
                {
                    throw Protocol.Invalid($"A Delete of the transaction {sequence} ends before its type id.");
                }

                typeId = (uint)deletedId;

                return EffectKindEnum.Delete;

            case EffectKindEnum.Unchanged:
                return EffectKindEnum.Unchanged;

            default:
                throw Protocol.Invalid($"The transaction {sequence} holds an effect of the kind {kind}, which is none.");
        }
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
