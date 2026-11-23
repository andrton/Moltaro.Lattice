namespace Moltaro.Lattice.Tests.Indexes.Model;

// How the entities of one entity type of a model test are written and read, and how a search of its index
// is given: the entity type and its index, the fields of the index in its order and the model of that
// order; an object of the class TEntity made from an entry of the model and read back as one; and the key
// of the leading values of a search, each given as the .NET type of its field, as an application gives
// it. A record is made and read by the names of the fields (ModelRun).
internal sealed class ModelTable<TEntity>
    where TEntity : class
{
    public required string EntityType { get; init; }

    public required string Index { get; init; }

    public required IReadOnlyList<string> Fields { get; init; }

    public required IndexModel Model { get; init; }

    public required Func<ModelEntry, TEntity> ToEntity { get; init; }

    public required Func<TEntity, ModelEntry> FromEntity { get; init; }

    // The key of the leading values given: IndexKey.All for none.
    public required Func<IReadOnlyList<object?>, IndexKey> Equal { get; init; }

    // The key of the search: its leading values and its range, made with the value of each bound as its
    // own .NET type (TypedValues).
    public IndexKey KeyOf(ModelSearch search)
    {
        var key = Equal(search.Leading);

        return search.HasRange ? key.Range(search.From?.Bound(), search.To?.Bound()) : key;
    }
}
