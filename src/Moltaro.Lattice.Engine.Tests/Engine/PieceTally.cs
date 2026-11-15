using System.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// What the pieces of a database hold in its arena, kept by the test apart from the engine. The test
// tells it every write that succeeded; it keeps, for every piece, the encoded lengths of its String
// and Binary values that are not null, and adds up from them, by the rules of the brief of S05 (the
// fixed part and where a value goes, StoredBytes.Of), the live bytes and the class slack the arena
// has to report.
internal sealed class PieceTally
{
    private readonly Dictionary<long, StoredBytes> _pieces = [];

    // The piece with this key was inserted, replaced or upserted with these values.
    public void Written(PieceSchema.Piece piece)
    {
        Span<int> lengths = stackalloc int[3];
        var count = 0;

        lengths[count++] = Encoding.UTF8.GetByteCount(piece.Name);

        if (piece.Note is { } note)
        {
            lengths[count++] = Encoding.UTF8.GetByteCount(note);
        }

        if (piece.Data is { } data)
        {
            lengths[count++] = data.Length;
        }

        _pieces[piece.Id] = StoredBytes.Of(PieceSchema.FixedSize, lengths[..count]);
    }

    public void Deleted(long key) => _pieces.Remove(key);

    // When every write has returned and no read is in progress: nothing is prepared or retired, the
    // live bytes and the class slack are those of the pieces, and the table of root entries has one
    // entry for every piece.
    public void ExpectHeldBy(EngineDatabase database)
    {
        var counters = database.Counters;
        var total = StoredBytes.Zero;

        foreach (var stored in _pieces.Values)
        {
            total += stored;
        }

        Assert.Equal(0, counters.Arena.PreparedBytes);
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(total, new StoredBytes(counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes));
        Assert.Equal(_pieces.Count, counters.RootEntries);
    }
}
