namespace Moltaro.Lattice.Tests.Indexes.Unique;

// A key of a unique index with a null in any of its parts is not complete and is not compared: any number
// of entities may hold it, and a search for it finds them all (decision on unique indexes, "If any key
// component is null ... exclude that key from uniqueness enforcement. It still participates in ordinary
// index queries"). A complete key of the same index keeps one owner. Slots of UniqueSchemas, whose unique
// index ByABC has three parts that can each be null: a null in the first, in the middle one, in the last,
// and in all of them.
public sealed class NullExemptionTests
{
    public static IEnumerable<TheoryDataRow<string?, int?, string?>> KeysWithANull =>
    [
        new(null, 2, "c"),
        new("a", null, "c"),
        new("a", 2, null),
        new(null, null, null),
    ];

    [Theory]
    [MemberData(nameof(KeysWithANull))]
    public async Task Key_with_a_null_in_any_part_is_held_by_several_entities_and_a_search_finds_them_all(string? a, int? b, string? c)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Slots);
        var slots = database.Set("Slot");

        for (var id = 1L; id <= 3; id++)
        {
            await slots.InsertAsync(UniqueSchemas.RecordOf(slots, id, ("A", a), ("B", b), ("C", c)));
        }

        Assert.Equal([1L, 2L, 3L], UniqueSchemas.IdsOf(slots.Index("ByABC").Find(IndexKey.Equal(a, b, c))));

        // A replace onto the same key with a null, of an entity that held a complete one, is accepted too.
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 4, ("A", "a"), ("B", 2), ("C", "c")));
        await slots.ReplaceAsync(UniqueSchemas.RecordOf(slots, 4, ("A", a), ("B", b), ("C", c)));

        Assert.Equal([1L, 2L, 3L, 4L], UniqueSchemas.IdsOf(slots.Index("ByABC").Find(IndexKey.Equal(a, b, c))));
    }

    [Theory]
    [MemberData(nameof(KeysWithANull))]
    public async Task Complete_key_beside_keys_with_a_null_keeps_one_owner(string? a, int? b, string? c)
    {
        await using var database = await TestData.OpenMasterAsync(schema: UniqueSchemas.Slots);
        var slots = database.Set("Slot");

        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 1, ("A", a), ("B", b), ("C", c)));
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 2, ("A", a), ("B", b), ("C", c)));
        await slots.InsertAsync(UniqueSchemas.RecordOf(slots, 3, ("A", "a"), ("B", 2), ("C", "c")));

        await UniqueSchemas.ExpectTakenAsync("ByABC", () => slots.InsertAsync(UniqueSchemas.RecordOf(slots, 4, ("A", "a"), ("B", 2), ("C", "c"))));
        await UniqueSchemas.ExpectTakenAsync("ByABC", () => slots.ReplaceAsync(UniqueSchemas.RecordOf(slots, 1, ("A", "a"), ("B", 2), ("C", "c"))));

        Assert.Equal([3L], UniqueSchemas.IdsOf(slots.Index("ByABC").Find(IndexKey.Equal("a", 2, "c"))));
        Assert.Equal([1L, 2L], UniqueSchemas.IdsOf(slots.Index("ByABC").Find(IndexKey.Equal(a, b, c))));
    }
}
