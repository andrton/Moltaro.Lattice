using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Following;

// On a child a read never returns a state at a position at which the master did not have it (principle 7
// of AGENTS.md, "never a stale or approximate success"; a read with a session returns the position of its
// answer). Review round 1 of Epic 1, sprint S11 found a child that published the position of a delete
// before the delete was visible, so that a read found the key at the position of its delete.
//
// No switch of the engine places a read between two stores of an application, so the test is a bounded
// soak of the shape that showed it: the master inserts and deletes one key, again and again, and so knows
// at every position of the run whether it held the key; readers on the child read the key with a session
// all the while, and each answer, found or not, is compared with what the master held at its position.
public sealed class ReadPositionsTests
{
    private const int Pairs = 5_000;

    private const int Readers = 4;

    private const long Key = 1;

    [Fact]
    public async Task Read_on_a_child_never_returns_a_state_at_a_position_at_which_the_master_did_not_have_it()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(PieceSchema.Schema);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var pieces = master.Set<PieceSchema.Piece>();

        // Whether the master held the key at each position from the readiness of the child on.
        var held = new Dictionary<ulong, bool> { [PositionSequence.Of(child.Position)] = false };
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, Readers).Select(_ => Task.Run(() => ReadAsync(child, stop.Token))).ToArray();

        for (var pair = 0; pair < Pairs; pair++)
        {
            var inserted = await pieces.InsertAsync(PieceSchema.Of(Key, name: 8));

            held[PositionSequence.Of(inserted.Position)] = true;

            var deleted = await pieces.DeleteAsync(Key);

            held[PositionSequence.Of(deleted.Position)] = false;
        }

        await Eventually.AtAsync(child, master.Position);
        await stop.CancelAsync();

        var answers = (await Task.WhenAll(readers).WaitAsync(ReplicationNodes.Bound)).SelectMany(read => read).ToList();
        var wrong = answers.Where(answer => answer.Found != held[answer.Sequence]).ToList();

        Assert.NotEmpty(answers);
        Assert.True(
            wrong.Count == 0,
            $"{wrong.Count} of {answers.Count} reads on the child answered with a state the master did not have at their position, the first {(wrong.FirstOrDefault().Found ? "found" : "missed")} the key at {wrong.FirstOrDefault().Sequence}.");
    }

    // Reads the key with a session until the token ends the reads; returns every answer.
    private static async Task<List<(bool Found, ulong Sequence)>> ReadAsync(ILatticeDatabase child, CancellationToken stop)
    {
        var pieces = child.Set<PieceSchema.Piece>();
        var session = new LatticeSession();
        var answers = new List<(bool Found, ulong Sequence)>();

        while (!stop.IsCancellationRequested)
        {
            LatticeReadResult<PieceSchema.Piece> result = await pieces.GetAsync(Key, session);

            answers.Add((result.Found, PositionSequence.Of(result.Position)));
        }

        return answers;
    }
}
