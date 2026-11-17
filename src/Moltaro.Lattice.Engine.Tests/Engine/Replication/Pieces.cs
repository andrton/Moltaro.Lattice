namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// The entities most tests of replication store: pieces of PieceSchema, inserted on the master before a
// child connects or written while it loads and follows, and the comparison of what a child holds with
// what its master holds, through the class.
internal static class Pieces
{
    // A piece whose every value derives from its version, so that a reader can tell one written state
    // from a mixture of two: the name "v{version}", the count the version, and Data of the given number
    // of bytes, each the low byte of the version.
    public static PieceSchema.Piece Versioned(long id, int version, int dataBytes) => new()
    {
        Id = id,
        Name = $"v{version}",
        Data = Enumerable.Repeat((byte)version, dataBytes).ToArray(),
        Count = version,
    };

    // Whether a piece read is one whole state that Versioned writes: its name, its count and every byte
    // of its Data name the same version.
    public static bool IsWhole(PieceSchema.Piece piece) =>
        piece.Name == $"v{piece.Count}" && piece.Data is { } data && data.AsSpan().IndexOfAnyExcept((byte)piece.Count) < 0;
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

    // The child is at the position of its master and holds what the master holds there: the same pieces,
    // with the same values and the same revisions (brief of Epic 1, sprint S11, the planner's block of
    // 17 November 2026, point 3). Nothing writes on the master while it compares.
    public static void ExpectSameAt(ILatticeDatabase master, ILatticeDatabase child)
    {
        Assert.Equal(master.Position, child.Position);
        ExpectSame(master, child);

        var onMaster = master.Set<PieceSchema.Piece>();
        var onChild = child.Set<PieceSchema.Piece>();

        foreach (var piece in onMaster.Enumerate())
        {
            Assert.True(onMaster.TryGet(piece.Id, out _, out var masterRevision));
            Assert.True(onChild.TryGet(piece.Id, out _, out var childRevision), $"The child has no piece {piece.Id}.");
            Assert.True(masterRevision.Equals(childRevision), $"Piece {piece.Id} has the revision {childRevision} on the child and {masterRevision} on the master.");
        }
    }
}
