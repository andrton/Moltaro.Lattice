using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Samples.Schema;

// What a schema declares besides entity types and their fields: enum types, how text is
// compared and stored, and a stable identity for every enum type, entity type, field and
// index. A compiled schema has a catalog version and two fingerprints: one of everything that
// decides how the data is stored, compared and kept unique, and one per entity type of how
// its entities are stored.
//
// This shows the intended use.
internal static class SchemaDeclarationsSample
{
    private const string SchemaJson = """
        {
          "SchemaVersion": 2,
          "StringEncoding": "Utf8",
          "Enums": [
            {
              "Id": 1, "Name": "AccountState", "UnderlyingType": "UInt8",
              "Members": [
                { "Name": "Disabled", "Value": 0 },
                { "Name": "Active", "Value": 1, "Canonical": true },
                { "Name": "Enabled", "Value": 1 }
              ]
            },
            {
              "Id": 2, "Name": "Permission", "Flags": true,
              "Members": [
                { "Name": "None", "Value": 0 },
                { "Name": "Read", "Value": 1 },
                { "Name": "Write", "Value": 2 },
                { "Name": "Export", "Value": 4 },
                { "Name": "ReadWrite", "Value": 3 }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Account",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
                { "Id": 3, "Name": "Region", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "State", "Type": "Enum", "Enum": "AccountState" },
                { "Id": 5, "Name": "Permissions", "Type": "Enum", "Enum": "Permission" }
              ],
              "PrimaryKey": [1],
              "Indexes": [
                { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true },
                { "Id": 2, "Name": "ByRegion", "Fields": [3] }
              ]
            }
          ]
        }
        """;

    public static void Run()
    {
        Console.WriteLine("== Schema declarations ==");

        var schema = LatticeSchema.Compile(SchemaJson);
        Console.WriteLine($"Catalog version {schema.Version}, text stored as {schema.StringEncoding}");

        // An enum type is an integer with named values. A value with several names has one
        // canonical name; an entity stores only the value.
        foreach (var enumType in schema.EnumTypes)
        {
            var kind = enumType.IsFlags ? "Flags" : "enum";
            var members = string.Join(", ", enumType.Members.Select(member => $"{member.Name} = {member.Value}{(member.IsCanonical ? "" : " (alias)")}"));
            Console.WriteLine($"{kind} {enumType.Name} (id {enumType.Id}, {enumType.UnderlyingType}): {members}");
        }

        // Only declared values can be stored. A Flags value is any combination of declared
        // bits, also one that no member names.
        if (schema.TryGetEnumType("Permission", out var permission))
        {
            Console.WriteLine($"Read and Export: {permission.IsValid(5)}; a bit no flag declares: {permission.IsValid(8)}");
        }

        // Every field has its identity and its declaration, every index its identity.
        if (schema.TryGetEntityType("Account", out var account))
        {
            foreach (var field in account.Fields)
            {
                var declaration = field.Declaration;
                var policies = declaration.Type switch
                {
                    LatticeFieldTypeEnum.String => $", compared {declaration.Comparison}, stored {declaration.StringStorage}",
                    LatticeFieldTypeEnum.Enum => $" of {declaration.EnumType!.Name}, held as {declaration.ValueType.Name}",
                    _ => "",
                };
                Console.WriteLine($"Field {field.Id} {field.Name}: {declaration.Type}{policies}");
            }

            foreach (var index in account.Indexes)
            {
                var fields = string.Join(", ", index.Parts.Select(part => part.Field.Name));
                Console.WriteLine($"Index {index.Id} {index.Name} ({fields}){(index.IsUnique ? ", unique" : "")}");
            }

            // The fingerprints. A rename changes neither of them; a comparison changes what
            // the schema compares, not how an account is stored.
            var renamed = LatticeSchema.Compile(SchemaJson.Replace("\"Name\": \"Login\"", "\"Name\": \"UserName\"", StringComparison.Ordinal));
            var caseSensitive = LatticeSchema.Compile(SchemaJson.Replace("\"Comparison\": \"OrdinalIgnoreCase\"", "\"Comparison\": \"Ordinal\"", StringComparison.Ordinal));
            Console.WriteLine($"Schema fingerprint {schema.Fingerprint}");
            Console.WriteLine($"Layout fingerprint of Account {account.LayoutFingerprint}");
            Console.WriteLine($"After a rename: same schema fingerprint {renamed.Fingerprint == schema.Fingerprint}");
            Console.WriteLine($"Case-sensitive login: same schema fingerprint {caseSensitive.Fingerprint == schema.Fingerprint}, same layout {caseSensitive.EntityTypes[0].LayoutFingerprint == account.LayoutFingerprint}");
        }

        // Saved and loaded again, the schema keeps every declaration and its fingerprint.
        using var stream = new MemoryStream();
        schema.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);
        Console.WriteLine($"Loaded from {stream.Length} bytes: same fingerprint {loaded.Fingerprint == schema.Fingerprint}");

        Console.WriteLine();
    }
}
