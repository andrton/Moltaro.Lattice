namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// The entities most tests of replication store: pieces of PieceSchema, inserted on the master before a
// child connects, and the comparison of what a child holds with what its master holds, through the class.
internal static class Pieces
{
    // Small pieces first and large ones after them: 16 of a few bytes and 8 with a Binary value of
    // 1 MiB, thirty-two times what a link passes before it holds (HeldLink.Passed).
    private const int Small = 16;

    private const int Large = 8;

    private const int LargeData = 1024 * 1024;

    // Inserts the given number of pieces with the identities from first, each with a Binary value of the
    // given size.
    public static async Task InsertAsync(EngineDatabase master, long first, int count, int dataBytes)
    {
        var pieces = master.Set<PieceSchema.Piece>();

        for (var id = first; id < first + count; id++)
        {
            await pieces.InsertAsync(PieceSchema.Of(id, name: 8, data: dataBytes));
        }
    }

    // Inserts pieces 1 to 24, of 8 MiB in all: a child that loads them through a HeldLink is still
    // loading when the link holds.
    public static async Task InsertBeyondALinkAsync(EngineDatabase master)
    {
        await InsertAsync(master, 1, Small, dataBytes: 16);
        await InsertAsync(master, 1 + Small, Large, LargeData);
    }

    // The child holds the pieces the master holds, with the same values.
    public static void ExpectSame(ILatticeDatabase master, ILatticeDatabase child)
    {
        var expected = master.Set<PieceSchema.Piece>().Enumerate().OrderBy(piece => piece.Id).ToList();
        var actual = child.Set<PieceSchema.Piece>().Enumerate().OrderBy(piece => piece.Id).ToList();

        Assert.Equal(expected.Select(piece => piece.Id), actual.Select(piece => piece.Id));

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Name, actual[index].Name);
            Assert.Equal(expected[index].Note, actual[index].Note);
            Assert.Equal(expected[index].Count, actual[index].Count);
            Assert.True(
                expected[index].Data.AsSpan().SequenceEqual(actual[index].Data),
                $"The Data of piece {expected[index].Id} on the child differs from the master's.");
        }
    }
}
