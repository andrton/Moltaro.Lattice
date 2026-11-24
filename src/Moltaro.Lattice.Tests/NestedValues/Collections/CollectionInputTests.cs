namespace Moltaro.Lattice.Tests.NestedValues.Collections;

// Bulk Map input has one entry per declared key and no null keys. A refusal leaves the previous
// entity and its revision, including when the write originates on a child (schema-and-types.md,
// "Collection input contract"; document-model.md, "Atomic WriteBatch within one database").
public sealed class CollectionInputTests
{
    [Fact]
    public async Task Duplicate_Map_keys_in_an_object_refuse_the_write_and_preserve_the_entity_and_its_revision()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(CollectionSchema.Compile());
        var baseline = new CollectionItem { Id = 1, Options = new() { ["original"] = null } };
        await master.Set<CollectionItem>().InsertAsync(baseline);
        await using var child = await TestDatabases.OpenChildAsync(master);
        Assert.True(master.Set("Item").TryGet(1L, out _, out var revision));

        var invalid = new CollectionItem
        {
            Id = 1,
            Options = new Dictionary<string, CollectionDetails?>(StringComparer.Ordinal) { ["ı"] = null, ["I"] = new() },
        };

        foreach (var database in new[] { master, child })
        {
            await TestData.ExpectValidationFailedAsync("Options", () => database.Set<CollectionItem>().ReplaceAsync(invalid, revision));
            var batch = database.CreateWriteBatch();
            batch.Insert(new CollectionItem { Id = 2 });
            TestData.ExpectValidationFailed("Options", () => batch.Replace(invalid, revision));
            Assert.Equal(1, batch.Count);
            Assert.True(database.Set("Item").TryGet(1L, out var record, out var after));
            Assert.Equal(revision, after);
            Assert.Equal("original", Assert.Single(record!.Get<Dictionary<string, LatticeOwnedValue?>>("Options")!).Key);
            Assert.Null(database.Set<CollectionItem>().Get(2L));
        }
    }

    [Fact]
    public async Task Duplicate_Map_keys_are_refused_before_a_stale_revision_on_master_and_child()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(CollectionSchema.Compile());
        var masterItems = master.Set<CollectionItem>();
        await masterItems.InsertAsync(new CollectionItem { Id = 1, Options = new() { ["before"] = null } });
        Assert.True(masterItems.TryGet(1L, out _, out var stale));
        await using var child = await TestDatabases.OpenChildAsync(master);

        var current = new CollectionItem { Id = 1, Options = new() { ["current"] = null } };
        var moved = await masterItems.ReplaceAsync(current);
        await child.WaitForPositionAsync(moved.Position, TimeSpan.FromMinutes(1));
        Assert.True(masterItems.TryGet(1L, out _, out var revision));
        Assert.NotEqual(stale, revision);

        var invalid = new CollectionItem
        {
            Id = 1,
            Options = new Dictionary<string, CollectionDetails?>(StringComparer.Ordinal) { ["ı"] = null, ["I"] = new() },
        };
        foreach (var database in new[] { master, child })
        {
            var items = database.Set<CollectionItem>();
            await TestData.ExpectErrorAsync(LatticeErrorEnum.RevisionConflict, () => items.ReplaceAsync(current, stale));
            await TestData.ExpectValidationFailedAsync("Options", () => items.ReplaceAsync(invalid, stale));
            Assert.True(items.TryGet(1L, out var unchanged, out var after));
            Assert.Equal(revision, after);
            Assert.Equal("current", Assert.Single(unchanged.Options!).Key);
        }
    }

    [Fact]
    public void A_record_refuses_duplicate_and_null_Map_keys_and_keeps_the_previously_assigned_value()
    {
        var schema = CollectionSchema.Compile();
        Assert.True(schema.TryGetEntityType("Item", out var entityType));
        var record = new LatticeRecord(entityType)
        {
            ["Options"] = new Dictionary<string, LatticeOwnedValue?> { ["original"] = null },
        };
        var previous = record["Options"];

        KeyValuePair<string, LatticeOwnedValue?>[] duplicate = [new("ı", null), new("I", null)];
        KeyValuePair<string, LatticeOwnedValue?>[] nullKey = [new(null!, null)];
        Assert.Throws<ArgumentException>(() => record["Options"] = duplicate);
        Assert.Throws<ArgumentException>(() => record["Options"] = nullKey);
        Assert.Same(previous, record["Options"]);
    }
}
