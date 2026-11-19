using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Replication;
using Moltaro.Lattice.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// One transaction in the place of two, as a master sends the writes of a batch: the sequence of the first,
// the effects of both, the root of the second with that sequence as its last mutation. The second may
// take several frames: the first frame of the merged transaction holds its sequence, the length of all
// the effects and the effects of the first, and does not end the transaction; the first frame of the
// second follows without its own sequence and length, then the rest of the second as it came. So the
// stream sequences of the frames stay as the master numbered them.
internal static class MergedTransaction
{
    public static IEnumerable<WireFrame> Of(WireFrame first, WireFrame second)
    {
        var sequence = TransactionPayload.SequenceOf(first);
        var effects = TransactionPayload.EffectsOf(first);

        Assert.Equal((ulong)effects.Length, TransactionPayload.AnnouncedLengthOf(first));

        var rest = second.Payload[TransactionPayload.HeaderSize..];

        Assert.Equal(TransactionPayload.Write, rest[0]);
        Assert.Equal(TransactionPayload.SequenceOf(second), BinaryPrimitives.ReadUInt64LittleEndian(rest.AsSpan(TransactionPayload.WriteLastMutationAt)));
        BinaryPrimitives.WriteUInt64LittleEndian(rest.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);

        var opening = TransactionPayload.Make(sequence, (ulong)effects.Length + TransactionPayload.AnnouncedLengthOf(second), effects);

        return
        [
            WireFrame.Make(FrameKindEnum.Transaction, first.Flags & ~WireFrame.EndsABodyFlag, first.CorrelationId, first.StreamSequence, opening),
            second.WithPayload(rest),
        ];
    }
}
