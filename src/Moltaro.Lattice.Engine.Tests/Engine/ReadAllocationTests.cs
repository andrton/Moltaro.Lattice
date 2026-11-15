using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Asserted (brief of Epic 1, sprint S06, Measured): a read by key allocates the returned copy and
// nothing else, through a class and by name. The returned copy is the object or the record with
// everything reachable from it ("The allocation of a read"): for the class Item, the object and its
// string; for a record, the record with the array of its values, the boxed Int64 of its key and
// the string. A key boxed by a public signature that takes object is the caller's allocation, outside
// the measured read, and a read that finds nothing returns no copy and allocates nothing. The string
// lies inline, in a block of its own or in chunks (brief of S05, "Where a value goes").
//
// What each part of the copy takes is measured, not computed, so that the test does not depend on
// the object layout of the runtime. Each read is measured alone, after the same read warmed it up,
// in the test collection of the tests that measure managed allocation.
[Collection<ManagedAllocationCollection>]
public sealed class ReadAllocationTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory(Skip = NotImplemented.EngineDatabase)]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(30_000)]
    [InlineData(200_000)]
    public async Task Read_by_key_through_a_class_allocates_the_returned_object_and_its_string_and_nothing_else(int characters)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var items = database.Set<Item>();
        var text = new string('t', characters);
        await items.InsertAsync(new Item { Id = 1, Text = text });
        var expected = Measure(() => new Item()) + Measure(() => new string(' ', characters));

        _ = items.Get(1L);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var read = items.Get(1L);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, read!.Text);
        Assert.Equal(expected, allocated);

        _ = items.TryGet(1L, out _, out _);
        start = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(items.TryGet(1L, out var withRevision, out _));
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, withRevision.Text);
        Assert.Equal(expected, allocated);

        _ = items.Get(2L);
        start = GC.GetAllocatedBytesForCurrentThread();
        var missing = items.Get(2L);
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Null(missing);
        Assert.Equal(0, allocated);
    }

    [Theory(Skip = NotImplemented.EngineDatabase)]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(30_000)]
    [InlineData(200_000)]
    public async Task Read_by_key_by_name_allocates_the_returned_record_and_what_it_holds_and_nothing_else(int characters)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var items = database.Set("Item");
        var text = new string('t', characters);
        await items.InsertAsync(new LatticeRecord(items.EntityType) { ["Id"] = 1L, ["Text"] = text });
        var id = 1L;
        var expected = Measure(() => new LatticeRecord(items.EntityType)) + Measure(() => id) + Measure(() => new string(' ', characters));

        // The keys are boxed by the caller, before the reads are measured.
        object key = 1L;
        object absent = 2L;

        _ = items.Get(key);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var read = items.Get(key);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, read!["Text"]);
        Assert.Equal(expected, allocated);

        _ = items.TryGet(key, out _, out _);
        start = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(items.TryGet(key, out var withRevision, out _));
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, withRevision["Text"]);
        Assert.Equal(expected, allocated);

        _ = items.Get(absent);
        start = GC.GetAllocatedBytesForCurrentThread();
        var missing = items.Get(absent);
        allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Null(missing);
        Assert.Equal(0, allocated);
    }

    // What the call allocates, measured after it ran once. A call that returns a value type boxes it,
    // which is what a record holds.
    private static long Measure(Func<object> call)
    {
        _ = call();

        var start = GC.GetAllocatedBytesForCurrentThread();
        _ = call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private sealed class Item
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }
}
