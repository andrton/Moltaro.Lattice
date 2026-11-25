using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Collections;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

[Collection<ManagedAllocationCollection>]
public sealed class ViewAllocationTests
{
    private const int Split = 5_038;
    private const int Repetitions = 1_000;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Document",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String" },
                { "Id": 2, "Name": "Scalar", "Type": "Int64" },
                { "Id": 3, "Name": "Numbers", "Type": "Map",
                  "Key": { "Type": "Int32" }, "Value": { "Type": "Int64", "Nullable": true } },
                { "Id": 4, "Name": "Tags", "Type": "List", "Element": { "Type": "Set",
                  "Element": { "Type": "Int64" } } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task Warm_scalar_views_do_not_allocate()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        await documents.InsertAsync(Document(documents));

        var field = documents.EntityType.GetField("Scalar");
        LatticeViewFunc<LatticeRecordView, LatticeField, long> viewer = static (view, scalar) => view[scalar].Get<long>();
        var total = ReadRepeatedly(documents, field, viewer);
        Assert.Equal(53L * Repetitions, total);

        var before = GC.GetAllocatedBytesForCurrentThread();
        total = ReadRepeatedly(documents, field, viewer);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(53L * Repetitions, total);
        Assert.Equal(0, bytes);
    }

    [Fact]
    public async Task A_packed_Map_reads_a_split_nullable_scalar_and_adjacent_records_without_allocating()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        await documents.InsertAsync(Document(documents));
        var owned = documents.Get("doc")!.Get<Dictionary<int, long?>>("Numbers")!;

        // Key (4 bytes), nullable value (8 bytes), and the null bitmap (1 byte) give
        // a 13-byte record. The non-null value crosses the 65,504-byte chunk boundary.
        Assert.True(Split * 13 + 5 < 65_504);
        Assert.True(65_504 < (Split + 1) * 13);
        database.ForEachLiveRoot((arena, layout, _, root) =>
        {
            var map = layout.EntityType.GetField("Numbers");
            var native = new RootReader(arena, layout, root);
            Assert.Equal(Split, native.FindEntry(map, CollectionProbes.Bind(map, Split)));
        });
        Assert.NotNull(owned[Split]);
        Assert.Null(owned[Split + 1]);

        Assert.True(documents.TryView("doc", owned, static (view, copy) =>
        {
            var map = view["Numbers"].Map;
            var seen = new HashSet<int>();
            foreach (var (key, value) in map)
            {
                var number = key.Get<int>();
                Assert.True(seen.Add(number));
                Assert.Equal(copy[number], value.IsNull ? (long?)null : value.Get<long>());
            }

            Assert.Equal(copy.Count, seen.Count);
            Assert.True(map.TryGetValue(Split - 1, out var left));
            Assert.True(map.TryGetValue(Split, out var split));
            Assert.True(map.TryGetValue(Split + 1, out var right));
            Assert.Equal(copy[Split - 1], left.Get<long>());
            Assert.Equal(copy[Split], split.Get<long>());
            Assert.True(right.IsNull);
            return split.Get<long>();
        }, out var actual));
        Assert.Equal(owned[Split], actual);

        var fields = (Map: documents.EntityType.GetField("Numbers"), Tags: documents.EntityType.GetField("Tags"));
        LatticeViewFunc<LatticeRecordView, (LatticeField Map, LatticeField Tags), long> viewer = static (view, handles) =>
        {
            var map = view[handles.Map].Map;
            var set = view[handles.Tags].List[0].Set;
            if (!map.TryGetValue(Split, out var value) || !set.Contains(3) || set.Contains(30))
            {
                return 0;
            }

            var sum = value.Get<long>();
            foreach (var member in set)
            {
                sum += member.Get<long>();
            }

            return sum;
        };
        var expected = (owned[Split]!.Value + 45) * Repetitions;
        Assert.Equal(expected, ReadRepeatedly(documents, fields, viewer));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var total = ReadRepeatedly(documents, fields, viewer);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected, total);
        Assert.Equal(0, bytes);
    }

    private static long ReadRepeatedly<TState>(ILatticeSet documents, TState state, LatticeViewFunc<LatticeRecordView, TState, long> viewer)
    {
        var sum = 0L;

        for (var read = 0; read < Repetitions; read++)
        {
            if (documents.TryView("doc", state, viewer, out var result))
            {
                sum += result;
            }
        }

        return sum;
    }

    private static LatticeRecord Document(ILatticeSet documents)
    {
        var map = new Dictionary<int, long?>();
        for (var key = 0; key < Split + 3; key++)
        {
            map.Add(key, key == Split + 1 ? null : ((long)key << 40) + key * 19L);
        }

        return new LatticeRecord(documents.EntityType)
        {
            ["Id"] = "doc",
            ["Scalar"] = 53L,
            ["Numbers"] = map,
            ["Tags"] = new List<HashSet<long>> { new(Enumerable.Range(0, 10).Select(number => (long)number)) },
        };
    }
}
