using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// A read by key through a class allocates the returned copy and nothing else with every key type
// (brief of Epic 1, sprint S07, "Types and members"; ReadAllocationTests states it for an Int64 key,
// StringKeyReadAllocationTests for a String key): with a key of a narrow integer type, of an unsigned
// type whose value is above the largest value of the signed type of its width, and of a Guid, given as
// its own type or as an integer of another width that fits, the key is converted without a box. The
// entity has an Enum field, which is set into the object without a box as well. The returned copy is
// the object and its string, measured as ReadAllocationTests measures it, in the test collection of
// the tests that measure managed allocation.
[Collection<ManagedAllocationCollection>]
public sealed class KeyReadAllocationTests
{
    private enum LevelEnum : byte
    {
        Low = 1,
        High = 2,
    }

    private static readonly Guid Key = Guid.Parse("7c9e6679-7425-40de-944b-e07fc1f90ae7");

    [Fact]
    public Task Read_by_an_Int8_key_given_as_its_own_type_or_another_width_allocates_the_returned_object_and_its_string_and_nothing_else() =>
        ExpectAsync("Int8", (sbyte)-128, static items => items.Get((sbyte)-128), static items => items.Get(-128L));

    [Fact]
    public Task Read_by_a_UInt32_key_above_the_largest_Int32_allocates_the_returned_object_and_its_string_and_nothing_else() =>
        ExpectAsync("UInt32", 4_000_000_000u, static items => items.Get(4_000_000_000u), static items => items.Get(4_000_000_000L));

    [Fact]
    public Task Read_by_a_UInt64_key_above_the_largest_Int64_allocates_the_returned_object_and_its_string_and_nothing_else() =>
        ExpectAsync("UInt64", ulong.MaxValue, static items => items.Get(ulong.MaxValue));

    [Fact]
    public Task Read_by_a_Guid_key_allocates_the_returned_object_and_its_string_and_nothing_else() =>
        ExpectAsync("Guid", Key, static items => items.Get(Key));

    // Stores one item with the key, and measures each read after the same read warmed it up.
    private static async Task ExpectAsync<TKey>(string keyType, TKey key, params Func<ILatticeSet<Item<TKey>>, Item<TKey>?>[] reads)
        where TKey : notnull
    {
        await using var database = await EngineMaster.OpenAsync(SchemaOf<TKey>(keyType));
        var items = database.Set<Item<TKey>>();
        await items.InsertAsync(new Item<TKey> { Id = key, Level = LevelEnum.High, Text = "text" });
        var expected = Measure(() => new Item<TKey>()) + Measure(() => new string(' ', 4));

        foreach (var read in reads)
        {
            _ = read(items);
            var start = GC.GetAllocatedBytesForCurrentThread();
            var found = read(items);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;

            Assert.NotNull(found);
            Assert.Equal(key, found.Id);
            Assert.Equal(LevelEnum.High, found.Level);
            Assert.Equal("text", found.Text);
            Assert.Equal(expected, allocated);
        }
    }

    // What the call allocates, measured after it ran once.
    private static long Measure(Func<object> call)
    {
        _ = call();

        var start = GC.GetAllocatedBytesForCurrentThread();
        _ = call();

        return GC.GetAllocatedBytesForCurrentThread() - start;
    }

    private static LatticeSchema SchemaOf<TKey>(string keyType) => LatticeSchema.Compile($$"""
        {
          "Enums": [ { "Id": 1, "Name": "Level", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 1 }, { "Name": "High", "Value": 2 } ] } ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item", "ClrType": "{{typeof(Item<TKey>).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "{{keyType}}" },
                { "Id": 2, "Name": "Level", "Type": "Enum", "Enum": "Level" },
                { "Id": 3, "Name": "Text", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private sealed class Item<TKey>
    {
        public TKey Id { get; set; } = default!;

        public LevelEnum Level { get; set; } = LevelEnum.Low;

        public string Text { get; set; } = "";
    }
}
