using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples;

// An entity is more than a flat row: it may own values with fields of their own, hold Lists,
// Maps and Sets, and refer to other entities by their keys. The schema declares each of them,
// position by position. A record holds them by name; a class holds them in its own types. An
// index may cover a field inside an owned value.
//
// This shows the intended use. Storing such entities comes in a later sprint, so the sample
// compiles, builds, binds, saves and loads, and writes nothing.
internal static class NestedValuesSample
{
    private static readonly string SchemaJson = $$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Address", "ClrType": "{{typeof(Address).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "City", "Type": "String" },
                { "Id": 2, "Name": "Lines", "Type": "List", "Element": { "Type": "String" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Person", "ClrType": "{{typeof(Person).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Address", "Nullable": true },
                { "Id": 3, "Name": "Manager", "Type": "Reference", "EntityType": "Person", "Nullable": true },
                { "Id": 4, "Name": "Teams", "Type": "List", "Element": { "Type": "Reference", "EntityType": "Team" } },
                { "Id": 5, "Name": "Options", "Type": "Map", "Key": { "Type": "String" }, "Value": { "Type": "String", "Nullable": true } },
                { "Id": 6, "Name": "Rights", "Type": "Set", "Element": { "Type": "String" } }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] } ]
            },
            {
              "Id": 2, "Name": "Team",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Guid" }, { "Id": 2, "Name": "Title", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    public static async Task RunAsync()
    {
        Console.WriteLine("== Owned values, collections and references ==");

        var schema = LatticeSchema.Compile(SchemaJson);

        // Every position has a declaration of its own: a List its element, a Map its key and
        // its value. ValueType is what a record holds there.
        if (!schema.TryGetEntityType("Person", out var person) || !schema.TryGetOwnedType("Address", out var address))
        {
            return;
        }

        foreach (var field in person.Fields)
        {
            Console.WriteLine($"Field {field.Name}: {Describe(field.Declaration)}, held as {field.Declaration.ValueType.Name}");
        }

        // An index part may be a path into an owned value.
        foreach (var index in person.Indexes)
        {
            var parts = index.Parts.Select(part => string.Join(".", part.Path.Select(step => step.Name)));
            Console.WriteLine($"Index {index.Name} on {string.Join(", ", parts)}");
        }

        // A record by name: an owned value is a LatticeOwnedValue, a collection is given as any
        // sequence and copied, a reference is the key of its target.
        var record = new LatticeRecord(person)
        {
            ["Id"] = 1L,
            ["Home"] = new LatticeOwnedValue(address) { ["City"] = "Oslo", ["Lines"] = new[] { "Karl Johans gate 1" } },
            ["Manager"] = 2L,
            ["Teams"] = new[] { Guid.NewGuid() },
            ["Options"] = new Dictionary<string, string?> { ["theme"] = "dark", ["mode"] = null },
            ["Rights"] = new[] { "read", "write", "read" },
        };
        var home = record.Get<LatticeOwnedValue>("Home")!;
        Console.WriteLine($"Record: lives in {home["City"]}, managed by {record["Manager"]}, rights {string.Join(", ", record.Get<HashSet<string>>("Rights")!)}");

        // The same data as an object of a class, which the database binds once.
        var ada = new Person
        {
            Id = 1,
            Home = new Address { City = "Oslo", Lines = ["Karl Johans gate 1"] },
            Manager = 2,
            Teams = [Guid.NewGuid()],
            Options = new() { ["theme"] = "dark" },
            Rights = ["read", "write"],
        };

        await using (var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        }))
        {
            var people = database.Set<Person>();
            Console.WriteLine($"Object: {ada.Home!.City}, bound to entity type {people.EntityType.Name}");
        }

        // Both fingerprints cover the nested declarations, and a saved schema keeps them all.
        using var stream = new MemoryStream();
        schema.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);
        Console.WriteLine($"Schema fingerprint {schema.Fingerprint}");
        Console.WriteLine($"Layout fingerprint of Person {person.LayoutFingerprint}");
        Console.WriteLine($"Loaded from {stream.Length} bytes: same fingerprint {loaded.Fingerprint == schema.Fingerprint}");

        Console.WriteLine();
    }

    private static string Describe(LatticeValueDeclaration declaration) => declaration.Type switch
    {
        LatticeFieldTypeEnum.Owned => $"Owned {declaration.OwnedType!.Name}",
        LatticeFieldTypeEnum.Reference => $"Reference to {declaration.ReferencedEntityTypeName}",
        LatticeFieldTypeEnum.List or LatticeFieldTypeEnum.Set => $"{declaration.Type} of {Describe(declaration.Element!)}",
        LatticeFieldTypeEnum.Map => $"Map of {Describe(declaration.Key!)} to {Describe(declaration.Value!)}",
        _ => declaration.IsNullable ? $"{declaration.Type}, nullable" : declaration.Type.ToString(),
    };

    // The class of the owned type Address.
    private sealed class Address
    {
        public string City { get; set; } = "";

        public List<string> Lines { get; set; } = [];
    }

    // The class of the entity type Person: an owned value as its class, a reference as the key
    // of its target, collections as List, Dictionary and HashSet.
    private sealed class Person
    {
        public long Id { get; set; }

        public Address? Home { get; set; }

        public long? Manager { get; set; }

        public List<Guid> Teams { get; set; } = [];

        public Dictionary<string, string?> Options { get; set; } = [];

        public HashSet<string> Rights { get; set; } = [];
    }
}
