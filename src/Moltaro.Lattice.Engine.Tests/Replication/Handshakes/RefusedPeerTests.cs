using System.Buffers;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Handshakes;

// The handshake checks that a peer is compatible (brief of Epic 1, sprint S10, Acceptance; the planner's
// blocks of 15 November 2026, "Identity", "Byte order" and "The version of the case table", and of
// 17 November, point 1): a peer whose Hello names another database, another history, another schema
// fingerprint, another wire format, the wire format 3 of a node before the reply of a forwarded write named
// the index of a refusal for a unique index, another layout format, among them the layout version 1 of the form
// of a root before an indexed value longer than 64 bytes was stored as a text entry, the byte order of
// a big-endian machine (bit 16 of the layout format, as the implementer of S10 encodes it) or another
// version of the case table, or the role of a master, is refused with the matching reason — Database, History, Schema,
// Format or Role — before any data frame is sent, and nothing of it is registered on the master
// (NothingRegistered). A real child cannot send most of these, so the test sends a Hello it
// makes itself, through a connection of its own (Peer): the Hello of a real child that holds nothing yet,
// recorded on its way to the master (Handshake), with one value changed, and the identity of the master
// taken from its reply where the value has to be the master's for the check of the next one to be
// reached. The master gives a peer far more than the bound to introduce itself, so that within the bound
// only its refusal ends the connection.
public sealed class RefusedPeerTests
{
    public static IEnumerable<TheoryDataRow<string, string>> Differences =>
    [
        new("another database", "Database"),
        new("another history", "History"),
        new("another schema", "Schema"),
        new("another wire format", "Format"),
        new("the wire format 3", "Format"),
        new("another layout format", "Format"),
        new("the layout version 1", "Format"),
        new("the byte order of a big-endian machine", "Format"),
        new("another version of the case table", "Format"),
        new("the role of a master", "Role"),
    ];

    [Theory]
    [MemberData(nameof(Differences))]
    public async Task Peer_whose_Hello_differs_is_refused_with_the_matching_reason_before_any_data_and_leaves_nothing_registered(string difference, string reason)
    {
        await using var master = await ReplicationNodes.OpenPatientMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        var handshake = await Handshake.RecordAsync(master);
        using var peer = await Peer.ConnectAsync(master.ListenUri!);

        await peer.SendAsync(handshake.HelloWith(Changed(handshake, difference)));
        var received = await peer.ReceiveUntilTheEndAsync();

        var refusal = Assert.Single(received, frame => frame.Kind == FrameKindEnum.Refuse);
        Assert.Equal(Enum.Parse<RefuseReasonEnum>(reason), FrameCodec.ReadRefuseBody(new ReadOnlySequence<byte>(refusal.Payload)));
        Assert.DoesNotContain(received, frame => frame.Kind is FrameKindEnum.HelloReply or FrameKindEnum.Schema or FrameKindEnum.Blocks or FrameKindEnum.LoadEnd);

        await NothingRegistered.ExpectAsync(master, 100);
    }

    // The Hello of the child with the one difference.
    private static HelloBody Changed(Handshake handshake, string difference)
    {
        var child = handshake.Child;
        var master = handshake.Master;

        return difference switch
        {
            "another database" => child with { DatabaseId = Guid.NewGuid() },
            "another history" => child with { DatabaseId = master.DatabaseId, History = master.History + 1, SchemaFingerprint = master.SchemaFingerprint },
            "another schema" => child with { DatabaseId = master.DatabaseId, History = master.History, SchemaFingerprint = OtherSchema.Fingerprint },
            "another wire format" => child with { WireFormat = child.WireFormat + 1 },

            // The format before the refusal of a forwarded write carried the index of a unique index it
            // would have violated.
            "the wire format 3" => child with { WireFormat = 3 },
            "another layout format" => child with { LayoutFormat = child.LayoutFormat + 1 },

            // The layout version is bits 0 to 15 of the layout format. Version 1 is the form of a root
            // before a field that is part of an index stored a value longer than 64 bytes as a text entry:
            // a node of that form stores roots otherwise and is refused. The byte order is kept.
            "the layout version 1" => child with { LayoutFormat = (child.LayoutFormat & ~0xFFFF) | 1 },

            // Bit 16, which a big-endian node sets, beside the layout version of this node; the nodes of the
            // tests are little-endian.
            "the byte order of a big-endian machine" => child with { LayoutFormat = child.LayoutFormat | Protocol.BigEndianLayout },
            "another version of the case table" => child with { CaseTableVersion = child.CaseTableVersion + 1 },
            "the role of a master" => child with { Role = LatticeRoleEnum.Master },
            _ => throw new ArgumentOutOfRangeException(nameof(difference), difference, "No such difference."),
        };
    }

    private static LatticeSchema OtherSchema { get; } = LatticeSchema.Compile("""
        { "EntityTypes": [ { "Id": 1, "Name": "Other", "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" } ], "PrimaryKey": [1] } ] }
        """);
}
