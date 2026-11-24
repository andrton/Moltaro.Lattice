using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.HeldRead;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Updates;

// A root and the index entries of its fields become visible at the same position
// on either role (docs/design/write-path.md, "How a publication is made").
public sealed class UpdateVisibilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true, Skip = UpdateSchema.Pending)]
    public async Task Reads_by_key_and_changed_index_wait_for_the_same_update_position(bool onChild)
    {
        await using var master = onChild
            ? await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema)
            : await EngineMaster.OpenAsync(UpdateSchema.Schema);
        var items = master.Set<UpdateItem>();
        await items.InsertAsync(UpdateSchema.Item("ada"));
        var grace = UpdateSchema.Item("grace");
        grace.Home!.City = "Berlin";
        await items.InsertAsync(grace);
        await using var child = onChild ? await ReplicationNodes.OpenChildAsync(master.ListenUri!) : null;

        var reader = child ?? master;
        var typed = reader.Set<UpdateItem>();
        var index = typed.Index("ByCity");
        var before = reader.Position;
        Assert.Equal("Oslo", typed.Get("ada")!.Home!.City);
        Assert.Equal("ada", Assert.Single(index.Find(IndexKey.Equal("Oslo"))).Id);
        Assert.Empty(index.Find(IndexKey.Equal("Paris")));

        using var held = HeldPublication.Arm(reader, PublicationPointEnum.BeforePosition);
        var update = items.CreateUpdate("ada");
        var city = update.Assign("Home.City", "Paris");
        var commit = OwnThread.Run(() => OwnThread.Wait(update.CommitAsync()));
        await held.ReachedAsync();

        var byKey = await StartAtTheRootAsync(reader, () => OwnThread.Wait(typed.GetAsync("ada", new LatticeSession())));
        var byIndex = await StartAtTheRootAsync(reader, () => OwnThread.Wait(index.FindAsync(IndexKey.Equal("Paris"), new LatticeFindOptions())));
        Assert.False(byKey.IsCompleted);
        Assert.False(byIndex.IsCompleted);
        Assert.Equal(before, reader.Position);
        Assert.Equal("Berlin", typed.Get("grace")!.Home!.City);

        held.Release();
        var written = await commit.WaitAsync(Bound);
        var read = await byKey.WaitAsync(Bound);
        var found = await byIndex.WaitAsync(Bound);
        Assert.Equal(written.Position, read.Position);
        Assert.Equal(written.Position, found.Position);
        Assert.True(city.Changed);
        Assert.Equal("Paris", read.Entity!.Home!.City);
        var indexed = Assert.Single(found.Items);
        Assert.Equal("ada", indexed.Id);
        Assert.Equal("Paris", indexed.Home!.City);
        Assert.Empty(index.Find(IndexKey.Equal("Oslo")));
    }
}
