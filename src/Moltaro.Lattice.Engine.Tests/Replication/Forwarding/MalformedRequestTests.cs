using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a master receives in a request is checked before it is used (brief of Epic 1, sprint S12, decided
// after the study, point 1): a body that ForwardEncoding.ReadRequest refuses with ValidationFailed — one
// that ends before its fields, an unknown kind, an unknown type id —, a key that is not the key in the
// root, and a root that fails the check a child makes of a root received in a load (its layout version, a
// descriptor against its length). Such a request is never executed. Whether the master then answers it
// with the refusal, ValidationFailed, and serves the next request of the connection, or ends the
// connection, the brief does not say (a question to the coordinator); the tests state what is decided and
// accept either: a reply is a refusal with ValidationFailed and the connection then serves the next
// request; an end leaves nothing registered. Either way "code" keeps its revision.
//
// The correlation id pairs a reply with its request, and 0 stays the load and the stream (decided after
// the study, point 3): a request with the id 0, and one with the id of a request of the connection that has
// not been answered, cannot be answered so that the child can tell the two apart. The master ends the
// connection and executes nothing of that request; the request received whole before it still runs
// (point 12). The master holds the commit of the earlier one (HeldCommitStorage), so that it is not
// answered when the second arrives.
public sealed class MalformedRequestTests
{
    public static IEnumerable<TheoryDataRow<string>> Damages =>
    [
        new("a body that ends in its key"),
        new("an unknown kind"),
        new("an unknown type id"),
        new("a key that is not the key in its root"),
        new("a root of an unknown layout version"),
        new("a root with a descriptor that does not match its length"),
    ];

    [Theory(Skip = SkipReason.BeforeSprint12)]
    [MemberData(nameof(Damages))]
    public async Task Request_that_is_not_valid_is_never_executed(string damage)
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);

        await child.SendRequestAsync(1, Damaged(child, damage));
        var reply = await child.ReceiveReplyOrTheEndAsync(1);

        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));

        if (reply is null)
        {
            await LoadedPeer.ExpectNothingRegisteredAsync(master);
            Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));

            return;
        }

        Assert.Equal(LatticeErrorEnum.ValidationFailed, reply.Refusal?.Error);

        await child.SendRequestAsync(2, LoadedPeer.DeleteOfCode());
        Assert.True((await child.ReceiveReplyAsync(2)).Changed);
        Assert.Null(LoadedPeer.RevisionOfCode(master));
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Request_with_the_correlation_id_0_ends_the_connection_and_is_not_executed()
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);

        await child.SendRequestAsync(0, LoadedPeer.DeleteOfCode());

        Assert.Null(await child.ReceiveReplyOrTheEndAsync(0));
        await LoadedPeer.ExpectNothingRegisteredAsync(master);
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Fact(Skip = SkipReason.BeforeSprint12)]
    public async Task Request_with_the_correlation_id_of_a_request_not_answered_yet_ends_the_connection_and_is_not_executed()
    {
        var storage = new HeldCommitStorage(holding: false);
        await using var master = await LoadedPeer.OpenMasterAsync(inputs: new EngineTestInputs { CommitStorage = storage });

        try
        {
            using var child = await LoadedPeer.ConnectAsync(master);
            var revision = LoadedPeer.RevisionOfCode(master);

            storage.Hold();
            await child.SendRequestAsync(1, child.UpsertOfCode());
            await storage.NextAsync();
            await child.SendRequestAsync(1, LoadedPeer.DeleteOfCode());

            Assert.Null(await child.ReceiveReplyOrTheEndAsync(1));

            storage.ReleaseAll();
            await LoadedPeer.ExpectNothingRegisteredAsync(master);

            // The upsert, received whole, ran; the delete did not.
            var upserted = LoadedPeer.RevisionOfCode(master);

            Assert.NotNull(upserted);
            Assert.NotEqual(revision, upserted);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    // The body of a request of "code" with the damage of the row.
    private static byte[] Damaged(LoadedPeer child, string damage)
    {
        switch (damage)
        {
            case "a body that ends in its key":
                return LoadedPeer.DeleteOfCode()[..^1];

            case "an unknown kind":
                return ForwardPayload.Request(9, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root);

            case "an unknown type id":
                return ForwardPayload.Request(ForwardPayload.Delete, MarkedRoot.TypeId + 1, ForwardPayload.KeyOf("code"), []);

            case "a key that is not the key in its root":
                return ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("other"), child.Root);
        }

        var root = child.Root.ToArray();

        switch (damage)
        {
            case "a root of an unknown layout version":
                // After the type id and the last mutation sequence (MarkedRoot).
                BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(sizeof(uint) + sizeof(ulong)), 999);
                break;

            case "a root with a descriptor that does not match its length":
                // Name is inline and 4 bytes long; a chunked value is longer than a block of its own holds.
                var descriptorAt = MarkedRoot.PayloadAt(root) + (MarkedRoot.FieldOf("Name").Offset - BlockHeader.Size);

                BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(descriptorAt), (uint)DescriptorKindEnum.Chunked);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), root);
    }
}
