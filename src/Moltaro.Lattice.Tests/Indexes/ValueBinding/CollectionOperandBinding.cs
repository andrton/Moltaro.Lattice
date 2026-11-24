using Moltaro.Lattice.Tests.Indexes.Model;

namespace Moltaro.Lattice.Tests.Indexes.ValueBinding;

// Put the target declaration of one allowlist row in every applicable collection
// position. Compare the actual stored values with the index tests' bit-exact oracle.
internal static class CollectionOperandBinding
{
    public static Task ExpectAsync(AllowlistCase row) => BindingSchema.EndsOf(row.Field)[0] switch
    {
        bool => ExpectAsync<bool>(row),
        sbyte => ExpectAsync<sbyte>(row),
        byte => ExpectAsync<byte>(row),
        short => ExpectAsync<short>(row),
        ushort => ExpectAsync<ushort>(row),
        int => ExpectAsync<int>(row),
        uint => ExpectAsync<uint>(row),
        long => ExpectAsync<long>(row),
        ulong => ExpectAsync<ulong>(row),
        float => ExpectAsync<float>(row),
        double => ExpectAsync<double>(row),
        decimal => ExpectAsync<decimal>(row),
        Guid => ExpectAsync<Guid>(row),
        DateTimeOffset => ExpectAsync<DateTimeOffset>(row),
        DateOnly => ExpectAsync<DateOnly>(row),
        TimeOnly => ExpectAsync<TimeOnly>(row),
        TimeSpan => ExpectAsync<TimeSpan>(row),
        string => ExpectAsync<string>(row),
        _ => throw new ArgumentOutOfRangeException(nameof(row), row.Field, "The row has no collection operand type."),
    };

    private static async Task ExpectAsync<T>(AllowlistCase row)
        where T : notnull
    {
        var scalar = Assert.Single(BindingSchema.Utf8.EntityTypes, entity => entity.Name == "Item");
        var type = scalar.GetField(row.Field).Declaration.Type;
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Operands", "ClrType": "{{typeof(CollectionOperandItem<T>).FullName}}",
                  "Fields": [
                    { "Id": 1, "Name": "Id", "Type": "Int64" },
                    { "Id": 2, "Name": "Keys", "Type": "Map", "Key": { "Type": "{{type}}" }, "Value": { "Type": "Boolean" } },
                    { "Id": 3, "Name": "Values", "Type": "Map", "Key": { "Type": "Int32" }, "Value": { "Type": "{{type}}" } },
                    { "Id": 4, "Name": "Members", "Type": "Set", "Element": { "Type": "{{type}}" } },
                    { "Id": 5, "Name": "Elements", "Type": "List", "Element": { "Type": "{{type}}" } }
                  ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);
        await using var database = await TestData.OpenMasterAsync(schema: schema);
        var typed = database.Set<CollectionOperandItem<T>>();
        var named = database.Set("Operands");

        foreach (var byClass in new[] { true, false })
        {
            var key = byClass ? 1L : 2L;
            await typed.InsertAsync(new CollectionOperandItem<T> { Id = key });
            var update = byClass ? typed.CreateUpdate(key) : named.CreateUpdate(key);
            if (row.Bound is null)
            {
                ExpectRefused(update, row.Given);
                continue;
            }

            var mapKey = update.MapSet("Keys", row.Given, true);
            var mapValue = update.MapSet("Values", 17, row.Given);
            var member = update.SetAdd("Members", row.Given);
            var element = update.ListAppend("Elements", row.Given);
            Assert.True((await update.CommitAsync()).Changed);
            Assert.All(new[] { mapKey, mapValue, member, element }, command => Assert.True(command.Changed));

            var fromClass = typed.Get(key)!;
            var fromRecord = named.Get(key)!;
            ExpectStored(row, fromClass.Keys, fromClass.Values, fromClass.Members, fromClass.Elements);
            ExpectStored(row, fromRecord.Get<Dictionary<T, bool>>("Keys")!, fromRecord.Get<Dictionary<int, T>>("Values")!,
                fromRecord.Get<HashSet<T>>("Members")!, fromRecord.Get<List<T>>("Elements")!);

            var next = byClass ? typed.CreateUpdate(key) : named.CreateUpdate(key);
            var removedKey = next.MapRemove("Keys", row.Given);
            var removedMember = next.SetRemove("Members", row.Given);
            var inserted = next.ListInsert("Elements", 1, row.Given);
            var replaced = next.ListReplace("Elements", 0, row.Given);
            await next.CommitAsync();
            Assert.All(new[] { removedKey, removedMember, inserted, replaced }, command => Assert.True(command.Changed));
            var final = typed.Get(key)!;
            Assert.Empty(final.Keys);
            Assert.Empty(final.Members);
            Assert.Equal(2, final.Elements.Count);
            IndexModel.ExpectStored(row.Name, [new ModelEntry(key, [row.Bound, row.Bound])],
                [new ModelEntry(key, [final.Elements[0], final.Elements[1]])]);
        }
    }

    private static void ExpectRefused(LatticeUpdate update, object given)
    {
        Assert.Throws<ArgumentException>(() => update.MapSet("Keys", given, true));
        Assert.Throws<ArgumentException>(() => update.MapSet("Values", 17, given));
        Assert.Throws<ArgumentException>(() => update.MapRemove("Keys", given));
        Assert.Throws<ArgumentException>(() => update.SetAdd("Members", given));
        Assert.Throws<ArgumentException>(() => update.SetRemove("Members", given));
        Assert.Throws<ArgumentException>(() => update.ListAppend("Elements", given));
        Assert.Throws<ArgumentException>(() => update.ListInsert("Elements", 0, given));
        Assert.Throws<ArgumentException>(() => update.ListReplace("Elements", 0, given));
        Assert.Equal(0, update.Count);
    }

    private static void ExpectStored<T>(AllowlistCase row, Dictionary<T, bool> keys, Dictionary<int, T> values, HashSet<T> members, List<T> elements)
        where T : notnull
    {
        var key = Assert.Single(keys);
        Assert.True(key.Value);
        var mapped = Assert.Single(values);
        Assert.Equal(17, mapped.Key);
        var member = Assert.Single(members);
        var element = Assert.Single(elements);
        IndexModel.ExpectStored(row.Name, [new ModelEntry(1, [row.Bound, row.Bound, row.Bound, row.Bound])],
            [new ModelEntry(1, [key.Key, mapped.Value, member, element])]);
    }
}
