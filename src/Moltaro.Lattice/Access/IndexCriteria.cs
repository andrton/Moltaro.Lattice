using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access;

// An index key checked against the index it is used with: values for the leading fields
// in the types of those fields, and an optional range on the field after them. This is
// what an index search works from.
internal sealed record IndexCriteria(LatticeIndex Index, object?[] Equal, LatticeField? RangeField, IndexCriteria.Bound? From, IndexCriteria.Bound? To)
{
    // Checks that the index can serve the key and brings its values to the field types.
    public static IndexCriteria Prepare(LatticeIndex index, IndexKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var parts = index.Parts;
        if (key.Values.Length > parts.Count)
        {
            throw new ArgumentException($"Index '{index.Name}' covers {parts.Count} field(s); the key has {key.Values.Length} values.", nameof(key));
        }

        var equal = new object?[key.Values.Length];
        for (var part = 0; part < equal.Length; part++)
        {
            // A part can be null when its field is nullable or when an owned value on its path
            // is, which is then absent. A Reference is given as the key of its target.
            var field = parts[part].Field;
            var value = key.Values[part];
            if (value is null && !parts[part].CanBeNull)
            {
                throw new ArgumentException($"Index '{index.Name}': field '{field.Name}' is never null.", nameof(key));
            }

            equal[part] = value is null ? null : FieldValues.Normalize(field, value, nameof(key));
        }

        if (key.From is null && key.To is null)
        {
            return new IndexCriteria(index, equal, null, null, null);
        }

        if (equal.Length == parts.Count)
        {
            throw new ArgumentException($"Index '{index.Name}' has no field left for a range: the key gives a value to every field.", nameof(key));
        }

        var rangeField = parts[equal.Length].Field;
        return new IndexCriteria(index, equal, rangeField, Normalize(rangeField, key.From), Normalize(rangeField, key.To));

        static Bound? Normalize(LatticeField field, IndexBound? bound) =>
            bound is null ? null : new Bound(FieldValues.Normalize(field, bound.Value, nameof(key)), bound.IsInclusive);
    }

    internal readonly record struct Bound(object Value, bool IsInclusive);
}
