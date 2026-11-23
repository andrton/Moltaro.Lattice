using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication.Forwarding;

// What a master receives in a request is checked before it is used (brief of Epic 1, sprint S12, decided
// after the study, point 1): a body that ends before its fields, an unknown kind, an unknown type id, a
// key that is not the key in the root, and a root that fails the check a child makes of a root received
// in a load (its layout version, a descriptor against its length), and the envelope of the root: the type
// id of the request and a last mutation of 0; an insert carries no expected revision (decided at the
// review of S12, point 4). A malformed request is a defect of the peer: the master executes nothing of
// it and ends the connection, without a reply (settled by the planner with the tests of S12); once it
// holds nothing for the child any more, "code" has the revision it had. So it is with the correlation id 0, which stays the load and the stream, and with the id of a
// request of the connection that has not been answered (decided after the study, point 3); the request
// received whole before it still runs (point 12). The master holds the commit of the earlier one
// (HeldPersistence), so that it is not answered when the second arrives.
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
        new("a root whose type id is not the one of the request"),
        new("a root whose last mutation is not 0"),
        new("an insert that carries an expected revision"),
    ];

    [Theory]
    [MemberData(nameof(Damages))]
    public async Task Request_that_is_not_valid_ends_the_connection_and_is_never_executed(string damage)
    {
        await using var master = await LoadedPeer.OpenMasterAsync();
        var revision = LoadedPeer.RevisionOfCode(master);
        using var child = await LoadedPeer.ConnectAsync(master);

        await child.SendRequestAsync(1, Damaged(child, damage, revision!.Value));

        Assert.Null(await child.ReceiveReplyOrTheEndAsync(1));
        await LoadedPeer.ExpectNothingRegisteredAsync(master);
        Assert.Equal(revision, LoadedPeer.RevisionOfCode(master));
    }

    [Fact]
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

    [Fact]
    public async Task Request_with_the_correlation_id_of_a_request_not_answered_yet_ends_the_connection_and_is_not_executed()
    {
        var storage = new HeldPersistence(holding: false);
        await using var master = await LoadedPeer.OpenMasterAsync(inputs: new EngineTestInputs { Persistence = storage });

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
    private static byte[] Damaged(LoadedPeer child, string damage, LatticeRevision revision)
    {
        switch (damage)
        {
            case "an insert that carries an expected revision":
                // The revision "code" has, so that a write that took it for a replace would change "code".
                return ForwardPayload.Request(ForwardPayload.Insert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), child.Root, expectedRevision: revision);

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
                // After the type id, the last mutation sequence and the member (MarkedRoot).
                BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(RootEnvelope.Size), 999);
                break;

            case "a root with a descriptor that does not match its length":
                // Name is inline and 4 bytes long; a chunked value is longer than a block of its own holds.
                var descriptorAt = MarkedRoot.PayloadAt(root) + (MarkedRoot.FieldOf("Name").Offset - BlockHeader.Size);

                BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(descriptorAt), (uint)DescriptorKindEnum.Chunked);
                break;

            case "a root whose type id is not the one of the request":
                BinaryPrimitives.WriteUInt32LittleEndian(root, MarkedRoot.TypeId + 1);
                break;

            case "a root whose last mutation is not 0":
                BinaryPrimitives.WriteUInt64LittleEndian(root.AsSpan(sizeof(uint)), 5);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }

        return ForwardPayload.Request(ForwardPayload.Upsert, MarkedRoot.TypeId, ForwardPayload.KeyOf("code"), root);
    }
}
