using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

[Collection<ManagedAllocationCollection>]
public sealed class TypedViewAllocationTests
{
    private const int Repetitions = 1_000;

    [Fact(Skip = TypedViewSupport.NotImplemented)]
    public async Task Warm_typed_and_by_name_scalar_views_allocate_no_managed_memory()
    {
        await using var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        var records = database.Set("Stamp");
        await stamps.InsertAsync(StampSchema.StampOf(1, 53));
        var number = records.EntityType.GetField("Number");
        LatticeViewFunc<StampView, long> byClass = static view => view.Number;
        LatticeViewFunc<LatticeRecordView, LatticeField, long> byName = static (view, field) => view[field].Get<long>();
        var expected = 53L * Repetitions;

        Assert.Equal(expected, ReadByClass(stamps, byClass));
        Assert.Equal(expected, ReadByName(records, number, byName));

        var beforeTyped = GC.GetAllocatedBytesForCurrentThread();
        var typedTotal = ReadByClass(stamps, byClass);
        var typedBytes = GC.GetAllocatedBytesForCurrentThread() - beforeTyped;

        var beforeByName = GC.GetAllocatedBytesForCurrentThread();
        var byNameTotal = ReadByName(records, number, byName);
        var byNameBytes = GC.GetAllocatedBytesForCurrentThread() - beforeByName;

        Assert.Equal(expected, typedTotal);
        Assert.Equal(expected, byNameTotal);
        Assert.Equal(0, typedBytes);
        Assert.Equal(0, byNameBytes);
    }

    private static long ReadByClass(ILatticeSet<StampSchema.Stamp> stamps, LatticeViewFunc<StampView, long> viewer)
    {
        var sum = 0L;
        for (var read = 0; read < Repetitions; read++)
        {
            if (stamps.TryView(1L, viewer, out var result))
            {
                sum += result;
            }
        }

        return sum;
    }

    private static long ReadByName(ILatticeSet stamps, LatticeField field, LatticeViewFunc<LatticeRecordView, LatticeField, long> viewer)
    {
        var sum = 0L;
        for (var read = 0; read < Repetitions; read++)
        {
            if (stamps.TryView(1L, field, viewer, out var result))
            {
                sum += result;
            }
        }

        return sum;
    }
}
