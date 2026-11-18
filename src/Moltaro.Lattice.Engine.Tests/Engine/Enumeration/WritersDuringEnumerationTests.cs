using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.TestSupport.Enumeration;

namespace Moltaro.Lattice.Engine.Tests.Engine.Enumeration;

// An enumeration while writers insert, replace, delete and delete and insert again other entities of
// its entity type, and the primary index grows at least twice during the pass (brief of Epic 1,
// sprint S08, Acceptance and "What every unchanged entity is measured against"; Q07.3.5 of the
// decision on queries; arena protocol, "Deferred reclamation, 16 November 2026"):
// - every entity of the first set, which nobody changes, is returned exactly once, in its one state;
// - no key is returned twice, also one deleted and created again after the pass returned it;
// - every returned entity is one written state of its key (StampSchema.NumberOf checks each value),
//   and the state of a key of the second set is one its writer wrote.
// The first set is in the database before the pass starts. Two writers on threads of their own
// (OwnThread; a write of MemoryOnly completes at once, so a writer never gives its thread back) start
// after the first step and run a fixed program over the keys of the second set: each key is inserted,
// replaced, deleted, inserted again and replaced, and every third is deleted at the end, so the count
// of entities grows from 200 to 800 and the main table of the index grows by the count twice or more.
// The switch of the engine that runs inside a growth (EngineDatabase.HoldNextGrowth), armed again
// each time it runs, counts the growths; the code that walks the pass waits, between two steps, until
// the first growth has come before it takes its 60th step and the second before its 120th, so both
// come during the pass whatever the threads do. Every wait is bounded. For an Int64 key and for a
// String key, which the pass remembers with its text; every form of an enumeration. Afterwards, with
// the writes returned and no read in progress, a drain leaves nothing retired and the table of root
// entries holds the live entities alone.
public sealed class WritersDuringEnumerationTests
{
    private const int FirstSet = 200;

    // The keys of the second set are SecondStart and after it; below 10,000, so that the name of a key
    // of NamedStamp has four digits (StampSchema.NameOf).
    private const int SecondStart = 1_000;

    private const int SecondSet = 900;

    private const int Writers = 2;

    // The states each writer writes for a key of the second set: inserted, replaced, inserted again
    // after a delete, replaced again.
    private static readonly long[] Written = [1, 2, 4, 5];

    // The steps before which the walk waits for the first and the second growth.
    private static readonly int[] GrowthBefore = [60, 120];

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    public static IEnumerable<TheoryDataRow<string, string>> Cases =>
        from keyType in new[] { "Int64", "String" }
        from form in EnumerationForms.All
        select new TheoryDataRow<string, string>(keyType, form);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Enumeration_while_writers_change_other_entities_and_the_index_grows_returns_each_unchanged_entity_once_and_no_key_twice(string keyType, string form)
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var records = database.Set(StampSchema.EntityTypeOf(keyType));
            var entityType = records.EntityType;

            for (var key = 0; key < FirstSet; key++)
            {
                await records.InsertAsync(StampSchema.RecordOf(entityType, key, 0));
            }

            await using var steps = PassOf(database, keyType, form).GetAsyncEnumerator();
            Assert.True(await steps.MoveNextAsync());
            var returned = new List<(int Key, long Number)> { steps.Current };

            // The growths from now on, each counted by the switch, which arms itself again.
            var growths = 0;
            TaskCompletionSource[] grown = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];

            void Grew()
            {
                var count = Interlocked.Increment(ref growths);

                if (count <= grown.Length)
                {
                    grown[count - 1].TrySetResult();
                }

                database.HoldNextGrowth(entityType, Grew);
            }

            database.HoldNextGrowth(entityType, Grew);

            var writing = Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => OwnThread.Run(() => Write(records, writer))));

            while (await steps.MoveNextAsync())
            {
                returned.Add(steps.Current);

                for (var growth = 0; growth < GrowthBefore.Length; growth++)
                {
                    if (returned.Count == GrowthBefore[growth] - 1)
                    {
                        await GrownAsync(grown[growth].Task, writing, growth + 1);
                    }
                }
            }

            await writing.WaitAsync(Bound);

            // Every key once at most; the first set each once, in its state; the second set in states
            // its writers wrote.
            Assert.Equal(returned.Count, returned.Select(item => item.Key).Distinct().Count());
            Assert.Equal(Enumerable.Range(0, FirstSet), returned.Where(item => item.Key < FirstSet).Select(item => item.Key).Order());
            Assert.All(returned.Where(item => item.Key < FirstSet), item => Assert.Equal(0, item.Number));
            Assert.All(returned.Where(item => item.Key >= FirstSet), item =>
            {
                Assert.InRange(item.Key, SecondStart, SecondStart + SecondSet - 1);
                Assert.Contains(item.Number, Written);
            });

            await steps.DisposeAsync();
            database.DrainRetired();

            var counters = database.Counters;

            Assert.Equal(0, counters.Arena.RetiredBytes);
            Assert.Equal(0, counters.RetiredIndexBytes);
            Assert.Equal(FirstSet + Enumerable.Range(0, SecondSet).Count(index => !IsDeletedAtTheEnd(index)), counters.RootEntries);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // The pass in the form, as the key number and the state number of each entity, after checking
    // that every value of the entity is of that state of that key.
    private static IAsyncEnumerable<(int Key, long Number)> PassOf(EngineDatabase database, string keyType, string form)
    {
        var records = database.Set(StampSchema.EntityTypeOf(keyType));

        if (keyType == "Int64")
        {
            return EnumerationForms.Of(
                form,
                database.Set<StampSchema.Stamp>(),
                records,
                stamp => ItemOf((int)stamp.Id, key => StampSchema.NumberOf(key, stamp)),
                record => ItemOf((int)record.Get<long>("Id"), key => StampSchema.NumberOf(key, record)));
        }

        // A NamedStamp names its key by its text alone.
        var keys = Enumerable.Range(0, FirstSet).Concat(Enumerable.Range(SecondStart, SecondSet)).ToDictionary(StampSchema.NameOf);

        return EnumerationForms.Of(
            form,
            database.Set<StampSchema.NamedStamp>(),
            records,
            stamp => ItemOf(keys[stamp.Name], key => StampSchema.NumberOf(key, stamp)),
            record => ItemOf(keys[record.Get<string>("Name")!], key => StampSchema.NumberOf(key, record)));
    }

    private static (int Key, long Number) ItemOf(int key, Func<int, long?> numberOf) => (key, numberOf(key)!.Value);

    // The program of a writer over its keys of the second set, in their order: insert, replace,
    // delete, insert again, replace, and for every third key a delete at the end.
    private static void Write(ILatticeSet records, int writer)
    {
        var entityType = records.EntityType;

        for (var index = writer; index < SecondSet; index += Writers)
        {
            var key = SecondStart + index;

            OwnThread.Wait(records.InsertAsync(StampSchema.RecordOf(entityType, key, Written[0])));
            Assert.True(OwnThread.Wait(records.ReplaceAsync(StampSchema.RecordOf(entityType, key, Written[1]))).Changed);
            Assert.True(OwnThread.Wait(records.DeleteAsync(StampSchema.KeyOf(entityType, key))).Changed);
            OwnThread.Wait(records.InsertAsync(StampSchema.RecordOf(entityType, key, Written[2])));
            Assert.True(OwnThread.Wait(records.ReplaceAsync(StampSchema.RecordOf(entityType, key, Written[3]))).Changed);

            if (IsDeletedAtTheEnd(index))
            {
                Assert.True(OwnThread.Wait(records.DeleteAsync(StampSchema.KeyOf(entityType, key))).Changed);
            }
        }
    }

    private static bool IsDeletedAtTheEnd(int index) => index % 3 == 0;

    // Waits until the growth has come, or the writers have ended without it, within the bound.
    private static async Task GrownAsync(Task growth, Task writing, int count)
    {
        await Task.WhenAny(growth, writing).WaitAsync(Bound);

        if (writing.IsFaulted)
        {
            await writing;
        }

        Assert.True(growth.IsCompleted, $"The primary index did not grow {count} times while the writers ran.");
    }
}
