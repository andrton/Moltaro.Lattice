using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewLookupFormTests
{
    [Fact]
    public async Task Protected_lookup_uses_each_nodes_resident_form_for_the_same_Map_and_Set_count()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema, new EngineTestInputs { IndexSeed = 11 });
        var items = master.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        var keys = Enumerable.Range(0, 9).Select(index => "Key" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
        item.Options = keys.ToDictionary(key => key, key => (UpdateHome?)new UpdateHome { City = key });
        item.Tags = keys.ToHashSet(StringComparer.Ordinal);
        await items.InsertAsync(item);

        var shrink = items.CreateUpdate("ada");
        foreach (var key in keys[5..])
        {
            shrink.MapRemove("Options", key);
            shrink.SetRemove("Tags", key);
        }

        var reduced = await shrink.CommitAsync();
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 23 });
        await child.WaitForPositionAsync(reduced.Position, Bound);

        // The master kept the lookup tables as the count fell to five. The child
        // loaded the same collections at five and built compact local forms.
        Assert.Equal((true, true), Hashed(master));
        Assert.Equal((false, false), Hashed(child));

        foreach (var database in new[] { master, child })
        {
            var records = database.Set("Item");
            Assert.True(records.TryView("ada", static view =>
            {
                var map = view["Options"].Map;
                var set = view["Tags"].Set;
                Assert.Equal(5, map.Count);
                Assert.Equal(5, set.Count);
                Assert.True(map.TryGetValue("kEY0", out var found));
                Assert.False(map.TryGetValue("key8", out _));
                Assert.True(set.Contains("kEY0"));
                Assert.False(set.Contains("key8"));
                return found.Owned["City"].GetString();
            }, out var city));
            Assert.Equal("Key0", city);
        }
    }

    private static (bool Map, bool Set) Hashed(EngineDatabase database)
    {
        (bool Map, bool Set)? forms = null;
        database.ForEachLiveRoot((arena, layout, _, root) =>
        {
            var bytes = arena.Payload(root);
            var map = layout.StoredFieldOf(layout.EntityType.GetField("Options")).Offset - BlockHeader.Size;
            var set = layout.StoredFieldOf(layout.EntityType.GetField("Tags")).Offset - BlockHeader.Size;
            forms = (ResidentDescriptor.Read(bytes[map..]).Kind == DescriptorKindEnum.Lookup,
                ResidentDescriptor.Read(bytes[set..]).Kind == DescriptorKindEnum.Lookup);
        });

        Assert.True(forms.HasValue);
        return forms.Value;
    }
}
