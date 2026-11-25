using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using static Moltaro.Lattice.Engine.Tests.Engine.Collections.CollectionShrinkPair;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

// Q17: capacity changes are local, preserve old readers and logical results, and progress
// within the maintenance budget. A native grant proves reuse independently of its address.
public sealed class CollectionShrinkTests
{
    private const string ShrinkPending = "Collection shrink is pending.";
    private const int ValueDescriptorBytes = 16;

    [Theory(Skip = ShrinkPending)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Sparse_regrowth_reuses_only_safe_old_slots_and_a_second_decline_waits_a_fresh_window(bool set, bool flattened)
    {
        await using var pair = await OpenAsync(set, flattened, Full);
        pair.TurnAll();
        var initial = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        var originalRevision = pair.ExpectCurrent(pair.Nodes[0], Full, pair.Position);

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var readingMaster = pair.HoldAsync(pair.Nodes[0], masterPause, Full);
        var readingChild = pair.HoldAsync(pair.Nodes[1], childPause, Full);
        await masterPause.EnteredAsync(readingMaster);
        await childPause.EnteredAsync(readingChild);
        var firstPosition = await pair.ResizeAsync(Full, Small);
        pair.Advance(5_000);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            Assert.NotEqual(originalRevision, pair.ExpectCurrent(node, Small, firstPosition));
            var previous = node.Observe(initial[role].Chunks);
            Assert.All(previous, slot => Assert.True(slot.Retired));
            AssertSameGrants(initial[role].Chunks, previous);
            Assert.True(node.Database.Counters.Arena.RetiredBytes >= 2 * WholeChunk);
            Assert.Empty(node.LeaseSnapshot());
            node.ExpectNativeLedger(heldRoots: [initial[role]]);
        }

        masterPause.Release();
        childPause.Release();
        Assert.Equal(originalRevision, await readingMaster.WaitAsync(Bound));
        Assert.Equal(originalRevision, await readingChild.WaitAsync(Bound));
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], initial[role].Chunks);
        }

        pair.Advance(4_999);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], initial[role].Chunks);
        }

        var grownPosition = await pair.ResizeAsync(Small, Full);
        pair.TurnAll();
        var regrown = new CollectionLookupShape[pair.Nodes.Length];
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var grown = ExpectFull(pair, node, Full, 2);
            AssertSameGrants(initial[role].Chunks, grown.Chunks);
            regrown[role] = grown;
            Assert.Empty(node.LeaseSnapshot());
            node.ExpectNativeLedger();
            pair.ExpectCurrent(node, Full, grownPosition);
        }

        await pair.ResizeAsync(Full, Small);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            Assert.Equal(8, pair.Shape(pair.Nodes[role]).Active);
            ExpectCold(pair, pair.Nodes[role], regrown[role].Chunks);
        }

        // A newer logical edit that stays above four times need must leave the first timestamp in place.
        pair.Advance(4_998);
        await pair.ResizeAsync(Small, Small + 1);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var cold = ExpectCold(pair, node, regrown[role].Chunks);
            Assert.True(pair.Shape(node).Active + cold * WholeChunkSlots > 4 * Need(Small + 1));
        }

        var lastPosition = await pair.ResizeAsync(Small + 1, Small);
        pair.TurnAll();
        var current = pair.Nodes.Select(pair.Shape).ToArray();
        var revisions = pair.Nodes.Select(node => pair.ExpectCurrent(node, Small, lastPosition)).ToArray();
        pair.Advance(1);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], regrown[role].Chunks);
        }

        pair.Advance(1);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            ExpectBoundedReturn(node, regrown[role].Chunks);
            ExpectNoLogicalRebuild(pair, node, current[role], Small, lastPosition, revisions[role]);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Growth_cannot_reuse_an_old_lookup_while_a_reader_still_owns_it(bool set)
    {
        await using var pair = await OpenAsync(set, flattened: false, Full);
        var initial = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        var originalRevision = pair.ExpectCurrent(pair.Nodes[0], Full, pair.Position);
        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var readingMaster = pair.HoldAsync(pair.Nodes[0], masterPause, Full);
        var readingChild = pair.HoldAsync(pair.Nodes[1], childPause, Full);
        await masterPause.EnteredAsync(readingMaster);
        await childPause.EnteredAsync(readingChild);
        await pair.ResizeAsync(Full, Small);
        var small = pair.Nodes.Select(pair.Shape).ToArray();
        var grownPosition = await pair.ResizeAsync(Small, Full);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var held = node.Observe(initial[role].Chunks);
            AssertSameGrants(initial[role].Chunks, held);
            Assert.All(held, slot => Assert.True(slot.Retired));
            var grown = ExpectFull(pair, node, Full, 2);
            Assert.Empty(grown.Chunks.Select(slot => slot.Address).Intersect(held.Select(slot => slot.Address)));
            Assert.NotEqual(originalRevision, pair.ExpectCurrent(node, Full, grownPosition));
            Assert.Empty(node.LeaseSnapshot());
            node.ExpectNativeLedger(heldRoots: [initial[role], small[role]]);
        }

        masterPause.Release();
        childPause.Release();
        Assert.Equal(originalRevision, await readingMaster.WaitAsync(Bound));
        Assert.Equal(originalRevision, await readingChild.WaitAsync(Bound));
    }

    [Theory(Skip = ShrinkPending)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exactly_four_times_need_clears_the_window_and_one_more_deletion_starts_it_again(bool firstAbove)
    {
        const int equal = 5_731;
        const int strict = 5_730;
        var before = firstAbove ? 22_000 : 17_100;
        var previous = firstAbove ? Small : before;
        await using var pair = await OpenAsync(set: false, flattened: false, before);
        var original = pair.Nodes.Select(node => ExpectFull(pair, node, before, firstAbove ? 4 : 3).Chunks).ToArray();

        if (firstAbove)
        {
            await pair.ResizeAsync(before, previous);
            pair.TurnAll();
            for (var role = 0; role < pair.Nodes.Length; role++)
            {
                var node = pair.Nodes[role];
                var cold = ExpectCold(pair, node, original[role]);
                Assert.True(pair.Shape(node).Active + cold * WholeChunkSlots > 4 * Need(previous));
            }

            pair.Advance(4_999);
            pair.TurnAll();
        }

        var equalityPosition = await pair.ResizeAsync(previous, equal);
        pair.TurnAll();
        var equalityShapes = pair.Nodes.Select(pair.Shape).ToArray();
        if (firstAbove)
        {
            for (var role = 0; role < pair.Nodes.Length; role++)
            {
                var active = Assert.Single(equalityShapes[role].Chunks);
                Assert.Contains(original[role], active.SameGrantAs);
                original[role] = original[role].Where(slot => !active.SameGrantAs(slot)).ToArray();
                Assert.Equal(3, original[role].Length);
            }
        }
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var cold = ExpectCold(pair, node, original[role]);
            Assert.Equal(4 * Need(equal), equalityShapes[role].Active + cold * WholeChunkSlots);
            Assert.Single(equalityShapes[role].Chunks);
            pair.ExpectCurrent(node, equal, equalityPosition);
        }

        pair.AdvancePastAnUnchangedWindow();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], original[role]);
        }

        var strictPosition = await pair.ResizeAsync(equal, strict);
        pair.TurnAll();
        var strictShapes = pair.Nodes.Select(pair.Shape).ToArray();
        var revisions = pair.Nodes.Select(node => pair.ExpectCurrent(node, strict, strictPosition)).ToArray();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var cold = ExpectCold(pair, pair.Nodes[role], original[role]);
            Assert.Empty(strictShapes[role].Chunks);
            Assert.True(strictShapes[role].Active + cold * WholeChunkSlots > 4 * Need(strict));
        }

        pair.Advance(4_999);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], original[role]);
        }

        pair.Advance(1);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            ExpectBoundedReturn(node, original[role]);
            ExpectNoLogicalRebuild(pair, node, strictShapes[role], strict, strictPosition, revisions[role]);
        }
    }

    [Fact(Skip = ShrinkPending)]
    public async Task An_eligible_shrink_finishes_after_an_ordinary_map_value_edit_below_four_times_need()
    {
        const int count = 4_200;
        const int changedValue = 0x723;
        await using var pair = await OpenAsync(set: false, flattened: false, Full);
        var original = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        var reduced = await pair.ResizeAsync(Full, count);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var cold = ExpectCold(pair, node, original[role].Chunks);
            Assert.True(pair.Shape(node).Active + cold * WholeChunkSlots > 4 * Need(count));
            pair.ExpectCurrent(node, count, reduced);
        }

        pair.Advance(5_000);
        var remaining = new CollectionNativeSlot[pair.Nodes.Length];
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var returns = node.Allocator.Returned;
            node.Turn();
            var (lease, slot) = Assert.Single(node.LeaseSnapshot());
            Assert.False(lease.Borrowed);
            Assert.Contains(original[role].Chunks, slot.SameGrantAs);
            Assert.Equal(1, node.Allocator.Returned - returns);
            var retained = pair.Shape(node).Active + WholeChunkSlots;
            Assert.InRange(retained, 2 * Need(count) + 1, 4 * Need(count));
            node.ExpectNativeLedger();
            remaining[role] = slot;
        }

        var previousOwner = pair.Nodes.Select(node => pair.Shape(node).Owner).ToArray();
        var update = pair.ChildItems.CreateUpdate(Key);
        var command = update.MapSet(pair.FieldPath, 0, changedValue);
        var changed = await update.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.True(command.Changed);
        Assert.Equal(reduced.Sequence + 1, changed.Position.Sequence);
        await pair.FollowAsync(changed.Position);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            node.Database.DrainRetired();
            var before = pair.Shape(node);
            Assert.NotEqual(previousOwner[role], before.Owner);
            var expected = ExpectEdited(node);
            ExpectBoundedReturn(node, [remaining[role]]);
            Assert.Equal(before.Owner, pair.Shape(node).Owner);
            Assert.Equal(expected, ExpectEdited(node));
        }

        LatticeRevision ExpectEdited(CollectionShrinkNode node)
        {
            Assert.Equal(changed.Position, node.Database.Position);
            var items = node.Database.Set("Item");
            Assert.True(items.TryGet(Key, out var record, out var revision));
            var copy = record.Get<Dictionary<int, int>>("Values")!;
            Assert.Equal(count, copy.Count);
            Assert.True(items.TryView(Key, view =>
            {
                var map = view["Values"].Map;
                Assert.Equal(count, map.Count);
                for (var index = 0; index < count; index++)
                {
                    var expected = index == 0 ? changedValue : 3 * index + 1;
                    Assert.Equal(expected, copy[index]);
                    Assert.True(map.TryGetValue(index, out var value));
                    Assert.Equal(expected, value.Get<int>());
                }

                Assert.False(map.TryGetValue(count, out _));
                return view.Revision;
            }, out var viewed));
            Assert.Equal(revision, viewed);
            return revision;
        }
    }

    [Theory(Skip = ShrinkPending)]
    [InlineData(355, 8_192, 1_016)]
    [InlineData(713, 16_384, 2_038)]
    public async Task Rounded_native_classes_are_fully_charged_when_shrink_caps_usable_capacity(int count, int nativeBytes, int maxSlots)
    {
        await using var pair = await OpenAsync(set: false, flattened: false, Full);
        var original = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        var position = await pair.ResizeAsync(Full, count);
        pair.TurnAll();
        var current = pair.Nodes.Select(pair.Shape).ToArray();
        var revisions = pair.Nodes.Select(node => pair.ExpectCurrent(node, count, position)).ToArray();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            Assert.InRange(current[role].Active, Need(count), maxSlots);
            var cold = ExpectCold(pair, pair.Nodes[role], original[role].Chunks);
            Assert.True(current[role].Active + cold * WholeChunkSlots > 4 * Need(count));
            Assert.Equal(nativeBytes, Assert.Single(current[role].Blocks).NativeBytes);
        }

        pair.Advance(5_000);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            ExpectBoundedReturn(node, original[role].Chunks);
            var shape = ExpectNoLogicalRebuild(pair, node, current[role], count, position, revisions[role]);
            Assert.InRange(shape.Active, Need(count), maxSlots);
            var active = Assert.Single(shape.Blocks);
            Assert.Equal(nativeBytes, active.NativeBytes);
            Assert.Equal(ValueDescriptorBytes + sizeof(ulong) * shape.Active, active.PayloadBytes);
        }
    }

    [Theory(Skip = ShrinkPending)]
    [InlineData(355, 8_192, 1_016)]
    [InlineData(713, 16_384, 2_038)]
    public async Task Clipped_lookup_payload_moves_and_preserves_public_map_hits_and_misses(int count, int nativeBytes, int maxSlots)
    {
        var fillerCount = WholeChunk / nativeBytes;
        await using var pair = await OpenAsync(set: false, flattened: false, count, fillerCount: fillerCount);
        var source = pair.Nodes.Select(pair.Shape).ToArray();
        var holes = pair.Nodes.Select(node => node.LookupOf(Key + fillerCount, flattened: false)).ToArray();
        var deletion = await pair.ChildItems.DeleteAsync(Key + fillerCount).AsTask().WaitAsync(Bound);
        await pair.FollowAsync(deletion.Position);
        foreach (var node in pair.Nodes)
        {
            node.Database.DrainRetired();
        }

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var readingMaster = pair.HoldAsync(pair.Nodes[0], masterPause, count);
        var readingChild = pair.HoldAsync(pair.Nodes[1], childPause, count);
        await masterPause.EnteredAsync(readingMaster);
        await childPause.EnteredAsync(readingChild);
        var revisions = pair.Nodes.Select(node => pair.ExpectCurrent(node, count, deletion.Position)).ToArray();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            var original = Assert.Single(source[role].Blocks);
            var hole = Assert.Single(holes[role].Blocks);
            Assert.Equal(nativeBytes, original.NativeBytes);
            Assert.Equal(nativeBytes, hole.NativeBytes);
            Assert.True(hole.Address.Segment < original.Address.Segment);
            Assert.False(Assert.Single(node.Observe([hole])).Allocated);
            var requests = node.Allocator.Requests;
            Assert.True(node.TryMoveLookup(source[role]));
            var moved = pair.Shape(node);
            Assert.Equal(hole.Address, moved.Owner);
            Assert.Equal(source[role].Active, moved.Active);
            Assert.Equal(original.PayloadBytes, Assert.Single(moved.Blocks).PayloadBytes);
            Assert.Equal(requests, node.Allocator.Requests);
            var retained = Assert.Single(node.Observe([original]));
            Assert.True(retained.SameGrantAs(original) && retained.Retired);
            Assert.Equal(revisions[role], pair.ExpectCurrent(node, count, deletion.Position));
            node.ExpectNativeLedger(movedOut: [original]);
        }

        masterPause.Release();
        childPause.Release();
        Assert.Equal(revisions[0], await readingMaster.WaitAsync(Bound));
        Assert.Equal(revisions[1], await readingChild.WaitAsync(Bound));
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            node.Database.DrainRetired();
            AssertNoFormerGrants(node, source[role].Blocks);
            node.ExpectNativeLedger();
            var current = pair.Shape(node);
            Assert.InRange(current.Active, Need(count), maxSlots);
            Assert.Equal(ValueDescriptorBytes + sizeof(ulong) * current.Active, Assert.Single(current.Blocks).PayloadBytes);
        }
    }

    [Theory(Skip = ShrinkPending)]
    [InlineData("assign")]
    [InlineData("null")]
    [InlineData("delete")]
    public async Task Ending_a_sparse_owner_releases_its_cold_slots_without_stranding_a_native_grant(string ending)
    {
        await using var pair = await OpenAsync(set: false, flattened: true, Full);
        var original = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        await pair.ResizeAsync(Full, Small);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], original[role].Chunks);
        }

        var previous = pair.Position;
        LatticeWriteResult ended;
        if (ending == "delete")
        {
            ended = await pair.ChildItems.DeleteAsync(Key).AsTask().WaitAsync(Bound);
        }
        else if (ending == "null")
        {
            var items = pair.ChildItems;
            var replacement = new LatticeRecord(items.EntityType) { ["Id"] = Key, ["Holder"] = null };
            ended = await items.ReplaceAsync(replacement).AsTask().WaitAsync(Bound);
        }
        else
        {
            var edit = pair.ChildItems.CreateUpdate(Key);
            edit.Assign(pair.FieldPath, Enumerable.Range(0, Small).ToDictionary(index => index, index => 3 * index + 1));
            ended = await edit.CommitAsync().AsTask().WaitAsync(Bound);
        }

        Assert.Equal(previous.Sequence + 1, ended.Position.Sequence);
        await pair.FollowAsync(ended.Position);
        pair.TurnAll();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            AssertNoFormerGrants(node, original[role].Chunks);
            Assert.Empty(node.LeaseSnapshot());
            node.ExpectNativeLedger();
            var items = node.Database.Set("Item");
            if (ending == "delete")
            {
                Assert.Null(items.Get(Key));
            }
            else if (ending == "null")
            {
                Assert.Null(items.Get(Key)!["Holder"]);
            }
            else
            {
                pair.ExpectCurrent(node, Small, ended.Position);
            }
        }
    }

    [Theory(Skip = ShrinkPending)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_refused_forwarded_batch_keeps_the_cold_grants_available_for_the_next_growth(bool set)
    {
        await using var pair = await OpenAsync(set, flattened: false, Full, duplicateTarget: true);
        var original = pair.Nodes.Select(node => ExpectFull(pair, node, Full, 2)).ToArray();
        var position = await pair.ResizeAsync(Full, Small);
        pair.TurnAll();
        var revisions = pair.Nodes.Select(node => pair.ExpectCurrent(node, Small, position)).ToArray();
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            ExpectCold(pair, pair.Nodes[role], original[role].Chunks);
        }

        // The subsequent entry allocation sees these same physical slots already linked to
        // the prepared lookup on the master, before the duplicate second member refuses it.
        var borrowed = pair.Nodes[0].WatchPreparedBorrow(original[0]);
        var batch = pair.Batch();
        batch.Update(pair.Resize(Small, Full));
        batch.Insert(pair.Input(2, Small));
        var refusal = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.DuplicateKey, refusal.Error);
        Assert.Equal(1, refusal.OperationIndex);
        Assert.True(borrowed(), "The refusal did not follow actual reuse of the cold native slot grants.");
        Assert.Equal(position, pair.Position);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            Assert.Equal(revisions[role], pair.ExpectCurrent(node, Small, position));
            ExpectCold(pair, node, original[role].Chunks);
        }

        var grown = await pair.ResizeAsync(Small, Full);
        for (var role = 0; role < pair.Nodes.Length; role++)
        {
            var node = pair.Nodes[role];
            AssertSameGrants(original[role].Chunks, ExpectFull(pair, node, Full, 2).Chunks);
            node.Database.DrainRetired();
            Assert.Empty(node.LeaseSnapshot());
            node.ExpectNativeLedger();
            pair.ExpectCurrent(node, Full, grown);
        }
    }

    private static CollectionLookupShape ExpectFull(CollectionShrinkPair pair, CollectionShrinkNode node, int count, int chunks)
    {
        var shape = pair.Shape(node);
        Assert.InRange(shape.Active, Need(count), 2 * Need(count));
        Assert.Equal(chunks, shape.Chunks.Length);
        Assert.All(shape.Chunks, slot => Assert.Equal(WholeChunk, slot.NativeBytes));
        Assert.Equal(shape.Chunks.Length, shape.Chunks.Select(slot => slot.Address.Segment).Distinct().Count());
        return shape;
    }

    private static int ExpectCold(CollectionShrinkPair pair, CollectionShrinkNode node, CollectionNativeSlot[] original)
    {
        var shape = pair.Shape(node);
        var leases = node.LeaseSnapshot();
        Assert.Equal(original.Length, leases.Length);
        foreach (var (lease, slot) in leases)
        {
            var old = Assert.Single(original, former => former.Address == lease.Address);
            Assert.Equal((shape.Handle, shape.DescriptorOffset, old.Parent), (lease.Owner, lease.DescriptorOffset, lease.FormerLookup));
            Assert.False(lease.Borrowed);
            Assert.True(slot.SameGrantAs(old));
            Assert.False(slot.Retired);
            Assert.DoesNotContain(shape.Blocks, block => block.Address == lease.Address);
        }

        var counters = node.Database.Counters.Arena;
        Assert.Equal(0, counters.RetiredBytes);
        Assert.Equal(0, counters.ReservedSegments);
        Assert.Equal(node.Allocator.Outstanding, counters.Segments);
        Assert.True((long)leases.Length * WholeChunk <= counters.LiveBytes + counters.ClassSlackBytes + counters.PreparedBytes);
        node.ExpectNativeLedger();
        return leases.Length;
    }

    private static void ExpectBoundedReturn(CollectionShrinkNode node, CollectionNativeSlot[] original)
    {
        var before = node.Allocator.Returned;
        var requests = node.Allocator.Requests;
        var remaining = original.Length;
        for (var attempt = 0; attempt < 32 && remaining != 0; attempt++)
        {
            node.Turn();
            var after = node.Observe(original);
            var retained = after.Count(slot => original.Any(slot.SameGrantAs));
            Assert.InRange(remaining - retained, 0, 1);
            Assert.Equal(original.Length - retained, node.Allocator.Returned - before);
            remaining = retained;
        }

        Assert.Equal(0, remaining);
        Assert.Empty(node.LeaseSnapshot());
        Assert.Equal(requests, node.Allocator.Requests);
        Assert.Equal(node.Allocator.Outstanding, node.Database.Counters.Arena.Segments);
        node.ExpectNativeLedger();
    }

    private static CollectionLookupShape ExpectNoLogicalRebuild(CollectionShrinkPair pair, CollectionShrinkNode node,
        CollectionLookupShape previous, int count, LatticePosition position, LatticeRevision revision)
    {
        var current = pair.Shape(node);
        Assert.Equal(previous.Owner, current.Owner);
        Assert.Equal(previous.Blocks, current.Blocks);
        Assert.InRange(current.Active, Need(count), 2 * Need(count));
        Assert.Equal(revision, pair.ExpectCurrent(node, count, position));
        return current;
    }

    private static void AssertNoFormerGrants(CollectionShrinkNode node, CollectionNativeSlot[] original) =>
        Assert.DoesNotContain(node.Observe(original), current => original.Any(current.SameGrantAs));

    private static void AssertSameGrants(CollectionNativeSlot[] expected, CollectionNativeSlot[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.All(expected, old => Assert.Contains(actual, slot => slot.SameGrantAs(old)));
    }
}
