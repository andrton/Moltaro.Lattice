using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Primary;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class PrimaryGenerationBudgetTests
{
    [Fact(Skip = Pending)]
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
