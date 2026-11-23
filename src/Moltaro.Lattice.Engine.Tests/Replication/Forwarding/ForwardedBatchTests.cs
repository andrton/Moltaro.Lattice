using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// A write batch travels from a child to its master as one request (the brief of Epic 1, sprint S17, point 7;
// the shape is ForwardPayload.BatchRequest): a head with the minimum position and the count of the members,
// then each member as its kind, its type, its expected revision, its key and its root in the load format,
// delimited by its length. The master checks every member and the batch as a whole before anything of it is
// used, and submits it as one write, all or nothing; a request that is not valid is a defect of the peer,
// and the master executes nothing of it and ends the connection without a reply, as for a single write
// (MalformedRequestTests): a member that names an entity a member before it names, a count of no member, a
// count the members do not reach, a member whose root goes on past its length, and a member that is itself a
// batch. A request in the shape is executed whole. On the way back:
// - a refusal of a batch for a unique index carries the number of the failing member, in whose entity type
//   the child resolves the index; a reply of UniqueConstraintViolation to a batch without a number, or with
//   one beyond its members, is a reply that is not valid, so the child ends its connection, serves its reads
//   Ready, and the batch ends in IndeterminateOutcome, since it was sent whole (MalformedReplyTests);
// - a batch whose body is above the memory limit of the master is refused at its first frame with
//   WriteRejected and Limit Memory, the master is as it was, and the connection goes on.
// The hand-made child is a LoadedPeer of a master of MarkedRoot; the real children follow a master of
// UniqueSchema, whose ByLogin has the identity 1.
public sealed class ForwardedBatchTests
{
    private const int Mebibyte = 1024 * 1024;

    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("a member that names the entity of a member before it"),
        new("a count of no member"),
        new("a count the members do not reach"),
        new("a member whose root goes on past its length"),
        new("a member that is itself a batch"),
    ];

    public static IEnumerable<TheoryDataRow<string>> NumbersOfAUniqueRefusal =>
    [
        new("no number"),
        new("a number beyond the members"),
    ];

    [Fact(Skip = SkipReason.BatchThroughAChild)]
    public async Task Batch_request_in_its_shape_is_executed_whole_with_one_reply()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);

        await child.SendRequestAsync(1, ForwardPayload.BatchRequest(
        [
            (ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root),
            (ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("absent"), []),
        ]));

        var reply = await child.ReceiveReplyAsync(1);

        Assert.Null(reply.Refusal);
        Assert.True(reply.Changed);
        Assert.Equal(reply.Position, master.Position);
        Assert.NotEqual(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Theory(Skip = SkipReason.BatchThroughAChild)]
    [MemberData(nameof(Damages))]
    public async Task Batch_request_that_is_not_valid_ends_the_connection_and_nothing_of_it_is_executed(string damage)
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);
        var upsert = (ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root);
        var deleteOther = (ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("other"), Array.Empty<byte>());

        var body = damage switch
        {
            "a member that names the entity of a member before it" => ForwardPayload.BatchRequest([upsert, (ForwardPayload.Delete, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), [])]),
            "a count of no member" => ForwardPayload.BatchRequest([], count: 0),
            "a count the members do not reach" => ForwardPayload.BatchRequest([upsert, deleteOther], count: 3),
            "a member whose root goes on past its length" => ForwardPayload.BatchRequest([upsert], rootLength: (ulong)child.Root.Length - 1),
            "a member that is itself a batch" => ForwardPayload.BatchRequest([deleteOther, (ForwardPayload.Batch, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root)]),
            _ => throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage."),
        };

        await child.SendRequestAsync(1, body);

        Assert.Null(await child.ReceiveReplyOrTheEndAsync(1));
        await LoadedPeer.ExpectNothingRegisteredAsync(master);
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Theory(Skip = SkipReason.BatchThroughAChild)]
    [MemberData(nameof(NumbersOfAUniqueRefusal))]
    public async Task Reply_to_a_batch_refused_for_a_unique_index_without_a_number_of_its_members_ends_the_connection_of_the_child(string number)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema);
        await master.Set<UniqueSchema.Account>().InsertAsync(UniqueSchema.Of(1, "ada"));
        await using var relay = FrameRelay.Start(master.ListenUri!, frame =>
            frame.Kind == ForwardPayload.WriteReply
                ? ForwardPayload.Frames(ForwardPayload.WriteReply, frame.CorrelationId, frame.StreamSequence, ForwardPayload.Refusal(
                    LatticeErrorEnum.UniqueConstraintViolation, "Taken.", null, number == "no number" ? null : 2, null, indexId: 1))
                : [frame]);
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        var batch = child.CreateWriteBatch();
        batch.Insert(UniqueSchema.Of(2, "grace"));
        batch.Insert(UniqueSchema.Of(3, "ada"));

        var exception = await Assert.ThrowsAsync<LatticeException>(async () => await batch.CommitAsync());
        await relay.ChildEndedAsync();

        Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, exception.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
    }

    // Three accounts of 4 MiB each, above the memory limit of a master of two segments of 4 MiB.
    [Fact(Skip = SkipReason.BatchThroughAChild)]
    public async Task Batch_whose_body_is_above_the_memory_limit_of_the_master_is_refused_at_its_first_frame_and_the_connection_goes_on()
    {
        var arena = new ArenaOptions { MemoryLimit = 2L * new ArenaOptions().SegmentSize };
        await using var master = await ReplicationNodes.OpenMasterAsync(UniqueSchema.Schema, new EngineTestInputs { Arena = arena });
        await master.Set<UniqueSchema.Account>().InsertAsync(UniqueSchema.Of(1, "ada"));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var before = master.Position;

        var large = child.CreateWriteBatch();

        for (var id = 2L; id <= 4; id++)
        {
            large.Insert(UniqueSchema.Of(id, $"large {id}", data: 4 * Mebibyte));
        }

        var refused = await Assert.ThrowsAsync<LatticeException>(async () => await large.CommitAsync().AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Equal(before, master.Position);
        Assert.Null(master.Set<UniqueSchema.Account>().Get(2L));

        var small = child.CreateWriteBatch();
        small.Insert(UniqueSchema.Of(2, "grace"));
        small.Insert(UniqueSchema.Of(3, "alan"));
        var result = await small.CommitAsync().AsTask().WaitAsync(Bound);

        Assert.Equal(result.Position, child.Position);
        Assert.NotNull(master.Set<UniqueSchema.Account>().Get(3L));
    }
}
