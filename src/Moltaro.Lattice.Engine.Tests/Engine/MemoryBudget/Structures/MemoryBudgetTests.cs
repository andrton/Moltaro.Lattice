using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The budget on its own: one counter that no reservation passes, arrays charged by their bytes, a
// close that clears the charge, and the wait of a child that a free counts and the child's own
// attempt does not.
public sealed class MemoryBudgetTests
{
    [Fact(Skip = Pending)]
    public void Reservations_racing_for_the_last_bytes_never_pass_the_limit()
    {
        const int Threads = 8;
        const long Limit = 100_000;
        var budget = new InstanceMemoryBudget(Limit);
        var admitted = new long[Threads];
        Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, thread =>
        {
            while (budget.TryReserve(3))
            {
                admitted[thread] += 3;
            }
        });

        Assert.Equal(Limit / 3 * 3, admitted.Sum());
        Assert.Equal(admitted.Sum(), budget.Charged);
        Assert.Equal(budget.Charged, budget.Peak);
        Assert.False(budget.TryReserve(Limit - budget.Charged + 1));
        Assert.True(budget.TryReserve(Limit - budget.Charged));
        Assert.Equal(Limit, budget.Charged);
    }

    [Fact(Skip = Pending)]
    public void A_budget_made_without_a_limit_holds_any_charge()
    {
        var budget = new InstanceMemoryBudget();
        Assert.Equal(long.MaxValue, budget.Limit);
        Assert.True(budget.TryReserve(long.MaxValue / 2));
        budget.Release(long.MaxValue / 2);
        Assert.Equal(0, budget.Charged);
    }

    [Fact(Skip = Pending)]
    public void Arrays_are_charged_by_their_bytes_and_a_growth_releases_the_array_it_replaces()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        var array = budget.Allocate<long>(4);
        array[3] = 17;
        Assert.Equal(ArrayBytes(typeof(long), 4), budget.Charged);
        budget.Grow(ref array, 16);
        Assert.Equal(16, array.Length);
        Assert.Equal(17, array[3]);
        Assert.Equal(ArrayBytes(typeof(long), 16), budget.Charged);
        Assert.Equal(ArrayBytes(typeof(long), 4) + ArrayBytes(typeof(long), 16), budget.Peak);

        var list = new List<int> { 1, 2, 3 };
        list.Capacity = 3;
        budget.Reserve(InstanceMemoryBudget.ArrayBytes<int>(3));
        budget.EnsureCapacity(list, 10);
        Assert.InRange(list.Capacity, 10, int.MaxValue);
        Assert.Equal(new[] { 1, 2, 3 }, list);
        Assert.Equal(ArrayBytes(typeof(long), 16) + ArrayBytes(typeof(int), list.Capacity), budget.Charged);

        budget.Release(array);
        budget.Release(ArrayBytes(typeof(int), list.Capacity));
        Assert.Equal(0, budget.Charged);
    }

    [Fact(Skip = Pending)]
    public void A_refused_growth_keeps_the_array_and_the_charge()
    {
        var budget = new InstanceMemoryBudget(ArrayBytes(typeof(long), 4) + ArrayBytes(typeof(long), 16) - 1);
        var array = budget.Allocate<long>(4);
        var refusal = Record.Exception(() => budget.Grow(ref array, 16));
        Assert.Equal("BudgetRefusedException", refusal?.GetType().Name);
        Assert.Equal(4, array.Length);
        Assert.Equal(ArrayBytes(typeof(long), 4), budget.Charged);
    }

    [Fact(Skip = Pending)]
    public void Close_clears_the_charge_ends_admission_and_ignores_a_later_release()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        budget.ReserveHeadroom(4_096);
        Assert.Equal(4_096, budget.Headroom);
        var array = budget.Allocate<byte>(100);
        Assert.True(budget.TryReserve(500));
        budget.Close();
        Assert.Equal(0, budget.Charged);
        Assert.False(budget.TryReserve(1));
        budget.Release(array);
        budget.Release(500);
        Assert.Equal(0, budget.Charged);
    }

    [Fact(Skip = Pending)]
    public async Task A_free_between_the_attempt_and_the_wait_moves_the_count_past_the_ticket()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        Assert.True(budget.TryReserve(1_000));
        var ticket = budget.BeginRetry();
        budget.EndRetry();
        try
        {
            await Task.Run(() => budget.Release(100)).WaitAsync(Bound);
            Assert.True(budget.WhenFreedAfter(ticket).IsCompleted);
        }
        finally
        {
            budget.EndWait();
        }
    }

    [Fact(Skip = Pending)]
    public async Task A_free_after_the_ticket_wakes_the_waiting_child_and_its_own_attempt_does_not()
    {
        var budget = new InstanceMemoryBudget(1024 * 1024);
        Assert.True(budget.TryReserve(1_000));

        // A free before the ticket is seen by the attempt that follows it, not by the wait.
        budget.Release(100);
        var ticket = budget.BeginRetry();
        try
        {
            // The attempt's own rollback runs on its thread and counts no free.
            Assert.True(budget.TryReserve(200));
            budget.Release(200);
        }
        finally
        {
            budget.EndRetry();
        }

        var wait = budget.WhenFreedAfter(ticket);
        try
        {
            Assert.False(wait.IsCompleted);
            await Task.Run(() => budget.Release(100)).WaitAsync(Bound);
            await wait.WaitAsync(Bound);
        }
        finally
        {
            budget.EndWait();
        }

        ticket = budget.BeginRetry();
        budget.EndRetry();
        wait = budget.WhenFreedAfter(ticket);
        try
        {
            await Task.Run(budget.NoteReusable).WaitAsync(Bound);
            await wait.WaitAsync(Bound);
        }
        finally
        {
            budget.EndWait();
        }

        ticket = budget.BeginRetry();
        budget.EndRetry();
        wait = budget.WhenFreedAfter(ticket);
        try
        {
            budget.Close();
            await wait.WaitAsync(Bound);
        }
        finally
        {
            budget.EndWait();
        }
    }
}
