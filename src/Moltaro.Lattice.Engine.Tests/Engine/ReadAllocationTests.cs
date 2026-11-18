using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Asserted (brief of Epic 1, sprint S06, Measured; brief of S09, part 2, the third of the findings of
// the outside review it carries, and Q51): a read by key allocates the returned copy and nothing else,
// through a class and by name. The returned copy is the object or the record with everything reachable
// from it ("The allocation of a read"): for the class Item, the object and its string; for a record,
// the record with the array of its values, the boxed Int64 of its key and the string. A key is given
// without a box by name as through a class: as its own type, as an integer of another width that fits,
// and as an object that the caller holds, which the read does not box again; only the caller's own box
// of such a key costs anything, and it is made before the reads are measured. A read that finds
// nothing returns no copy and allocates nothing. The string lies inline, in a block of its own or in
// chunks (brief of S05, "Where a value goes").
//
// What each part of the copy takes is measured, not computed, so that the test does not depend on
// the object layout of the runtime. Each read is measured alone, after the same read warmed it up,
// in the test collection of the tests that measure managed allocation.
//
// The same holds when the string is the text of a field with Dictionary storage, shared with another
// entity, in an entry of one block or in chunks (brief of Epic 1, sprint S13, the planner's decision
// after the study, point 1: a Dictionary read pays what a read of a Plain value stored out of line pays,
// and no read touches the text table).
[Collection<ManagedAllocationCollection>]
public sealed class ReadAllocationTests
{
    private static readonly LatticeSchema Schema = SchemaOf("Plain");

    private static readonly LatticeSchema DictionarySchema = SchemaOf("Dictionary");

    [Theory]
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

    [Theory]
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

        // The keys an object holds are boxed by the caller, before the reads are measured.
        object key = 1L;
        object absent = 2L;
        (string Form, Func<LatticeRecord?> Get, Func<LatticeRecord?> TryGet, Func<LatticeRecord?> Absent)[] forms =
        [
            ("an Int64", () => items.Get(1L), () => items.TryGet(1L, out var found, out _) ? found : null, () => items.Get(2L)),
            ("an Int32", () => items.Get(1), () => items.TryGet(1, out var found, out _) ? found : null, () => items.Get(2)),
            ("an object", () => items.Get(key), () => items.TryGet(key, out var found, out _) ? found : null, () => items.Get(absent)),
        ];

        foreach (var (form, get, tryGet, getAbsent) in forms)
        {
            var (read, allocated) = Measured(get);

            Assert.Equal(text, read!["Text"]);
            Assert.True(expected == allocated, $"Get by a key given as {form} allocated {allocated} bytes, not the {expected} of the returned record.");

            (read, allocated) = Measured(tryGet);

            Assert.Equal(text, read!["Text"]);
            Assert.True(expected == allocated, $"TryGet by a key given as {form} allocated {allocated} bytes, not the {expected} of the returned record.");

            (read, allocated) = Measured(getAbsent);

            Assert.Null(read);
            Assert.True(allocated == 0, $"Get by an absent key given as {form} allocated {allocated} bytes.");
        }
    }

    [Theory]
    [InlineData(10)]
    [InlineData(1_000)]
    [InlineData(30_000)]
    [InlineData(200_000)]
    public async Task Read_by_key_of_a_Dictionary_text_allocates_the_returned_object_or_record_and_nothing_else(int characters)
    {
        await using var database = await EngineMaster.OpenAsync(DictionarySchema);
        var items = database.Set<Item>();
        var records = database.Set("Item");
        var text = new string('t', characters);

        // Two entities share the entry.
        await items.InsertAsync(new Item { Id = 1, Text = text });
        await items.InsertAsync(new Item { Id = 2, Text = text });

        var id = 1L;
        var expectedObject = Measure(() => new Item()) + Measure(() => new string(' ', characters));
        var expectedRecord = Measure(() => new LatticeRecord(records.EntityType)) + Measure(() => id) + Measure(() => new string(' ', characters));

        _ = items.Get(1L);
        var start = GC.GetAllocatedBytesForCurrentThread();
        var read = items.Get(1L);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

        Assert.Equal(text, read!.Text);
        Assert.Equal(expectedObject, allocated);

        var (record, recordAllocated) = Measured(() => records.Get(2L));

        Assert.Equal(text, record!["Text"]);
        Assert.Equal(expectedRecord, recordAllocated);
    }

    // The schema of Item, whose Text has the storage given.
    private static LatticeSchema SchemaOf(string storage) => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "StringStorage": "{{storage}}" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // What the call allocates, measured after it ran once. A call that returns a value type boxes it,
    // which is what a record holds.
    private static long Measure(Func<object> call)
    {
        _ = call();

        var start = GC.GetAllocatedBytesForCurrentThread();
        _ = call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    // What a read returns and allocates, measured after the same read ran once.
    private static (LatticeRecord? Read, long Allocated) Measured(Func<LatticeRecord?> read)
    {
        _ = read();

        var start = GC.GetAllocatedBytesForCurrentThread();
        var result = read();

        return (result, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    private sealed class Item
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }
}
