using Moltaro.Lattice.Access;
using Moltaro.Lattice.TestSupport.Positions;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// The storage step of MemoryOnly assigns the positions of a group: the first group of a new database
// the sequence 1, and every group the sequences after those of the group before it, one for each of its
// writes (brief of Epic 1, sprint S09, "Positions"; Writes.MemoryOnlyStorage). The tests of the groups
// use a storage of their own (HeldCommitStorage), which assigns positions itself; here the database has
// the storage it makes for itself. Several callers write at once, each to entities of its own: every
// published write has a position of its own, and their sequences are exactly 1 to the number of writes,
// none skipped and none repeated, the last of them the position of the database. How many writes a
// group holds is not stated: under a storage step that completes at once every group is one write on
// the write path of S09, part 2 (milestone contract, "Write coordinator and revision semantics"), and
// may be more on another. A storage step that gave two writes one position, the same first sequence to
// two groups or one sequence to two writes of a group, repeats a sequence, and one that skipped a
// sequence leaves a gap; either differs from 1 to the number of writes.
public sealed class MemoryOnlyPositionsTests
{
    private const int Callers = 8;

    private const int WritesPerCaller = 200;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Writes_of_several_callers_at_once_take_the_sequences_1_to_their_number_under_the_storage_of_MemoryOnly()
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();

        var callers = Enumerable.Range(0, Callers).Select(caller => Task.Run(async () =>
        {
            var taken = new List<ulong>();

            for (var write = 0; write < WritesPerCaller; write++)
            {
                var result = await pieces.InsertAsync(PieceSchema.Of((caller * WritesPerCaller) + write, name: 10));

                taken.Add(PositionSequence.Of(result.Position));
            }

            return taken;
        }));

        var sequences = (await Task.WhenAll(callers).WaitAsync(Bound)).SelectMany(taken => taken).ToList();

        Assert.Equal(Callers * WritesPerCaller, sequences.Count);
        Assert.Equal(Enumerable.Range(1, sequences.Count).Select(sequence => (ulong)sequence), sequences.Order());
        Assert.Equal((ulong)sequences.Count, PositionSequence.Of(database.Position));
        Assert.Equal(sequences.Count, database.Counters.PublishedSequence);
    }
}
