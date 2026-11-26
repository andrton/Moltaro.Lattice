using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Hashing;
using Moltaro.Lattice.TestSupport.Concurrency;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PrimaryCapacityWithdrawalTests
{
    private const int ClaimsInA = 4_096;

    [Fact]
    public async Task A_cancelled_later_insert_gives_back_its_unclaimed_overflow_page_and_directory()
    {
        var control = await RunAsync(1, ClaimsInA, cancelB: false);
        var evidence = await RunAsync(1, ClaimsInA, cancelB: true);
        var b = Assert.IsType<OverflowState>(evidence.B);
        var cancelled = Assert.IsType<OverflowState>(evidence.Cancelled);
        Assert.Same(evidence.A.Table, b.Table);
        Assert.Same(evidence.A.Main, b.Main);
        Assert.Same(evidence.A.Holder, b.Holder);
        Assert.NotSame(evidence.A.Directory, b.Directory);
        Assert.Equal(11, b.Table.BucketBits);
        Assert.Equal((1, ClaimsInA, 0L), (evidence.A.Count, evidence.A.Reserved, evidence.A.Taken));
        Assert.Equal((1, ClaimsInA + 1, 0L), (b.Count, b.Reserved, b.Taken));
        Assert.Equal((1, ClaimsInA, 0L), (cancelled.Count, cancelled.Reserved, cancelled.Taken));
        Assert.Equal((1 + ClaimsInA, 0), (evidence.Published.Count, evidence.Published.Reserved));
        Assert.Equal(control.Published.Taken, evidence.Published.Taken);
        Assert.Equal(control.A.Bytes, evidence.A.Bytes);
        var bWords = Assert.Single(b.Words.Except<long[]>(evidence.A.Words, ReferenceEqualityComparer.Instance));
        var lastOfOld = (long)evidence.A.Words.Length << b.PageBits;
        Assert.Same(b.Words[^1], bWords);
        Assert.InRange(evidence.Published.Taken, 0, lastOfOld);
        Assert.All(b.Words[^1], word => Assert.Equal(0L, word));

        Assert.Equal((evidence.A.Bytes, control.Published.Bytes), (cancelled.Bytes, evidence.Published.Bytes));
        Assert.DoesNotContain(bWords, cancelled.Words, ReferenceEqualityComparer.Instance);
        Assert.DoesNotContain(bWords, evidence.Published.Words, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public async Task A_cancelled_later_insert_gives_back_its_unneeded_primary_generation()
    {
        var control = await RunAsync(4, 4, cancelB: false);
        var evidence = await RunAsync(4, 4, cancelB: true, holdReader: true);
        var b = Assert.IsType<OverflowState>(evidence.B);
        var cancelled = Assert.IsType<OverflowState>(evidence.Cancelled);
        Assert.Same(evidence.Initial.Table, evidence.A.Table);
        Assert.NotSame(evidence.A.Table, b.Table);
        Assert.NotSame(evidence.A.Main, b.Main);
        Assert.NotSame(evidence.A.Holder, b.Holder);
        Assert.Equal((4, 4), (evidence.A.Count, evidence.A.Reserved));
        Assert.Equal((4, 5), (b.Count, b.Reserved));
        Assert.Equal((4, 4), (cancelled.Count, cancelled.Reserved));
        Assert.Equal((8, 0), (evidence.Published.Count, evidence.Published.Reserved));
        Assert.Equal((8, 0), (control.Published.Count, control.Published.Reserved));

        Assert.Equal((evidence.A.GenerationBytes, control.Published.GenerationBytes),
            (cancelled.GenerationBytes, evidence.Published.GenerationBytes));
    }

    [Theory]
    [InlineData(1, ClaimsInA)]
    [InlineData(4, 4)]
    public async Task A_later_owner_keeps_the_capacity_it_still_needs_after_an_earlier_cancellation(int seed, int claims)
    {
        var control = await RunAsync(seed, claims, cancelB: false, includeC: true);
        var evidence = await RunAsync(seed, claims, cancelB: true, includeC: true);
        var b = Assert.IsType<OverflowState>(evidence.B);
        var cancelled = Assert.IsType<OverflowState>(evidence.Cancelled);
        var c = Assert.IsType<OverflowState>(control.C);
        Assert.Same(b.Table, cancelled.Table);
        Assert.Same(b.Directory, cancelled.Directory);
        Assert.Equal((c.Count, c.Reserved, c.Taken, c.GenerationBytes),
            (cancelled.Count, cancelled.Reserved, cancelled.Taken, cancelled.GenerationBytes));
        Assert.Equal(control.Published.GenerationBytes, evidence.Published.GenerationBytes);
    }

    private static async Task<(OverflowState Initial, OverflowState A, OverflowState? B, OverflowState? Cancelled,
        OverflowState? C, OverflowState Published)> RunAsync(int seedCount, int claims, bool cancelB,
        bool includeC = false, bool holdReader = false)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldPersistence(holding: false);
        await using var node = await OpenAsync(16L * Segment, allocator, persistence: storage);
        var pieces = node.Set<PieceSchema.Piece>();
        await RootSlotBudgetTests.FillAsync(node, seedCount);
        var initial = node.Position;
        var firstA = seedCount + 1L;
        var lastA = seedCount + claims;
        var keyB = lastA + 1L;
        var keyC = keyB + 1;
        Assert.True(pieces.TryGet(1L, out _, out var seedRevision));
        var before = await SnapshotAsync(node, allocator);
        storage.Hold();
        using var pause = new ViewerPause();
        try
        {
            var blocker = pieces.DeleteAsync(-1L).AsTask();
            await storage.NextAsync();
            var batch = node.CreateWriteBatch();
            for (var key = firstA; key <= lastA; key++)
            {
                batch.Insert(PieceSchema.Of(key, 8));
            }

            var a = batch.CommitAsync().AsTask();
            await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 2 && counters.RootEntries == lastA,
                "all A members are prepared behind the persistence gate");
            var stagedA = await SnapshotAsync(node, allocator);
            OverflowState? stagedB = null;
            OverflowState? withdrawn = null;
            OverflowState? stagedC = null;
            Task<LatticeWriteResult>? c = null;
            if (cancelB)
            {
                using var stop = new CancellationTokenSource();
                var b = pieces.InsertAsync(PieceSchema.Of(keyB, 19), stop.Token).AsTask();
                await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 3 && counters.RootEntries == keyB,
                    "B holds its own root and one more reservation");
                stagedB = await SnapshotAsync(node, allocator);
                if (includeC)
                {
                    c = pieces.InsertAsync(PieceSchema.Of(keyC, 27)).AsTask();
                    await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 4 && counters.RootEntries == keyC,
                        "C has prepared a real root using the grown backing");
                    stagedC = await SnapshotAsync(node, allocator);
                }

                Task<long>? read = null;
                if (holdReader)
                {
                    var primary = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
                        Backing<PrimaryIndex>(parts.Tables.Stored(1), "_index"));
                    var grown = stagedB;
                    read = OwnThread.Run(() =>
                    {
                        Assert.True(node.Set("Piece").TryView(1L, view =>
                        {
                            var captured = primary.TableAndVersion(out _);
                            Assert.Same(grown.Table, captured);
                            pause.Hold();
                            Assert.Equal(4, captured.Count);
                            Assert.True(view["Name"].GetPart(0).SequenceEqual("nnnnnnnn"u8));
                            return view["Id"].Get<long>();
                        }, out var value));
                        return value;
                    });
                    await pause.EnteredAsync(read);
                }

                await stop.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b.WaitAsync(Bound));
                withdrawn = await SnapshotAsync(node, allocator);
                Assert.Equal(initial, node.Position);
                Assert.Equal(includeC ? (3L, keyB) : (2L, lastA), (withdrawn.Queued, withdrawn.Roots));
                Assert.False(a.IsCompleted);
                Assert.False(blocker.IsCompleted);
                Assert.Null(pieces.Get(firstA));
                Assert.Null(pieces.Get(keyB));
                if (read is not null)
                {
                    Assert.Same(stagedA.Table, withdrawn.Table);
                    node.DrainRetired();
                    var held = ExpectCapacity(node, allocator, out _, stagedB.Table, stagedB.Main);
                    pause.Release();
                    Assert.Equal(1L, await read.WaitAsync(Bound));
                    node.DrainRetired();
                    var released = ExpectCapacity(node, allocator);
                    Assert.True(held.ChargedBytes - released.ChargedBytes >= stagedB.GenerationBytes);
                    node.InspectBudget((_, backing) => Assert.NotSame(stagedB.Main, backing));
                }
            }
            else if (includeC)
            {
                c = pieces.InsertAsync(PieceSchema.Of(keyC, 27)).AsTask();
                await CountersWait.UntilAsync(node, counters => counters.QueuedWrites == 3 && counters.RootEntries == keyB,
                    "C holds the later admitted capacity");
                stagedC = await SnapshotAsync(node, allocator);
            }

            storage.ReleaseAll();
            var predecessor = await blocker.WaitAsync(Bound);
            var committed = await a.WaitAsync(Bound);
            Assert.Equal(PositionSequence.Of(initial) + 1, PositionSequence.Of(predecessor.Position));
            Assert.Equal(PositionSequence.Of(predecessor.Position) + 1, PositionSequence.Of(committed.Position));
            var final = c is null ? committed : await c.WaitAsync(Bound);
            Assert.Equal(PositionSequence.Of(committed.Position) + (includeC ? 1UL : 0UL), PositionSequence.Of(final.Position));
            Assert.Equal(final.Position, node.Position);
            var expected = Enumerable.Range(1, lastA).Select(key => (long)key);
            Assert.Equal(includeC ? expected.Append(keyC) : expected,
                pieces.Enumerate().Select(piece => piece.Id).Order());
            Assert.True(pieces.TryGet(1L, out _, out var finalRevision));
            Assert.Equal(seedRevision, finalRevision);
            Assert.Null(pieces.Get(keyB));
            if (includeC)
            {
                Assert.Equal(27, pieces.Get(keyC)?.Name.Length);
            }

            OwnershipAudit.Expect(node);
            var published = await SnapshotAsync(node, allocator);
            Assert.Equal((0L, lastA + (includeC ? 1 : 0)), (published.Queued, published.Roots));
            return (before, stagedA, stagedB, withdrawn, stagedC, published);
        }
        finally
        {
            pause.Release();
            storage.ReleaseAll();
        }
    }

    private static Task<OverflowState> SnapshotAsync(EngineDatabase node, CountingNativeAllocator allocator) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var primary = Backing<PrimaryIndex>(parts.Tables.Stored(1), "_index");
            var table = primary.TableAndVersion(out _);
            var main = Backing<long[]>(table, "_words");
            var overflow = Backing<OverflowBuckets>(table, "_overflow");
            var directory = Backing<OverflowPage[]>(overflow, "_pages");
            var pages = Backing<int>(overflow, "_pageCount");
            var words = directory.Take(pages).Select(page => page.Words).ToArray();
            _ = ExpectCapacity(node, allocator, out _, [table, main, overflow, directory, .. words]);
            var counters = node.Counters;
            return new OverflowState(table, main, overflow, directory, words, Backing<int>(overflow, "_pageBits"),
                primary.Count, Backing<int>(primary, "_reserved"), overflow.Taken, counters.QueuedWrites,
                counters.RootEntries, PositionSequence.Of(node.Position));
        });

    private sealed record OverflowState(IndexTable Table, long[] Main, OverflowBuckets Holder, OverflowPage[] Directory,
        long[][] Words, int PageBits, int Count, int Reserved, long Taken, long Queued, int Roots, ulong Sequence)
    {
        public long Bytes => ManagedObjectSize(Holder) + ManagedObjectSize(Directory) + Words.Sum(ManagedObjectSize);

        public long GenerationBytes => ManagedObjectSize(Table) + ManagedObjectSize(Main) + Bytes;
    }
}
