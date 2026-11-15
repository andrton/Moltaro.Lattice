using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// A read by class leaves no reference to what it returned in the pools it rents its scratch from
// (the coordinator's decision after the second review of Epic 1, sprint S06): once the caller has
// dropped the returned object, its strings and arrays are collected, whichever form the read took.
// The array of values a read rents references them until it goes back to the pool, cleared; an
// array that went back uncleared would keep them alive, with values of up to 268,173,376 bytes each,
// until it is rented again.
//
// The test asks whether anything at all still references the values after a full garbage
// collection, the pool included, so it depends on no detail of the pool: a reference that a read
// leaves anywhere keeps its value alive and fails the test. Only a pool that dropped the array under
// memory pressure before the collection could hide one.
public sealed class ReadScratchTests
{
    public static TheoryData<string> Reads => ["Get", "TryGet with its revision", "GetAsync", "Enumerate", "EnumerateAsync"];

    [Theory]
    [MemberData(nameof(Reads))]
    public async Task Read_by_class_leaves_no_reference_to_what_it_returned_behind(string read)
    {
        await using var database = await PieceSchema.OpenAsync();
        var pieces = database.Set<PieceSchema.Piece>();

        // Each String and Binary value of its own: inline, in a block of its own and in chunks.
        await pieces.InsertAsync(PieceSchema.Of(1, name: 100, note: 4_000, data: 70_000));

        var returned = await ReadAndDropAsync(pieces, read);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.All(returned, value => Assert.False(value.IsAlive));
    }

    // Reads the piece and keeps only weak references to its String and Binary values, which the read
    // made for this object alone. Not inlined, so that nothing of the read stays on the stack of the
    // test.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ReadAndDropAsync(ILatticeSet<PieceSchema.Piece> pieces, string read)
    {
        var piece = read switch
        {
            "Get" => pieces.Get(1L),
            "TryGet with its revision" => pieces.TryGet(1L, out var found, out _) ? found : null,
            "GetAsync" => (await pieces.GetAsync(1L, new LatticeSession())).Entity,
            "Enumerate" => pieces.Enumerate().Single(),
            "EnumerateAsync" => (await pieces.EnumerateAsync(new LatticeSession()).ToListAsync()).Single(),
            _ => throw new ArgumentOutOfRangeException(nameof(read), read, "No such read."),
        };

        Assert.NotNull(piece);
        Assert.Equal(70_000, piece.Data!.Length);

        return [new WeakReference(piece.Name), new WeakReference(piece.Note), new WeakReference(piece.Data)];
    }
}
