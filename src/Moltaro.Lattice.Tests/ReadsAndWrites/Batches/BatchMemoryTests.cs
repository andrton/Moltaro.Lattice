namespace Moltaro.Lattice.Tests.ReadsAndWrites.Batches;

// A batch that the memory budget cannot hold is refused as a whole before anything is written (decision on
// the document model, "Added by the owner on 15 November 2026"; the remarks of ILatticeWriteBatch,
// "Executing"): WriteRejected with Limit Memory and no number of a member, since the batch as a whole does
// not fit. No entity, no entry of an index and no position of it is left, and a batch that fits is executed
// after it. Each member alone would fit: four Settings of 3 MiB under a budget of 8 MiB.
public sealed class BatchMemoryTests
{
    private const int Mebibyte = 1024 * 1024;

    [Fact]
    public async Task Batch_the_memory_budget_cannot_hold_is_refused_whole_and_changes_nothing()
    {
        await using var database = await TestDatabases.OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = TestData.Schema,
            MemoryLimit = 8L * Mebibyte,
        });
        var settings = database.Set("Setting");
        await settings.InsertAsync(SettingOf(settings, "small", priority: 1, payload: Mebibyte));
        await settings.InsertAsync(SettingOf(settings, "obsolete", priority: 2, payload: 16));
        var before = database.Position;

        var batch = database.CreateWriteBatch();
        batch.Replace(SettingOf(settings, "small", priority: 5, payload: 16));
        batch.Delete("Setting", "obsolete");

        for (var index = 0; index < 4; index++)
        {
            batch.Insert(SettingOf(settings, $"large {index}", priority: 5, payload: 3 * Mebibyte));
        }

        var refused = await TestData.ExpectErrorAsync(LatticeErrorEnum.WriteRejected, () => batch.CommitAsync());

        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Null(refused.OperationIndex);
        Assert.Equal(before, database.Position);
        Assert.Equal(1, settings.Get("small")!["Priority"]);
        Assert.NotNull(settings.Get("obsolete"));
        Assert.All(Enumerable.Range(0, 4), index => Assert.Null(settings.Get($"large {index}")));
        Assert.Empty(settings.Index("ByPriority").Find(IndexKey.Equal(5)));
        Assert.Equal(["small"], settings.Index("ByPriority").Find(IndexKey.Equal(1)).Select(record => (string)record["Name"]!));

        var fits = database.CreateWriteBatch();
        fits.Replace(SettingOf(settings, "small", priority: 5, payload: 16));
        fits.Insert(SettingOf(settings, "large 0", priority: 5, payload: 3 * Mebibyte));
        await fits.CommitAsync();

        Assert.Equal(["large 0", "small"], settings.Index("ByPriority").Find(IndexKey.Equal(5)).Select(record => (string)record["Name"]!).Order());
    }

    private static LatticeRecord SettingOf(ILatticeSet settings, string name, int priority, int payload) => new(settings.EntityType)
    {
        ["Name"] = name,
        ["Value"] = "value",
        ["Priority"] = priority,
        ["Payload"] = new byte[payload],
    };
}
