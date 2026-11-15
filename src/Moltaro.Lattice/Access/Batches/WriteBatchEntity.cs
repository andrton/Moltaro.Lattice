using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Access.Batches;

// The entity an operation of a write batch addresses: its entity type and its primary key, which the
// batch has brought to the key type the schema declares, as a read by key brings it
// (FieldValues.NormalizeKey, and the capture of an object or a record). Two are the same entity when
// they name the same entity type and their keys are equal as the schema compares them (the planner's
// answer to Q45): a String key under the comparison declared for the key field, by the one rule of
// equality for text (LatticeValueDeclaration.TextComparison), which a Map key and a Set element follow
// as well; any other key by the equality of its .NET value, as a Map key of its type is compared, so an
// integer key given in any width is one value once it is normalized.
internal readonly struct WriteBatchEntity : IEquatable<WriteBatchEntity>
{
    private readonly LatticeEntityType _entityType;

    private readonly object _key;

    public WriteBatchEntity(LatticeEntityType entityType, object key)
    {
        _entityType = entityType;
        _key = key;
    }

    // The comparison of a String key; null for a key of another type.
    private TextComparison? TextComparison => _entityType.Key.Declaration.TextComparison;

    public bool Equals(WriteBatchEntity other) =>
        ReferenceEquals(_entityType, other._entityType)
        && (TextComparison is { } text ? text.Equals((string)_key, (string)other._key) : _key.Equals(other._key));

    public override bool Equals(object? obj) => obj is WriteBatchEntity other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(_entityType, TextComparison is { } text ? text.GetHashCode((string)_key) : _key.GetHashCode());
}
