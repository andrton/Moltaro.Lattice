using Moltaro.Lattice.Access.Values;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Sets;

// An index key checked against the index it is used with: values for the leading fields in the types
// of those fields, and an optional range on the field after them, which is the field of the part at
// the place Count of the key. This is what an index search works from. The key it returns is the key it
// was given with every value bound to its field (IndexValue.Bind), so a key that is accepted is
// checked without allocating and without a box; a refusal is an ArgumentException for the parameter
// "key", with the message of the check that refuses it.
internal static class IndexCriteria
{
    // Checks that the index can serve the key and brings its values to the field types.
    public static IndexKey Prepare(LatticeIndex index, in IndexKey key)
    {
        var parts = index.Parts;

        if (key.Count > parts.Count)
        {
            throw new ArgumentException($"Index '{index.Name}' covers {parts.Count} field(s); the key has {key.Count} values.", nameof(key));
        }

        var value1 = Equal(index, key, 0);
        var value2 = Equal(index, key, 1);
        var value3 = Equal(index, key, 2);
        var value4 = Equal(index, key, 3);

        if (key.From is null && key.To is null)
        {
            return new IndexKey(key.Count, value1, value2, value3, value4, null, null);
        }

        if (key.Count == parts.Count)
        {
            throw new ArgumentException($"Index '{index.Name}' has no field left for a range: the key gives a value to every field.", nameof(key));
        }

        var rangeField = parts[key.Count].Field;

        return new IndexKey(key.Count, value1, value2, value3, value4, Range(rangeField, key.From), Range(rangeField, key.To));
    }

    // The value of the part at the place given bound to its field; the default, no value, for a place
    // after the values of the key.
    private static IndexValue Equal(LatticeIndex index, in IndexKey key, int place)
    {
        if (place >= key.Count)
        {
            return default;
        }

        // A part can be null when its field is nullable or when an owned value on its path is, which
        // is then absent. A Reference is given as the key of its target.
        var part = index.Parts[place];
        var value = key.ValueAt(place);

        if (value.IsNull)
        {
            return part.CanBeNull
                ? value
                : throw new ArgumentException($"Index '{index.Name}': field '{part.Field.Name}' is never null.", nameof(key));
        }

        return value.Bind(part.Field, nameof(key));
    }

    // A bound of the range bound to the field after the equal ones; a bound always has a value.
    private static IndexBound? Range(LatticeField field, IndexBound? bound)
    {
        if (bound is not { } given)
        {
            return null;
        }

        if (given.Held.IsNull)
        {
            throw new ArgumentException($"A bound of a range on field '{field.Name}' has no value; IndexBound.Inclusive and IndexBound.Exclusive make one.", "key");
        }

        return new IndexBound(given.Held.Bind(field, "key"), given.IsInclusive);
    }
}
