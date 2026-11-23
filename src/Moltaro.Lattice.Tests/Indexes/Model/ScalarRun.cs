namespace Moltaro.Lattice.Tests.Indexes.Model;

// How a cell of one scalar type runs (ScalarCell, ModelRun): Count entities of Sample, each with a value
// of the type or, when the field is nullable, about one in eight with none; the edge values of the type,
// then values from a generator of the seed Seed, a third of them repeats of a value drawn before, so that
// keys have runs of entities; in an order the generator shuffles. A nullable cell holds back the least and
// the greatest value of the type, and no cell stores a value between (ScalarType). The searches are every
// distinct value stored, null among them, and every value between and held back, each by itself, and
// ranges over the whole index from bounds on stored values, between them and beyond both ends
// (ModelSearches).
//
// Count makes several leaves certain for every type: a leaf of 1 KiB holds at most 109 entries of the
// narrowest key, an identity of 8 bytes and one byte of value, so 400 entries take at least four leaves,
// and the keys of 16 bytes at least ten (docs/design/ordered-index.md, "Nodes in the arena").
internal static class ScalarRun
{
    public const int Count = 400;

    public const int Seed = 20_261_123;

    // How a cell of a type whose .NET type is the value type given runs: with a Nullable of it when the
    // field is nullable.
    public static Func<ScalarCell, Task> Of<T>()
        where T : struct =>
        cell => cell.Nullable ? RunAsync<T?>(cell) : RunAsync<T>(cell);

    // How a cell of a text runs.
    public static Func<ScalarCell, Task> OfText() => RunAsync<string>;

    private static Task RunAsync<TValue>(ScalarCell cell)
    {
        var model = new IndexModel(cell.Type.Order);
        var given = Generate(cell, model);
        var table = new ModelTable<Sample<TValue>>
        {
            EntityType = "Sample",
            Index = "ByValue",
            Fields = ["Value"],
            Model = model,
            ToEntity = entry => new Sample<TValue> { Id = entry.Id, Value = (TValue)entry.Key[0]! },
            FromEntity = sample => new ModelEntry(sample.Id, [sample.Value]),
            Equal = leading => leading switch
            {
                [] => IndexKey.All,
                [{ } value] => TypedValues.Equal(value),
                _ => IndexKey.Equal<TValue>(default!),
            },
        };
        IReadOnlyList<object> neverStored = [.. cell.Type.Between, .. HeldBack(cell)];

        return ModelRun.RunAsync(
            cell.ToString(),
            ModelSchema.Of(cell, typeof(Sample<TValue>)),
            table,
            given,
            ModelSearches.Of(model, given, [neverStored], mostKeys: int.MaxValue),
            ModelSearches.Pages(model, given));
    }

    private static List<ModelEntry> Generate(ScalarCell cell, IndexModel model)
    {
        var random = new Random(Seed);
        var neverStored = cell.Type.Between.Concat(HeldBack(cell)).ToList();

        bool Stores(object value) => !neverStored.Any(never => model.ComparePart(0, never, value) == 0);

        var values = cell.Type.Edges.Where(Stores).Cast<object?>().ToList();

        while (values.Count < Count)
        {
            if (cell.Nullable && random.Next(8) == 0)
            {
                values.Add(null);
            }
            else if (random.Next(3) == 0)
            {
                values.Add(values[random.Next(values.Count)]);
            }
            else if (cell.Type.Random(random) is var drawn && Stores(drawn))
            {
                values.Add(drawn);
            }
        }

        random.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(values));

        return [.. values.Select((value, place) => new ModelEntry(place + 1, [value]))];
    }

    // The least and the greatest value of the type, which a nullable cell does not store.
    private static IEnumerable<object> HeldBack(ScalarCell cell) =>
        cell.Nullable ? new[] { cell.Type.Least, cell.Type.Greatest }.OfType<object>() : [];
}
