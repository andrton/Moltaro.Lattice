using Moltaro.Lattice.TestSupport.PartialUpdates;

namespace Moltaro.Lattice.Tests.NestedValues.Collections;

public sealed class CollectionUpdateBindingTests
{
    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Map_keys_values_and_Set_members_use_lossless_numeric_binding(bool byClass)
    {
        await using var database = await TestDatabases.OpenMasterAsync(CollectionSchema.Compile());
        var typed = database.Set<CollectionItem>();
        var named = database.Set("Item");
        await typed.InsertAsync(new CollectionItem { Id = 1L, Numbers = new() { [3] = 300 }, Bytes = [3] });

        var update = byClass ? typed.CreateUpdate(1L) : named.CreateUpdate(1L);
        Assert.Throws<ArgumentException>(() => update.MapSet("Numbers", 256, 2));
        Assert.Throws<ArgumentException>(() => update.MapSet("Numbers", 2, 65_536L));
        Assert.Throws<ArgumentException>(() => update.MapRemove("Numbers", -1L));
        Assert.Throws<ArgumentException>(() => update.SetAdd("Bytes", 2.0));
        Assert.Throws<ArgumentException>(() => update.SetRemove("Bytes", 256U));
        Assert.Equal(0, update.Count);

        var stored = update.MapSet("Numbers", 255U, 65_535L);
        var removed = update.MapRemove("Numbers", (short)3);
        var added = update.SetAdd("Bytes", 255L);
        var duplicate = update.SetAdd("Bytes", 255U);
        var removedMember = update.SetRemove("Bytes", (sbyte)3);
        Assert.True((await update.CommitAsync()).Changed);
        Assert.Equal(new[] { true, true, true, false, true }, new[] { stored.Changed, removed.Changed, added.Changed, duplicate.Changed, removedMember.Changed });
        Assert.Equal(new KeyValuePair<byte, ushort>(255, 65_535), Assert.Single(typed.Get(1L)!.Numbers));
        Assert.Equal((byte)255, Assert.Single(named.Get(1L)!.Get<HashSet<byte>>("Bytes")!));
    }
}
