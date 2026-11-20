using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Upkeep;

// Two keys can be equal under the comparison of an index and not the same bytes, and an entry holds the
// bytes of its own root's value: a replace that changes only the bytes of a key rewrites its entry at its
// place in the order, by a copy of its leaf, never skips it (docs/design/ordered-index.md, "Entries and their
// order", "The entries of one write"). The rule is the equality of the bytes of the two entries; equality
// under the comparison is not the rule. Four such replaces, each of the middle one of three entities:
// - a text changed in its case only, under a comparison without case (Region of Ranked);
// - a Decimal of another scale, 1.0 to 1.00 (Price of Priced), which compares by its value;
// - +0 to −0 (Value of Measured), which compare equal;
// - a NaN to a NaN of other bits, which compare equal.
// Each states that the write retired a node of its index (RetiredNodes), that the entry in the walk of the
// index (EngineDatabase.IndexNodes, IndexContents) holds the new bytes in the place of the old, and that a
// search by the key, under either of its forms, finds the entity.
public sealed class RewrittenEntryTests
{
    [Fact]
    public async Task Replace_that_changes_a_text_in_its_case_only_under_a_comparison_without_case_rewrites_its_entry_at_its_place()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var ranked = database.Set<IndexSchema.Ranked>();
        var tally = new IndexTally("Utf8");

        foreach (var (id, region) in new[] { (1L, "East"), (2L, "North"), (3L, "South") })
        {
            var entity = new IndexSchema.Ranked { Id = id, Number = id, Login = $"login {id}", Region = region };

            await ranked.InsertAsync(entity);
            tally.Written(entity);
        }

        var replaced = new IndexSchema.Ranked { Id = 2, Number = 2, Login = "login 2", Region = "NORTH" };

        var nodes = await RetiredNodes.OfAsync(database, () => ranked.Get(1L)?.Number, () => ranked.ReplaceAsync(replaced).AsTask(), "Ranked", tally.BytesOf("login 2"));
        tally.Written(replaced);

        Assert.True(nodes > 0, "The replace retired no node of ByRegion.");
        Assert.Equal(["East", "NORTH", "South"], IndexContents.Of(database, "Ranked", "ByRegion").Entries.Select(entry => (string)entry.Key[0]!));
        tally.ExpectEntriesOf(database);
        Assert.Equal([2L], ranked.Index("ByRegion").Find(IndexKey.Equal("north")).Select(entity => entity.Id));
        Assert.Equal([2L], ranked.Index("ByRegion").Find(IndexKey.Equal("North")).Select(entity => entity.Id));
    }

    [Fact]
    public async Task Replace_that_changes_the_scale_of_a_Decimal_key_rewrites_its_entry_with_the_new_scale()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var priced = database.Set("Priced");

        foreach (var (id, price) in new[] { (1L, 0.5m), (2L, 1.0m), (3L, 2.0m) })
        {
            await priced.InsertAsync(new LatticeRecord(priced.EntityType) { ["Id"] = id, ["Price"] = price });
        }

        var nodes = await RetiredNodes.OfAsync(database, () => priced.Get(1L) is null ? null : 1L, () => priced.ReplaceAsync(new LatticeRecord(priced.EntityType) { ["Id"] = 2L, ["Price"] = 1.00m }).AsTask(), "Priced");
        var entries = IndexContents.Of(database, "Priced", "ByPrice").Entries;

        Assert.True(nodes > 0, "The replace retired no node of ByPrice.");
        Assert.Equal([1L, 2L, 3L], entries.Select(entry => entry.Entity));
        Assert.Equal([1, 2, 1], entries.Select(entry => (int)((decimal)entry.Key[0]!).Scale));
        Assert.Equal([2L], FoundIn(priced, "ByPrice", 1m));
        Assert.Equal([2L], FoundIn(priced, "ByPrice", 1.000m));
    }

    [Fact]
    public async Task Replace_of_a_positive_zero_by_a_negative_zero_rewrites_its_entry_with_the_sign()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var measured = database.Set("Measured");

        await InsertMeasuredAsync(measured, -1.0, 0.0, 1.0);

        var nodes = await RetiredNodes.OfAsync(database, () => measured.Get(1L) is null ? null : 1L, () => measured.ReplaceAsync(MeasuredOf(measured, 2, -0.0)).AsTask(), "Measured");
        var entries = IndexContents.Of(database, "Measured", "ByValue").Entries;

        Assert.True(nodes > 0, "The replace retired no node of ByValue.");
        Assert.Equal([1L, 2L, 3L], entries.Select(entry => entry.Entity));
        Assert.Equal(BitConverter.DoubleToInt64Bits(-0.0), BitConverter.DoubleToInt64Bits((double)entries[1].Key[0]!));
        Assert.Equal([2L], FoundIn(measured, "ByValue", 0.0));
        Assert.Equal([2L], FoundIn(measured, "ByValue", -0.0));
    }

    [Fact]
    public async Task Replace_of_a_NaN_by_a_NaN_of_other_bits_rewrites_its_entry_with_the_new_bits()
    {
        await using var database = await EngineMaster.OpenAsync(UpkeepSchema.Schema);
        var measured = database.Set("Measured");
        var otherNaN = BitConverter.Int64BitsToDouble(0x7FF8_0000_0000_0001);

        // The premise: the two NaNs differ in their bits.
        Assert.NotEqual(BitConverter.DoubleToInt64Bits(double.NaN), BitConverter.DoubleToInt64Bits(otherNaN));

        await InsertMeasuredAsync(measured, -1.0, double.NaN, 1.0);

        var nodes = await RetiredNodes.OfAsync(database, () => measured.Get(1L) is null ? null : 1L, () => measured.ReplaceAsync(MeasuredOf(measured, 2, otherNaN)).AsTask(), "Measured");
        var entries = IndexContents.Of(database, "Measured", "ByValue").Entries;
        var rewritten = entries.Single(entry => entry.Entity == 2);

        Assert.True(nodes > 0, "The replace retired no node of ByValue.");
        Assert.Equal(BitConverter.DoubleToInt64Bits(otherNaN), BitConverter.DoubleToInt64Bits((double)rewritten.Key[0]!));
        Assert.Equal([2L], FoundIn(measured, "ByValue", double.NaN));
        Assert.Equal([2L], FoundIn(measured, "ByValue", otherNaN));
    }

    private static async Task InsertMeasuredAsync(ILatticeSet measured, params double[] values)
    {
        for (var id = 1; id <= values.Length; id++)
        {
            await measured.InsertAsync(MeasuredOf(measured, id, values[id - 1]));
        }
    }

    private static LatticeRecord MeasuredOf(ILatticeSet measured, long id, double value) =>
        new(measured.EntityType) { ["Id"] = id, ["Value"] = value };

    // The ids of the entities a search of the index by the one value given finds, in ascending order.
    private static List<long> FoundIn<TValue>(ILatticeSet set, string index, TValue value) =>
        [.. set.Index(index).Find(IndexKey.Equal(value)).Select(found => found.Get<long>("Id")).Order()];
}
