using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// The bytes of entries are bytes of the arena, counted as such, and the rule of a write refused for
// memory covers a new entry (brief of Epic 1, sprint S13, the planner's decision after the study, point
// 7; the rule of the refusal, brief of S09). The database is opened with an arena of two segments of the
// default size, 8 MiB (EngineTestInputs, exception X3):
// - a write that introduces a text the arena cannot hold beside what is stored is WriteRejected with
//   Limit Memory, has no effect and leaves no entry;
// - a write of a text the database holds takes no memory for it: the entry of 5 MiB serves a second
//   entity and a second field, where a second copy would not fit.
public sealed class MemoryTests
{
    private const int Mebibyte = 1024 * 1024;

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 2L * new ArenaOptions().SegmentSize };

    [Fact]
    public async Task Write_of_a_new_text_the_arena_cannot_hold_is_WriteRejected_with_Limit_Memory_and_leaves_no_entry()
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8, TwoSegments, new NativeAllocator());
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally("Utf8");
        var stored = new TextSchema.Place { Id = 1, Region = new string('a', 3 * Mebibyte), Label = "label" };

        await places.InsertAsync(stored);
        tally.Written(stored);

        var refused = await Assert.ThrowsAsync<LatticeException>(() => places.InsertAsync(new TextSchema.Place { Id = 2, Region = new string('b', 6 * Mebibyte), Label = "label" }).AsTask());

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Null(places.Get(2L));
        Assert.Equal(1, database.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
    }

    // A write takes its references as it encodes its fields, in their order, so one whose first Dictionary
    // text is a text the database holds has taken a reference to that entry when a later field does not fit
    // the arena. A refused write leaks no count (decision on strings, Q04.4): the reference goes with the
    // blocks the write frees, the entry keeps the count it had, no entry is left of what the write brought,
    // the arena holds what it held, and the same write with a later field that fits succeeds. The later
    // field is a new Dictionary text or a Plain value of 5 MiB, which the arena of 8 MiB holds alone but not
    // beside the Plain value of 5 MiB it stores, for an insert and for a replace.
    [Theory]
    [InlineData("insert", "Zone")]
    [InlineData("insert", "Label")]
    [InlineData("replace", "Zone")]
    [InlineData("replace", "Label")]
    public async Task Write_refused_for_memory_after_it_took_a_reference_to_a_text_the_database_holds_leaves_the_count_as_it_was(string kind, string later)
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8, TwoSegments, new NativeAllocator());
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally("Utf8");

        foreach (var stored in new[]
        {
            new TextSchema.Place { Id = 1, Region = "North", Label = "label" },
            new TextSchema.Place { Id = 2, Region = "South", Label = new string('m', 5 * Mebibyte) },
        })
        {
            await places.InsertAsync(stored);
            tally.Written(stored);
        }

        var before = EntryCounts.Of(database, "Utf8")["North"];
        var id = kind == "insert" ? 3 : 1;
        var tooLarge = new string('x', 5 * Mebibyte);
        var refusedValues = new TextSchema.Place
        {
            Id = id,
            Number = 1,
            Region = "North",
            Zone = later == "Zone" ? tooLarge : null,
            Label = later == "Label" ? tooLarge : "label",
        };

        var refused = await Assert.ThrowsAsync<LatticeException>(() => (kind == "insert" ? places.InsertAsync(refusedValues) : places.ReplaceAsync(refusedValues)).AsTask());

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);
        Assert.Equal(before, EntryCounts.Of(database, "Utf8")["North"]);
        Assert.Equal(2, database.Counters.TextEntries);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal(kind == "insert" ? null : 0L, places.Get((long)id)?.Number);

        // The same write, its later field small enough.
        var fitting = new TextSchema.Place { Id = id, Number = 2, Region = "North", Zone = later == "Zone" ? "a zone" : null, Label = "label" };

        await (kind == "insert" ? places.InsertAsync(fitting) : places.ReplaceAsync(fitting));
        tally.Written(fitting);

        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal("North", places.Get((long)id)?.Region);
    }

    [Fact]
    public async Task Write_of_a_text_the_database_holds_takes_no_memory_for_the_text()
    {
        await using var database = await EngineMaster.OpenAsync(TextSchema.Utf8, TwoSegments, new NativeAllocator());
        var places = database.Set<TextSchema.Place>();
        var tally = new TextTally("Utf8");
        var shared = new string('s', 5 * Mebibyte);

        // Two copies of the text would not fit the arena; one entry does.
        foreach (var place in new[]
        {
            new TextSchema.Place { Id = 1, Region = shared, Label = "label" },
            new TextSchema.Place { Id = 2, Region = shared, Zone = shared, Label = "label" },
        })
        {
            await places.InsertAsync(place);
            tally.Written(place);
        }

        Assert.Equal(3, EntryCounts.Of(database, "Utf8")[shared]);
        EntryCounts.ExpectEqual(tally, database);
        tally.ExpectHeldBy(database);
        Assert.Equal(shared, places.Get(2L)?.Zone);
    }
}
