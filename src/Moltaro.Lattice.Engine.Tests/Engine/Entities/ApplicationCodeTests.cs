using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.ApplicationCode;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The engine runs no code of the application inside the protection of a read or the close barrier
// (the planner's word on the first review of Epic 1, sprint S06; arena protocol, "Deferred
// reclamation, 16 November 2026"): a read is inside the protection only while it looks up and
// copies, and the constructor and the set accessors of a class run after it has left. What a read
// returns while such code uses the database is stated by ApplicationCodeDuringReadsTests of
// Moltaro.Lattice.Tests; here, what only the counters show: once the read has returned and a drain
// has found no read in progress, nothing is prepared, nothing is retired, and the read holds nothing
// that keeps the entry of a deleted entity in the table of root entries (the planner's answer to
// Q32, as amended on 16 November 2026), so every test deletes the entity it read and then drains and
// counts. The same holds after a constructor or a set accessor that throws.
//
// While that code runs, the read has already left the protection: a delete the code makes of the
// entity being read, and a drain after it, free the old root and release its entry as they do when
// no read is in progress, and the counters taken inside the code are those taken after the read has
// returned. A read that held its protection, or anything else that keeps the entity, across that
// code shows only there: the other tests here count after the read has returned, when it has let
// go. Every wait is bounded, so that code that cannot complete fails the test instead of hanging the
// run.
public sealed class ApplicationCodeTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Hooked", "ClrType": "{{typeof(HookedEntity).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static IEnumerable<TheoryDataRow<string, string>> HooksAndActions =>
        from hook in new[] { "a set accessor", "the constructor" }
        from action in new[]
        {
            "reads the entity being read",
            "reads another entity",
            "enumerates the entity type",
            "replaces the entity being read",
            "deletes the entity being read",
            "waits for a write of it by another thread",
        }
        select new TheoryDataRow<string, string>(hook, action);

    [Theory]
    [MemberData(nameof(HooksAndActions))]
    public async Task After_code_of_the_application_used_the_database_during_a_read_and_a_drain_nothing_is_prepared_retired_or_held(string hook, string action)
    {
        var database = await EngineMaster.OpenAsync(Schema);

        try
        {
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            await entities.InsertAsync(new HookedEntity { Id = 2, Text = "other" });

            Action code = action switch
            {
                "reads the entity being read" => () => _ = entities.Get(1L),
                "reads another entity" => () => _ = entities.Get(2L),
                "enumerates the entity type" => () => _ = entities.Enumerate().Count(),
                "replaces the entity being read" => () => Completes(entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask()),
                "deletes the entity being read" => () => Completes(entities.DeleteAsync(1L).AsTask()),
                "waits for a write of it by another thread" => () => Completes(Task.Run(() => entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask())),
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action."),
            };

            await Task.Run(() =>
            {
                Arm(hook, code);
                return entities.Get(1L);
            }).WaitAsync(Bound);

            await ExpectNothingLeftAsync(database, entities);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData("a set accessor")]
    [InlineData("the constructor")]
    public async Task After_code_of_the_application_threw_during_a_read_and_a_drain_nothing_is_prepared_retired_or_held(string hook)
    {
        var database = await EngineMaster.OpenAsync(Schema);

        try
        {
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            await entities.InsertAsync(new HookedEntity { Id = 2, Text = "other" });

            await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            {
                Arm(hook, () => throw new InvalidOperationException("Thrown by the code of the application."));
                return entities.Get(1L);
            }).WaitAsync(Bound));

            await ExpectNothingLeftAsync(database, entities);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [InlineData("a set accessor")]
    [InlineData("the constructor")]
    public async Task Code_of_the_application_that_runs_during_a_read_finds_nothing_held_for_that_read(string hook)
    {
        var database = await EngineMaster.OpenAsync(Schema);

        try
        {
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            await entities.InsertAsync(new HookedEntity { Id = 2, Text = "other" });
            EngineCounters? during = null;

            // Inside the code, on the thread of the read: the entity being read is deleted, what no read
            // in progress can reach is freed, and the counters are taken.
            var read = await Task.Run(() =>
            {
                Arm(hook, () =>
                {
                    Completes(entities.DeleteAsync(1L).AsTask());
                    database.DrainRetired();
                    during = database.Counters;
                });
                return entities.Get(1L);
            }).WaitAsync(Bound);

            // The read returns the state it copied; inside the code, entity 2 alone was held, and nothing
            // of entity 1 was left retired for the read.
            Assert.Equal("first", read?.Text);
            Assert.NotNull(during);
            Assert.Equal(1, during.Value.RootEntries);
            Assert.Equal(0, during.Value.Arena.PreparedBytes);
            Assert.Equal(0, during.Value.Arena.RetiredBytes);
            Assert.Equal(0, during.Value.RetiredIndexBytes);

            database.DrainRetired();
            Assert.Equal(database.Counters, during.Value);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static void Arm(string hook, Action code)
    {
        if (hook == "the constructor")
        {
            HookedEntity.ArmConstruction(code);
        }
        else
        {
            HookedEntity.ArmSetText(code);
        }
    }

    // The code of the application waits for what it started, within the bound.
    private static void Completes(Task task) => Assert.True(task.Wait(Bound), "The write did not complete.");

    // Deletes the entity that was read, from another thread within the bound, drains with no read in
    // progress and counts: entity 2 alone is left.
    private static async Task ExpectNothingLeftAsync(EngineDatabase database, ILatticeSet<HookedEntity> entities)
    {
        await Task.Run(() => entities.DeleteAsync(1L).AsTask()).WaitAsync(Bound);
        database.DrainRetired();
        var counters = database.Counters;

        Assert.Equal(0, counters.Arena.PreparedBytes);
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(0, counters.RetiredIndexBytes);
        Assert.Equal(1, counters.RootEntries);
    }
}
