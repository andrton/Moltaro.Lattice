namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// Bound to the entity type "Keyed" of BindingSchema.KeyedBy: a key and an indexed value of one key type.
internal sealed class Keyed<TKey>
{
    public TKey Id { get; set; } = default!;

    public TKey Value { get; set; } = default!;
}
