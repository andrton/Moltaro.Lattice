using System.Buffers.Binary;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// The payload of a Transaction frame and of an Applied frame as the tests of Epic 1, sprint S11 assume
// them, apart from the codec of the engine. The shapes, from the brief of S11 ("Types and members of
// S11" and the planner's decision after the study, points 5 to 8), with the widths and the order the
// brief leaves open chosen as S10 chose its own — little-endian, a UInt64 for a sequence and a length, a
// UInt32 for a type id:
// - FrameKindEnum.Transaction = 7 carries a committed transaction; FrameKindEnum.Applied = 9 the
//   acknowledgment of a child; 8 is not used.
// - A transaction begins a frame of its own. The payload of its first frame is its sequence (UInt64),
//   the length it announces for its effects (UInt64), and then the first bytes of its effects; each
//   further frame of it holds only the next bytes of its effects. The announced length counts the
//   effects alone, over all the frames of the transaction. Whatever flags the frames carry, a test that
//   changes a frame keeps them.
// - Its effects follow one another until the announced length, each a kind (one byte, EffectKindEnum)
//   and what that kind carries: a Write (1) the root as a load sends it, its length (UInt64) and then its
//   bytes — type id (UInt32), last mutation sequence (UInt64), layout version (UInt32), root payload,
//   values out of line —; a Delete (2) the type id (UInt32) and then the key in the form the implementer
//   of S11 chose (Replication.Transactions.TransactionFormat): an integer key as an Int64, a String key
//   as the length of its UTF-8 (UInt32) and that UTF-8; a write that changed nothing, Unchanged (3),
//   nothing more.
// - The root of a Write carries the sequence of its transaction as its last mutation (the planner's
//   block of 17 November 2026, point 3), and a child checks it on receipt: a Write whose root carries
//   another sequence is a malformed transaction, refused whole (the coordinator's decision of
//   17 November 2026). The state of a key that an effect carries has the sequence of its transaction,
//   for a Write and a Delete alike; during a load the rule compares the last mutation of each root
//   copied with the state the child holds, as before.
// - The payload of Applied is the sequence up to which the child has applied all it has received
//   (UInt64).
// - The payload of LoadEnd carries S, R and the number of roots sent (LoadEndPayload).
// - The tests of the engine besides these use EngineDatabase.SuspendApplying(), an IDisposable a Ready
//   child holds back the application of what it receives with until it is disposed, and the counters
//   EngineCounters.ActiveCaptures (int) and PendingDeliveryBytes (long).
internal static class TransactionPayload
{
    // The sequence and the announced length at the start of the first frame of a transaction.
    public const int HeaderSize = 2 * sizeof(ulong);

    public const byte Write = 1;

    public const byte Delete = 2;

    public const byte Unchanged = 3;

    // Where the last mutation of the root stands in a Write: after its kind, the length of its root and
    // the type id.
    public const int WriteLastMutationAt = 1 + sizeof(ulong) + sizeof(uint);

    public static ulong SequenceOf(WireFrame frame)
    {
        Assert.Equal(FrameKindEnum.Transaction, frame.Kind);

        return BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
    }

    public static ulong AnnouncedLengthOf(WireFrame frame) => BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(sizeof(ulong)));

    // The effects in the first frame of a transaction: of the whole transaction, when it has one frame.
    public static byte[] EffectsOf(WireFrame frame) => frame.Payload[HeaderSize..];

    // The payload of the first frame of a transaction of one frame: its sequence, the length of the
    // effects given, and the effects.
    public static byte[] Make(ulong sequence, ReadOnlySpan<byte> effects) => Make(sequence, (ulong)effects.Length, effects);

    // The same with an announced length of the test's own.
    public static byte[] Make(ulong sequence, ulong announcedLength, ReadOnlySpan<byte> effects)
    {
        var payload = new byte[HeaderSize + effects.Length];

        BinaryPrimitives.WriteUInt64LittleEndian(payload, sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(payload.AsSpan(sizeof(ulong)), announcedLength);
        effects.CopyTo(payload.AsSpan(HeaderSize));

        return payload;
    }

    // The sequence an Applied frame acknowledges.
    public static ulong AppliedOf(WireFrame frame)
    {
        Assert.Equal(FrameKindEnum.Applied, frame.Kind);
        Assert.Equal(sizeof(ulong), frame.Payload.Length);

        return BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload);
    }
}
