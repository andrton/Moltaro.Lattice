using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// A transaction of several effects — what a write batch of S17 commits — is applied whole, at its one
// position (brief of Epic 1, sprint S11, Acceptance: "each whole"; "One for a single write, several for a
// batch"). The engine commits no batch before S17, so a relay (FrameRelay) makes one: in the place of the
// transactions of two inserts it sends one, at the place and the sequence of the first, that carries
// both Writes, the second with the sequence of the one transaction as its last mutation, as a master
// writes the members of a batch (the shapes are in TransactionPayload). The master writes nothing after
// them, since the frames that would follow are those of the master's own order. The child applies the
// transaction: it is at its position and holds both pieces. That a transaction with one effect that is
// not valid applies none of them is stated by MalformedTransactionTests.
public sealed class WholeTransactionTests
{
    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Transaction_of_several_effects_is_applied_whole_at_its_one_position()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 1, dataBytes: 16);
        WireFrame? first = null;
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
        {
            if (frame.Kind != FrameKindEnum.Transaction)
            {
                return [frame];
            }

            if (first is null)
            {
                first = frame;
                return [];
            }

            return [Merged(first, frame)];
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);
        var pieces = master.Set<PieceSchema.Piece>();

        var batch = await pieces.InsertAsync(Pieces.Versioned(2, version: 1, dataBytes: 100));
        await pieces.InsertAsync(Pieces.Versioned(3, version: 1, dataBytes: 100));

        await Eventually.AtAsync(child, batch.Position);

        Assert.Equal("v1", child.Set<PieceSchema.Piece>().Get(2L)?.Name);
        Assert.Equal("v1", child.Set<PieceSchema.Piece>().Get(3L)?.Name);
        Assert.Equal([1L, 2L, 3L], child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order());
    }

    private static WireFrame Merged(WireFrame first, WireFrame second)
    {
        var sequence = TransactionPayload.SequenceOf(first);
        var member = TransactionPayload.EffectsOf(second);

        BinaryPrimitives.WriteUInt64LittleEndian(member.AsSpan(TransactionPayload.WriteLastMutationAt), sequence);

        return first.WithPayload(TransactionPayload.Make(sequence, [.. TransactionPayload.EffectsOf(first), .. member]));
    }
}
