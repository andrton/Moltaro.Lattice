using System.Text.Json;

namespace Moltaro.Lattice.Schema;

// Reads the JSON schema descriptor. The descriptor grammar is not final; properties this
// reader does not know are ignored.
internal static class SchemaJsonReader
{
    public static LatticeSchema Read(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("SchemaVersion", out var versionElement) ? versionElement.GetInt32() : 1;

            var entityTypes = new List<LatticeEntityType>();
            foreach (var table in root.GetProperty("Tables").EnumerateArray())
            {
                entityTypes.Add(ReadEntityType(table));
            }

            return SchemaBuilder.BuildSchema(version, entityTypes);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw SchemaBuilder.Invalid("the JSON descriptor cannot be read.", exception);
        }
    }

    private static LatticeEntityType ReadEntityType(JsonElement table)
    {
        var name = table.GetProperty("Name").GetString();
        var clrTypeName = table.TryGetProperty("ClrType", out var clrType) ? clrType.GetString() : null;

        var primaryKey = table.GetProperty("PrimaryKey");
        if (primaryKey.GetArrayLength() != 1)
        {
            throw SchemaBuilder.Invalid($"entity type '{name}' must have exactly one primary key field.");
        }

        var fields = new List<FieldDraft>();
        foreach (var field in table.GetProperty("Fields").EnumerateArray())
        {
            var typeName = field.GetProperty("Type").GetString();
            if (!Enum.TryParse<LatticeFieldTypeEnum>(typeName, out var type) || !Enum.IsDefined(type) || type.ToString() != typeName)
            {
                throw SchemaBuilder.Invalid($"field '{name}.{field.GetProperty("Name").GetString()}' has the unknown type '{typeName}'.");
            }

            fields.Add(new FieldDraft(
                field.GetProperty("Id").GetInt32(),
                field.GetProperty("Name").GetString(),
                type,
                field.TryGetProperty("Nullable", out var nullable) && nullable.GetBoolean()));
        }

        var indexes = new List<IndexDraft>();
        if (table.TryGetProperty("Indexes", out var indexElements))
        {
            foreach (var index in indexElements.EnumerateArray())
            {
                indexes.Add(new IndexDraft(
                    index.GetProperty("Name").GetString(),
                    [.. index.GetProperty("Fields").EnumerateArray().Select(fieldId => fieldId.GetInt32())],
                    index.TryGetProperty("Unique", out var unique) && unique.GetBoolean()));
            }
        }

        return SchemaBuilder.BuildEntityType(table.GetProperty("Id").GetInt32(), name, clrTypeName, fields, primaryKey[0].GetInt32(), indexes);
    }
}
