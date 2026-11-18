using static Moltaro.Lattice.Tests.DictionaryStrings.DictionarySchema;

namespace Moltaro.Lattice.Tests.DictionaryStrings;

// A child reads the texts of Dictionary fields as its master holds them (brief of Epic 1, sprint S13,
// Acceptance, and the planner's decision after the study, point 8: text travels as bytes with each root,
// and a child stores each distinct text through its own table):
// - a child that loads while its master holds entities with Dictionary fields reads every text its
//   master reads, by key and in an enumeration;
// - each change of the master reaches it with its text: an insert, a replace to another text, a replace
//   that keeps the text, an upsert and a delete;
// - a write through a child of an entity with Dictionary fields, a Dictionary key among them, returns once
//   the child has applied it, and reads back on the child and on the master.
// A test waits for a child through the position of a write (WaitForPositionAsync), never by sleeping.
public sealed class DictionaryTextThroughAChildTests
{
    private static readonly string[] Shared = ["North", "Südwest", "Жук", "a😀b"];

    public static IEnumerable<TheoryDataRow<LatticeStringEncodingEnum>> Encodings => EveryEncoding;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Child_that_loads_while_its_master_holds_Dictionary_texts_reads_the_texts_its_master_reads(LatticeStringEncodingEnum encoding)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(Of(encoding, LatticeStringComparisonEnum.OrdinalIgnoreCase));
        var places = master.Set<Place>();

        // Texts shared by many entities, texts of one entity, the empty text and null.
        for (var id = 0; id < 40; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = Shared[id % Shared.Length], Zone = id % 5 == 0 ? null : $"zone {id}", Label = Shared[0] });
        }

        await places.InsertAsync(new Place { Id = 40, Region = "", Zone = "" });
        await master.Set<Code>().InsertAsync(new Code { Name = "North", Body = "Südwest" });

        await using var child = await TestDatabases.OpenChildAsync(master);

        ExpectSame(master, child);
        Assert.Equal("Südwest", child.Set<Code>().Get("NORTH")?.Body);
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Each_change_of_the_master_reaches_the_child_with_its_text(LatticeStringEncodingEnum encoding)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(Of(encoding, LatticeStringComparisonEnum.Ordinal));
        var places = master.Set<Place>();
        var records = master.Set("Place");

        for (var id = 0; id < 8; id++)
        {
            await places.InsertAsync(new Place { Id = id, Region = Shared[id % Shared.Length], Zone = Shared[0] });
        }

        await using var child = await TestDatabases.OpenChildAsync(master);

        Func<ValueTask<LatticeWriteResult>>[] changes =
        [
            () => places.InsertAsync(new Place { Id = 8, Region = "North", Zone = "new on the master" }),
            () => places.ReplaceAsync(new Place { Id = 1, Region = "another text", Zone = Shared[0] }),
            () => records.ReplaceAsync(PlaceRecord(records, 2, Shared[2], Shared[0], label: "text kept")),
            () => records.UpsertAsync(PlaceRecord(records, 3, "north", zone: null)),
            () => places.UpsertAsync(new Place { Id = 9, Region = "another text", Zone = "Жук" }),
            () => places.DeleteAsync(0),
            () => records.DeleteAsync(4L),
        ];

        foreach (var change in changes)
        {
            var result = await change();

            await child.WaitForPositionAsync(result.Position);
            ExpectSame(master, child);
        }

        Assert.Equal("another text", child.Set<Place>().Get(1)?.Region);
        Assert.Equal("north", child.Set<Place>().Get(3)?.Region);
        Assert.Null(child.Set<Place>().Get(0));
    }

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Write_through_a_child_of_an_entity_with_Dictionary_fields_reads_back_on_the_child_and_on_the_master(LatticeStringEncodingEnum encoding)
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(Of(encoding, LatticeStringComparisonEnum.OrdinalIgnoreCase));
        await master.Set<Place>().InsertAsync(new Place { Id = 1, Region = "North", Zone = "Жук" });
        await using var child = await TestDatabases.OpenChildAsync(master);
        var places = child.Set<Place>();
        var codes = child.Set<Code>();
        var records = child.Set("Place");

        // A text the master holds, one it does not, and a Dictionary key.
        await places.InsertAsync(new Place { Id = 2, Region = "North", Zone = "new through the child" });
        await records.ReplaceAsync(PlaceRecord(records, 1, "a😀b", "North"));
        await codes.InsertAsync(new Code { Name = "Main", Body = "North" });
        var last = await codes.UpsertAsync(new Code { Name = "MAIN", Body = "Жук" });

        // Each write returned once the child had applied it.
        Assert.Equal("new through the child", places.Get(2)?.Zone);
        Assert.Equal("a😀b", places.Get(1)?.Region);
        Assert.Equal(("Main", "Жук"), (codes.Get("main")!.Name, codes.Get("main")!.Body));

        await master.WaitForPositionAsync(last.Position);
        ExpectSame(master, child);
        Assert.Equal(("Main", "Жук"), (master.Set<Code>().Get("main")!.Name, master.Set<Code>().Get("main")!.Body));
    }

    // The child holds every entity of the master with the texts the master reads, by key and in an
    // enumeration.
    private static void ExpectSame(ILatticeDatabase master, ILatticeDatabase child)
    {
        var onMaster = master.Set<Place>().Enumerate().OrderBy(place => place.Id).ToList();
        var onChild = child.Set<Place>().Enumerate().OrderBy(place => place.Id).ToList();

        Assert.Equal(onMaster.Select(place => (place.Id, place.Region, place.Zone, place.Label)), onChild.Select(place => (place.Id, place.Region, place.Zone, place.Label)));

        foreach (var place in onMaster)
        {
            var read = child.Set("Place").Get(place.Id);

            Assert.NotNull(read);
            Assert.Equal(place.Region, read["Region"]);
            Assert.Equal(place.Zone, read["Zone"]);
        }
    }
}
