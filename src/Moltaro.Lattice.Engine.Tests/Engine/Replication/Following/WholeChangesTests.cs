using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Positions;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// Changes after readiness are applied in order, each whole (brief of Epic 1, sprint S11, Acceptance), and
// reads on the child are never stopped by an application (the planner's block of 17 November 2026,
// point 4). The master replaces one piece again and again with states whose every value derives from a
// counter (Pieces.Versioned), with Data of 64 KiB, so that the transaction of each write takes more
// than one frame; readers on the child, through the class and by name, read the piece all the while.
// Every state a reader reads is one whole state the master wrote, and the states one reader reads never
// go back: a read is coherent for one entity, and the child applies the writes in their order. The
// position of the child that a reader reads never goes back either. At the end the child holds the last
// state, with the revision of the master.
public sealed class WholeChangesTests
{
    private const int Replaces = 200;

    private const int Readers = 2;

    private const int DataBytes = 64 * 1024;

    private static readonly TimeSpan Bound = ReplicationNodes.Bound;

    [Fact]
    public async Task Readers_on_a_child_see_each_change_of_the_master_whole_and_in_order()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        var pieces = master.Set<PieceSchema.Piece>();
        await pieces.InsertAsync(Pieces.Versioned(1, version: 0, DataBytes));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, Readers).Select(reader => Task.Run(() => Read(child, byName: reader % 2 == 1, stop.Token))).ToArray();

        for (var version = 1; version <= Replaces; version++)
        {
            await pieces.ReplaceAsync(Pieces.Versioned(1, version, DataBytes));
        }

        await Eventually.AtAsync(child, master.Position);
        await stop.CancelAsync();

        var seen = await Task.WhenAll(readers).WaitAsync(Bound);

        Assert.All(seen, count => Assert.True(count > 0, "A reader read nothing."));
        Assert.Equal($"v{Replaces}", child.Set<PieceSchema.Piece>().Get(1L)?.Name);
        Pieces.ExpectSameAt(master, child);
    }

    // Reads the piece until the token ends the reads and returns how many it read; fails at the first read
    // that is not a whole state or that goes back.
    private static int Read(ILatticeDatabase child, bool byName, CancellationToken stop)
    {
        var lastVersion = -1;
        var lastPosition = 0UL;
        var reads = 0;

        while (!stop.IsCancellationRequested)
        {
            var position = PositionSequence.Of(child.Position);
            var piece = byName ? FromRecord(child.Set("Piece").Get(1L)) : child.Set<PieceSchema.Piece>().Get(1L);

            Assert.NotNull(piece);
            Assert.True(Pieces.IsWhole(piece), $"A reader read a state of the piece that the master never wrote: {piece.Name} with the count {piece.Count}.");
            Assert.True(piece.Count >= lastVersion, $"A reader read version {piece.Count} after version {lastVersion}.");
            Assert.True(position >= lastPosition, $"A reader read the position {position} after {lastPosition}.");

            lastVersion = piece.Count;
            lastPosition = position;
            reads++;
        }

        return reads;
    }

    private static PieceSchema.Piece? FromRecord(LatticeRecord? record) => record is null
        ? null
        : new PieceSchema.Piece
        {
            Id = record.Get<long>("Id"),
            Name = record.Get<string>("Name")!,
            Note = record.Get<string>("Note"),
            Data = record.Get<byte[]>("Data"),
            Count = record.Get<int>("Count"),
        };
}
