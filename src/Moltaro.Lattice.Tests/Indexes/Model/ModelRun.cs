namespace Moltaro.Lattice.Tests.Indexes.Model;

// One run of a model test: the entities are written to a master, the first half before a child opens, so
// that the child builds its index from what it loads, and the second half after, so that it keeps its
// index while it follows (docs/design/ordered-index.md, "A child"); the even ids through the class, the
// odd ones by name. When the child has applied the last write, every search is made on the master and on
// the child and held against the model:
// - through the class and by name, by FindAsync in both directions and by the lazy Find in the index
//   order; HasMore is false without a Limit;
// - the pages after an Offset and within a Limit of each page search, through the class and by name in
//   both directions, with HasMore true exactly when a match follows the page;
// - every entity returned holds what it was stored with, bit for bit, as a read returns it: a
//   DateTimeOffset at the offset zero (IndexModel.AsRead).
// A failure names the cell, which says the type and its seed, the node, the search and the form.
internal static class ModelRun
{
    // The pages of each page search: at the start, inside the matches, at their end and beyond it.
    private static readonly (int Offset, int? Limit)[] PageCuts = [(0, 1), (0, 10), (5, 10), (-3, 10), (-3, 3), (-1, 1), (0, null), (3, null)];

    public static async Task RunAsync<TEntity>(string cell, LatticeSchema schema, ModelTable<TEntity> table, IReadOnlyList<ModelEntry> given, IReadOnlyList<ModelSearch> searches, IReadOnlyList<ModelSearch> pages)
        where TEntity : class
    {
        var stored = given.Select(entry => entry with { Key = [.. entry.Key.Select(IndexModel.AsRead)] }).ToList();
        var half = given.Count / 2;

        await using var master = await TestData.OpenMasterAsync(MasterAndChild.Loopback, schema);
        await InsertAsync(master, table, given.Take(half));

        await using var child = await TestData.OpenChildAsync(master.ListenUri!);
        await InsertAsync(master, table, given.Skip(half));
        await child.WaitForPositionAsync(master.Position);

        foreach (var (node, database) in new[] { ("master", master), ("child", child) })
        {
            foreach (var search in searches)
            {
                await ExpectAsync(database, table, stored, search, $"{cell} on the {node}: {table.Index}{search}");
            }

            foreach (var search in pages)
            {
                await ExpectPagesAsync(database, table, stored, search, $"{cell} on the {node}: {table.Index}{search}");
            }
        }
    }

    private static async Task InsertAsync<TEntity>(ILatticeDatabase database, ModelTable<TEntity> table, IEnumerable<ModelEntry> entries)
        where TEntity : class
    {
        var entities = database.Set<TEntity>();
        var records = database.Set(table.EntityType);

        foreach (var entry in entries)
        {
            if (entry.Id % 2 == 0)
            {
                await entities.InsertAsync(table.ToEntity(entry));
            }
            else
            {
                await records.InsertAsync(RecordOf(records.EntityType, table, entry));
            }
        }
    }

    // The record of the entry. A field named Owner.Field is the field of the owned value Owner, which the
    // record holds only when the field has a value.
    private static LatticeRecord RecordOf<TEntity>(LatticeEntityType entityType, ModelTable<TEntity> table, ModelEntry entry)
        where TEntity : class
    {
        var record = new LatticeRecord(entityType) { ["Id"] = entry.Id };

        for (var part = 0; part < table.Fields.Count; part++)
        {
            if (entry.Key[part] is not { } value)
            {
                continue;
            }

            if (table.Fields[part].Split('.') is [var owner, var field])
            {
                Assert.True(entityType.TryGetField(owner, out var ownerField));

                var owned = record[owner] as LatticeOwnedValue ?? new LatticeOwnedValue(ownerField.Declaration.OwnedType!);

                owned[field] = value;
                record[owner] = owned;
            }
            else
            {
                record[table.Fields[part]] = value;
            }
        }

        return record;
    }

    // The value of the field of the record, null where an owned value on its path is absent.
    private static object? ValueOf(LatticeRecord record, string name) =>
        name.Split('.') is [var owner, var field] ? (record[owner] as LatticeOwnedValue)?[field] : record[name];

    private static async Task ExpectAsync<TEntity>(ILatticeDatabase database, ModelTable<TEntity> table, List<ModelEntry> stored, ModelSearch search, string name)
        where TEntity : class
    {
        var byClass = database.Set<TEntity>().Index(table.Index);
        var byName = database.Set(table.EntityType).Index(table.Index);
        var key = table.KeyOf(search);
        var options = new LatticeFindOptions { Reverse = search.Reverse };
        var expected = table.Model.Answer(stored, search);

        var throughClass = await byClass.FindAsync(key, options);
        var byRecord = await byName.FindAsync(key, options);

        Expect(table, expected, Read(table, throughClass.Items), $"{name}, by FindAsync through the class");
        Expect(table, expected, Read(table, byRecord.Items), $"{name}, by FindAsync by name");
        Assert.False(throughClass.HasMore || byRecord.HasMore, $"{name} has more, which only a search with a limit can have.");

        if (!search.Reverse)
        {
            Expect(table, expected, Read(table, byClass.Find(key)), $"{name}, by the lazy Find through the class");
            Expect(table, expected, Read(table, byName.Find(key)), $"{name}, by the lazy Find by name");
        }
    }

    private static async Task ExpectPagesAsync<TEntity>(ILatticeDatabase database, ModelTable<TEntity> table, List<ModelEntry> stored, ModelSearch search, string name)
        where TEntity : class
    {
        var byClass = database.Set<TEntity>().Index(table.Index);
        var byName = database.Set(table.EntityType).Index(table.Index);
        var key = table.KeyOf(search);
        var all = table.Model.Answer(stored, search);

        foreach (var (cutOffset, limit) in PageCuts)
        {
            // A negative offset counts back from the end of the matches.
            var offset = cutOffset < 0 ? Math.Max(0, all.Count + cutOffset) : cutOffset;
            var options = new LatticeFindOptions { Reverse = search.Reverse, Offset = offset, Limit = limit };
            var page = $"{name} after {offset} with a limit of {limit?.ToString() ?? "none"}";
            var hasMore = limit is { } most && offset + most < all.Count;

            var throughClass = await byClass.FindAsync(key, options);
            var byRecord = await byName.FindAsync(key, options);

            table.Model.ExpectPage($"{page}, through the class", all, offset, limit, Read(table, throughClass.Items));
            table.Model.ExpectPage($"{page}, by name", all, offset, limit, Read(table, byRecord.Items));
            IndexModel.Check(throughClass.HasMore == hasMore, () => $"{page}, through the class, has more: {throughClass.HasMore}, where the model has {hasMore}.");
            IndexModel.Check(byRecord.HasMore == hasMore, () => $"{page}, by name, has more: {byRecord.HasMore}, where the model has {hasMore}.");
        }
    }

    private static void Expect<TEntity>(ModelTable<TEntity> table, List<ModelEntry> expected, List<ModelEntry> actual, string name)
        where TEntity : class
    {
        table.Model.Expect(name, expected, actual);
        IndexModel.ExpectStored(name, expected, actual);

        foreach (var entry in actual)
        {
            IndexModel.Check(entry.Key.All(value => value is not DateTimeOffset moment || moment.Offset == TimeSpan.Zero), () => $"{name} returned {entry}, with an offset a read does not return.");
        }
    }

    private static List<ModelEntry> Read<TEntity>(ModelTable<TEntity> table, IEnumerable<TEntity> entities)
        where TEntity : class =>
        [.. entities.Select(table.FromEntity)];

    private static List<ModelEntry> Read<TEntity>(ModelTable<TEntity> table, IEnumerable<LatticeRecord> records)
        where TEntity : class =>
        [.. records.Select(record => new ModelEntry(record.Get<long>("Id"), [.. table.Fields.Select(field => ValueOf(record, field))]))];
}
