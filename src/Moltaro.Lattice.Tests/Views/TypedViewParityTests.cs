using Moltaro.Lattice.Tests.Indexes.ValueBinding;
using Moltaro.Lattice.Tests.NestedValues;
using Moltaro.Lattice.Tests.NestedValues.Collections;
using Moltaro.Lattice.Tests.NestedValues.Copies;
using Moltaro.Lattice.Tests.NestedValues.References;

namespace Moltaro.Lattice.Tests.Views;

public sealed class TypedViewParityTests
{
    [Theory]
    [MemberData(nameof(BindingSchema.Encodings), MemberType = typeof(BindingSchema))]
    public async Task Generated_views_read_all_scalar_families_with_the_same_values_and_absence_as_views_by_name(string encoding)
    {
        await using var database = await BindingSchema.OpenAsync(encoding);
        var items = database.Set<Item>();
        var records = database.Set("Item");
        string[] fields = [.. BindingSchema.ScalarFields, "Level", "Wide", "Rights", "Exact", "Folded"];
        var key = 0L;

        foreach (var field in fields)
        {
            foreach (var value in BindingSchema.EndsOf(field))
            {
                var item = Item.Of(++key, field, value);
                await items.InsertAsync(item);
                Assert.True(items.TryView(key, field, static (ScalarItemView view, string name) => ScalarByClass(view, name), out var byClass));
                Assert.True(records.TryView(key, field, static (view, name) => ScalarByName(view[name], name), out var byName));
                Assert.Equal(value, byName);
                Assert.Equal(byName, byClass);
            }
        }

        await items.InsertAsync(new Item { Id = ++key, Count = 42 });
        foreach (var field in fields)
        {
            Assert.True(items.TryView(key, field, static (ScalarItemView view, string name) => ScalarByClass(view, name), out var byClass));
            Assert.True(records.TryView(key, field, static (view, name) => ScalarByName(view[name], name), out var byName));
            Assert.Null(byName);
            Assert.Equal(byName, byClass);
        }

        Assert.True(items.TryView(key, static (ScalarItemView view) => (view.Id, view.Count), out var generatedRequired));
        Assert.True(records.TryView(key, static view => (view["Id"].Get<long>(), view["Count"].Get<int>()), out var namedRequired));
        Assert.Equal((key, 42), namedRequired);
        Assert.Equal(namedRequired, generatedRequired);
    }

    [Fact]
    public async Task Generated_nested_views_read_their_own_fields_and_keep_collection_elements_in_the_borrowed_representation()
    {
        await using var database = await NestedSchema.OpenMasterAsync();
        var people = database.Set<Person>();
        var records = database.Set("Person");
        await people.InsertAsync(NestedSchema.Full("ada"));
        var absent = NestedSchema.Full("other");
        absent.Home = null;
        absent.Manager = null;
        absent.Photo = null;
        await people.InsertAsync(absent);

        Assert.True(people.TryView("ada", static (PersonView view) =>
        {
            var home = view.Home;
            return (view.Code.GetString(), home.Value.Zip, home.Value.Geo.Value.Lat, view.Work.Zip, view.Work.Geo.HasValue,
                view.Scores[1].Get<long>(), view.Marks[1].IsNull, view.Places[1].Owned["City"].GetString(),
                view.Pins[1].Owned["Lon"].Get<double>(), view.Grid[2].List[1].Get<int>(),
                view.Manager.GetString(), view.Photo.GetPart(0)[2], view.Notes[2].ByteLength, view.Files[0].ByteLength);
        }, out var byClass));
        Assert.True(records.TryView("ada", static view =>
        {
            var home = view["Home"].Owned;
            return (view["Code"].GetString(), home["Zip"].Get<int>(), home["Geo"].Owned["Lat"].Get<double>(),
                view["Work"].Owned["Zip"].Get<int>(), !view["Work"].Owned["Geo"].IsNull,
                view["Scores"].List[1].Get<long>(), view["Marks"].List[1].IsNull,
                view["Places"].List[1].Owned["City"].GetString(), view["Pins"].List[1].Owned["Lon"].Get<double>(),
                view["Grid"].List[2].List[1].Get<int>(), view["Manager"].GetString(), view["Photo"].GetPart(0)[2],
                view["Notes"].List[2].ByteLength, view["Files"].List[0].ByteLength);
        }, out var byName));
        Assert.Equal(("ada", 150, 59.91, 5003, false, -1L, true, "Rome", double.MaxValue, 2, "manager of ada", (byte)3, 0, 0), byName);
        Assert.Equal(byName, byClass);

        Assert.True(people.TryView("other", static (PersonView view) => (view.Home.HasValue, view.Manager.IsNull, view.Photo.IsNull), out var classNulls));
        Assert.True(records.TryView("other", static view => (!view["Home"].IsNull, view["Manager"].IsNull, view["Photo"].IsNull), out var nameNulls));
        Assert.Equal((false, true, true), nameNulls);
        Assert.Equal(nameNulls, classNulls);
        Assert.Throws<InvalidOperationException>(() => people.TryView("other", static (PersonView view) => view.Home.Value.Zip, out _));
    }

    [Fact]
    public async Task Generated_optional_maps_and_sets_distinguish_absent_empty_and_populated_values()
    {
        await using var database = await TestDatabases.OpenMasterAsync(CollectionSchema.Compile());
        var items = database.Set<CollectionItem>();
        var records = database.Set("Item");
        await items.InsertAsync(new CollectionItem { Id = 1 });
        await items.InsertAsync(new CollectionItem { Id = 2, Options = [], Rights = [] });
        await items.InsertAsync(new CollectionItem
        {
            Id = 3,
            Options = new Dictionary<string, CollectionDetails?> { ["Ada"] = new CollectionDetails { Data = [42] }, ["empty"] = null },
            Rights = ["Reader", null],
            Numbers = new Dictionary<byte, ushort> { [1] = 30 },
            Bytes = [3],
            Scores = [5L],
        });

        (bool HasMap, int MapEntries, bool HasSet, int SetEntries)[] expected =
            [(false, 0, false, 0), (true, 0, true, 0), (true, 2, true, 2)];
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.True(items.TryView(index + 1L, static (CollectionItemView view) =>
                (view.Options.HasValue, view.Options.HasValue ? view.Options.Value.Count : 0,
                    view.Rights.HasValue, view.Rights.HasValue ? view.Rights.Value.Count : 0), out var byClass));
            Assert.True(records.TryView(index + 1L, static view =>
                (!view["Options"].IsNull, view["Options"].IsNull ? 0 : view["Options"].Map.Count,
                    !view["Rights"].IsNull, view["Rights"].IsNull ? 0 : view["Rights"].Set.Count), out var byName));
            Assert.Equal(expected[index], byName);
            Assert.Equal(byName, byClass);
        }

        Assert.True(items.TryView(3L, static (CollectionItemView view) =>
        {
            var map = view.Options.Value;
            Assert.True(map.TryGetValue("aDA", out var entry));
            Assert.True(map.TryGetValue("empty", out var empty));
            Assert.True(view.Numbers.TryGetValue((byte)1, out var number));

            return (entry.Owned["Data"].GetPart(0)[0], empty.IsNull, number.Get<ushort>(),
                view.Rights.Value.Contains("READER"), view.Rights.Value.Contains<string?>(null),
                view.Bytes.Contains((byte)3), view.Scores.Contains(5L));
        }, out var generated));
        Assert.True(records.TryView(3L, static view =>
        {
            var map = view["Options"].Map;
            Assert.True(map.TryGetValue("aDA", out var entry));
            Assert.True(map.TryGetValue("empty", out var empty));
            Assert.True(view["Numbers"].Map.TryGetValue((byte)1, out var number));

            return (entry.Owned["Data"].GetPart(0)[0], empty.IsNull, number.Get<ushort>(),
                view["Rights"].Set.Contains("READER"), view["Rights"].Set.Contains<string?>(null),
                view["Bytes"].Set.Contains((byte)3), view["Scores"].Set.Contains(5L));
        }, out var named));
        Assert.Equal(((byte)42, true, (ushort)30, true, true, true, true), named);
        Assert.Equal(named, generated);
    }

    [Fact]
    public async Task A_generated_enum_property_preserves_the_CLR_type_and_distinguishes_a_present_zero_from_null()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
        var forms = database.Set<Form>();
        var records = database.Set("Form");
        await forms.InsertAsync(new Form { Id = 1, Main = new Label { Grade = Grade.Low }, Spare = new Label { Grade = null }, MaybeGrades = [null, Grade.High] });

        Assert.True(forms.TryView(1L, static (FormView view) =>
            (view.Main.Grade, view.Spare.Value.Grade, view.MaybeGrades[0].IsNull, view.MaybeGrades[1].Get<byte>()), out var byClass));
        Assert.True(records.TryView(1L, static view =>
        {
            var main = view["Main"].Owned["Grade"];
            var spare = view["Spare"].Owned["Grade"];
            return (main.IsNull ? (Grade?)null : (Grade)main.Get<byte>(), spare.IsNull ? (Grade?)null : (Grade)spare.Get<byte>(),
                view["MaybeGrades"].List[0].IsNull, view["MaybeGrades"].List[1].Get<byte>());
        }, out var byName));

        Assert.Equal(((Grade?)Grade.Low, null, true, (byte)Grade.High), byName);
        Assert.Equal(byName, byClass);
    }

    [Fact]
    public async Task Generated_views_read_reference_keys_without_resolving_their_targets()
    {
        await using var database = await TestDatabases.OpenMasterAsync(ReferenceSchema.Schema);
        var links = database.Set<Link>();
        var records = database.Set("Link");
        await links.InsertAsync(new Link
        {
            Code = "ada",
            Required = 404L,
            ToInt8 = -8,
            ToUInt8 = 8,
            ToInt16 = -16,
            ToUInt16 = 16,
            ToInt32 = -32,
            ToUInt32 = 32,
            ToInt64 = -64L,
            ToUInt64 = 64UL,
            ToGuid = Guid.Empty,
            ToName = "OrIgInAl",
            Many = [404L, 405L],
            Names = ["Unknown"],
            Guids = [null, Guid.Empty],
        });
        Assert.Null(database.Set("Int64").Get(404L));

        Assert.True(links.TryView("ada", static (LinkView view) =>
            (view.Required, view.ToInt8, view.ToUInt8, view.ToInt16, view.ToUInt16, view.ToInt32, view.ToUInt32, view.ToInt64,
                view.ToUInt64, view.ToGuid, view.ToText.IsNull, view.ToName.GetString(), view.Many[1].Get<long>(),
                view.Names[0].GetString(), view.Guids[0].IsNull, view.Guids[1].Get<Guid>()), out var byClass));
        Assert.True(records.TryView("ada", static view =>
            (view["Required"].Get<long>(), OptionalScalar<sbyte>(view["ToInt8"]), OptionalScalar<byte>(view["ToUInt8"]),
                OptionalScalar<short>(view["ToInt16"]), OptionalScalar<ushort>(view["ToUInt16"]), OptionalScalar<int>(view["ToInt32"]),
                OptionalScalar<uint>(view["ToUInt32"]), OptionalScalar<long>(view["ToInt64"]), OptionalScalar<ulong>(view["ToUInt64"]),
                OptionalScalar<Guid>(view["ToGuid"]), view["ToText"].IsNull, view["ToName"].GetString(), view["Many"].List[1].Get<long>(),
                view["Names"].List[0].GetString(), view["Guids"].List[0].IsNull, view["Guids"].List[1].Get<Guid>()), out var byName));

        Assert.Equal(byName, byClass);
        Assert.Equal(404L, byClass.Required);
        Assert.Equal("OrIgInAl", byClass.Item12);
    }

    private static object? ScalarByClass(scoped ScalarItemView view, string name) => name switch
    {
        "Boolean" => view.Boolean,
        "Int8" => view.Int8,
        "UInt8" => view.UInt8,
        "Int16" => view.Int16,
        "UInt16" => view.UInt16,
        "Int32" => view.Int32,
        "UInt32" => view.UInt32,
        "Int64" => view.Int64,
        "UInt64" => view.UInt64,
        "Float32" => view.Float32,
        "Float64" => view.Float64,
        "Decimal" => view.Decimal,
        "Guid" => view.Guid,
        "DateTimeOffset" => view.DateTimeOffset,
        "DateOnly" => view.DateOnly,
        "TimeOnly" => view.TimeOnly,
        "TimeSpan" => view.TimeSpan,
        "Level" => view.Level,
        "Wide" => view.Wide,
        "Rights" => view.Rights,
        "Exact" => Text(view.Exact),
        "Folded" => Text(view.Folded),
        _ => throw new ArgumentException("Unknown fixture field.", nameof(name)),
    };

    private static object? ScalarByName(scoped LatticeValueView view, string name) => name switch
    {
        "Boolean" => OptionalScalar<bool>(view),
        "Int8" => OptionalScalar<sbyte>(view),
        "UInt8" => OptionalScalar<byte>(view),
        "Int16" => OptionalScalar<short>(view),
        "UInt16" => OptionalScalar<ushort>(view),
        "Int32" => OptionalScalar<int>(view),
        "UInt32" => OptionalScalar<uint>(view),
        "Int64" => OptionalScalar<long>(view),
        "UInt64" => OptionalScalar<ulong>(view),
        "Float32" => OptionalScalar<float>(view),
        "Float64" => OptionalScalar<double>(view),
        "Decimal" => OptionalScalar<decimal>(view),
        "Guid" => OptionalScalar<Guid>(view),
        "DateTimeOffset" => OptionalScalar<DateTimeOffset>(view),
        "DateOnly" => OptionalScalar<DateOnly>(view),
        "TimeOnly" => OptionalScalar<TimeOnly>(view),
        "TimeSpan" => OptionalScalar<TimeSpan>(view),
        "Level" => OptionalScalar<short>(view),
        "Wide" => OptionalScalar<ulong>(view),
        "Rights" => OptionalScalar<int>(view),
        "Exact" or "Folded" => Text(view),
        _ => throw new ArgumentException("Unknown fixture field.", nameof(name)),
    };

    private static T? OptionalScalar<T>(scoped LatticeValueView view)
        where T : struct =>
        view.IsNull ? null : view.Get<T>();

    private static string? Text(scoped LatticeValueView view) => view.IsNull ? null : view.GetString();
}
