using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

[Collection<ManagedAllocationCollection>]
public sealed class ViewListTraversalTests
{
    private const int ChunkBytes = 65_504;
    private const int LastInFirstChunk = 8_187;
    private const int Split = 7_278;
    private const int TailOnly = 36_391;
    private const int SplitOwned = 2_113;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Point",
              "Fields": [
                { "Id": 1, "Name": "Number", "Type": "Int64", "Nullable": true },
                { "Id": 2, "Name": "Total", "Type": "Int64", "Nullable": true },
                { "Id": 3, "Name": "Width", "Type": "Int32", "Nullable": true },
                { "Id": 4, "Name": "Height", "Type": "Int32", "Nullable": true },
                { "Id": 5, "Name": "Left", "Type": "UInt8", "Nullable": true },
                { "Id": 6, "Name": "Top", "Type": "UInt8", "Nullable": true },
                { "Id": 7, "Name": "Right", "Type": "UInt8", "Nullable": true },
                { "Id": 8, "Name": "Bottom", "Type": "UInt8", "Nullable": true },
                { "Id": 9, "Name": "Tag", "Type": "UInt8", "Nullable": true }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Document",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String" },
                { "Id": 2, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 3, "Name": "Optional", "Type": "List", "Element": { "Type": "Int64", "Nullable": true } },
                { "Id": 4, "Name": "Names", "Type": "List", "Element": { "Type": "String" } },
                { "Id": 5, "Name": "Points", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Point" } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData(8, (int)DescriptorKindEnum.Inline, 0)]
    [InlineData(9, (int)DescriptorKindEnum.Block, 0)]
    [InlineData(512, (int)DescriptorKindEnum.Block, 0)]
    [InlineData(513, (int)DescriptorKindEnum.Chunked, 1)]
    [InlineData(8_188, (int)DescriptorKindEnum.Chunked, 1)]
    [InlineData(8_189, (int)DescriptorKindEnum.Chunked, 2)]
    public async Task A_packed_List_is_traversed_in_order_in_each_resident_shape(int count, int kind, int chunks)
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        await documents.InsertAsync(Document(documents, numbers: Enumerable.Range(0, count).Select(NumberAt).ToList()));

        AssertPackedShape(database, "Numbers", count * 8, (DescriptorKindEnum)kind, chunks);
        Assert.True(documents.TryView("doc", count, static (view, expectedCount) =>
        {
            var values = view["Numbers"].List;
            Assert.Equal(expectedCount, values.Count);

            var index = 0;
            foreach (var value in values)
            {
                Assert.Equal(NumberAt(index), value.Get<long>());
                index++;
            }

            return index;
        }, out var visited));
        Assert.Equal(count, visited);
    }

    [Fact]
    public async Task A_List_reads_split_and_tail_only_nullable_scalars_in_order_without_allocating()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        var input = Enumerable.Range(0, TailOnly + 2).Select(OptionalAt).ToList();
        await documents.InsertAsync(Document(documents, optional: input));

        // The bitmap is the first of each record's nine bytes. The first boundary
        // splits a scalar after its first byte. The fifth is right after its bitmap.
        Assert.Equal(ChunkBytes - 2, Split * 9);
        Assert.Equal(5 * ChunkBytes - 1, TailOnly * 9);
        AssertPackedShape(database, "Optional", input.Count * 9, DescriptorKindEnum.Chunked, 6);

        var copy = documents.Get("doc")!.Get<List<long?>>("Optional")!;
        Assert.Equal(input, copy);
        Assert.True(documents.TryView("doc", copy, static (view, owned) =>
        {
            var list = view["Optional"].List;
            var index = 0;
            foreach (var value in list)
            {
                var expected = OptionalAt(index);
                if (ReadOptional(value) != expected || owned[index] != expected || ReadOptional(list[index]) != expected)
                {
                    Assert.Fail($"The List value at ordinal {index} differed from the expected value.");
                }

                index++;
            }

            return index;
        }, out var visited));
        Assert.Equal(input.Count, visited);

        var field = documents.EntityType.GetField("Optional");
        var sum = input.Sum().GetValueOrDefault();
        LatticeViewFunc<LatticeRecordView, LatticeField, long> viewer = static (view, numbers) =>
        {
            var total = 0L;
            foreach (var value in view[numbers].List)
            {
                if (!value.IsNull)
                {
                    total += value.Get<long>();
                }
            }

            return total;
        };
        Assert.True(ReadRepeatedly(documents, field, viewer, sum));
        var before = GC.GetAllocatedBytesForCurrentThread();
        var allCorrect = ReadRepeatedly(documents, field, viewer, sum);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allCorrect);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task A_List_of_fixed_width_owned_values_can_read_a_null_bit_in_the_tail()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        Assert.True(Schema.TryGetOwnedType("Point", out var pointType));
        var count = SplitOwned + 2;
        var points = new List<LatticeOwnedValue>(count);
        for (var index = 0; index < count; index++)
        {
            points.Add(new LatticeOwnedValue(pointType)
            {
                ["Number"] = NumberAt(index),
                ["Total"] = 7L,
                ["Width"] = 8,
                ["Height"] = 9,
                ["Left"] = (byte)10,
                ["Top"] = (byte)11,
                ["Right"] = (byte)12,
                ["Bottom"] = (byte)13,
                ["Tag"] = index == SplitOwned ? null : (byte)14,
            });
        }

        await documents.InsertAsync(Document(documents, points: points));

        // Two bitmap bytes and 29 scalar bytes put the second bitmap byte of
        // this record at the start of the next chunk. It holds the null bit of Tag.
        Assert.Equal(ChunkBytes - 1, SplitOwned * 31);
        AssertPackedShape(database, "Points", count * 31, DescriptorKindEnum.Chunked, 2);
        Assert.True(documents.TryView("doc", static view =>
        {
            var index = 0;
            foreach (var value in view["Points"].List)
            {
                var point = value.Owned;
                Assert.Equal(NumberAt(index), point["Number"].Get<long>());
                var tag = point["Tag"];
                if (index == SplitOwned)
                {
                    Assert.True(tag.IsNull);
                }
                else
                {
                    Assert.Equal((byte)14, tag.Get<byte>());
                }

                index++;
            }

            return index;
        }, out var visited));
        Assert.Equal(count, visited);
    }

    [Fact]
    public async Task An_empty_List_has_no_current_value_and_a_List_with_descriptors_keeps_its_order()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        List<string> names = ["first", new string('z', 100), "last"];
        await documents.InsertAsync(Document(documents, names: names));

        var defaultList = default(LatticeListView);
        var defaultEnumerator = defaultList.GetEnumerator();
        Assert.False(defaultEnumerator.MoveNext());
        AssertNoCurrent(defaultEnumerator);

        Assert.True(documents.TryView("doc", names, static (view, expected) =>
        {
            var empty = view["Numbers"].List.GetEnumerator();
            AssertNoCurrent(empty);
            Assert.False(empty.MoveNext());
            Assert.False(empty.MoveNext());
            AssertNoCurrent(empty);

            var visited = 0;
            foreach (var item in view["Names"].List)
            {
                Assert.Equal(expected[visited], item.GetString());
                visited++;
            }

            return visited;
        }, out var count));
        Assert.Equal(names.Count, count);
    }

    [Fact]
    public async Task A_List_enumerator_and_its_copy_keep_the_old_chunks_during_a_Replace()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        var count = LastInFirstChunk + 2;
        await documents.InsertAsync(Document(documents, numbers: Enumerable.Range(0, count).Select(NumberAt).ToList()));

        AssertPackedShape(database, "Numbers", count * 8, DescriptorKindEnum.Chunked, 2);
        database.DrainRetired();
        var before = database.Counters.Arena;
        var oldSlots = before.LiveBytes + before.ClassSlackBytes;

        using var pause = new ViewerPause();
        var reading = OwnThread.Run(() =>
        {
            Assert.True(documents.TryView("doc", pause, static (view, gate) =>
            {
                var listing = view["Numbers"].List.GetEnumerator();
                AssertNoCurrent(listing);
                for (var index = 0; index <= LastInFirstChunk; index++)
                {
                    Assert.True(listing.MoveNext());
                }

                var oldValue = listing.Current;
                var copy = listing;
                Assert.Equal(NumberAt(LastInFirstChunk), oldValue.Get<long>());
                gate.Hold();

                Assert.True(listing.MoveNext());
                var next = listing.Current.Get<long>();
                Assert.Equal(NumberAt(LastInFirstChunk), oldValue.Get<long>());
                Assert.Equal(NumberAt(LastInFirstChunk), copy.Current.Get<long>());
                Assert.True(copy.MoveNext());
                Assert.Equal(next, copy.Current.Get<long>());
                Assert.False(listing.MoveNext());
                Assert.False(listing.MoveNext());
                AssertNoCurrent(listing);

                return next;
            }, out var last));
            return last;
        });

        await pause.EnteredAsync(reading);
        await OwnThread.Run(() => OwnThread.Wait(documents.ReplaceAsync(Document(documents, numbers: [-1L])))).WaitAsync(Bound);
        Assert.Equal(-1L, Assert.Single(documents.Get("doc")!.Get<List<long>>("Numbers")!));
        database.DrainRetired();
        Assert.Equal(oldSlots, database.Counters.Arena.RetiredBytes);

        pause.Release();
        Assert.Equal(NumberAt(LastInFirstChunk + 1), await reading.WaitAsync(Bound));
        database.DrainRetired();
        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
    }

    private static long NumberAt(int index) => index * 0x1_0000_0011L + 457;

    private static long? OptionalAt(int index) => index == Split + 1 || index == TailOnly + 1 ? null : NumberAt(index);

    private static long? ReadOptional(scoped LatticeValueView value) => value.IsNull ? null : value.Get<long>();

    private static void AssertNoCurrent(scoped LatticeListView.Enumerator listing)
    {
        try
        {
            _ = listing.Current;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.Fail("The enumerator returned a current value outside the List.");
    }

    private static bool ReadRepeatedly(ILatticeSet documents, LatticeField field,
        LatticeViewFunc<LatticeRecordView, LatticeField, long> viewer, long expected)
    {
        for (var read = 0; read < 32; read++)
        {
            if (!documents.TryView("doc", field, viewer, out var result) || result != expected)
            {
                return false;
            }
        }

        return true;
    }

    private static void AssertPackedShape(EngineDatabase database, string name, int length, DescriptorKindEnum kind, int chunks)
    {
        var roots = 0;
        database.ForEachLiveRoot((arena, layout, _, address) =>
        {
            roots++;
            var field = layout.StoredFieldOf(layout.EntityType.GetField(name));
            var descriptor = ResidentDescriptor.Read(arena.Payload(address)[(field.Offset - BlockHeader.Size)..]);
            Assert.Equal(kind, descriptor.Kind);
            Assert.Equal((uint)length, descriptor.Length);
            if (chunks > 0)
            {
                var directory = ValueBlocks.Directory(arena, descriptor);
                Assert.Equal(chunks * 16, directory.Length);
                Assert.Equal(Math.Min(length, ChunkBytes), ValueBlocks.Chunk(arena, directory, 0).Length);
                Assert.Equal(length - (chunks - 1) * ChunkBytes, ValueBlocks.Chunk(arena, directory, chunks - 1).Length);
            }
        });
        Assert.Equal(1, roots);
    }

    private static LatticeRecord Document(
        ILatticeSet set, List<long>? numbers = null, List<long?>? optional = null, List<string>? names = null, List<LatticeOwnedValue>? points = null) =>
        new(set.EntityType)
        {
            ["Id"] = "doc",
            ["Numbers"] = numbers ?? [],
            ["Optional"] = optional ?? [],
            ["Names"] = names ?? [],
            ["Points"] = points ?? [],
        };
}
