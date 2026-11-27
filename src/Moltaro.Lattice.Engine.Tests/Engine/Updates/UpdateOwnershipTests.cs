using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
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
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Updates;

// Successive versions may share a value of one lifetime. Each native block has one
// owner, with its exact backlink, and older protected aliases remain readable until
// their epochs drain (decisions/memory-and-access.md and design/arena-protocol.md).
public sealed class UpdateOwnershipTests
{
    [Fact]
    public async Task Growing_inline_data_keeps_its_neighbor_intact_and_can_move_out_of_line_without_changing_old_roots()
    {
        await using var database = await EngineMaster.OpenAsync(UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var first = UpdateSchema.Item("ada");
        var neighbor = Bytes(37, 41);
        var original = Bytes(7, 17);
        var grown = Bytes(ValueBlocks.InlineLimit - 1, 23);
        var outOfLine = Bytes(ValueBlocks.InlineLimit + 1, 29);
        first.Data = neighbor;
        first.Home!.Data = original;
        await items.InsertAsync(first);
        using var held = await HeldRead.StartAsync(database, () => items.Get("ada")?.Data.Length);
        var a = InspectInline(original, DescriptorKindEnum.Inline);

        // These bytes would overlap at the old offsets. The new holder has its
        // own placement and must give both values disjoint space.
        Assert.True(a.Changed.Location < a.Neighbor.Location);
        Assert.True(a.Changed.Location + (ulong)grown.Length > a.Neighbor.Location);
        var inside = items.CreateUpdate("ada");
        inside.Assign("Home.Data", grown);
        await inside.CommitAsync();
        var b = InspectInline(grown, DescriptorKindEnum.Inline);
        Assert.NotEqual(a.Root, b.Root);
        Assert.NotEqual((a.Changed.Location, a.Neighbor.Location), (b.Changed.Location, b.Neighbor.Location));

        var outside = database.Set("Item").CreateUpdate("ada");
        outside.Assign("Home.Data", outOfLine);
        await outside.CommitAsync();
        var c = InspectInline(outOfLine, DescriptorKindEnum.Block);
        Assert.NotEqual(b.Root, c.Root);
        database.ForEachLiveRoot((arena, layout, _, _) =>
        {
            var home = layout.EntityType.GetField("Home");
            var data = layout.EntityType.GetField("Data");
            var oldReader = new RootReader(arena, layout, a.Root);
            var grownReader = new RootReader(arena, layout, b.Root);
            Assert.Equal(original, Assert.IsType<LatticeOwnedValue>(oldReader.GetNested(home)).Get<byte[]>("Data"));
            Assert.Equal(grown, Assert.IsType<LatticeOwnedValue>(grownReader.GetNested(home)).Get<byte[]>("Data"));
            Assert.Equal(neighbor, oldReader.GetBinary(data));
            Assert.Equal(neighbor, grownReader.GetBinary(data));
        });

        await held.ReleaseAsync();
        OwnershipAudit.Expect(database);

        (ArenaAddress Root, ValueDescriptor Changed, ValueDescriptor Neighbor) InspectInline(byte[] expected, DescriptorKindEnum kind)
        {
            (ArenaAddress, ValueDescriptor, ValueDescriptor) result = default;
            database.ForEachLiveRoot((arena, layout, handle, root) =>
            {
                var home = layout.EntityType.GetField("Home");
                var sibling = layout.EntityType.GetField("Data");
                var declaration = Assert.Single(home.Declaration.OwnedType!.Fields, field => field.Name == "Data");
                var changedAt = layout.StoredFieldOf([home, declaration]).Offset;
                var neighborAt = layout.StoredFieldOf(sibling).Offset;
                var payload = arena.Payload(root);
                var changed = ResidentDescriptor.Read(payload[(changedAt - BlockHeader.Size)..]);
                var untouched = ResidentDescriptor.Read(payload[(neighborAt - BlockHeader.Size)..]);
                Assert.Equal(kind, changed.Kind);
                Assert.Equal((uint)expected.Length, changed.Length);
                Assert.Equal(DescriptorKindEnum.Inline, untouched.Kind);
                if (kind == DescriptorKindEnum.Inline)
                {
                    Assert.True(changed.Location + changed.Length <= untouched.Location ||
                        untouched.Location + untouched.Length <= changed.Location);
                }
                else
                {
                    AssertOwner(arena, ValueBlocks.AddressOf(changed), handle, root, changedAt);
                }

                var reader = new RootReader(arena, layout, root);
                Assert.Equal(expected, Assert.IsType<LatticeOwnedValue>(reader.GetNested(home)).Get<byte[]>("Data"));
                Assert.Equal(neighbor, reader.GetBinary(sibling));
                result = (root, changed, untouched);
            });

            Assert.NotEqual(default, result.Item1);
            return result;
        }
    }

    [Fact]
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

    [Fact]
    public async Task Arena_refusal_of_a_mixed_batch_gives_back_prepared_blocks_but_keeps_borrowed_values()
    {
        const long limit = 8L * Segment;
        const int tooLargeBytes = 18 * 1024 * 1024;
        const long roomForPreparation = Segment + Segment / 2;
        var allocator = new CountingNativeAllocator();
        await using var database = await OpenAsync(limit, allocator, schema: UpdateSchema.Schema);
        var items = database.Set<UpdateItem>();
        var first = UpdateSchema.Item("ada");
        first.Data = Bytes(140_000, 17);
        first.Options!["Theme"]!.Data = Bytes(95_000, 23);
        await items.InsertAsync(first);
        await items.InsertAsync(UpdateSchema.Item("grace"));
        database.DrainRetired();
        Assert.True(items.TryGet("ada", out _, out var revision));
        var before = database.Counters;
        var parts = await MaintenanceStep.RunAfterAdmissionAsync(database, parts => parts);

        var update = items.CreateUpdate("ada");
        var changed = update.MapSet("Options", "new", new UpdateHome { City = "Paris", Data = Bytes(95_000, 47) });
        var tooLarge = items.CreateUpdate("grace");
        var larger = new byte[tooLargeBytes];
        tooLarge.MapSet("Options", "large", new UpdateHome { City = "Paris", Data = larger });
        var linus = UpdateSchema.Item("linus");
        var estimates = BatchInputCharge.PerBuilderBytes + UpdateEstimate(database, update) + MemberEstimate(database, linus) +
            UpdateEstimate(database, tooLarge);
        var batch = database.CreateWriteBatch();
        batch.Update(update);
        batch.Insert(linus);
        batch.Update(tooLarge);

        Assert.Equal(3, batch.Count);
        var built = ExpectInFlight(database, allocator, estimates);
        Assert.InRange(built.Charged, Segment + ArrayBytes(typeof(byte), larger.Length), limit - 1);
        Assert.Equal(Segment, NativeBytes(built));
        var requests = allocator.Requests;
        var reachedLastMember = false;
        allocator.HoldNextAllocation(() =>
        {
            // The replacement uses its old handle. The earlier addition must already
            // hold a new root before the oversized final update obtains a second grant.
            Assert.Equal(3, parts.Roots.Count);
            reachedLastMember = true;
        });
        LatticeException error;
        using (LeaveAbout(database, roomForPreparation, out _))
        {
            _ = ExpectInFlight(database, allocator, estimates);
            error = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        }

        Assert.True(reachedLastMember);
        Assert.True(allocator.Requests > requests);
        Assert.Equal(2L * Segment, allocator.PeakBytes);
        Assert.Equal(NativeBytes(built), allocator.OutstandingBytes);
        Assert.Equal(LatticeErrorEnum.WriteRejected, error.Error);
        Assert.Equal(LatticeLimitEnum.Memory, error.Limit);
        Assert.Null(error.OperationIndex);
        Assert.Throws<InvalidOperationException>(() => changed.Changed);
        OwnershipAudit.Expect(database);
        _ = ExpectAtRest(database, allocator);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Fault_after_transfer_uses_publication_failure_and_close_releases_every_slot(bool onMaster)
    {
        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        var masterInputs = new EngineTestInputs { Allocator = masterAllocator };
        await using var master = onMaster
            ? await EngineMaster.OpenAsync(UpdateSchema.Schema, masterInputs)
            : await ReplicationNodes.OpenMasterAsync(UpdateSchema.Schema, masterInputs);
        var item = UpdateSchema.Item("ada");
        item.Data = Bytes(140_000, 17);
        await master.Set<UpdateItem>().InsertAsync(item);
        await using var child = onMaster ? null
            : await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs { Allocator = childAllocator });
        var failingNode = child ?? master;
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

        if (child is not null)
        {
            await child.CloseAsync().AsTask().WaitAsync(Bound);
        }

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
        ValueBlocks.AddressOf(ResidentDescriptor.Read(arena.Payload(parent)[(offset - BlockHeader.Size)..]));

    private static byte[] Bytes(int count, byte first)
    {
        var bytes = new byte[count];
        bytes[0] = first;
        return bytes;
    }
}
