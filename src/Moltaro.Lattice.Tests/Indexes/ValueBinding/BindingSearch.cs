using Moltaro.Lattice.Tests.Indexes.Model;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// The searches of the binding tests over an index of Item on one field, "By" and its name, and the writes
// that fill it. Every search runs through the class and by name; in the order of the index by Find and by
// FindAsync, in reverse by FindAsync, which takes the direction in its options. Each answer is held
// against the model of the order (IndexModel), of one part ordered by the comparison a test gives or by
// value, and each entity returned against the value it was stored with, bit for bit; a refusal is the ArgumentException of a key the index cannot serve,
// by Find and by FindAsync, before anything is searched.
internal static class BindingSearch
{
    private static readonly LatticeFindOptions Forward = new();

    private static readonly LatticeFindOptions Backward = new() { Reverse = true };

    // Inserts each entity with its value in the field named, a Count of 0 and no other value: the even ids
    // through the class, the odd ones by name.
    public static async Task InsertAsync(ILatticeDatabase database, string field, IEnumerable<Stored> stored)
    {
        var records = database.Set("Item");

        foreach (var entity in stored)
        {
            if (entity.Id % 2 == 0)
            {
                await database.Set<Item>().InsertAsync(Item.Of(entity.Id, field, entity.Value));
            }
            else
            {
                var record = new LatticeRecord(records.EntityType) { ["Id"] = entity.Id, ["Count"] = 0 };

                if (entity.Value is not null)
                {
                    record[field] = entity.Value;
                }

                await records.InsertAsync(record);
            }
        }
    }

    // The entities, numbered from 1, each with one of the values in the field named.
    public static List<Stored> Numbered(params object?[] values) =>
        [.. values.Select((value, place) => new Stored(place + 1, value))];

    // The entities whose value equals the value given, in the order of the index (IndexModel).
    public static List<Stored> Equal(IEnumerable<Stored> stored, object? value, IComparer<object>? order = null) =>
        Answer(stored, new ModelSearch([value], null, null, Reverse: false), order);

    // The entities a range finds, either end open, in the order of the index or its reverse; with both ends
    // open, every entity of the index (IndexModel).
    public static List<Stored> Range(IEnumerable<Stored> stored, RangeEnd? from, RangeEnd? to, bool reverse, IComparer<object>? order = null) =>
        Answer(stored, new ModelSearch([], from, to, reverse), order);

    // That a search of the key over the index of the field finds what the model expects, in every form.
    public static async Task ExpectAsync(ILatticeDatabase database, string field, IndexKey key, bool reverse, IReadOnlyList<Stored> expected, string search, IComparer<object>? order = null)
    {
        var byClass = database.Set<Item>().Index($"By{field}");
        var byName = database.Set("Item").Index($"By{field}");
        var options = reverse ? Backward : Forward;

        Expect($"{search} through the class", expected, Of(field, (await byClass.FindAsync(key, options)).Items), order);
        Expect($"{search} by name", expected, Of(field, (await byName.FindAsync(key, options)).Items), order);

        if (!reverse)
        {
            Expect($"{search} by the lazy Find through the class", expected, Of(field, [.. byClass.Find(key)]), order);
            Expect($"{search} by the lazy Find by name", expected, Of(field, [.. byName.Find(key)]), order);
        }
    }

    // That a range over the stored entities finds what the model expects, in both directions and every
    // form.
    public static async Task ExpectRangeAsync(ILatticeDatabase database, string field, IReadOnlyList<Stored> stored, RangeEnd? from, RangeEnd? to, IComparer<object>? order = null)
    {
        var key = from is null && to is null ? IndexKey.All : IndexKey.All.Range(from?.Bound(), to?.Bound());

        foreach (var reverse in new[] { false, true })
        {
            var expected = Range(stored, from, to, reverse, order);
            var search = $"By{field} from {Show(from)} to {Show(to)}{(reverse ? " in reverse" : "")}";

            await ExpectAsync(database, field, key, reverse, expected, search, order);
        }
    }

    // That the index refuses the key with ArgumentException itself through the class and by name, by Find
    // and by FindAsync (ArgumentRefusal).
    public static async Task ExpectRefusedAsync(ILatticeDatabase database, string index, IndexKey key, string search)
    {
        var byClass = database.Set<Item>().Index(index);
        var byName = database.Set("Item").Index(index);

        ArgumentRefusal.Expect(() => byClass.Find(key).ToList(), $"{search} through the class by Find");
        ArgumentRefusal.Expect(() => byName.Find(key).ToList(), $"{search} by name by Find");
        await ArgumentRefusal.ExpectAsync(async () => await byClass.FindAsync(key, Forward), $"{search} through the class by FindAsync");
        await ArgumentRefusal.ExpectAsync(async () => await byName.FindAsync(key, Forward), $"{search} by name by FindAsync");
    }

    // That the answer is the one of the model: the same values in the same order, the same ids, and each
    // entity with the value stored for its id, bit for bit.
    private static void Expect(string search, IReadOnlyList<Stored> expected, IReadOnlyList<Stored> actual, IComparer<object>? order)
    {
        List<ModelEntry> expectedEntries = [.. expected.Select(EntryOf)];
        List<ModelEntry> actualEntries = [.. actual.Select(EntryOf)];

        ModelOf(order).Expect(search, expectedEntries, actualEntries);
        IndexModel.ExpectStored(search, expectedEntries, actualEntries);
    }

    private static List<Stored> Answer(IEnumerable<Stored> stored, ModelSearch search, IComparer<object>? order) =>
        [.. ModelOf(order).Answer(stored.Select(EntryOf), search).Select(entry => new Stored(entry.Id, entry.Key[0]))];

    private static IndexModel ModelOf(IComparer<object>? order) => new(order ?? IndexModel.ByValue);

    private static ModelEntry EntryOf(Stored stored) => new(stored.Id, [stored.Value]);

    private static List<Stored> Of(string field, IEnumerable<Item> items) => [.. items.Select(item => new Stored(item.Id, item.ValueOf(field)))];

    private static List<Stored> Of(string field, IEnumerable<LatticeRecord> records) =>
        [.. records.Select(record => new Stored(record.Get<long>("Id"), record[field]))];

    private static string Show(RangeEnd? end) => end is null ? "open" : $"{(end.Inclusive ? "inclusive" : "exclusive")} {end.Value}";
}
