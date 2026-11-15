using Moltaro.Lattice.TestSupport.ApplicationCode;

namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// The engine runs no code of the application inside a gate or the close barrier (the planner's word
// on the first review of Epic 1, sprint S06): under the protection of a root it copies the values
// into its own memory, and the constructor and the set accessors of the class run after the
// protection is released. So that code may use the database while a read builds its object: a set
// accessor, and separately the constructor, that reads the entity being read, reads another entity,
// enumerates the entity type, replaces or deletes the entity being read, waits for a write of it by
// another thread, or disposes the database completes, and so does the read. An enumeration builds
// each of its objects the same way, synchronous and asynchronous alike.
//
// What the read returns: one written state, the one it copied before the code of the application
// ran, so the state the entity had when it was read, "first"; the replace, the delete or the close
// that code makes comes after the copy. A close waits for the reads in progress and refuses what
// comes after them: the read returns its entity, and then the database is closed. An enumeration
// returns the item it was building, and a replace does not end its pass; after a delete or a close
// the tests take no further step, since the first is the rule of an enumeration until S08 and the
// second is not stated. Afterwards nothing stays locked: the entity is written from another thread
// and read again. A constructor or a set accessor that throws: the exception reaches the caller of
// the read, and the next read and write work.
//
// Every wait is bounded, so that code that cannot complete fails the test instead of hanging the
// run. The placeholder holds no lock while it builds an object, so the tests run on it too.
public sealed class ApplicationCodeDuringReadsTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    private static readonly string[] Hooks = ["a set accessor", "the constructor"];

    private static readonly string[] Actions =
    [
        "reads the entity being read",
        "reads another entity",
        "enumerates the entity type",
        "replaces the entity being read",
        "deletes the entity being read",
        "waits for a write of it by another thread",
        "disposes the database",
    ];

    private static readonly string[] Reads = ["TryGet with its revision", "GetAsync", "Enumerate", "EnumerateAsync"];

    private static readonly string[] Enumerations = ["Enumerate", "EnumerateAsync"];

    // What the code of the application does during an enumeration that the read by key does not
    // already show: it removes or changes the entity the pass is on, or ends the database under it.
    private static readonly string[] EnumerationActions =
    [
        "deletes the entity being read",
        "waits for a write of it by another thread",
        "disposes the database",
    ];

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

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string, string>> HooksAndActions =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from hook in Hooks
        from action in Actions
        select new TheoryDataRow<DatabaseImplementationEnum, string, string>(implementation, hook, action);

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string, string>> ReadsAndActions =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from read in Reads
        from action in new[] { "reads the entity being read", "replaces the entity being read" }
        select new TheoryDataRow<DatabaseImplementationEnum, string, string>(implementation, read, action);

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string, string, string>> EnumerationsHooksAndActions =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from enumeration in Enumerations
        from hook in Hooks
        from action in EnumerationActions
        select new TheoryDataRow<DatabaseImplementationEnum, string, string, string>(implementation, enumeration, hook, action);

    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum, string>> ImplementationsAndHooks =>
        from implementation in new[] { DatabaseImplementationEnum.Placeholder, DatabaseImplementationEnum.Engine }
        from hook in Hooks
        select new TheoryDataRow<DatabaseImplementationEnum, string>(implementation, hook);

    [Theory]
    [MemberData(nameof(HooksAndActions))]
    public async Task Code_of_the_application_that_uses_the_database_while_a_read_builds_its_object_completes(DatabaseImplementationEnum implementation, string hook, string action)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation, Schema);

        try
        {
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            await entities.InsertAsync(new HookedEntity { Id = 2, Text = "other" });
            string? seen = null;

            Action code = action switch
            {
                "reads the entity being read" => () => seen = entities.Get(1L)?.Text,
                "reads another entity" => () => seen = entities.Get(2L)?.Text,
                "enumerates the entity type" => () => seen = string.Join(",", entities.Enumerate().Select(entity => entity.Id).Order()),
                "replaces the entity being read" => () => Completes(entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask()),
                "deletes the entity being read" => () => Completes(entities.DeleteAsync(1L).AsTask()),
                "waits for a write of it by another thread" => () => Completes(Task.Run(() => entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask())),
                "disposes the database" => database.Dispose,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action."),
            };

            var read = await Task.Run(() =>
            {
                Arm(hook, code);
                return entities.Get(1L);
            }).WaitAsync(Bound);

            Assert.NotNull(read);
            Assert.Equal(1, read.Id);
            Assert.Equal("first", read.Text);

            switch (action)
            {
                case "reads the entity being read":
                    Assert.Equal("first", seen);
                    break;
                case "reads another entity":
                    Assert.Equal("other", seen);
                    break;
                case "enumerates the entity type":
                    Assert.Equal("1,2", seen);
                    break;
                case "replaces the entity being read" or "waits for a write of it by another thread":
                    Assert.Equal("second", entities.Get(1L)!.Text);
                    break;
                case "deletes the entity being read":
                    Assert.Null(entities.Get(1L));
                    break;
                case "disposes the database":
                    Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
                    Assert.Throws<ObjectDisposedException>(() => entities.Get(1L));
                    return;
            }

            await ExpectNothingLockedAsync(entities);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(ReadsAndActions))]
    public async Task Code_of_the_application_that_uses_the_database_completes_during_every_read_through_a_class(DatabaseImplementationEnum implementation, string read, string action)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation, Schema);

        try
        {
            // One entity, so that the object an enumeration builds first is the one being read.
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            string? seen = null;

            Action code = action == "reads the entity being read"
                ? () => seen = entities.Get(1L)?.Text
                : () => Completes(entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask());

            var returned = await Task.Run(async () =>
            {
                HookedEntity.ArmSetText(code);
                return read switch
                {
                    "TryGet with its revision" => entities.TryGet(1L, out var entity, out _) ? [entity] : [],
                    "GetAsync" => [(await entities.GetAsync(1L, new LatticeSession())).Entity!],
                    "Enumerate" => entities.Enumerate().ToList(),
                    "EnumerateAsync" => await entities.EnumerateAsync(new LatticeSession()).ToListAsync(),
                    _ => throw new ArgumentOutOfRangeException(nameof(read), read, "No such read."),
                };
            }).WaitAsync(Bound);

            Assert.Equal("first", Assert.Single(returned).Text);

            if (action == "reads the entity being read")
            {
                Assert.Equal("first", seen);
            }
            else
            {
                Assert.Equal("second", entities.Get(1L)!.Text);
            }

            await ExpectNothingLockedAsync(entities);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(EnumerationsHooksAndActions))]
    public async Task Code_of_the_application_that_uses_the_database_while_an_enumeration_builds_its_object_completes(DatabaseImplementationEnum implementation, string enumeration, string hook, string action)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation, Schema);

        try
        {
            // One entity, so that the object an enumeration builds first is the one being read, and
            // the pass has no other to return: each primary key appears at most once per pass.
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });

            Action code = action switch
            {
                "deletes the entity being read" => () => Completes(entities.DeleteAsync(1L).AsTask()),
                "waits for a write of it by another thread" => () => Completes(Task.Run(() => entities.ReplaceAsync(new HookedEntity { Id = 1, Text = "second" }).AsTask())),
                "disposes the database" => database.Dispose,
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, "No such action."),
            };

            if (action == "waits for a write of it by another thread")
            {
                // A replace does not end the pass, and the pass returns the key once.
                var returned = await Task.Run(async () =>
                {
                    Arm(hook, code);
                    return await AllItemsAsync(entities, enumeration);
                }).WaitAsync(Bound);

                Assert.Equal("first", Assert.Single(returned).Text);
                Assert.Equal("second", entities.Get(1L)!.Text);
                await ExpectNothingLockedAsync(entities);
                return;
            }

            // The delete and the dispose return, and the item being built reaches the caller. The
            // test takes no further step of the enumeration: after a delete what it does is the rule
            // of an enumeration until S08, which EnumerationTests states, and after a close it is
            // not stated.
            var item = await Task.Run(async () =>
            {
                Arm(hook, code);
                return await FirstItemAsync(entities, enumeration);
            }).WaitAsync(Bound);

            Assert.Equal(1, item.Id);
            Assert.Equal("first", item.Text);

            if (action == "disposes the database")
            {
                Assert.Equal(LatticeDatabaseStateEnum.Closed, database.State);
                return;
            }

            Assert.Null(entities.Get(1L));
            await ExpectNothingLockedAsync(entities);
        }
        finally
        {
            await database.DisposeAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Theory]
    [MemberData(nameof(ImplementationsAndHooks))]
    public async Task Exception_of_the_code_of_the_application_reaches_the_caller_of_the_read_and_the_next_read_and_write_work(DatabaseImplementationEnum implementation, string hook)
    {
        var database = await TestDatabases.OpenMasterAsync(implementation, Schema);

        try
        {
            var entities = database.Set<HookedEntity>();
            await entities.InsertAsync(new HookedEntity { Id = 1, Text = "first" });
            var failure = new InvalidOperationException("Thrown by the code of the application.");

            var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => Task.Run(() =>
            {
                Arm(hook, () => throw failure);
                return entities.Get(1L);
            }).WaitAsync(Bound));

            Assert.Same(failure, thrown);
            Assert.Equal("first", entities.Get(1L)!.Text);
            await ExpectNothingLockedAsync(entities);
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

    // The first step of an enumeration, and only that step.
    private static async Task<HookedEntity> FirstItemAsync(ILatticeSet<HookedEntity> entities, string enumeration)
    {
        if (enumeration == "Enumerate")
        {
            using var enumerator = entities.Enumerate().GetEnumerator();

            Assert.True(enumerator.MoveNext());
            return enumerator.Current;
        }

        await using var asyncEnumerator = entities.EnumerateAsync(new LatticeSession()).GetAsyncEnumerator();

        Assert.True(await asyncEnumerator.MoveNextAsync());
        return asyncEnumerator.Current;
    }

    private static async Task<List<HookedEntity>> AllItemsAsync(ILatticeSet<HookedEntity> entities, string enumeration)
    {
        if (enumeration == "Enumerate")
        {
            return entities.Enumerate().ToList();
        }

        return await entities.EnumerateAsync(new LatticeSession()).ToListAsync();
    }

    // The code of the application waits for what it started, within the bound.
    private static void Completes(Task task) => Assert.True(task.Wait(Bound), "The write did not complete.");

    // The entity is written from another thread, then on this one, and read.
    private static async Task ExpectNothingLockedAsync(ILatticeSet<HookedEntity> entities)
    {
        await Task.Run(() => entities.UpsertAsync(new HookedEntity { Id = 1, Text = "third" }).AsTask()).WaitAsync(Bound);
        await entities.UpsertAsync(new HookedEntity { Id = 1, Text = "fourth" });

        Assert.Equal("fourth", entities.Get(1L)!.Text);
    }
}
