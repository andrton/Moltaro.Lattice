namespace Moltaro.Lattice.Tests.Indexes.Model;

// A composite key of the model tests (CompositeKeys) and how a cell of it runs: Count entities, each part
// drawn by a generator of the seed ScalarRun.Seed from the few values it takes, a null among them where the
// part can be null, so that every count of leading parts has runs of entities and a null stands in every
// position the index allows. The searches are every count of leading parts, each with at most MostKeys of
// the distinct keys spread over the order and with keys no entity has, and ranges on the part after them
// from bounds on stored values, between them and beyond both ends (ModelSearches); each in both directions,
// through the class and by name, on a master and on a child (ModelRun).
internal sealed class CompositeKey<TEntity>
    where TEntity : class
{
    public const int Count = 400;

    private const int MostKeys = 8;

    public required ModelTable<TEntity> Table { get; init; }

    // The values each part takes, in the order of the index.
    public required IReadOnlyList<IReadOnlyList<object?>> Values { get; init; }

    // Values of each part that no entity has: between the values it takes, or beyond them.
    public required IReadOnlyList<IReadOnlyList<object>> NeverStored { get; init; }

    public Task RunAsync(string encoding)
    {
        var random = new Random(ScalarRun.Seed);
        var given = new List<ModelEntry>();

        for (var id = 1L; id <= Count; id++)
        {
            given.Add(new ModelEntry(id, [.. Values.Select(values => values[random.Next(values.Count)])]));
        }

        return ModelRun.RunAsync(
            $"{Table.EntityType}({string.Join(", ", Table.Fields)}) in {encoding}, {Count} entities of seed {ScalarRun.Seed}",
            ModelSchema.Composite(encoding),
            Table,
            given,
            ModelSearches.Of(Table.Model, given, NeverStored, MostKeys),
            ModelSearches.Pages(Table.Model, given));
    }
}
