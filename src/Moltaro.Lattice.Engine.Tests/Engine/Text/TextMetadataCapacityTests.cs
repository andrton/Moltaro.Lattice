using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Rollback and reclamation restore the native slots and logical references.
// Bounded maintenance also releases the excess managed capacity those texts left.
// See docs/decisions/strings.md, Q04.4; docs/design/arena-protocol.md, "Deferred reclamation".
public sealed class TextMetadataCapacityTests
{
    private const string ShrinkNotImplemented = "Managed text metadata shrink is not implemented.";
    private const int Count = 200;
    private const int SegmentSize = 256 * 1024;
    private const long AnchorId = 1;
    private static readonly string AnchorText = new('a', 96);

    [Theory(Skip = ShrinkNotImplemented)]
    [InlineData("Utf8", false)]
    [InlineData("Utf8", true)]
    [InlineData("Utf16", false)]
    [InlineData("Utf16", true)]
    public async Task Excess_metadata_of_a_late_refused_batch_is_released_without_affecting_the_child(string encoding, bool forwarded)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding), Inputs(allocator, 17));
        var anchor = new TextSchema.Place { Id = AnchorId, Region = AnchorText, Label = "anchor" };
        var initial = await master.Set<TextSchema.Place>().InsertAsync(anchor);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(new CountingNativeAllocator(), 23));
        Assert.Equal(initial.Position, child.Position);
        var childBefore = child.Counters.TextTableBytes;
        Assert.True(master.Set("Place").TryGet(AnchorId, out _, out var originalRevision));
        var through = forwarded ? child : master;
        var staged = Places(encoding);

        // A successful prefix independently checks that this same working set grows the table.
        long prefixFootprint;
        await using (var control = await EngineMaster.OpenAsync(TextSchema.Of(encoding), Inputs(new CountingNativeAllocator(), 17)))
        {
            await control.Set<TextSchema.Place>().InsertAsync(anchor);
            var controlOriginal = control.Counters.TextTableBytes;
            Assert.True((await Inserting(control, staged).CommitAsync().AsTask().WaitAsync(Bound)).Changed);
            prefixFootprint = control.Counters.TextTableBytes;
            Assert.True(prefixFootprint > controlOriginal);
            Assert.Equal(Count + 1, control.Counters.TextEntries);
        }

        var batch = Inserting(through, staged);
        batch.Insert(new TextSchema.Place { Id = AnchorId, Region = "collision", Label = "duplicate" });
        var nativeBefore = allocator.Obtained;
        var refused = await Assert.ThrowsAsync<LatticeException>(() => batch.CommitAsync().AsTask().WaitAsync(Bound));
        Assert.Equal(LatticeErrorEnum.DuplicateKey, refused.Error);
        Assert.Equal(Count, refused.OperationIndex);
        Assert.True(allocator.Obtained > nativeBefore, "The valid prefix never took a native segment.");
        Assert.Equal(initial.Position, master.Position);
        Assert.Equal(initial.Position, child.Position);

        var tally = TextTally.Of(encoding, anchor);
        foreach (var node in new[] { master, child })
        {
            var records = node.Set("Place");
            Assert.True(records.TryGet(AnchorId, out var record, out var revision));
            Assert.Equal(originalRevision, revision);
            Assert.Equal(AnchorText, record.Get<string>("Region"));
            Assert.All(staged, place => Assert.Null(records.Get(place.Id)));
            ExpectTally(tally, node);
        }

        Assert.Equal(childBefore, child.Counters.TextTableBytes);
        var retained = MetadataAfterTurns(master, prefixFootprint);
        Assert.True(retained < prefixFootprint, $"The text table still holds {retained} managed bytes after its owners were released.");

        var next = await Inserting(through, [staged[0]]).CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(initial.Position.Sequence + 1, next.Position.Sequence);
        await child.WaitForPositionAsync(next.Position);
        tally.Written(staged[0]);
        foreach (var node in new[] { master, child })
        {
            Assert.Equal(staged[0].Region, node.Set<TextSchema.Place>().Get(staged[0].Id)?.Region);
            ExpectTally(tally, node);
        }
    }

    [Theory(Skip = ShrinkNotImplemented)]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public async Task Both_nodes_release_excess_metadata_after_the_last_reader_of_deleted_texts_leaves(string encoding)
    {
        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Of(encoding), Inputs(masterAllocator, 17));
        var anchor = new TextSchema.Place { Id = AnchorId, Region = AnchorText, Label = "anchor" };
        await master.Set<TextSchema.Place>().InsertAsync(anchor);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(childAllocator, 23));
        var originalMaster = master.Counters.TextTableBytes;
        var originalChild = child.Counters.TextTableBytes;
        var staged = Places(encoding);
        var written = await Inserting(master, staged).CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(written.Position);
        var peakMaster = master.Counters.TextTableBytes;
        var peakChild = child.Counters.TextTableBytes;
        Assert.True(peakMaster > originalMaster);
        Assert.True(peakChild > originalChild);

        var tally = TextTally.Of(encoding, [anchor, .. staged]);
        var target = staged[0];
        var masterSet = master.Set("Place");
        var childSet = child.Set("Place");
        Assert.True(masterSet.TryGet(target.Id, out var masterCopy, out var revision));
        Assert.True(childSet.TryGet(target.Id, out var childCopy, out var childRevision));
        Assert.Equal(revision, childRevision);
        ExpectTally(tally, master);
        ExpectTally(tally, child);

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = OwnThread.Run(() => ExpectHeld(masterSet, target, revision, masterPause));
        var childRead = OwnThread.Run(() => ExpectHeld(childSet, target, revision, childPause));
        var returnedMaster = masterAllocator.Returned;
        var returnedChild = childAllocator.Returned;
        try
        {
            await masterPause.EnteredAsync(masterRead);
            await childPause.EnteredAsync(childRead);
            var delete = child.CreateWriteBatch();
            foreach (var place in staged)
            {
                delete.Delete<TextSchema.Place>(place.Id);
            }

            var removed = await delete.CommitAsync().AsTask().WaitAsync(Bound);
            Assert.Equal(written.Position.Sequence + 1, removed.Position.Sequence);
            await child.WaitForPositionAsync(removed.Position);
            Assert.Equal(removed.Position, master.Position);
            tally.Retaining = true;
            foreach (var place in staged)
            {
                tally.Deleted("Place", place.Id);
            }

            foreach (var node in new[] { master, child })
            {
                Assert.All(staged, place => Assert.Null(node.Set<TextSchema.Place>().Get(place.Id)));
                ExpectTally(tally, node);
            }

            Assert.Equal(returnedMaster, masterAllocator.Returned);
            Assert.Equal(returnedChild, childAllocator.Returned);
        }
        finally
        {
            masterPause.Release();
            childPause.Release();
            await Task.WhenAll(masterRead, childRead).WaitAsync(Bound);
        }

        tally.ReadLeft();
        ExpectTally(tally, master);
        ExpectTally(tally, child);
        Assert.True(masterAllocator.Returned > returnedMaster);
        Assert.True(childAllocator.Returned > returnedChild);
        Assert.Equal(master.Counters.Arena.Segments, masterAllocator.Outstanding);
        Assert.Equal(child.Counters.Arena.Segments, childAllocator.Outstanding);
        Assert.Equal(target.Region, masterCopy.Get<string>("Region"));
        Assert.Equal(target.Region, childCopy.Get<string>("Region"));

        var retainedMaster = MetadataAfterTurns(master, peakMaster);
        var retainedChild = MetadataAfterTurns(child, peakChild);
        Assert.True(retainedMaster < peakMaster && retainedChild < peakChild,
            $"The text tables still hold {retainedMaster} and {retainedChild} managed bytes after the master and child owners were released.");
    }

    private static void ExpectTally(TextTally tally, EngineDatabase node)
    {
        EntryCounts.ExpectEqual(tally, node);
        tally.ExpectHeldBy(node);
    }

    private static long MetadataAfterTurns(EngineDatabase node, long peak)
    {
        // The outer bound protects the test from a worker with no progress on this finite fixture.
        for (var turn = 0; turn < Count * Count && node.Counters.TextTableBytes >= peak; turn++)
        {
            Assert.True(node.TryRunMaintenanceTurn());
        }

        return node.Counters.TextTableBytes;
    }

    private static ILatticeWriteBatch Inserting(EngineDatabase node, IEnumerable<TextSchema.Place> places)
    {
        var batch = node.CreateWriteBatch();
        foreach (var place in places)
        {
            batch.Insert(place);
        }

        return batch;
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator allocator, ulong seed) => new()
    {
        IndexSeed = seed,
        Allocator = allocator,
        Arena = new ArenaOptions { SegmentSize = SegmentSize, MemoryLimit = 16 * 1024 * 1024 },
        DisableBackgroundMaintenance = true,
    };

    private static TextSchema.Place[] Places(string encoding)
    {
        var places = Enumerable.Range(0, Count).Select(index => new TextSchema.Place
        {
            Id = index + 2,
            Number = index,
            Region = new string('r', index % 2 == 0 ? 4_480 : 96) + "é\U0001F603" + index.ToString("D4", CultureInfo.InvariantCulture),
            Label = "published",
        }).ToArray();
        var codec = TextTally.EncodingOf(encoding);
        var chunked = places.Count(place => codec.GetByteCount(place.Region) > 4_096);
        var single = places.Count(place => codec.GetByteCount(place.Region) is > 64 and <= 4_096);
        Assert.Equal((Count / 2, Count / 2), (single, chunked));
        return places;
    }

    private static void ExpectHeld(ILatticeSet places, TextSchema.Place target, LatticeRevision revision, ViewerPause pause)
    {
        Assert.True(places.TryView(target.Id, view =>
        {
            var value = view["Region"];
            Assert.Equal(target.Region, value.GetString());
            pause.Hold();
            Assert.Equal(target.Region, value.GetString());
            Assert.Equal(target.Number, view["Number"].Get<long>());
            return view.Revision;
        }, out var seen));
        Assert.Equal(revision, seen);
    }
}
