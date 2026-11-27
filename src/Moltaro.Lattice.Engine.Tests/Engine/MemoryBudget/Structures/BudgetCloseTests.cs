using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// After close the charge of a budget is zero, and a release that races the close changes nothing:
// it lands before the close, which clears it with the charge, or after it. The order cannot be driven
// from outside the budget, so close and a release are raced on two threads that a barrier starts
// together, once for each of many budgets.
public sealed class BudgetCloseTests
{
    private const long Limit = 1_000_000;
    private const long Freed = 1_000;
    private const int Rounds = 50_000;

    [Fact]
    public async Task A_release_racing_close_leaves_the_charge_at_zero()
    {
        var budgets = new InstanceMemoryBudget[Rounds];
        for (var round = 0; round < Rounds; round++)
        {
            budgets[round] = new InstanceMemoryBudget(Limit);
            Assert.True(budgets[round].TryReserve(Limit / 2));
        }

        using var start = new Barrier(2);
        var releaser = Task.Factory.StartNew(() =>
        {
            foreach (var budget in budgets)
            {
                start.SignalAndWait();
                budget.Release(Freed);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        foreach (var budget in budgets)
        {
            start.SignalAndWait();
            budget.Close();
        }

        await releaser.WaitAsync(Bound);
        var left = budgets.Where(budget => budget.Charged != 0).Select(budget => budget.Charged).ToArray();
        Assert.True(left.Length == 0, $"In {left.Length} of {Rounds} rounds the charge after close was {left.FirstOrDefault()}, not 0.");
    }
}
