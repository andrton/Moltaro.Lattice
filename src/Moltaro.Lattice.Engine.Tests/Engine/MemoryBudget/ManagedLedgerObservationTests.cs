using Moltaro.Lattice.Engine.Tests.Storage;
using Xunit.Sdk;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using OwnedMemoryBudget = Moltaro.Lattice.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class ManagedLedgerObservationTests
{
    [Fact]
    public async Task Physical_object_sizing_uses_the_actual_backing_and_detects_an_undercharge()
    {
        Assert.Equal(3L * IntPtr.Size, ManagedObjectSize(new Empty()));
        var owned = new Fields(1, 2, 3);
        Assert.Equal(2L * IntPtr.Size + 3 * sizeof(long), ManagedObjectSize(owned));
        Assert.Equal(32, ManagedObjectSize(new string('x', 2)));
        Assert.Equal(32, ManagedObjectSize(new string('x', 3)));

        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(2L * Segment, allocator);
        var before = ExpectCapacity(node, allocator);
        var budget = Backing<OwnedMemoryBudget>(node, "_budget");
        var undercharge = ManagedObjectSize(owned) - sizeof(long);
        budget.Reserve(undercharge);
        budget.Own(owned, nameof(ManagedLedgerObservationTests));
        try
        {
            Assert.Throws<EqualException>(() => ExpectCapacity(node, allocator, out _, owned));
        }
        finally
        {
            budget.Release(undercharge, owned);
        }

        Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
    }

    private sealed class Empty;

    private sealed record Fields(long First, long Second, long Third);
}
