using System.Collections;
using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Fixture;

// Comparing and copying the records of the fixture by their values. A record holds a List as a
// List, a Set as a HashSet and a Map as a Dictionary (LatticeValueDeclaration.ValueType): a List is
// equal to another with the same elements in the same order, a Set or a Map to another with the
// same members, in whatever order they enumerate. The comparison is written out rather than made
// with an assertion per value, because the tests compare 121,000 records at a time.
internal static class RecordValues
{
    // Whether the two records are of entity types of the same name and every field holds an equal
    // value.
    public static bool Equal(LatticeRecord expected, LatticeRecord actual)
    {
        if (expected.EntityType.Name != actual.EntityType.Name)
        {
            return false;
        }

        foreach (var field in expected.EntityType.Fields)
        {
            if (!ValueEqual(expected[field.Name], actual[field.Name]))
            {
                return false;
            }
        }

        return true;
    }

    // A new record of the same entity type with the same values; the record copies every value.
    public static LatticeRecord Copy(LatticeRecord record)
    {
        var copy = new LatticeRecord(record.EntityType);

        foreach (var field in record.EntityType.Fields)
        {
            copy[field.Name] = record[field.Name];
        }

        return copy;
    }

    private static bool ValueEqual(object? expected, object? actual)
    {
        switch (expected)
        {
            case IDictionary map:
                if (actual is not IDictionary actualMap || actualMap.Count != map.Count)
                {
                    return false;
                }

                foreach (DictionaryEntry entry in map)
                {
                    if (!actualMap.Contains(entry.Key) || !Equals(entry.Value, actualMap[entry.Key]))
                    {
                        return false;
                    }
                }

                return true;
            case IList list:
                return actual is IList actualList && list.Cast<object?>().SequenceEqual(actualList.Cast<object?>());
            case IEnumerable set when expected is not string:
                return actual is IEnumerable actualSet && set.Cast<object?>().ToHashSet().SetEquals(actualSet.Cast<object?>());
            default:
                return Equals(expected, actual);
        }
    }
}
