namespace Moltaro.Lattice.Tests.Binding;

// A schema for the tests of the binding of nested values: an entity type Holder bound to the
// class THolder, with the key Id and the fields under test. What they may name is declared
// around it: the enum type Color; the owned types Address and Point, bound to the classes
// below; the owned type Note, which names no class; the owned type Envelope, whose class holds
// a Note; and the entity type Group with a Guid key.
internal static class BindingSchema
{
    // The fields are written with ' for ".
    public static LatticeSchema For<THolder>(string fields) => LatticeSchema.Compile($$"""
        {
          "Enums": [
            { "Id": 1, "Name": "Color", "Members": [ { "Name": "Red", "Value": 1 }, { "Name": "Green", "Value": 2 } ] }
          ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Street", "Type": "String", "Nullable": true },
                { "Id": 3, "Name": "Location", "Type": "Owned", "OwnedType": "Point", "Nullable": true },
                { "Id": 4, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 5, "Name": "Corners", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Point" } }
              ]
            },
            {
              "Id": 2, "Name": "Point", "ClrType": "{{typeof(Point).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Lat", "Type": "Float64" }, { "Id": 2, "Name": "Lon", "Type": "Float64" } ]
            },
            {
              "Id": 3, "Name": "Note",
              "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ]
            },
            {
              "Id": 4, "Name": "Envelope", "ClrType": "{{typeof(Envelope).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Memo", "Type": "Owned", "OwnedType": "Note", "Nullable": true } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(THolder).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, {{fields.Replace('\'', '"')}} ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Group",
              "Fields": [ { "Id": 1, "Name": "Key", "Type": "Guid" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // The class binds: a set serves it.
    public static async Task ExpectBoundAsync<THolder>(string fields)
        where THolder : class
    {
        await using var database = await TestData.OpenMasterAsync(schema: For<THolder>(fields));

        Assert.Equal("Holder", database.Set<THolder>().EntityType.Name);
    }

    // The class does not bind, from a set and from every typed member of a write batch, each
    // time with BindingFailed and the given names in the message.
    public static async Task ExpectNotBoundAsync<THolder>(string fields, params string[] named)
        where THolder : class, new()
    {
        await using var database = await TestData.OpenMasterAsync(schema: For<THolder>(fields));
        var batch = database.CreateWriteBatch();

        ExpectBindingFailed(() => database.Set<THolder>());
        ExpectBindingFailed(() => batch.Insert(new THolder()));
        ExpectBindingFailed(() => batch.Upsert(new THolder()));
        ExpectBindingFailed(() => batch.Delete<THolder>(1L));
        Assert.Equal(0, batch.Count);

        void ExpectBindingFailed(Action bind)
        {
            var exception = Assert.Throws<LatticeException>(bind);
            Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
            Assert.All(named, name => Assert.Contains(name, exception.Message));
        }
    }

    // A class for an entity type with one field besides its key, named Value.
    public sealed class Holder<TValue>
    {
        public long Id { get; set; }

        public TValue Value { get; set; } = default!;
    }

    // The class of Address: an owned value inside it, a List of strings and a List of owned
    // values, as an array.
    public sealed class Address
    {
        public string City { get; set; } = "";

        public string? Street { get; set; }

        public Point? Location { get; set; }

        public List<string> Lines { get; set; } = [];

        public Point[] Corners { get; set; } = [];
    }

    public sealed class Point
    {
        public double Lat { get; set; }

        public double Lon { get; set; }
    }

    // Note names no class, so no property type can hold it.
    public sealed class Envelope
    {
        public object? Memo { get; set; }
    }
}
