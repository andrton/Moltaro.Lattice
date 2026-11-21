using System.Globalization;

namespace Moltaro.Lattice.Tests.Indexes.Walk;

// A walk beside a writer (docs/decisions/queries.md, Q07.3.5: a live traversal returns no identity twice
// and every unchanged match, and the order it was asked for; docs/design/ordered-index.md, "How a range
// will be walked"). Readers walk the index ByAB of Row by the leading value A = 1 alone and with the range
// of B from "b" to "u", forwards and in reverse, through the lazy Find and through FindAsync, while one
// writer:
// - moves rows of A = 1 from one B to the next, through values below the range, inside it and above it,
//   so that a key moves ahead of a walk and behind it;
// - inserts and deletes rows of the key (1, "m"), the one key of a long run of rows that never change, so
//   that the leaves of that run split and merge under the walk;
// - deletes rows of the key (1, "h") and inserts them again.
// Each answer holds:
// - no row twice, a row moved ahead of the walk and a row deleted and created again included;
// - every row that never changes, those of the run of (1, "m") among them;
// - only rows that match the search, each in a state the writer wrote: its fields agree as that state set
//   them;
// - its keys in the order of the walk: B never goes down in the index order, never up in its reverse.
// The readers run on threads of their own until the writer has made its writes; nothing sleeps.
public sealed class WalkUnderChangeTests
{
    // The rows of the long run of (1, "m"), more than a leaf of the index holds, and the rows of other keys
    // inside the range that never change either.
    private const int Run = 300;

    private const int Others = 20;

    private const int Moving = 32;

    private const int Churning = 32;

    private const int Recreated = 16;

    private const int Writes = 3_000;

    // The values of B a moving row takes in turn: below the range, inside it, above it.
    private static readonly string[] MovingKeys = ["a", "e", "m", "r", "v", "c", "t", "z"];

    private static int Readers => Math.Clamp(Environment.ProcessorCount - 2, 2, 6);

    [Fact]
    public async Task Walk_beside_a_writer_returns_no_row_twice_every_unchanged_match_and_its_keys_in_order_in_both_directions()
    {
        await using var database = await WalkSchema.OpenAsync("Utf8");
        var rows = database.Set<Row>();

        for (var id = 1L; id <= Run; id++)
        {
            await rows.InsertAsync(Stable(id, "m"));
        }

        for (var id = Run + 1L; id <= Run + Others; id++)
        {
            await rows.InsertAsync(Stable(id, id % 2 == 0 ? "d" : "s"));
        }

        for (var id = 1001L; id < 1001 + Moving; id++)
        {
            await rows.InsertAsync(MovingState(id, 0));
        }

        for (var id = 3001L; id < 3001 + Recreated; id++)
        {
            await rows.InsertAsync(RecreatedState(id, 0));
        }

        await ReadersAndWriter.RunAsync(Readers, reader => ReaderOf(rows, reader), () => OwnThread.Run(() => Write(rows)));
    }

    // The walks of one reader, in turn of their four forms.
    private static Action ReaderOf(ILatticeSet<Row> rows, int reader)
    {
        var byAB = rows.Index("ByAB");
        var walk = (long)reader;

        return () =>
        {
            var form = walk++ % 4;
            var ranged = form % 2 == 0;
            var key = ranged ? IndexKey.Equal<int?>(1).Range(IndexBound.Inclusive("b"), IndexBound.Inclusive("u")) : IndexKey.Equal<int?>(1);
            var reverse = form >= 2;
            var found = reverse
                ? OwnThread.Wait(byAB.FindAsync(key, new LatticeFindOptions { Reverse = true })).Items.ToList()
                : walk % 3 == 0 ? OwnThread.Wait(byAB.FindAsync(key, new LatticeFindOptions())).Items.ToList() : [.. byAB.Find(key)];
            var name = $"{(ranged ? "the range from \"b\" to \"u\"" : "the leading value")}{(reverse ? " in reverse" : "")}";

            Assert.True(found.Count == found.Select(row => row.Id).Distinct().Count(), $"The walk of {name} returned a row twice.");
            Assert.All(found, row => Assert.True(row.A == 1 && (!ranged || (string.CompareOrdinal(row.B, "b") >= 0 && string.CompareOrdinal(row.B, "u") <= 0)), $"The walk of {name} returned {row}, which does not match it."));
            Assert.All(found, ExpectWritten);

            var ids = found.Select(row => row.Id).ToHashSet();

            for (var id = 1L; id <= Run + Others; id++)
            {
                Assert.True(ids.Contains(id), $"The walk of {name} did not return the unchanged row {id}.");
            }

            for (var place = 1; place < found.Count; place++)
            {
                var order = string.CompareOrdinal(found[place - 1].B, found[place].B);

                Assert.True(reverse ? order >= 0 : order <= 0, $"The walk of {name} returned {found[place]} after {found[place - 1]}.");
            }
        };
    }

    // The writer: moves a row to its next state at every write; every fourth write inserts a row of the
    // run of (1, "m") or deletes it again; every tenth deletes a row of (1, "h") and inserts it again.
    private static void Write(ILatticeSet<Row> rows)
    {
        for (var write = 1; write <= Writes; write++)
        {
            var moving = 1001L + ((write - 1) % Moving);

            OwnThread.Wait(rows.ReplaceAsync(MovingState(moving, ((write - 1) / Moving) + 1)));

            if (write % 4 == 0)
            {
                var churning = 2001L + ((write / 4) % Churning);
                var present = (write / 4 / Churning) % 2 == 0;

                OwnThread.Wait(present ? rows.InsertAsync(Stable(churning, "m")) : rows.DeleteAsync(churning));
            }

            if (write % 10 == 0)
            {
                var recreated = 3001L + ((write / 10) % Recreated);

                OwnThread.Wait(rows.DeleteAsync(recreated));
                OwnThread.Wait(rows.InsertAsync(RecreatedState(recreated, write)));
            }
        }
    }

    // A row that never changes, of the key (1, b), or one of the run that the writer inserts and deletes.
    private static Row Stable(long id, string b) => new() { Id = id, A = 1, B = b, C = 0, D = "stable" };

    // A moving row in the state of the number: B the value of the number in turn, the number in C and in D.
    private static Row MovingState(long id, long number) => new()
    {
        Id = id,
        A = 1,
        B = MovingKeys[(int)((number + id) % MovingKeys.Length)],
        C = number,
        D = string.Create(CultureInfo.InvariantCulture, $"moving {id} {number}"),
    };

    private static Row RecreatedState(long id, long number) => new()
    {
        Id = id,
        A = 1,
        B = "h",
        C = number,
        D = string.Create(CultureInfo.InvariantCulture, $"again {id} {number}"),
    };

    // A row found is one state the writer wrote: its fields agree as that state set them.
    private static void ExpectWritten(Row row)
    {
        var written = row.Id switch
        {
            <= Run => Stable(row.Id, "m"),
            <= Run + Others => Stable(row.Id, row.Id % 2 == 0 ? "d" : "s"),
            < 2001 => MovingState(row.Id, row.C!.Value),
            < 3001 => Stable(row.Id, "m"),
            _ => RecreatedState(row.Id, row.C!.Value),
        };

        Assert.Equal((written.A, written.B, written.C, written.D), (row.A, row.B, row.C, row.D));
    }
}
