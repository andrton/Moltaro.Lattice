using Moltaro.Lattice.Tests.NestedValues;
using Moltaro.Lattice.Tests.NestedValues.References;

namespace Moltaro.Lattice.Tests.Views;

public sealed class RecordViewTests
{
    [Fact]
    public async Task View_reads_the_same_written_state_and_nested_values_as_an_owned_copy()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");
        Assert.True(people.TryGet("ada", out var owned, out var revision));

        Assert.True(people.TryView("ada", owned, static (view, record) =>
        {
            Assert.Same(record.EntityType, view.EntityType);
            Assert.Equal(record.Get<string>("Code"), view["Code"].GetString());
            Assert.Equal(record.Get<string>("Name"), view["Name"].GetString());

            var home = view["Home"].Owned;
            var copiedHome = record.Get<LatticeOwnedValue>("Home")!;
            Assert.Same(copiedHome.OwnedType, home.OwnedType);
            Assert.True(home.OwnedType.TryGetField("Zip", out var zip));
            Assert.Same(zip.Declaration, home[zip].Declaration);
            Assert.Equal(copiedHome.Get<int>("Zip"), home[zip].Get<int>());
            Assert.Equal(copiedHome.Get<LatticeOwnedValue>("Geo")!.Get<double>("Lat"), home["Geo"].Owned["Lat"].Get<double>());
            Assert.True(view["Work"].Owned["Geo"].IsNull);

            var scores = new List<long>();
            foreach (var score in view["Scores"].List)
            {
                scores.Add(score.Get<long>());
            }

            Assert.Equal(record.Get<List<long>>("Scores"), scores);
            Assert.Equal(record.Get<List<int?>>("Marks")![0], view["Marks"].List[0].Get<int>());
            Assert.True(view["Marks"].List[1].IsNull);
            Assert.Equal(record.Get<List<LatticeOwnedValue>>("Pins")![1].Get<double>("Lon"), view["Pins"].List[1].Owned["Lon"].Get<double>());
            Assert.Equal(record.Get<List<LatticeOwnedValue>>("Places")![1].Get<string>("City"), view["Places"].List[1].Owned["City"].GetString());
            Assert.Equal(record.Get<List<List<int>>>("Grid")![2][1], view["Grid"].List[2].List[1].Get<int>());
            Assert.Equal(record.Get<string>("Manager"), view["Manager"].GetString());
            Assert.Equal(record.Get<byte[]>("Photo"), view["Photo"].GetPart(0).ToArray());

            var emptyText = view["Notes"].List[2];
            Assert.False(emptyText.IsNull);
            Assert.Equal((0, 1, ""), (emptyText.ByteLength, emptyText.PartCount, emptyText.GetString()));
            Assert.True(emptyText.GetPart(0).IsEmpty);

            var emptyBinary = view["Files"].List[0];
            Assert.False(emptyBinary.IsNull);
            Assert.Equal((0, 1), (emptyBinary.ByteLength, emptyBinary.PartCount));
            Assert.True(emptyBinary.GetPart(0).IsEmpty);

            return view.Revision;
        }, out var seen));
        Assert.Equal(revision, seen);
    }

    [Fact]
    public async Task A_view_reads_reference_keys_without_resolving_the_referenced_entities()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set("Link");
        await links.InsertAsync(new LatticeRecord(links.EntityType)
        {
            ["Code"] = "ada",
            ["Required"] = 404L,
            ["ToInt8"] = sbyte.MinValue,
            ["ToName"] = "OrIgInAl",
            ["Many"] = new List<long> { 404L, 405L },
            ["Names"] = new List<string> { "Unknown" },
            ["Guids"] = new List<Guid?> { null, Guid.Empty },
        });

        var owned = links.Get("ada")!;
        Assert.Null(database.Set("Int64").Get(404L));

        Assert.True(links.TryView("ada", owned, static (view, record) =>
        {
            Assert.Equal(record.Get<long>("Required"), view["Required"].Get<long>());
            Assert.Equal(record.Get<sbyte>("ToInt8"), view["ToInt8"].Get<sbyte>());
            Assert.Equal(record.Get<string>("ToName"), view["ToName"].GetString());
            Assert.Equal(record.Get<List<long>>("Many")![1], view["Many"].List[1].Get<long>());
            Assert.Equal(record.Get<List<string>>("Names")![0], view["Names"].List[0].GetString());
            Assert.True(view["ToGuid"].IsNull);
            Assert.True(view["Guids"].List[0].IsNull);
            return view["Guids"].List[1].Get<Guid>();
        }, out var key));
        Assert.Equal(Guid.Empty, key);
    }

    [Fact]
    public async Task A_field_handle_must_be_the_exact_field_of_the_declaring_type()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");
        var otherSchema = LatticeSchema.Compile(NestedSchema.Json);
        Assert.True(otherSchema.TryGetEntityType("Person", out var otherPerson));
        Assert.True(database.Schema.TryGetOwnedType("Address", out var address));
        Assert.True(database.Schema.TryGetOwnedType("Point", out var point));
        Assert.True(address.TryGetField("City", out var city));
        Assert.True(point.TryGetField("Lat", out var latitude));
        Assert.Equal(city.Id, latitude.Id);
        Assert.Same(city, address.Fields[0]);
        Assert.Same(latitude, point.Fields[0]);

        var name = people.EntityType.GetField("Name");
        Assert.True(people.TryView("ada", name, static (view, field) => view[field].GetString(), out var actual));
        Assert.Equal("name of ada", actual);

        Assert.Throws<ArgumentException>(() => people.TryView("ada", otherPerson.GetField("Name"), static (view, field) => view[field].GetString(), out _));
        Assert.Throws<ArgumentException>(() => people.TryView("ada", latitude, static (view, field) => view["Home"].Owned[field].Get<double>(), out _));
        Assert.Throws<ArgumentException>(() => people.TryView("ada", city, static (view, field) => view[field].GetString(), out _));
        Assert.Throws<ArgumentException>(() => people.TryView("ada", name, static (view, field) => view["Home"].Owned[field].GetString(), out _));
        Assert.Throws<ArgumentException>(() => people.TryView("ada", static view => view["Home.City"].GetString(), out _));
        Assert.Throws<ArgumentException>(() => people.TryView("ada", static view => view["Home"].Owned["Geo.Lat"].Get<double>(), out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public async Task List_position_outside_the_element_range_is_refused(int index)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => people.TryView("ada", index, static (view, position) => view["Scores"].List[position].Get<long>(), out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Part_outside_the_range_of_a_single_part_is_refused(int part)
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");

        Assert.Throws<ArgumentOutOfRangeException>(
            () => people.TryView("ada", part, static (view, index) => view["Photo"].GetPart(index).Length, out _));
    }

    [Fact]
    public async Task Reading_a_null_or_wrong_kind_is_refused_and_scalar_type_must_match_exactly()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        await database.Set<Person>().InsertAsync(NestedSchema.Full("ada"));
        var people = database.Set("Person");

        Assert.Throws<InvalidCastException>(() => people.TryView("ada", static view => view["Scores"].List[0].Get<int>(), out _));
        Assert.Throws<InvalidCastException>(() => people.TryView("ada", static view => view["Marks"].List[0].Get<int?>(), out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Marks"].List[1].Get<int>(), out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Notes"].List[1].GetString(), out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Notes"].List[1].ByteLength, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Notes"].List[1].PartCount, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Notes"].List[1].GetPart(0).Length, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Name"].List.Count, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Name"].Map.Count, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Name"].Set.Count, out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Work"].Owned["Geo"].Owned["Lat"].Get<double>(), out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Photo"].GetString(), out _));
        Assert.Throws<InvalidOperationException>(() => people.TryView("ada", static view => view["Scores"].GetPart(0).Length, out _));
    }
}
