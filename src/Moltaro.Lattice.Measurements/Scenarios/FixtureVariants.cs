using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The variants of the descriptors of the reference fixture that the scenarios store, and the copy of a
// record of the fixture into a schema of such a variant. A variant that changes a fingerprint has records
// of its own only as copies: the fixture generates its records for one of its forms (ReferenceFixture).
// Each variant checks that the descriptor declares what it changes, so that a change of the fixture's
// descriptor fails the scenario instead of leaving the variant unchanged.
internal static class FixtureVariants
{
    // The descriptor with the class named as the ClrType of the entity type, which changes neither
    // fingerprint, so the schema is still the form it was.
    public static string WithClass(string json, string entityTypeName, Type type)
    {
        var entityType = $"\"Name\": \"{entityTypeName}\",";

        if (!json.Contains(entityType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The descriptor of the fixture does not name the entity type {entityTypeName} as this scenario expects.");
        }

        return json.Replace(entityType, $"{entityType} \"ClrType\": \"{type.FullName}\",", StringComparison.Ordinal);
    }

    // The descriptor with Region of the users in Dictionary storage, as the full form declares it.
    public static string WithDictionaryRegion(string json)
    {
        const string Region = "\"Name\": \"Region\", \"Type\": \"String\"";

        if (!json.Contains(Region, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The descriptor of the fixture does not declare Region as this scenario expects.");
        }

        return json.Replace(Region, $"{Region}, \"StringStorage\": \"Dictionary\"", StringComparison.Ordinal);
    }

    // The descriptor with no entity type declaring an index: every list of indexes left empty.
    public static string WithoutIndexes(string json)
    {
        const string Indexes = "\"Indexes\": [";

        var first = json.IndexOf(Indexes, StringComparison.Ordinal);

        if (first < 0)
        {
            throw new InvalidOperationException("The descriptor of the fixture declares no indexes as this scenario expects.");
        }

        var result = new StringBuilder();
        var from = 0;

        for (var start = first; start >= 0; start = json.IndexOf(Indexes, from, StringComparison.Ordinal))
        {
            // The list ends at the bracket that closes the one it opens; the lists of fields inside it open
            // and close brackets of their own.
            var close = start + Indexes.Length;

            for (var depth = 1; depth > 0; close++)
            {
                depth += json[close] switch
                {
                    '[' => 1,
                    ']' => -1,
                    _ => 0,
                };
            }

            result.Append(json, from, start + Indexes.Length - from).Append(']');
            from = close;
        }

        return result.Append(json, from, json.Length - from).ToString();
    }

    // A record of the fixture as a record of the entity type of the same name of the schema, field by field.
    public static LatticeRecord CopyInto(LatticeSchema schema, LatticeRecord record)
    {
        if (!schema.TryGetEntityType(record.EntityType.Name, out var entityType))
        {
            throw new InvalidOperationException($"The schema has no entity type {record.EntityType.Name}.");
        }

        var copy = new LatticeRecord(entityType);

        foreach (var field in record.EntityType.Fields)
        {
            copy[field.Name] = record[field.Name];
        }

        return copy;
    }
}
