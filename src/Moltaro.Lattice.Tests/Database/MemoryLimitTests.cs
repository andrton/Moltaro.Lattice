namespace Moltaro.Lattice.Tests.Database;

// The memory budget of an instance, LatticeDatabaseOptions.MemoryLimit (brief of Epic 1, sprint S09,
// Acceptance and "The refusal"); that Validate refuses a value set below 4 MiB is
// stated by LatticeDatabaseOptionsTests, and what the limit in force is, by the tests of the engine:
// - A write that the budget cannot hold is refused before it has any effect, with WriteRejected and
//   Limit Memory, and it never runs later: it takes no position, the entity stays absent, and writes
//   after it, which free memory, complete without it. The database stays Ready. On the engine, with a
//   budget of two segments of 4 MiB, an entity of 9 MiB.
public sealed class MemoryLimitTests
{
    private const int Mebibyte = 1024 * 1024;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Write_the_memory_limit_cannot_hold_is_WriteRejected_with_Limit_Memory_has_no_effect_and_never_runs_later()
    {
        await using var database = await TestDatabases.OpenAsync(Options(memoryLimit: 8L * Mebibyte));
        var settings = database.Set("Setting");
        await settings.InsertAsync(SettingOf(settings, "small", Mebibyte));
        var before = database.Position;

        var refused = await Assert.ThrowsAsync<LatticeException>(() => settings.InsertAsync(SettingOf(settings, "large", 9 * Mebibyte)).AsTask().WaitAsync(Bound));

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Equal(before, database.Position);
        Assert.Null(settings.Get("large"));
        Assert.Equal(LatticeDatabaseStateEnum.Ready, database.State);

        // Memory is freed and other writes complete; the refused write does not run after them.
        Assert.True((await settings.DeleteAsync("small")).Changed);
        var last = await settings.InsertAsync(SettingOf(settings, "after", Mebibyte));

        Assert.Null(settings.Get("large"));
        Assert.NotNull(settings.Get("after"));
        Assert.Equal(last.Position, database.Position);
    }

    private static LatticeDatabaseOptions Options(long memoryLimit) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = TestData.Schema,
        MemoryLimit = memoryLimit,
    };

    // A Setting whose Payload has the given length.
    private static LatticeRecord SettingOf(ILatticeSet settings, string name, int payload) => new(settings.EntityType)
    {
        ["Name"] = name,
        ["Value"] = "value",
        ["Priority"] = 1,
        ["Payload"] = new byte[payload],
    };
}
