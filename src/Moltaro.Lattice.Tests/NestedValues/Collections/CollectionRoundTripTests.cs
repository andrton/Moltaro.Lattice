namespace Moltaro.Lattice.Tests.NestedValues.Collections;

// The ordinary read copies Map and Set contents under the protection of the root. The returned
// collection uses its declared equality, and the copies expose neither the arena nor one another
// (schema-and-types.md, "Ownership, equality and .NET access"; memory-and-access.md).
public sealed class CollectionRoundTripTests
{
    [Theory]
    [InlineData(false, 8)]
    [InlineData(true, 8)]
    [InlineData(false, 9)]
    [InlineData(true, 9)]
    public async Task A_Map_and_a_Set_read_by_name_or_through_a_class_have_the_schema_comparer_and_own_their_values(bool ignoresCase, int count)
    {
        await using var database = await TestDatabases.OpenMasterAsync(CollectionSchema.Compile(ignoresCase));
        var typed = database.Set<CollectionItem>();
        var records = database.Set("Item");
        var item = Item(1, count);
        await typed.InsertAsync(item);

        var first = typed.Get(item.Id)!;
        var second = records.Get(item.Id)!;
        AssertItem(Item(1, count), first, second);

        // Lattice's simple uppercase rule maps dotless i to I. An ordinal collection keeps them apart.
        Assert.Equal(ignoresCase, first.Options!.ContainsKey("I"));
        Assert.Equal(ignoresCase, first.Rights!.Contains("I"));
        var options = second.Get<Dictionary<string, LatticeOwnedValue?>>("Options")!;
        var rights = second.Get<HashSet<string?>>("Rights")!;
        Assert.Equal(ignoresCase, options.ContainsKey("I"));
        Assert.Equal(ignoresCase, rights.Contains("I"));

        item.Options!["ı"]!.Data[0] = 9;
        first.Options["ı"]!.Lines[0] = "changed";
        first.Options["ı"]!.Data[0] = 8;
        first.Options["ı"]!.Layers[0].Clear();
        Assert.Equal(0, options["ı"]!.Get<byte[]>("Data")![0]);
        Assert.Equal(9, options["ı"]!.Get<List<Dictionary<byte, ushort>>>("Layers")![0].Count);
        options["ı"]!.Get<byte[]>("Data")![0] = 7;
        rights.Clear();

        AssertItem(Item(1, count), typed.Get(item.Id)!, records.Get(item.Id)!);
    }

    [Fact]
    public async Task A_whole_write_crosses_the_collection_thresholds_and_keeps_null_distinct_from_empty()
    {
        await using var database = await TestDatabases.OpenMasterAsync(CollectionSchema.Compile());
        var typed = database.Set<CollectionItem>();
        var records = database.Set("Item");
        await typed.InsertAsync(Item(1, 8));

        foreach (var count in new[] { 9, 4, 0 })
        {
            var item = Item(1, count);
            await records.ReplaceAsync(CollectionSchema.RecordOf(database, item));

            AssertItem(item, typed.Get(item.Id)!, records.Get(item.Id)!);
        }

        await typed.ReplaceAsync(new CollectionItem { Id = 1 });
        Assert.Null(typed.Get(1L)!.Options);
        Assert.Null(typed.Get(1L)!.Rights);
        Assert.Null(records.Get(1L)!["Options"]);
        Assert.Null(records.Get(1L)!["Rights"]);
    }

    [Fact]
    public async Task A_child_loads_maps_and_sets_applies_replaces_and_forwards_a_whole_write_and_a_batch()
    {
        await using var master = await TestDatabases.OpenListeningMasterAsync(CollectionSchema.Compile());
        await master.Set<CollectionItem>().InsertAsync(Item(1, 8));
        await using var child = await TestDatabases.OpenChildAsync(master);

        AssertItem(Item(1, 8), child.Set<CollectionItem>().Get(1L)!, child.Set("Item").Get(1L)!);

        // A String record crosses its entry chunk well before the directory of an Int64 Set crosses
        // its first lookup block. The short packed Set still fits 9 bytes at this lookup threshold.
        var changed = Item(1, 5_731);
        var written = await master.Set<CollectionItem>().ReplaceAsync(changed);
        await child.WaitForPositionAsync(written.Position, TimeSpan.FromMinutes(1));
        AssertItem(changed, child.Set<CollectionItem>().Get(1L)!, child.Set("Item").Get(1L)!);

        var forwarded = Item(2, 9);
        written = await child.Set("Item").InsertAsync(CollectionSchema.RecordOf(child, forwarded));
        await child.WaitForPositionAsync(written.Position, TimeSpan.FromMinutes(1));

        var batched = Item(3, 9);
        var batch = child.CreateWriteBatch();
        batch.Insert(batched);
        batch.Replace(CollectionSchema.RecordOf(child, Item(1, 4)));
        var committed = await batch.CommitAsync();
        await child.WaitForPositionAsync(committed.Position, TimeSpan.FromMinutes(1));

        foreach (var database in new[] { master, child })
        {
            foreach (var expected in new[] { Item(1, 4), forwarded, batched })
            {
                AssertItem(expected, database.Set<CollectionItem>().Get(expected.Id)!, database.Set("Item").Get(expected.Id)!);
            }
        }
    }

    private static CollectionItem Item(long id, int count)
    {
        var item = new CollectionItem
        {
            Id = id,
            Options = new Dictionary<string, CollectionDetails?>(StringComparer.Ordinal),
            Rights = new HashSet<string?>(StringComparer.Ordinal),
            Scores = [.. Enumerable.Range(0, count).Select(value => (long)value)],
        };

        for (var index = 0; index < count; index++)
        {
            var key = index == 0 ? "ı" : $"key{index}";
            item.Options.Add(key, index % 2 != 0 ? null : new CollectionDetails { Lines = [$"line{index}"], Data = [(byte)index] });
            item.Rights.Add(index == 1 ? null : key);
        }

        for (var index = 0; index < Math.Min(count, 9); index++)
        {
            item.Numbers.Add((byte)index, (ushort)(index * 256));
            item.Bytes.Add((byte)index);
        }

        if (count > 0)
        {
            var details = item.Options["ı"]!;
            details.Layers = [Enumerable.Range(0, 9).ToDictionary(key => (byte)key, key => (ushort)(key + 20))];
            details.Flags = [.. Enumerable.Range(0, 9).Select(value => (byte)value)];
        }

        return item;
    }

    private static void AssertItem(CollectionItem expected, CollectionItem typed, LatticeRecord record)
    {
        Assert.Equal(expected.Id, typed.Id);
        Assert.Equal(expected.Id, record.Get<long>("Id"));
        Assert.Equal(expected.Numbers.OrderBy(pair => pair.Key), typed.Numbers.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Numbers.OrderBy(pair => pair.Key), record.Get<Dictionary<byte, ushort>>("Numbers")!.OrderBy(pair => pair.Key));
        Assert.Equal(expected.Bytes.Order(), typed.Bytes.Order());
        Assert.Equal(expected.Bytes.Order(), record.Get<HashSet<byte>>("Bytes")!.Order());
        Assert.Equal(expected.Scores.Order(), typed.Scores.Order());
        Assert.Equal(expected.Scores.Order(), record.Get<HashSet<long>>("Scores")!.Order());
        var rights = expected.Rights!.Order(StringComparer.Ordinal);
        Assert.Equal(rights, typed.Rights!.Order(StringComparer.Ordinal));
        Assert.Equal(rights, record.Get<HashSet<string?>>("Rights")!.Order(StringComparer.Ordinal));

        var options = record.Get<Dictionary<string, LatticeOwnedValue?>>("Options")!;
        Assert.Equal(expected.Options!.Keys.Order(StringComparer.Ordinal), typed.Options!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(expected.Options.Keys.Order(StringComparer.Ordinal), options.Keys.Order(StringComparer.Ordinal));
        foreach (var (key, value) in expected.Options)
        {
            if (value is null)
            {
                Assert.Null(typed.Options[key]);
                Assert.Null(options[key]);
                continue;
            }

            Assert.Equal(value.Lines, typed.Options[key]!.Lines);
            Assert.Equal(value.Lines, options[key]!.Get<List<string>>("Lines"));
            Assert.Equal(value.Data, typed.Options[key]!.Data);
            Assert.Equal(value.Data, options[key]!.Get<byte[]>("Data"));
            Assert.Equal(value.Flags.Order(), typed.Options[key]!.Flags.Order());
            Assert.Equal(value.Flags.Order(), options[key]!.Get<HashSet<byte>>("Flags")!.Order());
            var byName = options[key]!.Get<List<Dictionary<byte, ushort>>>("Layers")!;
            Assert.Equal(value.Layers.Count, typed.Options[key]!.Layers.Count);
            Assert.Equal(value.Layers.Count, byName.Count);
            for (var index = 0; index < value.Layers.Count; index++)
            {
                var expectedLayer = value.Layers[index].OrderBy(pair => pair.Key);
                Assert.Equal(expectedLayer, typed.Options[key]!.Layers[index].OrderBy(pair => pair.Key));
                Assert.Equal(expectedLayer, byName[index].OrderBy(pair => pair.Key));
            }
        }
    }
}
