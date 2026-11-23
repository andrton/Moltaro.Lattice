namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// A complete write replaces the whole tree of an entity: nothing of the old tree remains in what is read
// afterwards, whatever its shape was (docs/decisions/document-model.md, Q02; a full Save prepares a complete
// replacement without a diff, docs/design/arena-protocol.md, "Read and mutation paths"). And one protection
// covers an entity and all it owns: a read copies one written state of the whole tree, never a tree half
// replaced, by key and by a search (docs/decisions/memory-and-access.md; arena protocol, "The reads of an
// entity table").
//
// The readers check a state by its stamp: in Stamped(n) every value at every depth is made from n, the
// number of the write, so a tree that mixes two writes holds two numbers. Home is present for an even n and
// absent for an odd one, and the Lists change their lengths with n, the text of Tags crossing from inline
// to a block of its own and to chunks, so that a replace changes the shape of the tree and not only its
// values. One writer replaces, readers on threads of their own read until it is done (ReadersAndWriter);
// what is asserted does not depend on how the threads interleave.
public sealed class WholeTreeTests
{
    private const int Readers = 4;

    private const int Replaces = 200;

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Complete_write_replaces_the_whole_tree_and_nothing_of_the_old_one_remains()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();

        var full = NestedSchema.Full("ada");
        var bare = new Person { Code = "ada", Name = "bare" };

        // From the full tree to one whose nullable owned value is absent, with no element in any List and no
        // Binary, and back, through the class and by name.
        await byClass.InsertAsync(full);
        await byName.ReplaceAsync(NestedSchema.RecordOf(database.Schema, bare));
        Assert.Equal(NestedSchema.Describe(database.Schema, bare), NestedSchema.Describe(byName.Get("ada")));

        await byClass.ReplaceAsync(full);
        Assert.Equal(NestedSchema.Describe(database.Schema, full), NestedSchema.Describe(database.Schema, byClass.Get("ada")));

        // Trees of other shapes, each read back whole.
        for (var number = 1; number <= 6; number++)
        {
            await byName.UpsertAsync(NestedSchema.RecordOf(database.Schema, Stamped("ada", number)));

            Assert.Equal(number, ExpectOneState(byClass.Get("ada")!));
            Assert.Equal(NestedSchema.Describe(database.Schema, Stamped("ada", number)), NestedSchema.Describe(byName.Get("ada")));
        }
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Read_by_key_returns_one_whole_tree_while_the_entity_is_being_replaced()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();
        await byClass.InsertAsync(Stamped("gauge", 0));

        await ReadersAndWriter.RunAsync(
            Readers,
            reader => reader % 2 == 0
                ? () => ExpectOneState(byClass.Get("gauge")!)
                : () => ExpectOneState(byName.Get("gauge")!),
            async () =>
            {
                for (var number = 1; number <= Replaces; number++)
                {
                    await byClass.ReplaceAsync(Stamped("gauge", number));
                }
            });

        Assert.Equal(Replaces, ExpectOneState(byName.Get("gauge")!));
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Search_returns_whole_trees_while_the_entities_are_being_replaced()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var byName = database.Set("Person");
        var byClass = database.Set<Person>();
        string[] codes = ["first", "second", "third"];

        foreach (var code in codes)
        {
            await byClass.InsertAsync(Stamped(code, 0));
        }

        await ReadersAndWriter.RunAsync(
            Readers,
            reader => reader % 2 == 0
                ? () => Assert.All(byClass.Index("ByName").Find(IndexKey.All), person => ExpectOneState(person))
                : () => Assert.All(byName.Index("ByName").Find(IndexKey.All), record => ExpectOneState(record)),
            async () =>
            {
                for (var number = 1; number <= Replaces; number++)
                {
                    await byName.ReplaceAsync(NestedSchema.RecordOf(database.Schema, Stamped(codes[number % codes.Length], number)));
                }
            });
    }

    // A Person whose every value at every depth is made from the number: see the class.
    private static Person Stamped(string code, int number)
    {
        var text = Text(number);
        var length = number % 5;

        return new Person
        {
            Code = code,
            Name = $"{code} {number}",
            Home = number % 2 == 0 ? AddressOf(number) : null,
            Work = AddressOf(number),
            Tags = [.. Enumerable.Repeat(text, 1 + length)],
            Scores = [.. Enumerable.Repeat((long)number, 1 + (number % 600))],
            Marks = [number, null, number],
            Notes = [null, text],
            Places = [.. Enumerable.Range(0, length).Select(_ => AddressOf(number))],
            Pins = [new Point { Lat = number, Lon = number }],
            Grid = [[number], [.. Enumerable.Repeat(number, length)]],
            Manager = text,
            Photo = [(byte)number, (byte)number],
            Files = [[(byte)number], [.. Enumerable.Repeat((byte)number, 70_000 * (number % 2))]],
        };
    }

    private static Address AddressOf(int number) => new()
    {
        City = Text(number),
        Zip = number,
        Lines = [Text(number)],
        Geo = new Point { Lat = number, Lon = -number },
    };

    // The text of a number: its digits, then one of three lengths, so that it lies inline, in a block of its
    // own or in chunks by turns.
    private static string Text(int number)
    {
        var digits = number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return digits + new string('.', (number % 3) switch { 0 => 10, 1 => 100, _ => 70_000 });
    }

    // The number of the one state the object holds, after checking every value at every depth against it.
    private static int ExpectOneState(Person person)
    {
        var number = int.Parse(person.Name[(person.Name.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(NestedSchema.Describe(NestedSchema.Schema, Stamped(person.Code, number)), NestedSchema.Describe(NestedSchema.Schema, person));

        return number;
    }

    private static int ExpectOneState(LatticeRecord record)
    {
        var name = record.Get<string>("Name")!;
        var number = int.Parse(name[(name.LastIndexOf(' ') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(NestedSchema.Describe(NestedSchema.RecordOf(NestedSchema.Schema, Stamped(record.Get<string>("Code")!, number))), NestedSchema.Describe(record));

        return number;
    }
}
