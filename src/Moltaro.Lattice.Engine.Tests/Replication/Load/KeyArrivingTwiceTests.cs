using System.Buffers.Binary;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Replication.Load;

// A key that arrives twice in a load is resolved by the rule a child applies a copy and an effect by,
// not refused (brief of Epic 1, sprint S11, decided after the study, point 7, which replaces the refusal
// of S10): a state of a key at sequence s is skipped when the child holds that key with a last mutation
// not earlier than s, and otherwise replaces what it holds. Under writes this happens when the capture
// copies an entity and later meets the same key again in a slot it walks afterwards; here a relay
// (FrameRelay) makes it happen at once: in the place of the one root of piece 1 it sends two states of
// that key in one Blocks frame — the state the master holds, and an older one recorded from an earlier
// load of the same master — in either order, and a LoadEnd that announces the two roots sent. Either
// way the child is Ready and holds the later state, with the revision of the master, and the key once.
public sealed class KeyArrivingTwiceTests
{
    public static IEnumerable<TheoryDataRow<string>> Orders =>
    [
        new("the older state first"),
        new("the newer state first"),
    ];

    [Theory(Skip = ChildFollowing.NotImplemented)]
    [MemberData(nameof(Orders))]
    public async Task Key_that_arrives_twice_in_a_load_is_resolved_by_the_later_state_and_not_refused(string order)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(Pieces.Versioned(1, version: 1, dataBytes: 16));
        var older = await RecordOnlyRootAsync(master);
        await pieces.ReplaceAsync(Pieces.Versioned(1, version: 2, dataBytes: 16));

        await using var relay = FrameRelay.Start(master.ListenUri!, frame => frame.Kind switch
        {
            FrameKindEnum.Blocks => [frame.WithPayload(order == "the older state first" ? RootsFrame(older, OnlyRootIn(frame)) : RootsFrame(OnlyRootIn(frame), older))],
            FrameKindEnum.LoadEnd => [frame.WithPayload((LoadEndPayload.Of(frame) with { Roots = 2 }).ToBytes())],
            _ => [frame],
        });
        await using var child = await ReplicationNodes.OpenChildAsync(relay.Address);

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal("v2", child.Set<PieceSchema.Piece>().Get(1L)?.Name);
        Assert.Single(child.Set<PieceSchema.Piece>().Enumerate());
        Pieces.ExpectSameAt(master, child);
    }

    // The bytes of the one root of the master, as a load sends them: a child loads through a relay, and
    // the root is read from the one Blocks frame of its load.
    private static async Task<byte[]> RecordOnlyRootAsync(EngineDatabase master)
    {
        await using var relay = FrameRelay.Start(master.ListenUri!);

        await using (await ReplicationNodes.OpenChildAsync(relay.Address))
        {
        }

        return OnlyRootIn(Assert.Single(relay.FromMaster, frame => frame.Kind == FrameKindEnum.Blocks));
    }

    // The root of a Blocks frame of one small root: its count, 1, a UInt32; its length, a UInt64; its
    // bytes (Replication.Load.RootFramer).
    private static byte[] OnlyRootIn(WireFrame frame)
    {
        var payload = frame.Payload;

        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(payload));
        Assert.Equal((ulong)(payload.Length - sizeof(uint) - sizeof(ulong)), BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(sizeof(uint))));

        return payload[(sizeof(uint) + sizeof(ulong))..];
    }

    // The payload of a Blocks frame of the roots, in their order.
    private static byte[] RootsFrame(params byte[][] roots)
    {
        var payload = new List<byte>();
        Span<byte> number = stackalloc byte[sizeof(ulong)];

        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)roots.Length);
        payload.AddRange(number[..sizeof(uint)]);

        foreach (var root in roots)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(number, (ulong)root.Length);
            payload.AddRange(number);
            payload.AddRange(root);
        }

        return [.. payload];
    }
}
