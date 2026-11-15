using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.ApplicationCode;

namespace Moltaro.Lattice.Engine.Tests.Engine.Entities;

// The engine runs no code of the application inside a gate or the close barrier (the planner's word
// on the first review of Epic 1, sprint S06): the constructor and the set accessors of a class run
// after the protection of the root is released. What a read returns while such code uses the
// database is stated by ApplicationCodeDuringReadsTests of Moltaro.Lattice.Tests; here, what only the
// counters show: once the read has returned, nothing is prepared, nothing is retired, and no claim
// is left. A claim left on an entry would keep the entry of a deleted entity in the table of root
// entries (the planner's answer to Q32), so every test deletes the entity it read and then counts the
// entries. The same holds after a constructor or a set accessor that throws. Every wait is bounded,
// so that code that cannot complete fails the test instead of hanging the run.
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
    public async Task After_code_of_the_application_used_the_database_during_a_read_nothing_is_prepared_retired_or_claimed(string hook, string action)
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
    public async Task After_code_of_the_application_threw_during_a_read_nothing_is_prepared_retired_or_claimed(string hook)
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

    // Deletes the entity that was read, from another thread within the bound, and counts: entity 2
    // alone is left.
    private static async Task ExpectNothingLeftAsync(EngineDatabase database, ILatticeSet<HookedEntity> entities)
    {
        await Task.Run(() => entities.DeleteAsync(1L).AsTask()).WaitAsync(Bound);
        var counters = database.Counters;

        Assert.Equal(0, counters.Arena.PreparedBytes);
        Assert.Equal(0, counters.Arena.RetiredBytes);
        Assert.Equal(1, counters.RootEntries);
    }
}
