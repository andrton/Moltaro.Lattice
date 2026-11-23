namespace Moltaro.Lattice.Tests.NestedValues.Lists;

// A List is read back by name as it was written, whatever it holds: no element, one element, nulls among its
// elements, owned values with their own Lists and absent owned values, Lists of Lists, and elements of every
// length, inline (64 bytes), in a block of their own (65) and in chunks (65,505), in each form of a List and
// at every depth of the person (NestedSchema).
public sealed class ListContentsByNameTests
{
    [Fact]
    public async Task Lists_empty_of_one_element_with_nulls_of_owned_values_of_Lists_and_of_every_length_are_read_back_by_name()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");

        foreach (var person in new[] { Empty(), Single(), Nulls(), Owned(), OfLists(), Lengths() })
        {
            await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, person));

            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));
        }
    }

    private static Person Empty()
    {
        var person = NestedSchema.Full("empty");

        person.Tags = [];
        person.Scores = [];
        person.Marks = [];
        person.Notes = [];
        person.Places = [];
        person.Pins = [];
        person.Grid = [];
        person.Files = [];
        person.Home!.Lines = [];

        return person;
    }

    private static Person Single()
    {
        var person = NestedSchema.Full("single");

        person.Tags = ["only"];
        person.Scores = [42];
        person.Marks = [null];
        person.Notes = [null];
        person.Places = [new Address { City = "Turin", Zip = 10121 }];
        person.Pins = [new Point { Lat = 45.07, Lon = 7.69 }];
        person.Grid = [[7]];
        person.Files = [[0]];
        person.Home!.Lines = ["one line"];

        return person;
    }

    private static Person Nulls()
    {
        var person = NestedSchema.Full("nulls");

        person.Marks = [null, 0, null, null, -1, null];
        person.Notes = [null, "", null, "text", ""];
        person.Files = [[], [0], []];

        return person;
    }

    private static Person Owned()
    {
        var person = NestedSchema.Full("owned");

        person.Places =
        [
            new Address { City = "Lyon", Zip = 69001, Lines = ["Place Bellecour", "1"], Geo = new Point { Lat = 45.76, Lon = 4.83 } },
            new Address { City = "", Zip = 0, Lines = [], Geo = null },
        ];
        person.Pins = [new Point { Lat = double.NegativeInfinity, Lon = double.NaN }, new Point()];

        return person;
    }

    private static Person OfLists()
    {
        var person = NestedSchema.Full("lists");

        person.Grid = [[], [int.MaxValue, int.MinValue], [], [1, 1, 1], [.. Enumerable.Range(0, 1_000).Reverse()]];

        return person;
    }

    private static Person Lengths()
    {
        var inline = new string('i', 64);
        var ownBlock = new string('o', 65);
        var chunked = new string('c', 65_505);
        var person = NestedSchema.Full("lengths");

        person.Tags = [inline, ownBlock, chunked, inline];
        person.Notes = [chunked, null, ownBlock];
        person.Files = [new byte[64], Enumerable.Repeat((byte)2, 65).ToArray(), Enumerable.Range(0, 65_505).Select(index => (byte)index).ToArray()];
        person.Places = [new Address { City = ownBlock, Lines = [inline, chunked] }, new Address { City = inline }];
        person.Home!.Lines = [chunked, ownBlock, inline];

        return person;
    }
}
