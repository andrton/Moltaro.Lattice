using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Capacity;

// The memory budget in force, fixed when the instance opens and shown by EngineCounters.MemoryLimit
// (brief of Epic 1, sprint S09, Acceptance, the owner's decision O17 and the answers of the planner on
// the computed default and on the internal Create with ArenaOptions):
// - An instance opened without LatticeDatabaseOptions.MemoryLimit has half of
//   GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, read when it opens, as its limit, and never less
//   than one segment, 4 MiB. The test reads the figure before and after the open, so that a change of
//   it between the two, which a garbage collection can make, does not fail the test; that the
//   default is never less than 4 MiB cannot be shown on a machine with the memory to run the tests.
// - An instance opened with MemoryLimit has the given value as its limit.
// - The options of the arena that a test gives (EngineTestInputs.Arena) are the options of the arena,
//   and MemoryLimit of the options of the database is not read.
// That the limit refuses a write is stated by RefusedWriteTests and by MemoryLimitTests of the
// scenario tests; that Validate refuses a value below 4 MiB, by LatticeDatabaseOptionsTests there.
public sealed class MemoryLimitTests
{
    private static readonly long Segment = new ArenaOptions().SegmentSize;

    [Fact]
    public async Task Instance_opened_without_a_MemoryLimit_has_half_of_the_memory_the_runtime_reports_as_its_limit()
    {
        var before = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        await using var database = await EngineMaster.OpenAsync(PieceSchema.Schema);
        var after = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;

        Assert.Contains(database.Counters.MemoryLimit, new[] { DefaultOf(before), DefaultOf(after) });
    }

    [Fact]
    public async Task Instance_opened_with_a_MemoryLimit_has_that_value_as_its_limit()
    {
        var given = (12 * Segment) + 1;

        await using var database = (EngineDatabase)LatticeDatabase.Create(OptionsOf(given));
        await database.OpenAsync();

        Assert.Equal(given, database.Counters.MemoryLimit);
    }

    [Fact]
    public async Task Options_of_the_arena_a_test_gives_are_the_limit_and_the_MemoryLimit_of_the_options_is_not_read()
    {
        var arena = new ArenaOptions { MemoryLimit = 3 * Segment };
        await using var database = EngineDatabase.Create(OptionsOf(64 * Segment), new EngineTestInputs { Arena = arena, Allocator = new NativeAllocator() });
        await database.OpenAsync();

        Assert.Equal(3 * Segment, database.Counters.MemoryLimit);
    }

    private static long DefaultOf(long totalAvailable) => Math.Max(totalAvailable / 2, Segment);

    private static LatticeDatabaseOptions OptionsOf(long memoryLimit) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = PieceSchema.Schema,
        MemoryLimit = memoryLimit,
    };
}
