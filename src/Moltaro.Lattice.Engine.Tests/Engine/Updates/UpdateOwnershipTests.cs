using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Batches;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.PartialUpdates;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Updates;

// Successive versions may share a value of one lifetime. Each native block has one
// owner, with its exact backlink, and older protected aliases remain readable until
// their epochs drain (decisions/memory-and-access.md and design/arena-protocol.md).
public sealed class UpdateOwnershipTests
{
    [Fact(Skip = UpdateSchema.Pending)]
    public async Task An_unchanged_branch_and_a_child_of_copied_records_survive_successive_owners_and_recreation()
    {
        await using var database = await EngineMaster.OpenAsync(UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var first = UpdateSchema.Item("ada");
        first.Data = Bytes(140_000, 17);
        first.Options!["Theme"]!.Data = Bytes(95_000, 23);
        await items.InsertAsync(first);
        using var protectsA = await HeldRead.StartAsync(database, () => items.Get("ada")?.Data.Length);
        var a = Inspect(database);

        var second = items.CreateUpdate("ada");
        second.Assign("Login", "Ada two");
        await second.CommitAsync();
        var b = Inspect(database);
        Assert.Equal((a.Handle, a.Branch, a.Records, a.Child), (b.Handle, b.Branch, b.Records, b.Child));
        Assert.NotEqual(a.Root, b.Root);
        Assert.Equal(2L, Assert.Single(database.TextReferences()).References);
        Assert.InRange(database.Counters.Arena.RetiredBytes, 1, first.Data.Length - 1);

        database.DrainRetired();
        using var protectsB = await HeldRead.StartAsync(database, () => items.Get("ada")?.Data.Length);

        var third = database.Set("Item").CreateUpdate("ada");
        third.MapSet<string, LatticeOwnedValue?>("Options", "Other", null);
        await third.CommitAsync();
        var c = Inspect(database);
        Assert.Equal((b.Handle, b.Branch, b.Child), (c.Handle, c.Branch, c.Child));
        Assert.NotEqual(b.Root, c.Root);
        Assert.NotEqual(b.Records, c.Records);
        Assert.Equal(3L, Assert.Single(database.TextReferences()).References);
        AssertProtected(database, a.Root, "ada", onlyTheme: true);
        AssertProtected(database, b.Root, "Ada two", onlyTheme: true);

        await items.DeleteAsync("ADA");
        await items.InsertAsync(UpdateSchema.Item("ada"));
        Assert.Equal(4L, Assert.Single(database.TextReferences()).References);
        Assert.True(database.Counters.Arena.RetiredBytes >= first.Data.Length + first.Options["Theme"]!.Data.Length);
        AssertProtected(database, a.Root, "ada", onlyTheme: true);
        AssertProtected(database, c.Root, "Ada two", onlyTheme: false);
        Assert.Equal(2, database.Counters.RootEntries);

        await protectsA.ReleaseAsync();
        database.DrainRetired();
        AssertProtected(database, b.Root, "Ada two", onlyTheme: true);
        AssertProtected(database, c.Root, "Ada two", onlyTheme: false);

        await protectsB.ReleaseAsync();
        OwnershipAudit.Expect(database);
        Assert.Equal(1L, Assert.Single(database.TextReferences()).References);
        Assert.Equal(1, database.Counters.RootEntries);
        Assert.Empty(items.Get("ada")!.Data);

        first.Label = "ordinary replacement";
        await items.ReplaceAsync(first);
        OwnershipAudit.Expect(database);
        Assert.Equal(first.Data, items.Get("ada")!.Data);
        Assert.Equal("ordinary replacement", items.Get("ada")!.Label);
    }

    [Fact(Skip = UpdateSchema.Pending)]
    public async Task Arena_refusal_of_a_mixed_batch_gives_back_prepared_blocks_but_keeps_borrowed_values()
    {
        var arena = new ArenaOptions { MemoryLimit = 4L * new ArenaOptions().SegmentSize };
        await using var database = await EngineMaster.OpenAsync(UpdateSchema.Schema, new EngineTestInputs { Arena = arena });
        var items = database.Set<UpdateItem>();
        var first = UpdateSchema.Item("ada");
        first.Data = Bytes(140_000, 17);
        first.Options!["Theme"]!.Data = Bytes(95_000, 23);
        await items.InsertAsync(first);
        await items.InsertAsync(UpdateSchema.Item("grace"));
        database.DrainRetired();
        Assert.True(items.TryGet("ada", out _, out var revision));
        var before = database.Counters;

        var update = items.CreateUpdate("ada");
        var changed = update.MapSet("Options", "new", new UpdateHome { City = "Paris", Data = Bytes(95_000, 47) });
        var tooLarge = items.CreateUpdate("grace");
        tooLarge.MapSet("Options", "large", new UpdateHome { City = "Paris", Data = new byte[18 * 1024 * 1024] });
        var batch = database.CreateWriteBatch();
        batch.Update(update);
        batch.Insert(UpdateSchema.Item("linus"));
        batch.Update(tooLarge);

        var error = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.WriteRejected, error.Error);
        Assert.Equal(LatticeLimitEnum.Memory, error.Limit);
        Assert.Null(error.OperationIndex);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
        OwnershipAudit.Expect(database);
        var after = database.Counters;
        Assert.Equal(before.PublishedSequence, after.PublishedSequence);
        Assert.Equal(before.Arena.LiveBytes, after.Arena.LiveBytes);
        Assert.Equal(before.Arena.ClassSlackBytes, after.Arena.ClassSlackBytes);
        Assert.Equal(before.TextEntries, after.TextEntries);
        Assert.True(items.TryGet("ada", out var stored, out var unchanged));
        Assert.Equal(revision, unchanged);
        Assert.Equal(first.Data, stored.Data);
        Assert.Equal(first.Options["Theme"]!.Data, Assert.Single(stored.Options!).Value!.Data);
        Assert.Null(items.Get("linus"));

        var retry = items.CreateUpdate("ada");
        retry.MapSet<string, UpdateHome?>("Options", "new", null);
        await retry.CommitAsync();
        OwnershipAudit.Expect(database);
    }

    [Theory(Skip = UpdateSchema.Pending)]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fault_after_transfer_uses_publication_failure_and_close_releases_every_slot(bool onMaster)
    {
        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema, new EngineTestInputs { Allocator = masterAllocator });
        var item = UpdateSchema.Item("ada");
        item.Data = Bytes(140_000, 17);
        await master.Set<UpdateItem>().InsertAsync(item);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { Allocator = childAllocator });
        var failingNode = onMaster ? master : child;
        var before = master.Position;
        using var held = HeldPublication.Arm(failingNode, PublicationPointEnum.AfterFirstChange);
        var update = failingNode.Set<UpdateItem>().CreateUpdate("ada");
        var outcome = update.Assign("Home.City", "Paris");
        var batch = failingNode.CreateWriteBatch();
        batch.Update(update);
        batch.Insert(UpdateSchema.Item("grace"));
        var committing = OwnThread.Run(() => OwnThread.Wait(batch.CommitAsync()));
        await held.ReachedAsync();
        Assert.Throws<InvalidOperationException>(() => outcome.Changed);
        held.Fail();

        var failure = await Assert.ThrowsAsync<LatticeException>(() => committing.WaitAsync(Bound));
        Assert.Equal(onMaster ? LatticeErrorEnum.IndeterminateOutcome : LatticeErrorEnum.SynchronizationTimeout, failure.Error);
        Assert.Equal(LatticeDatabaseStateEnum.Faulted, failingNode.State);
        Assert.Throws<InvalidOperationException>(() => outcome.Changed);
        if (!onMaster)
        {
            Assert.NotEqual(before, master.Position);
            Assert.Equal(master.Position, failure.CommittedPosition);
            Assert.Equal("Paris", master.Set<UpdateItem>().Get("ada")!.Home!.City);
        }

        await child.CloseAsync().AsTask().WaitAsync(Bound);
        await master.CloseAsync().AsTask().WaitAsync(Bound);
        masterAllocator.ExpectEverythingReturned();
        childAllocator.ExpectEverythingReturned();
    }

    private static (RootHandle Handle, ArenaAddress Root, ArenaAddress Branch, ArenaAddress Records, ArenaAddress Child) Inspect(EngineDatabase database)
    {
        (RootHandle, ArenaAddress, ArenaAddress, ArenaAddress, ArenaAddress) result = default;
        database.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            ref readonly var dataField = ref layout.StoredFieldOf(layout.EntityType.GetField("Data"));
            ref readonly var options = ref layout.StoredFieldOf(layout.EntityType.GetField("Options"));
            var branch = AddressOf(arena, root, dataField.Offset);
            var records = AddressOf(arena, root, options.Offset);
            var element = layout.Structure.ElementOf(options.Field.Ordinal);
            var childField = Enumerable.Range(0, element.DescriptorCount)
                .Select(index => element.DescriptorField(index))
                .Single(field => field.Field.Name == "Data");
            var childOffset = BlockHeader.Size + childField.Offset;
            var child = AddressOf(arena, records, childOffset);

            AssertOwner(arena, branch, handle, root, dataField.Offset);
            AssertOwner(arena, records, handle, root, options.Offset);
            AssertOwner(arena, child, handle, records, childOffset);
            result = (handle, root, branch, records, child);
        });

        Assert.NotEqual(default, result.Item2);
        return result;
    }

    private static void AssertProtected(EngineDatabase database, ArenaAddress root, string login, bool onlyTheme) =>
        database.ForEachLiveRoot((arena, layout, _, _) =>
        {
            var reader = new RootReader(arena, layout, root);
            Assert.Equal(login, reader.GetString(layout.EntityType.GetField("Login")));
            Assert.Equal(17, reader.GetBinary(layout.EntityType.GetField("Data"))[0]);
            var map = Assert.IsType<Dictionary<string, LatticeOwnedValue?>>(reader.GetNested(layout.EntityType.GetField("Options")));
            Assert.Equal(onlyTheme ? 1 : 2, map.Count);
            Assert.Equal(23, map["Theme"]!.Get<byte[]>("Data")![0]);
            Assert.Equal("shared label", reader.GetString(layout.EntityType.GetField("Label")));
        });

    private static void AssertOwner(Arena arena, ArenaAddress block, RootHandle handle, ArenaAddress parent, int offset)
    {
        var header = arena.Header(block);
        Assert.Equal((handle, parent, (uint)offset), (header.Root, header.Parent, header.DescriptorOffset));
    }

    private static ArenaAddress AddressOf(Arena arena, ArenaAddress parent, int offset) =>
        ValueBlocks.AddressOf(ValueBlocks.Read(arena.Payload(parent)[(offset - BlockHeader.Size)..]));

    private static byte[] Bytes(int count, byte first)
    {
        var bytes = new byte[count];
        bytes[0] = first;
        return bytes;
    }
}
