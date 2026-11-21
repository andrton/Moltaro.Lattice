using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// A transaction of any size is delivered (brief of Epic 1, sprint S11, the planner's block of 15 November
// 2026; decision O19: a root of tens of mebibytes is ordinary): it is cut into frames, and a child puts it
// together whole before it applies it (decided after the study, point 6).
// - A write of an entity of 32 MiB on the master reaches a child whole: every byte as the master wrote
//   it, with the revision of the master.
// - A transaction larger than a frame is applied only when it is complete: a link lets the first frames
//   of a write of 1 MiB through and holds the rest (HeldLink), and the child, which has received a part
//   of it, neither holds the piece nor moves its position. Let through, the rest completes it, and the
//   child applies it.
// - A connection cut in the middle of such a transaction applies nothing of it: the child keeps serving
//   its reads at the position it had, without the piece.
// The load of each child is a few small pieces, far less than what the link passes before it holds.
public sealed class LargeChangeTests
{
    private const int ThirtyTwoMebibytes = 32 * 1024 * 1024;

    private const int OneMebibyte = 1024 * 1024;

    [Fact]
    public async Task Write_of_an_entity_of_32_MiB_on_the_master_reaches_a_child_whole()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);

        // Bytes that do not repeat, so that a part placed in the wrong place or lost would show.
        var data = new byte[ThirtyTwoMebibytes];
        new Random(42).NextBytes(data);
        var written = await master.Set<PieceSchema.Piece>().InsertAsync(new PieceSchema.Piece { Id = 3, Name = "large", Data = data, Count = 3 });

        await Eventually.AtAsync(child, written.Position);

        var piece = child.Set<PieceSchema.Piece>().Get(3L);
        Assert.NotNull(piece);
        Assert.True(data.AsSpan().SequenceEqual(piece.Data), "The Data of 32 MiB on the child differs from what the master wrote.");
        Pieces.ExpectSameAt(master, child);
    }

    [Fact]
    public async Task Transaction_larger_than_a_frame_is_applied_only_once_all_of_it_has_arrived()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var readiness = child.Position;

        var written = await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(3, version: 7, OneMebibyte));
        await link.HeldAsync();

        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(3L));

        link.Release();
        await Eventually.AtAsync(child, written.Position);

        Assert.True(Pieces.IsWhole(child.Set<PieceSchema.Piece>().Get(3L)!), "The piece of 1 MiB on the child is not the state the master wrote.");
        Pieces.ExpectSameAt(master, child);
    }

    [Fact]
    public async Task Connection_cut_in_the_middle_of_a_transaction_applies_nothing_of_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await Pieces.InsertAsync(master, 1, 2, dataBytes: 16);
        await using var link = HeldLink.Start(master.ListenUri!);
        await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildOptions(link.Address, ReplicationNodes.BeyondTheBound), new EngineTestInputs());
        var readiness = child.Position;
        var loaded = child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order().ToList();

        await master.Set<PieceSchema.Piece>().InsertAsync(Pieces.Versioned(3, version: 7, OneMebibyte));
        await link.HeldAsync();
        link.Cut();

        Assert.Equal(LatticeDatabaseStateEnum.Ready, child.State);
        Assert.Equal(readiness, child.Position);
        Assert.Null(child.Set<PieceSchema.Piece>().Get(3L));
        Assert.Equal(loaded, child.Set<PieceSchema.Piece>().Enumerate().Select(piece => piece.Id).Order());
        Assert.Equal(new string('n', 8), child.Set("Piece").Get(2L)?["Name"]);
    }
}
