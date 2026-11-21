using System.Text;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// A schema whose entity type Item has a field of every type a root of S05 stores, for the tests
// that call every member of the writer and the reader on every kind of field: every scalar type
// of the catalog (Q03.4.1), String and Binary, an Enum of a signed enum type, Level, and an Enum of
// an unsigned Flags enum type, Mask. For each type there is a field that is not nullable, named
// after the type, and a nullable one, named the same with "OrNull". A second entity type, Twin, has
// the same fields with the same names and identities, so that a field of Twin differs from the
// field of Item only in its entity type.
internal static class EveryType
{
    public static readonly IReadOnlyList<string> Names =
    [
        "Boolean", "Int8", "UInt8", "Int16", "UInt16", "Int32", "UInt32", "Int64", "UInt64",
        "Float32", "Float64", "Decimal", "Guid", "DateTimeOffset", "DateOnly", "TimeOnly", "TimeSpan",
        "String", "Binary", "SignedEnum", "UnsignedEnum",
    ];

    private const string Enums = """
        { "Id": 1, "Name": "Level", "UnderlyingType": "Int16", "Members": [ { "Name": "Low", "Value": -5 }, { "Name": "High", "Value": 7 } ] },
        { "Id": 2, "Name": "Mask", "UnderlyingType": "UInt32", "Flags": true, "Members": [ { "Name": "A", "Value": 1 }, { "Name": "B", "Value": 2 } ] }
        """;

    public static LatticeSchema Compile(string encoding = "Utf8") => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "Enums": [ {{Enums}} ],
          "EntityTypes": [
            { "Id": 1, "Name": "Item", "Fields": [ {{Fields()}} ], "PrimaryKey": [1] },
            { "Id": 2, "Name": "Twin", "Fields": [ {{Fields()}} ], "PrimaryKey": [1] }
          ]
        }
        """);

    public static RootArena Open(string encoding = "Utf8") => new(Compile(encoding));

    // Sets the key and every other field that is not nullable to a valid value of its type, all but
    // the fields named, so that Complete finds no field missing but those.
    public static void SetRequired(ref RootWriter writer, RootArena item, params string[] except)
    {
        foreach (var field in item.EntityType.Fields)
        {
            if (!field.Declaration.IsNullable && !except.Contains(field.Name))
            {
                WriterMembers.SetValid(ref writer, field);
            }
        }
    }

    // Writes an entity of Item with every field set to a valid value, the nullable ones included.
    public static ArenaAddress WriteFull(RootArena item)
    {
        var writer = item.Writer();

        foreach (var field in item.EntityType.Fields)
        {
            WriterMembers.SetValid(ref writer, field);
        }

        return item.Publish(writer.Complete());
    }

    private static string Fields()
    {
        var fields = new StringBuilder("{ \"Id\": 1, \"Name\": \"Id\", \"Type\": \"Int64\" }");

        for (var index = 0; index < Names.Count; index++)
        {
            var name = Names[index];
            var declaration = name switch
            {
                "SignedEnum" => "\"Type\": \"Enum\", \"Enum\": \"Level\"",
                "UnsignedEnum" => "\"Type\": \"Enum\", \"Enum\": \"Mask\"",
                _ => $"\"Type\": \"{name}\"",
            };

            fields.Append($", {{ \"Id\": {2 + 2 * index}, \"Name\": \"{name}\", {declaration} }}");
            fields.Append($", {{ \"Id\": {3 + 2 * index}, \"Name\": \"{name}OrNull\", {declaration}, \"Nullable\": true }}");
        }

        return fields.ToString();
    }
}
