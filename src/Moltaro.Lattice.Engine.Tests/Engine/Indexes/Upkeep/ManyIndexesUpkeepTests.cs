using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// What a change does to each index is recorded with the change, one bit for each of the first 63 indexes of
// its entity type, and the last bit stands for every index from the 64th on, which each step decides again
// from the two roots (Entities.PreparedChange.BitOf). An entity type with more indexes than that keeps every
// one of them: Wide has seventy, each of one Int64 field of its own. Two entities are written; then replaces
// change a key among the first indexes, a key among the last ones, and both; then one entity is deleted.
// After each write a search of every index finds each entity by its key in that index, and nothing by a key
// it no longer has, and the walk of each index holds one entry for each entity.
public sealed class ManyIndexesUpkeepTests
{
    private const int Indexes = 70;

    [Fact]
    public async Task Entity_type_with_more_indexes_than_a_mask_has_bits_keeps_every_index_through_replaces_and_a_delete()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var wide = database.Set("Wide");
        var states = new Dictionary<long, long[]>
        {
            [1] = ValuesOf(1),
            [2] = ValuesOf(2),
        };

        foreach (var (id, values) in states)
        {
            await wide.InsertAsync(RecordOf(wide, id, values));
        }

        ExpectFound(database, wide, states, gone: []);

        foreach (var changed in new[] { new[] { 5 }, new[] { 66 }, new[] { 0, 69 } })
        {
            var before = states[1];
            var after = (long[])before.Clone();

            foreach (var field in changed)
            {
                after[field] += 1_000_000;
            }

            await wide.ReplaceAsync(RecordOf(wide, 1, after));
            states[1] = after;

            ExpectFound(database, wide, states, gone: [.. changed.Select(field => (field, before[field]))]);
        }

        await wide.DeleteAsync(2L);
        var deleted = states[2];

        states.Remove(2);

        ExpectFound(database, wide, states, gone: [.. Enumerable.Range(0, Indexes).Select(field => (field, deleted[field]))]);
    }

    // Each index finds each entity by its value there, and no entity by the values gone; its walk holds one
    // entry for each entity.
    private static void ExpectFound(EngineDatabase database, ILatticeSet wide, Dictionary<long, long[]> states, (int Field, long Value)[] gone)
    {
        for (var field = 0; field < Indexes; field++)
        {
            var index = wide.Index($"ByValue{field}");

            foreach (var (id, values) in states)
            {
                Assert.Equal([id], index.Find(IndexKey.Equal(values[field])).Select(found => found.Get<long>("Id")));
            }

            Assert.Equal(states.Count, IndexContents.Of(database, "Wide", $"ByValue{field}").Entries.Count);
        }

        foreach (var (field, value) in gone)
        {
            Assert.Empty(wide.Index($"ByValue{field}").Find(IndexKey.Equal(value)));
        }
    }

    private static long[] ValuesOf(long id) => [.. Enumerable.Range(0, Indexes).Select(field => (id * 1_000) + field)];

    private static LatticeRecord RecordOf(ILatticeSet wide, long id, long[] values)
    {
        var record = new LatticeRecord(wide.EntityType) { ["Id"] = id };

        for (var field = 0; field < Indexes; field++)
        {
            record[$"Value{field}"] = values[field];
        }

        return record;
    }

    // Wide: the key Id and seventy Int64 fields, each in an index of its own.
    private static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Wide",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }{{Fields()}} ],
              "PrimaryKey": [1],
              "Indexes": [ {{Declarations()}} ]
            }
          ]
        }
        """);

    private static string Fields() =>
        string.Concat(Enumerable.Range(0, Indexes).Select(field => $$""", { "Id": {{field + 2}}, "Name": "Value{{field}}", "Type": "Int64" }"""));

    private static string Declarations() =>
        string.Join(", ", Enumerable.Range(0, Indexes).Select(field => $$"""{ "Id": {{field + 1}}, "Name": "ByValue{{field}}", "Fields": [{{field + 2}}] }"""));
}
