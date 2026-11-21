namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// The searches of the binding tests over an index of Item on one field, "By" and its name, and the writes
// that fill it. Every search runs through the class and by name; in the order of the index by Find and by
// FindAsync, in reverse by FindAsync, which takes the direction in its options. Each answer is held
// against the oracle (ValueOracle), and a refusal is the ArgumentException of a key the index cannot serve,
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

    // That a search of the key over the index of the field finds what the oracle expects, in every form.
    public static async Task ExpectAsync(ILatticeDatabase database, string field, IndexKey key, bool reverse, IReadOnlyList<Stored> expected, string search, IComparer<object>? order = null)
    {
        var byClass = database.Set<Item>().Index($"By{field}");
        var byName = database.Set("Item").Index($"By{field}");
        var options = reverse ? Backward : Forward;

        ValueOracle.Expect($"{search} through the class", expected, Of(field, (await byClass.FindAsync(key, options)).Items), order);
        ValueOracle.Expect($"{search} by name", expected, Of(field, (await byName.FindAsync(key, options)).Items), order);

        if (!reverse)
        {
            ValueOracle.Expect($"{search} by the lazy Find through the class", expected, Of(field, [.. byClass.Find(key)]), order);
            ValueOracle.Expect($"{search} by the lazy Find by name", expected, Of(field, [.. byName.Find(key)]), order);
        }
    }

    // That a range over the stored entities finds what the oracle expects, in both directions and every
    // form.
    public static async Task ExpectRangeAsync(ILatticeDatabase database, string field, IReadOnlyList<Stored> stored, RangeEnd? from, RangeEnd? to, IComparer<object>? order = null)
    {
        var key = from is null && to is null ? IndexKey.All : IndexKey.All.Range(from?.Bound(), to?.Bound());

        foreach (var reverse in new[] { false, true })
        {
            var expected = ValueOracle.Range(stored, from, to, reverse, order);
            var search = $"By{field} from {Show(from)} to {Show(to)}{(reverse ? " in reverse" : "")}";

            await ExpectAsync(database, field, key, reverse, expected, search, order);
        }
    }

    // That the index refuses the key with ArgumentException through the class and by name, by Find and by
    // FindAsync.
    public static async Task ExpectRefusedAsync(ILatticeDatabase database, string index, IndexKey key, string search)
    {
        var byClass = database.Set<Item>().Index(index);
        var byName = database.Set("Item").Index(index);

        Assert.True(Record.Exception(() => byClass.Find(key).ToList()) is ArgumentException, $"{search} through the class was not refused by Find.");
        Assert.True(Record.Exception(() => byName.Find(key).ToList()) is ArgumentException, $"{search} by name was not refused by Find.");
        Assert.True(await Record.ExceptionAsync(async () => await byClass.FindAsync(key, Forward)) is ArgumentException, $"{search} through the class was not refused by FindAsync.");
        Assert.True(await Record.ExceptionAsync(async () => await byName.FindAsync(key, Forward)) is ArgumentException, $"{search} by name was not refused by FindAsync.");
    }

    private static List<Stored> Of(string field, IEnumerable<Item> items) => [.. items.Select(item => new Stored(item.Id, item.ValueOf(field)))];

    private static List<Stored> Of(string field, IEnumerable<LatticeRecord> records) =>
        [.. records.Select(record => new Stored(record.Get<long>("Id"), record[field]))];

    private static string Show(RangeEnd? end) => end is null ? "open" : $"{(end.Inclusive ? "inclusive" : "exclusive")} {end.Value}";
}
