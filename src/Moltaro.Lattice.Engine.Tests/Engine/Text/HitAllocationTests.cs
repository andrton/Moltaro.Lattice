using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A write whose Dictionary text the database already holds finds its entry in the text table and
// allocates nothing managed for it (brief of Epic 1, sprint S13, the planner's decision after the study,
// points 3 and 10: the table is managed memory that writes use, and a hit is measured): a replace that
// moves the field between two texts the database holds allocates no more managed memory than the same
// replace of the same entity with the field Plain. A write allocates on its way through the write
// coordinator, so the test does not state a number of bytes; it compares the two, as the test of a delete
// by name does (DeleteByNameAllocationTests): each form is measured in many rounds after rounds that took
// the one-time costs of its path, and the least of its rounds counts, so that what another thread of the
// process allocates meanwhile adds to neither form. The count is that of the whole process, because a write
// may run on threads of the engine. A short text, which Plain keeps inline, and one Plain stores in a block
// of its own.
[Collection<WholeProcessAllocationCollection>]
public sealed class HitAllocationTests
{
    private const int Rounds = 50;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Shared", "ClrType": "{{typeof(Shared).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "StringStorage": "Dictionary" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Plain", "ClrType": "{{typeof(Plain).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory(Skip = SkipReason.BeforeSprint13)]
    [InlineData(9)]
    [InlineData(100)]
    public async Task Replace_to_a_text_the_database_holds_allocates_no_more_managed_memory_than_the_same_replace_of_a_Plain_field(int characters)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var shared = database.Set<Shared>();
        var plain = database.Set<Plain>();
        var first = new string('f', characters);
        var second = new string('s', characters);

        // Other entities hold both texts, so that every replace of entity 1 finds its text in the table.
        await shared.InsertAsync(new Shared { Id = 2, Text = first });
        await shared.InsertAsync(new Shared { Id = 3, Text = second });
        await shared.InsertAsync(new Shared { Id = 1, Text = first });
        await plain.InsertAsync(new Plain { Id = 1, Text = first });

        // The objects are made before anything is measured.
        Shared[] sharedStates = [new() { Id = 1, Text = second }, new() { Id = 1, Text = first }];
        Plain[] plainStates = [new() { Id = 1, Text = second }, new() { Id = 1, Text = first }];
        var leastShared = long.MaxValue;
        var leastPlain = long.MaxValue;

        // The one-time costs of both paths, then the measured rounds.
        for (var round = 0; round < 2 * Rounds; round++)
        {
            var sharedBytes = await AllocatedAsync(() => shared.ReplaceAsync(sharedStates[round % 2]));
            var plainBytes = await AllocatedAsync(() => plain.ReplaceAsync(plainStates[round % 2]));

            if (round >= Rounds)
            {
                leastShared = Math.Min(leastShared, sharedBytes);
                leastPlain = Math.Min(leastPlain, plainBytes);
            }
        }

        Assert.True(
            leastShared <= leastPlain,
            $"A replace to a Dictionary text the database holds allocated {leastShared} bytes, more than the {leastPlain} of the same replace of a Plain field.");
        Assert.Equal(first, shared.Get(1L)?.Text);
    }

    // What the process allocates while the replace runs to its outcome.
    private static async Task<long> AllocatedAsync(Func<ValueTask<LatticeWriteResult>> replace)
    {
        var before = GC.GetTotalAllocatedBytes(precise: true);
        var result = await replace();
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.True(result.Changed);

        return allocated;
    }

    private sealed class Shared
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }

    private sealed class Plain
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }
}
