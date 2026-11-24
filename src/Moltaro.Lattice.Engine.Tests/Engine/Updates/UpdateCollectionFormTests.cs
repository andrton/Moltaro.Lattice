using System.Globalization;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Updates;

// Compact and hashed lookup serve the same logical values. Each node tracks the
// transitions at each effective command from its own local form (physical-storage.md).
public sealed class UpdateCollectionFormTests
{
    [Fact]
    public async Task Collection_hysteresis_uses_each_effective_command_and_allows_different_local_forms()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema, new EngineTestInputs { IndexSeed = 11 });
        var items = master.Set<UpdateItem>();
        var item = UpdateSchema.Item("ada");
        item.Tags = Enumerable.Range(0, 8).Select(index => index.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        await items.InsertAsync(item);
        Assert.False(Hashed(master));

        var crossesUp = items.CreateUpdate("ada");
        crossesUp.SetAdd("Tags", "extra");
        crossesUp.SetRemove("Tags", "extra");
        await crossesUp.CommitAsync();
        Assert.True(Hashed(master));
        Assert.Equal(8, items.Get("ada")!.Tags!.Count);

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { IndexSeed = 23 });
        var childItems = child.Set<UpdateItem>();
        Assert.False(Hashed(child));
        Assert.Equal(items.Get("ada")!.Tags!.Order(), childItems.Get("ada")!.Tags!.Order());

        var stillHashed = items.CreateUpdate("ada");
        stillHashed.SetRemove("Tags", "7");
        stillHashed.SetRemove("Tags", "6");
        stillHashed.SetRemove("Tags", "5");
        var write = await stillHashed.CommitAsync();
        await child.WaitForPositionAsync(write.Position, Bound);
        Assert.True(Hashed(master));
        Assert.False(Hashed(child));
        Assert.Equal(5, items.Get("ada")!.Tags!.Count);
        Assert.Equal(items.Get("ada")!.Tags!.Order(), childItems.Get("ada")!.Tags!.Order());

        var crossesDown = child.Set("Item").CreateUpdate("ada");
        crossesDown.SetRemove("Tags", "4");
        crossesDown.SetAdd("Tags", "new");
        await crossesDown.CommitAsync();
        Assert.False(Hashed(master));
        Assert.False(Hashed(child));
        Assert.Equal(new[] { "0", "1", "2", "3", "new" }, childItems.Get("ada")!.Tags!.Order());
        Assert.Equal(items.Get("ada")!.Tags!.Order(), childItems.Get("ada")!.Tags!.Order());
        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
    }

    private static bool Hashed(EngineDatabase database)
    {
        var hashed = false;
        database.ForEachLiveRoot((arena, layout, _, address) =>
        {
            ref readonly var field = ref layout.StoredFieldOf(layout.EntityType.GetField("Tags"));
            var descriptor = ValueBlocks.Read(arena.Payload(address)[(field.Offset - BlockHeader.Size)..]);
            hashed = descriptor.Kind == DescriptorKindEnum.Lookup;
        });

        return hashed;
    }
}
