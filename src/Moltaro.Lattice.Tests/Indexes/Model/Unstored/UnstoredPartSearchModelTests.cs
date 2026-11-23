namespace Moltaro.Lattice.Tests.Indexes.Model.Unstored;

// A search of an index of a part the engine cannot store yet agrees with the model of the order, as every
// other part does (ModelRun, ModelSearches), once its feature is implemented (SkipReason):
// - a Reference part is compared as the key of its target (LatticeIndex), here an Int64, nullable and never
//   null; a reference is a key without integrity enforcement, so it may name a key no entity has;
// - a part on a path into an owned value is the field at the end of the path, and null where the owned value
//   is absent, here the City of the nullable owned value Home (IndexKey; LatticeIndexPart).
// Each stores 400 entities and searches every value it stored and values it did not, ranges, both
// directions and pages, through the class and by name, on a master and on a child.
public sealed class UnstoredPartSearchModelTests
{
    private const int Count = 400;

    // The keys no entity is given as a reference, nor as a City.
    private static readonly IReadOnlyList<object> NeverReferenced = [50L, 100L];

    [Fact(Skip = SkipReason.OwnedValuesAndReferences)]
    public Task Search_of_an_index_of_a_nullable_Reference_part_by_value_by_range_and_in_pages_agrees_with_the_model_on_a_master_and_a_child()
    {
        var table = new ModelTable<Colleague>
        {
            EntityType = "Colleague",
            Index = "ByManager",
            Fields = ["Manager"],
            Model = new IndexModel(IndexModel.ByValue),
            ToEntity = entry => new Colleague { Id = entry.Id, Manager = (long?)entry.Key[0] },
            FromEntity = colleague => new ModelEntry(colleague.Id, [colleague.Manager]),
            Equal = values => values.Count == 0 ? IndexKey.All : IndexKey.Equal((long?)values[0]),
        };

        return RunAsync(table, UnstoredSchema.References, References(nullable: true), [NeverReferenced]);
    }

    [Fact(Skip = SkipReason.OwnedValuesAndReferences)]
    public Task Search_of_an_index_of_a_Reference_part_never_null_by_value_by_range_and_in_pages_agrees_with_the_model_on_a_master_and_a_child()
    {
        var table = new ModelTable<Apprentice>
        {
            EntityType = "Apprentice",
            Index = "ByMentor",
            Fields = ["Mentor"],
            Model = new IndexModel(IndexModel.ByValue),
            ToEntity = entry => new Apprentice { Id = entry.Id, Mentor = (long)entry.Key[0]! },
            FromEntity = apprentice => new ModelEntry(apprentice.Id, [apprentice.Mentor]),
            Equal = values => values.Count == 0 ? IndexKey.All : IndexKey.Equal((long)values[0]!),
        };

        return RunAsync(table, UnstoredSchema.References, References(nullable: false), [NeverReferenced]);
    }

    [Fact(Skip = SkipReason.OwnedValuesAndReferences)]
    public Task Search_of_an_index_on_a_path_into_an_owned_value_by_value_by_range_and_in_pages_agrees_with_the_model_with_a_null_where_the_owned_value_is_absent()
    {
        var table = new ModelTable<Resident>
        {
            EntityType = "Resident",
            Index = "ByHomeCity",
            Fields = ["Home.City"],
            Model = new IndexModel(IndexModel.Ordinal),
            ToEntity = entry => new Resident { Id = entry.Id, Home = entry.Key[0] is string city ? new Residence { City = city } : null },
            FromEntity = resident => new ModelEntry(resident.Id, [resident.Home?.City]),
            Equal = values => values.Count == 0 ? IndexKey.All : IndexKey.Equal((string?)values[0]),
        };
        var random = new Random(ModelRun.Seed);
        string?[] cities = [null, "", "Bergen", "bergen", "Oslo", "Tromsø", "Ålesund", "\U0001F600", new string('x', 300)];
        var given = Enumerable.Range(1, Count).Select(id => new ModelEntry(id, [cities[random.Next(cities.Length)]])).ToList();

        return RunAsync(table, UnstoredSchema.OwnedPath, given, [["Molde", "\U0000FFFF"]]);
    }

    // The entities of a reference: the key of another entity or, for a few, a key no entity has, the least
    // and the greatest Int64 or -1; about one in eight without a reference when it is nullable.
    private static List<ModelEntry> References(bool nullable)
    {
        var random = new Random(ModelRun.Seed);

        object? Reference()
        {
            var draw = random.Next(Count + 3);

            return draw switch
            {
                < Count when nullable && random.Next(8) == 0 => null,
                < Count => (draw + 1) % 50 == 0 ? 1L : draw + 1L,
                Count => long.MinValue,
                Count + 1 => -1L,
                _ => long.MaxValue,
            };
        }

        return [.. Enumerable.Range(1, Count).Select(id => new ModelEntry(id, [Reference()]))];
    }

    private static Task RunAsync<TEntity>(ModelTable<TEntity> table, LatticeSchema schema, List<ModelEntry> given, IReadOnlyList<IReadOnlyList<object>> neverStored)
        where TEntity : class =>
        ModelRun.RunAsync(
            $"{table.EntityType}.{table.Fields[0]}, {Count} entities of seed {ModelRun.Seed}",
            schema,
            table,
            given,
            ModelSearches.Of(table.Model, given, neverStored, mostKeys: int.MaxValue),
            ModelSearches.Pages(table.Model, given));
}
