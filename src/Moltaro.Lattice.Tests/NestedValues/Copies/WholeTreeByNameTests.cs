using System.Globalization;
using Moltaro.Lattice.TestSupport.Concurrency;

namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// One protection covers an entity and all it owns: a read by name copies one written state of the whole tree,
// never a tree half replaced, by key and by a search (docs/decisions/memory-and-access.md;
// docs/design/arena-protocol.md, "The reads of an entity table"). A state is checked by its stamp: in
// Stamped(n) every value at every depth is made from n, the number of the write, so a tree that mixes two
// writes holds two numbers. Home is present for an even n and absent for an odd one, and the Lists change
// their lengths with n, the texts crossing from inline to a block of their own and to chunks, so that a
// replace changes the shape of the tree and not only its values. One writer replaces two entities in turn,
// readers on threads of their own read until it is done (ReadersAndWriter), half of them by key and half by a
// search through ByName; what is asserted does not depend on how the threads interleave.
public sealed class WholeTreeByNameTests
{
    private const int Readers = 4;

    private const int Replaces = 200;

    [Fact]
    public async Task Reads_by_key_and_by_a_search_return_one_whole_tree_by_name_while_the_entities_are_being_replaced()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");

        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, Stamped("gauge", 0)));
        await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, Stamped("other", 0)));

        await ReadersAndWriter.RunAsync(
            Readers,
            reader => reader % 2 == 0
                ? () => ExpectOneState(byName.Get("gauge")!)
                : () => Assert.All(byName.Index("ByName").Find(IndexKey.All), ExpectOneState),
            async () =>
            {
                for (var number = 1; number <= Replaces; number++)
                {
                    await byName.ReplaceAsync(NestedSchema.RecordOf(database.Schema, Stamped(number % 2 == 0 ? "gauge" : "other", number)));
                }
            });
    }

    private static Person Stamped(string code, int number)
    {
        var text = $"{number}" + new string('.', (number % 3) switch { 0 => 10, 1 => 100, _ => 70_000 });
        var length = number % 5;

        Address AddressOf() => new() { City = text, Zip = number, Lines = [text], Geo = new Point { Lat = number, Lon = -number } };

        return new Person
        {
            Code = code,
            Name = $"{code} {number}",
            Home = number % 2 == 0 ? AddressOf() : null,
            Work = AddressOf(),
            Tags = [.. Enumerable.Repeat(text, 1 + length)],
            Scores = [.. Enumerable.Repeat((long)number, 1 + (number % 600))],
            Marks = [number, null, number],
            Notes = [null, text],
            Places = [.. Enumerable.Range(0, length).Select(_ => AddressOf())],
            Pins = [new Point { Lat = number, Lon = number }],
            Grid = [[number], [.. Enumerable.Repeat(number, length)]],
            Manager = text,
            Photo = [(byte)number, (byte)number],
            Files = [[(byte)number], [.. Enumerable.Repeat((byte)number, 70_000 * (number % 2))]],
        };
    }

    // The record is the whole tree of the write its Name carries the number of.
    private static void ExpectOneState(LatticeRecord record)
    {
        var name = record.Get<string>("Name")!;
        var number = int.Parse(name[(name.LastIndexOf(' ') + 1)..], CultureInfo.InvariantCulture);
        var expected = NestedSchema.RecordOf(NestedSchema.Schema, Stamped(record.Get<string>("Code")!, number));

        Assert.Equal(NestedSchema.Describe(expected), NestedSchema.Describe(record));
    }
}
