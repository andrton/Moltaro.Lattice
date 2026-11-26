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

    [Fact]
    public void Physical_ownership_detects_unregistered_embedded_backing_and_its_captured_callback()
    {
        var ledger = new OwnedMemoryBudget(observesBackings: true);
        ledger.Open(Segment);
        var current = new byte[8];
        var previous = new byte[16];
        var held = new byte[32];
        var callback = Capture(held);
        var holder = new Probe(current, new EmbeddedBacking(previous, callback));
        object[] omitted = [previous, callback, callback.Target!, held];

        Own(holder);
        Own(current);
        var actual = OwnedBackingGraph.Find(holder);
        var registered = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var partial = ledger.Inspect((_, backing) => registered.Add(backing));
        Assert.True(registered.SetEquals([holder, current]));
        Assert.True(actual.Except(registered, ReferenceEqualityComparer.Instance)
            .ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(omitted));
        Assert.Equal(omitted.Sum(ManagedObjectSize), actual.Sum(ManagedObjectSize) - partial.Charged);

        foreach (var backing in omitted)
        {
            Own(backing);
        }

        registered.Clear();
        var complete = ledger.Inspect((_, backing) => registered.Add(backing));
        Assert.True(actual.SetEquals(registered));
        Assert.Equal(actual.Sum(ManagedObjectSize), complete.Charged);

        void Own(object backing)
        {
            ledger.Reserve(ManagedObjectSize(backing));
            ledger.Own(backing, nameof(ManagedLedgerObservationTests));
        }

        static Action Capture(byte[] backing) => () => GC.KeepAlive(backing);
    }

    private sealed record Probe(byte[] Current, EmbeddedBacking Older);

    private readonly record struct EmbeddedBacking(byte[] Previous, Action Release);

    private sealed class Empty;

    private sealed record Fields(long First, long Second, long Third);
}
