using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The retirement queue keeps its items in the order they were retired across a wrap of its ring and
// across a growth, enqueues without allocating into the room ensured before, charges its ring by its
// bytes, keeps the ring and its items when a growth is refused, and gives the ring back when it is
// trimmed empty.
[Collection<ManagedAllocationCollection>]
public sealed class RetiredQueueTests
{
    [Fact]
    public void Items_leave_in_the_order_they_were_retired_across_a_wrap_and_a_growth()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        var queue = new RetiredQueue();
        queue.EnsureRoom(4, budget);
        var next = 0;
        var expected = 0;
        for (var round = 0; round < 3; round++)
        {
            queue.Enqueue(Item(next++));
            queue.Enqueue(Item(next++));
            Assert.Equal(expected, queue.Peek().Epoch);
            Assert.Equal(expected++, queue.Dequeue().Epoch);
        }

        Assert.Equal(3, queue.Count);
        queue.EnsureRoom(queue.Count + 16, budget);
        for (var added = 0; added < 16; added++)
        {
            queue.Enqueue(Item(next++));
        }

        while (queue.Count > 0)
        {
            Assert.Equal(expected++, queue.Dequeue().Epoch);
        }

        Assert.Equal(next, expected);
    }

    [Fact]
    public void An_item_enqueued_into_the_room_ensured_before_allocates_nothing()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        var queue = new RetiredQueue();
        queue.EnsureRoom(8, budget);
        queue.Enqueue(Item(0));
        _ = queue.Dequeue();
        var item = Item(1);

        var before = GC.GetAllocatedBytesForCurrentThread();
        queue.Enqueue(item);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void The_ring_is_charged_by_its_bytes_kept_when_a_growth_is_refused_and_given_back_when_trimmed_empty()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        var queue = new RetiredQueue();
        queue.EnsureRoom(4, budget);
        queue.Enqueue(Item(0));
        queue.Enqueue(Item(1));
        var held = new List<HeldArray>();
        queue.ReportHeld(held);
        var ring = Assert.Single(held);
        Assert.Equal(typeof(RetiredItem), ring.ElementType);
        Assert.InRange(ring.Length, 4, int.MaxValue);
        Assert.Equal(ArrayBytes(ring), budget.Charged);

        budget.Open(budget.Charged + ArrayBytes(typeof(RetiredItem), 2 * ring.Length) - 1);
        var refusal = Record.Exception(() => queue.EnsureRoom(2 * ring.Length, budget));
        Assert.Equal("BudgetRefusedException", refusal?.GetType().Name);
        held.Clear();
        queue.ReportHeld(held);
        Assert.Equal(ring, Assert.Single(held));
        Assert.Equal(0, queue.Dequeue().Epoch);
        Assert.Equal(1, queue.Dequeue().Epoch);

        queue.TrimWhenEmpty(budget);
        held.Clear();
        queue.ReportHeld(held);
        Assert.Empty(held);
        Assert.Equal(0, budget.Charged);
    }

    private static RetiredItem Item(int epoch) => new(RetiredKindEnum.IndexTable, epoch, null, default(ArenaAddress), default(RootHandle), 0);
}
