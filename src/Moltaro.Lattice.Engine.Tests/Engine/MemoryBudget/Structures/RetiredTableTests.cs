using System.Runtime.CompilerServices;
using Moltaro.Lattice.Engine.Indexes;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The table a primary index replaces when it grows stays reachable through its retirement for the
// reads that may still hold it, and nothing keeps it once that retirement is freed.
public sealed class RetiredTableTests
{
    [Fact]
    public void A_primary_table_replaced_by_a_growth_is_let_go_once_its_retirement_is_freed()
    {
        using var arena = new Arena(new ArenaOptions { MemoryLimit = new ArenaOptions().SegmentSize }, new InstanceMemoryBudget());
        var retired = new RetiredMemory(new ReadEpochs(new object()), arena, new RootTable(new InstanceMemoryBudget(), pageLimit: null));
        var index = new PrimaryIndex(retired);
        var first = FirstTable(index);
        for (var slot = 0u; slot < 9; slot++)
        {
            index.Reserve(slot * 2_654_435_761u);
            index.Add(slot * 2_654_435_761u, slot);
        }

        Assert.True(index.TableAndVersion(out var version).BucketBits > 1);
        Assert.Equal(1, version);
        Collect();
        Assert.True(first.IsAlive);

        retired.Drain();
        Collect();
        Assert.False(first.IsAlive);
        GC.KeepAlive(index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference FirstTable(PrimaryIndex index) => new(index.TableAndVersion(out _));

    private static void Collect()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}
