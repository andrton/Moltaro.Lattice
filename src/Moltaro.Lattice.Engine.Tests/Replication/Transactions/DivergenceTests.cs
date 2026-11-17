using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Transactions;

// After its readiness position R a child that receives an effect not later than the state it holds has
// met a broken invariant, not lag (brief of Epic 1, sprint S11, decided after the study, point 9): the
// instance becomes Faulted, the connection is closed, and every operation is Unavailable. Not "stops
// following". A real master never sends such an effect, so the stream is made by hand (the shapes the
// tests assume are in TransactionPayload):
// - A relay (FrameRelay) records what a real master sends a child that loads one piece and then applies
//   one replace of it: the handshake, the schema, the root, LoadEnd, and the transaction of the replace.
// - A peer acting as the master (ScriptedMaster) sends a new child the same frames, and then that
//   transaction once more under the next sequence: a transaction that follows the one before, whose
//   Write carries the state the child already holds — its root has the last mutation of the replace,
//   not later than what the child holds.
// The child loads, is Ready, applies the replace, and then becomes Faulted and ends its connection; every
// operation is Unavailable, and its close returns every piece of native memory it obtained.
public sealed class DivergenceTests
{
    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact(Skip = ChildFollowing.NotImplemented)]
    public async Task Child_that_receives_after_its_readiness_an_effect_not_later_than_the_state_it_holds_becomes_Faulted_and_serves_nothing()
    {
        var recorded = await RecordLoadAndOneReplaceAsync();
        var replace = recorded[^1];
        var replayed = replace.WithPayload(TransactionPayload.Make(TransactionPayload.SequenceOf(replace) + 1, TransactionPayload.EffectsOf(replace)));

        Assert.Equal(TransactionPayload.Write, TransactionPayload.EffectsOf(replace)[0]);
        Assert.Equal(TransactionPayload.SequenceOf(replace), BinaryPrimitives.ReadUInt64LittleEndian(TransactionPayload.EffectsOf(replace).AsSpan(TransactionPayload.WriteLastMutationAt)));

        await using var master = ScriptedMaster.Start([.. recorded, replayed]);
        var allocator = new CountingNativeAllocator();
        var child = EngineDatabase.Create(ReplicationNodes.ChildOptions(master.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs { Allocator = allocator });

        await child.OpenAsync().AsTask().WaitAsync(Bound);
        await master.ChildEndedAsync();

        Assert.Equal(LatticeDatabaseStateEnum.Faulted, child.State);
        await NotReady.ExpectEveryOperationUnavailableAsync(child);

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    // The frames a real master sends a child that loads piece 1 and then applies one replace of it, up to
    // and with the transaction of the replace, the last of them.
    private static async Task<List<WireFrame>> RecordLoadAndOneReplaceAsync()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 16));
        await using var relay = FrameRelay.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var replaced = await master.Set<PieceSchema.Piece>().ReplaceAsync(Pieces.Versioned(1, version: 2, dataBytes: 16));
        await Eventually.AtAsync(child, replaced.Position);

        var frames = relay.FromMaster.ToList();

        Assert.Equal(FrameKindEnum.Transaction, frames[^1].Kind);
        Assert.Single(frames, frame => frame.Kind == FrameKindEnum.Transaction);

        return frames;
    }
}
