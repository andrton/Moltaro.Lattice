using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PrimaryGenerationBudgetTests
{
    [Fact]
    public async Task Index_growth_holds_both_main_and_overflow_backings_until_the_reader_leaves()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(64L * Segment, allocator);
        var set = node.Set<PieceSchema.Piece>();
        var keys = CollidingKeys.Of(9);
        for (var index = 0; index < 8; index++)
        {
            await set.InsertAsync(PieceSchema.Of(keys[index], 8));
        }

        var indexHolder = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            Backing<PrimaryIndex>(parts.Tables.Stored(1), "_index"));
        var original = indexHolder.TableAndVersion(out var firstVersion);
        var previous = ArraysOf(original);
        Assert.NotEmpty(OverflowWords(original));
        var before = ExpectCapacity(node, allocator, out _, previous);
        using var reader = await HeldRead.StartAsync(node, () => set.Get(keys[0])?.Id);
        BudgetCapacitySample? overlap = null;
        object[]? fresh = null;
        node.ArmGrowthSwitch(set.EntityType, () =>
        {
            var current = indexHolder.TableAndVersion(out var version);
            Assert.Equal(firstVersion + 1, version);
            fresh = ArraysOf(current);
            Assert.NotEmpty(OverflowWords(current));
            overlap = ExpectCapacity(node, allocator, out _, [.. previous, .. fresh]);
        });

        await set.InsertAsync(PieceSchema.Of(keys[8], 8));
        var atSwitch = Assert.IsType<BudgetCapacitySample>(overlap);
        var replacement = Assert.IsType<object[]>(fresh);
        var expectedNew = replacement.Sum(ManagedObjectSize);
        Assert.True(atSwitch.PeakBytes >= before.ChargedBytes + expectedNew);
        var retirement = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            Backing<Array>(Backing<object>(parts.Retired, "_items"), "_array"));
        Assert.NotEmpty(retirement);
        var retained = ExpectCapacity(node, allocator, out _, [retirement, .. previous, .. replacement]);
        Assert.Equal(before.NativeBytes, retained.NativeBytes);
        Assert.True(node.Counters.RetiredIndexBytes > 0);
        Assert.Equal(keys[0], await reader.ReleaseAsync());
        node.DrainRetired();

        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance);
        _ = node.InspectBudget((_, backing) => owners.Add(backing));
        foreach (var oldBacking in previous)
        {
            Assert.DoesNotContain(oldBacking, owners);
        }

        var released = ExpectCapacity(node, allocator, out _, replacement);
        Assert.True(retained.ChargedBytes - released.ChargedBytes >= previous.Sum(ManagedObjectSize));
        Assert.Equal(0, node.Counters.RetiredIndexBytes);
        Assert.Equal(keys.Order(), set.Enumerate().Select(piece => piece.Id).Order());
    }

    [Fact]
    public async Task A_budget_that_fits_the_net_primary_growth_can_still_refuse_its_overlapping_generations()
    {
        var keys = CollidingKeys.Of(9);
        long narrowLimit;
        long fittedLimit;
        var reference = new CountingNativeAllocator();
        await using (var control = await OpenAsync(64L * Segment, reference))
        {
            await SeedAsync(control, keys);
            var retirement = await HoldRetirementRoomAsync(control);
            try
            {
                var index = await PrimaryAsync(control);
                var original = index.TableAndVersion(out _);
                var oldOwners = ArraysOf(original);
                var oldBytes = oldOwners.Sum(ManagedObjectSize);
                var set = control.Set<PieceSchema.Piece>();
                var resident = 0L;
                var coexist = 0L;
                control.ArmGrowthSwitch(set.EntityType, () =>
                {
                    var replacement = index.TableAndVersion(out _);
                    var newOwners = ArraysOf(replacement);
                    Assert.NotEmpty(OverflowWords(original));
                    Assert.NotEmpty(OverflowWords(replacement));
                    Assert.True(original.BucketBits < replacement.BucketBits);
                    _ = ExpectCapacity(control, reference, out var owned, [retirement, .. oldOwners, .. newOwners]);
                    var replacementBytes = newOwners.Sum(ManagedObjectSize);
                    var withoutEitherGeneration = owned - oldBytes - replacementBytes;
                    resident = withoutEitherGeneration + replacementBytes;
                    coexist = withoutEitherGeneration + oldBytes + replacementBytes;
                });

                await set.InsertAsync(PieceSchema.Of(keys[8], 8));
                Assert.True(resident > Segment);
                Assert.True(coexist > resident);
                narrowLimit = resident + oldBytes / 2;
                fittedLimit = coexist + oldBytes / 2;
                Assert.InRange(narrowLimit, resident, coexist - 1);
                control.DrainRetired();
                Assert.Equal(0, control.Counters.RetiredIndexBytes);
                Assert.InRange(ExpectCapacity(control, reference).ChargedBytes, Segment, narrowLimit);
            }
            finally
            {
                await ReleaseRetirementRoomAsync(control);
            }
        }

        foreach (var fits in new[] { false, true })
        {
            var allocator = new CountingNativeAllocator();
            await using var node = await OpenAsync(fits ? fittedLimit : narrowLimit, allocator);
            await SeedAsync(node, keys);
            var retirement = await HoldRetirementRoomAsync(node);
            try
            {
                var set = node.Set<PieceSchema.Piece>();
                var index = await PrimaryAsync(node);
                var original = index.TableAndVersion(out var version);
                var previous = ArraysOf(original);
                var start = node.Position;
                var grants = allocator.Requests;
                var atGrowth = false;
                node.ArmGrowthSwitch(set.EntityType, () => atGrowth = true);
                _ = ExpectCapacity(node, allocator, out _, [retirement, .. previous]);
                var insertion = set.InsertAsync(PieceSchema.Of(keys[8], 8)).AsTask();
                if (fits)
                {
                    var admitted = await insertion.WaitAsync(Bound);
                    Assert.True(atGrowth);
                    Assert.Equal(admitted.Position, node.Position);
                    _ = index.TableAndVersion(out var next);
                    Assert.Equal(version + 1, next);
                }
                else
                {
                    await RefusedAsync(insertion, LatticeLimitEnum.Memory);
                    Assert.False(atGrowth);
                    Assert.Same(original, index.TableAndVersion(out _));
                    Assert.Equal(start, node.Position);
                    Assert.Null(set.Get(keys[8]));
                    Assert.Equal(grants, allocator.Requests);

                    // The same insert needs the same native root class, root page and dispatch
                    // workspace but no replacement table once one old key has been removed.
                    await set.DeleteAsync(keys[7]);
                    node.DrainRetired();
                    var withoutGrowth = await set.InsertAsync(PieceSchema.Of(keys[8], 8)).AsTask().WaitAsync(Bound);
                    Assert.Equal(withoutGrowth.Position, node.Position);
                    Assert.False(atGrowth);
                    Assert.Same(original, index.TableAndVersion(out _));
                    Assert.Equal(grants, allocator.Requests);
                }
            }
            finally
            {
                await ReleaseRetirementRoomAsync(node);
            }
        }
    }

    private static async Task SeedAsync(EngineDatabase node, long[] keys)
    {
        var set = node.Set<PieceSchema.Piece>();
        foreach (var key in keys[..8])
        {
            await set.InsertAsync(PieceSchema.Of(key, 8));
        }
    }

    private static Task<PrimaryIndex> PrimaryAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => Backing<PrimaryIndex>(parts.Tables.Stored(1), "_index"));

    private const int HeldRetirementPlaces = 5;

    private static Task<Array> HoldRetirementRoomAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            // A fixture-held reservation keeps the same already-owned retirement
            // queue through ordinary drains, with ample room to retire one table.
            parts.Retired.Reserve(HeldRetirementPlaces);
            var queue = Backing<Array>(Backing<object>(parts.Retired, "_items"), "_array");
            Assert.True(queue.Length >= 8);
            return queue;
        });

    private static Task<bool> ReleaseRetirementRoomAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            parts.Retired.Release(HeldRetirementPlaces);
            return true;
        });

    internal static object[] ArraysOf(IndexTable table)
    {
        var overflow = Backing<OverflowBuckets>(table, "_overflow");
        return [table, Backing<long[]>(table, "_words"), overflow,
            Backing<OverflowPage[]>(overflow, "_pages"), .. OverflowWords(table)];
    }

    private static long[][] OverflowWords(IndexTable table)
    {
        var overflow = Backing<OverflowBuckets>(table, "_overflow");
        var pages = Backing<OverflowPage[]>(overflow, "_pages");
        var used = Backing<int>(overflow, "_pageCount");
        return [.. pages.Take(used).Select(page => page.Words)];
    }
}
