namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// Bound to the entity type "Sample" of a model test of one scalar type (ScalarSchema): the key Id and
// the indexed field Value, whose .NET type T is the type of the field, a Nullable when the field is
// nullable, and for an Enum the integer of its underlying type.
internal sealed class Sample<T>
{
    public long Id { get; set; }

    public T Value { get; set; } = default!;
}
