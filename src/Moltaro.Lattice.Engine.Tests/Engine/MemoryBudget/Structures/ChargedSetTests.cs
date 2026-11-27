using Moltaro.Lattice.Storage.Budgeting;
using Moltaro.Lattice.Storage.Hashing;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Structures;

// The engine's one hash set charges its array by its bytes: a growth needs the old and the new array
// at once and is refused one byte short of that, keeping the set; a removed element is gone to a
// probe while the others stay, and a cleared set keeps its array.
public sealed class ChargedSetTests
{
    [Fact(Skip = Pending)]
    public void A_growth_is_admitted_exactly_when_both_arrays_fit_and_a_refused_one_keeps_the_set()
    {
        var control = new InstanceMemoryBudget();
        var grown = Fill(control);
        var small = Held(grown);
        grown.EnsureCapacity(64);
        var large = Held(grown);
        Assert.Equal(large.Sum(ArrayBytes), control.Charged);

        foreach (var difference in new[] { -1, 0 })
        {
            var budget = new InstanceMemoryBudget();
            var set = Fill(budget);
            Assert.Equal(small, Held(set));
            budget.Open(budget.Charged + large.Sum(ArrayBytes) + difference);
            if (difference < 0)
            {
                var refusal = Record.Exception(() => set.EnsureCapacity(64));
                Assert.Equal("BudgetRefusedException", refusal?.GetType().Name);
                Assert.Equal(small, Held(set));
                Assert.Equal(small.Sum(ArrayBytes), budget.Charged);
            }
            else
            {
                set.EnsureCapacity(64);
                Assert.Equal(large, Held(set));
                Assert.Equal(large.Sum(ArrayBytes), budget.Charged);
            }

            Assert.All(Enumerable.Range(0, 4), key => Assert.True(set.Contains(key)));
        }
    }

    [Fact(Skip = Pending)]
    public void A_removed_element_is_gone_to_a_probe_and_a_cleared_set_keeps_its_array()
    {
        var budget = new InstanceMemoryBudget();
        var set = Fill(budget);
        var arrays = Held(set);
        Assert.True(set.Remove(1));
        Assert.False(set.Remove(1));
        Assert.False(set.Contains(1));
        Assert.True(set.TryGet(2, out var found));
        Assert.Equal(2, found);
        Assert.False(set.TryGet(1, out _));
        Assert.True(set.Add(1));
        Assert.False(set.Add(3));

        set.Clear();
        Assert.All(Enumerable.Range(0, 4), key => Assert.False(set.Contains(key)));
        Assert.Equal(arrays, Held(set));
        Assert.Equal(arrays.Sum(ArrayBytes), budget.Charged);
    }

    private static ChargedSet<int> Fill(InstanceMemoryBudget budget)
    {
        var set = new ChargedSet<int>(EqualityComparer<int>.Default, budget);
        set.EnsureCapacity(4);
        for (var key = 0; key < 4; key++)
        {
            Assert.True(set.Add(key));
        }

        return set;
    }

    private static List<HeldArray> Held(ChargedSet<int> set)
    {
        var held = new List<HeldArray>();
        set.ReportHeld(held);
        return held;
    }
}
