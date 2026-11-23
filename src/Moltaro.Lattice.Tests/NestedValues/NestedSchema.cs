using System.Collections;
using System.Globalization;

namespace Moltaro.Lattice.Tests.NestedValues;

// The schema the tests of owned values, Lists and references share: one entity type, Person, whose key Code
// is a String, with every kind of nested value the engine stores:
// - Home, a nullable owned Address, and Work, one that is not; an Address holds a String, an Int32, a List of
//   String and a nullable owned Point of two Float64 fields, so that an owned value is absent at two depths;
// - Lists whose elements hold no descriptor, packed as bytes: Scores of Int64, Marks of nullable Int32 and
//   Pins of owned Points;
// - Lists whose elements hold descriptors, records of one stride: Tags of String, Notes of nullable String,
//   Places of owned Addresses, Grid of Lists of Int32 and Files of Binary;
// - Manager, a nullable reference to a Person, held as its key;
// - Photo, a nullable Binary;
// - the index ByName over its Name, for a search.
// Person, Address and Point are the classes the schema names. A record is compared with another, and an
// object with a record, by Describe, which writes every value at every depth in one text, so that a test
// states the whole tree in one assertion.
internal static class NestedSchema
{
    public static readonly string Json = $$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Zip", "Type": "Int32" },
                { "Id": 3, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 4, "Name": "Geo", "Type": "Owned", "OwnedType": "Point", "Nullable": true }
              ]
            },
            {
              "Id": 2, "Name": "Point", "ClrType": "{{typeof(Point).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" }, { "Id": 2, "Name": "Lon", "Type": "Float64" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Person", "ClrType": "{{typeof(Person).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Name", "Type": "String" },
                { "Id": 3, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 4, "Name": "Work", "Type": "Owned", "OwnedType": "Address" },
                { "Id": 5, "Name": "Tags", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 6, "Name": "Scores", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 7, "Name": "Marks", "Type": "List", "Element": { "Type": "Int32", "Nullable": true } },
                { "Id": 8, "Name": "Notes", "Type": "List", "Element": { "Type": "String", "Nullable": true } },
                { "Id": 9, "Name": "Places", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Address" } },
                { "Id": 10, "Name": "Pins", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Point" } },
                { "Id": 11, "Name": "Grid", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int32" } } },
                { "Id": 12, "Name": "Manager", "Type": "Reference", "EntityType": "Person", "Nullable": true },
                { "Id": 13, "Name": "Photo", "Type": "Binary", "Nullable": true },
                { "Id": 14, "Name": "Files", "Type": "List", "Element": { "Type": "Binary" } }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByName", "Fields": [2] } ]
            }
          ]
        }
        """;

    public static readonly LatticeSchema Schema = LatticeSchema.Compile(Json);

    public static ValueTask<ILatticeDatabase> OpenMasterAsync() => TestDatabases.OpenMasterAsync(Schema);

    public static ValueTask<ILatticeDatabase> OpenListeningMasterAsync() => TestDatabases.OpenListeningMasterAsync(Schema);

    // A Person with a value in every field and at every depth: both owned values present, Home with its Geo
    // and Work without, every List with duplicates, a null where its elements may be null, an empty element
    // where they are Lists, Strings or Binary values, and a reference to a Person that does not exist.
    public static Person Full(string code) => new()
    {
        Code = code,
        Name = $"name of {code}",
        Home = new Address { City = "Oslo", Zip = 150, Lines = ["Karl Johans gate 1", "3rd floor"], Geo = new Point { Lat = 59.91, Lon = 10.75 } },
        Work = new Address { City = "Bergen", Zip = 5003, Lines = [], Geo = null },
        Tags = ["b", "a", "b"],
        Scores = [3, -1, 3, long.MaxValue],
        Marks = [1, null, 1, int.MinValue],
        Notes = ["x", null, "", "x"],
        Places =
        [
            new Address { City = "Paris", Zip = 75001, Lines = ["Rue de Rivoli"] },
            new Address { City = "Rome", Zip = 118, Lines = [], Geo = new Point { Lat = 41.9, Lon = 12.5 } },
            new Address { City = "Paris", Zip = 75001, Lines = ["Rue de Rivoli"] },
        ],
        Pins = [new Point { Lat = 1, Lon = 2 }, new Point { Lat = -0.0, Lon = double.MaxValue }, new Point { Lat = 1, Lon = 2 }],
        Grid = [[], [1], [2, 2, 1]],
        Manager = $"manager of {code}",
        Photo = [1, 2, 3],
        Files = [[], [9], [8, 7], [9]],
    };

    // The record of Person with the values of the object, as a record holds them.
    public static LatticeRecord RecordOf(LatticeSchema schema, Person person)
    {
        Assert.True(schema.TryGetEntityType("Person", out var entityType));

        return new LatticeRecord(entityType)
        {
            ["Code"] = person.Code,
            ["Name"] = person.Name,
            ["Home"] = person.Home is null ? null : OwnedOf(schema, person.Home),
            ["Work"] = OwnedOf(schema, person.Work),
            ["Tags"] = person.Tags,
            ["Scores"] = person.Scores,
            ["Marks"] = person.Marks,
            ["Notes"] = person.Notes,
            ["Places"] = person.Places.Select(place => OwnedOf(schema, place)).ToList(),
            ["Pins"] = person.Pins.Select(pin => OwnedOf(schema, pin)).ToList(),
            ["Grid"] = person.Grid,
            ["Manager"] = person.Manager,
            ["Photo"] = person.Photo,
            ["Files"] = person.Files,
        };
    }

    public static LatticeOwnedValue OwnedOf(LatticeSchema schema, Address address)
    {
        Assert.True(schema.TryGetOwnedType("Address", out var ownedType));

        return new LatticeOwnedValue(ownedType)
        {
            ["City"] = address.City,
            ["Zip"] = address.Zip,
            ["Lines"] = address.Lines,
            ["Geo"] = address.Geo is null ? null : OwnedOf(schema, address.Geo),
        };
    }

    public static LatticeOwnedValue OwnedOf(LatticeSchema schema, Point point)
    {
        Assert.True(schema.TryGetOwnedType("Point", out var ownedType));

        return new LatticeOwnedValue(ownedType) { ["Lat"] = point.Lat, ["Lon"] = point.Lon };
    }

    // Every value of the record at every depth, in the order of the fields, as one text: "absent" for no
    // record. Two records that hold the same values, of the same types, have the same text.
    public static string Describe(LatticeRecord? record) =>
        record is null ? "absent" : FieldsOf(record.EntityType.Fields, name => record[name]);

    // The text of the object, as Describe gives it for the record of its values.
    public static string Describe(LatticeSchema schema, Person? person) =>
        person is null ? "absent" : Describe(RecordOf(schema, person));

    private static string FieldsOf(IReadOnlyList<LatticeField> fields, Func<string, object?> valueOf) =>
        $"{{ {string.Join(", ", fields.Select(field => $"{field.Name}: {Show(valueOf(field.Name))}"))} }}";

    // A value with its .NET type, so that a value of another type reads as another text.
    private static string Show(object? value) => value switch
    {
        null => "null",
        string text => $"\"{text}\"",
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        LatticeOwnedValue owned => $"{owned.OwnedType.Name} {FieldsOf(owned.OwnedType.Fields, name => owned[name])}",
        double number => $"{number.ToString("R", CultureInfo.InvariantCulture)}d",
        float number => $"{number.ToString("R", CultureInfo.InvariantCulture)}f",
        IEnumerable items => $"[{string.Join(", ", items.Cast<object?>().Select(Show))}]",
        IFormattable formattable => $"{formattable.ToString(null, CultureInfo.InvariantCulture)} {value.GetType().Name}",
        _ => $"{value} {value.GetType().Name}",
    };
}
