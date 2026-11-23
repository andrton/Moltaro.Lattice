namespace Moltaro.Lattice.Tests.NestedValues.Lists;

// A List keeps the order and the duplicates of the values it was given (LatticeFieldTypeEnum.List; the
// milestone contract, "Collections and strings": never reorder elements), in each of its two stored forms,
// packed as bytes when its elements hold no descriptor and as records when they do:
// - an empty List and a List of one element;
// - a List of nullable elements keeps each null in its place, and an empty String or Binary element is a
//   value, not a null;
// - a List of owned values keeps each owned value with all it holds, an absent owned value inside one
//   included, packed for an owned type of fixed-width fields (Pins) and as records for one with a String
//   and a List (Places);
// - a List of Lists keeps every inner List with its order, an empty one included;
// - a String element of 64 bytes lies inline in the block of its record, one of 65 bytes in a block of its
//   own and one above a chunk, 65,505 bytes, in chunks; each is read back whole, as is a Binary element of
//   each of those lengths and a String of each inside an owned value of a List.
// Each case is written by name and read back by name and through the class, and written through the class
// and read back by name, with the whole tree compared (NestedSchema.Describe).
public sealed class ListContentTests
{
    // The bytes of a value inline, and one more (ValueBlocks.InlineLimit); and one more than a chunk holds
    // (ValueBlocks.ChunkPayload), so that the value lies in two chunks.
    private const int Inline = 64;

    private const int AboveAChunk = 65_505;

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Empty_List_and_List_of_one_element_are_read_back_as_written_in_each_form()
    {
        var empty = NestedSchema.Full("empty");
        empty.Tags = [];
        empty.Scores = [];
        empty.Marks = [];
        empty.Notes = [];
        empty.Places = [];
        empty.Pins = [];
        empty.Grid = [];
        empty.Files = [];
        empty.Home!.Lines = [];

        var single = NestedSchema.Full("single");
        single.Tags = ["only"];
        single.Scores = [42];
        single.Marks = [null];
        single.Notes = [null];
        single.Places = [new Address { City = "Turin", Zip = 10121 }];
        single.Pins = [new Point { Lat = 45.07, Lon = 7.69 }];
        single.Grid = [[7]];
        single.Files = [[0]];
        single.Home!.Lines = ["one line"];

        await ExpectReadBackAsync(empty, single);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task List_keeps_the_supplied_order_and_its_duplicates_in_each_form()
    {
        var person = NestedSchema.Full("ordered");
        person.Tags = ["c", "a", "b", "a", "c", "c"];
        person.Scores = [5, 5, 1, 9, 1, -3];
        person.Pins = [new Point { Lat = 2, Lon = 2 }, new Point { Lat = 1, Lon = 1 }, new Point { Lat = 2, Lon = 2 }];
        person.Grid = [[3, 1, 2], [3, 1, 2], [], [1]];

        await ExpectReadBackAsync(person);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task List_of_nullable_elements_keeps_each_null_in_its_place_and_an_empty_element_is_a_value()
    {
        var person = NestedSchema.Full("nulls");
        person.Marks = [null, 0, null, null, -1, null];
        person.Notes = [null, "", null, "text", ""];
        person.Files = [[], [0], []];

        await ExpectReadBackAsync(person);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task List_of_owned_values_keeps_each_owned_value_with_all_it_holds()
    {
        var person = NestedSchema.Full("owned");
        person.Places =
        [
            new Address { City = "Lyon", Zip = 69001, Lines = ["Place Bellecour", "1"], Geo = new Point { Lat = 45.76, Lon = 4.83 } },
            new Address { City = "", Zip = 0, Lines = [], Geo = null },
            new Address { City = "Lyon", Zip = 69001, Lines = ["Place Bellecour", "1"], Geo = null },
        ];
        person.Pins = [new Point { Lat = double.NegativeInfinity, Lon = double.NaN }, new Point()];

        await ExpectReadBackAsync(person);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task List_of_Lists_keeps_every_inner_List_in_its_order()
    {
        var person = NestedSchema.Full("lists");
        person.Grid = [[], [int.MaxValue, int.MinValue], [], [1, 1, 1], [.. Enumerable.Range(0, 1_000).Reverse()]];

        await ExpectReadBackAsync(person);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Text_and_Binary_elements_of_64_and_65_bytes_and_above_a_chunk_are_read_back_whole()
    {
        // ASCII text, one byte a character in UTF-8, each length distinct in its characters.
        var inline = new string('i', Inline);
        var ownBlock = new string('o', Inline + 1);
        var chunked = new string('c', AboveAChunk);

        var person = NestedSchema.Full("lengths");
        person.Tags = [inline, ownBlock, chunked, inline];
        person.Notes = [chunked, null, ownBlock];
        person.Files = [Bytes(Inline, 1), Bytes(Inline + 1, 2), Bytes(AboveAChunk, 3)];
        person.Places = [new Address { City = ownBlock, Lines = [inline, chunked] }, new Address { City = inline }];
        person.Home!.Lines = [chunked, ownBlock, inline];

        await ExpectReadBackAsync(person);
    }

    // Writes each person by name and reads it back by name and through the class, then writes it again
    // through the class under another key and reads that back by name: each read is the tree written.
    private static async Task ExpectReadBackAsync(params Person[] persons)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();

        foreach (var person in persons)
        {
            var expected = NestedSchema.Describe(database.Schema, person);

            await byName.InsertAsync(NestedSchema.RecordOf(database.Schema, person));

            Assert.Equal(expected, NestedSchema.Describe(byName.Get(person.Code)));
            Assert.Equal(expected, NestedSchema.Describe(database.Schema, byClass.Get(person.Code)));

            var code = person.Code;
            person.Code = $"{code} through the class";
            await byClass.InsertAsync(person);

            Assert.Equal(NestedSchema.Describe(database.Schema, person), NestedSchema.Describe(byName.Get(person.Code)));
            person.Code = code;
        }
    }

    private static byte[] Bytes(int length, byte seed) => [.. Enumerable.Range(0, length).Select(index => (byte)(index * 7 + seed))];
}
