using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class RootGenerationAdmissionTests
{
    private const long Limit = 2L * Segment;

    [Fact]
    public async Task A_direct_add_that_cannot_finish_generation_storage_returns_the_page_it_just_obtained()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var roots = parts.Roots;
            var original = Backing<RootEntry[][]>(roots, "_pages");
            var initial = ExpectCapacity(node, allocator);
            var first = roots.Add(1);
            var second = roots.Add(1);
            var directory = Backing<RootEntry[][]>(roots, "_pages");
            var returnable = ManagedObjectSize(directory) + ManagedObjectSize(Assert.Single(directory));
            roots.Release(second);
            var newer = roots.Add(1);
            Assert.Equal(new RootHandle(2, second.Slot), newer);
            roots.Release(first);
            roots.Release(newer);
            roots.WithdrawUnusedPages();
            Assert.Same(original, Backing<RootEntry[][]>(roots, "_pages"));
            Assert.Equal(initial.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);

            var dictionaryOwner = ManagedObjectSize(new Dictionary<uint, uint>());
            using (node.HoldBudgetBacking(checked((int)(Limit - initial.ChargedBytes - returnable - dictionaryOwner - 64))))
            {
                var before = ExpectCapacity(node, allocator);
                Assert.InRange(Limit - before.ChargedBytes - returnable, dictionaryOwner, dictionaryOwner + 63);
                Assert.Equal(LatticeLimitEnum.Memory, Assert.Throws<BudgetRefusedException>(() => roots.Add(1)).Limit);
                Assert.Same(original, Backing<RootEntry[][]>(roots, "_pages"));
                Assert.Equal(0, roots.Count);
                Assert.Equal(0, roots.SlotExtent);
                Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
                Assert.False(roots.IsCurrent(newer));
            }

            Assert.Equal(new RootHandle(2, first.Slot), roots.Add(1));
            Assert.Equal(new RootHandle(3, second.Slot), roots.Add(1));
            Assert.False(roots.IsCurrent(first));
            Assert.False(roots.IsCurrent(second));
            Assert.False(roots.IsCurrent(newer));
            return true;
        });
    }

    [Fact]
    public async Task An_existing_sparse_history_keeps_its_backing_and_lifetimes_if_growth_cannot_fit()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var roots = parts.Roots;
            var first = Enumerable.Range(0, 8).Select(_ => roots.Add(1)).ToArray();
            var reused = new List<RootHandle>();
            for (var index = 0; index < 7; index++)
            {
                roots.Release(first[index]);
                reused.Add(roots.Add(1));
            }

            var history = Backing<Dictionary<uint, uint>>(roots, "_generations");
            var original = DictionaryArrays(history);
            Assert.Equal(7, Assert.IsType<int[]>(original[0]).Length);
            Assert.Equal(7, history.Count);
            var cap = ExpectCapacity(node, allocator, out _, original);
            roots.Release(first[^1]);
            using (node.HoldBudgetBacking(checked((int)(Limit - cap.ChargedBytes - 64))))
            {
                var before = ExpectCapacity(node, allocator, out _, original);
                Assert.Equal(LatticeLimitEnum.Memory, Assert.Throws<BudgetRefusedException>(() => roots.Add(1)).Limit);
                Assert.Equal(before.ChargedBytes, ExpectCapacity(node, allocator, out _, original).ChargedBytes);
                Assert.All(DictionaryArrays(history).Zip(original), pair => Assert.Same(pair.Second, pair.First));
                Assert.All(reused, handle => Assert.True(roots.IsCurrent(handle)));
                Assert.Equal(7, history.Count);
            }

            var acquired = roots.Add(1);
            Assert.Equal(new RootHandle(2, first[^1].Slot), acquired);
            var grown = DictionaryArrays(history);
            Assert.All(grown.Zip(original), pair => Assert.NotSame(pair.Second, pair.First));
            Assert.Equal(8, history.Count);
            Assert.Equal(cap.ChargedBytes + grown.Sum(ManagedObjectSize) - original.Sum(ManagedObjectSize),
                ExpectCapacity(node, allocator, out _, grown).ChargedBytes);
            Assert.All(reused, handle => Assert.True(roots.IsCurrent(handle)));
            return true;
        });
    }

    [Fact]
    public async Task A_terminal_unpublished_slot_makes_its_page_permanent_without_making_it_visible_to_capture()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(Limit, allocator);
        await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var roots = parts.Roots;
            var first = roots.Add(1);
            var neighbor = roots.Add(1);
            var original = Backing<RootEntry[][]>(roots, "_pages");
            roots.Entry(first).Generation = uint.MaxValue - 1;
            roots.Release(new RootHandle(uint.MaxValue - 1, first.Slot));
            var terminal = roots.Add(1);
            Assert.Equal(new RootHandle(uint.MaxValue, first.Slot), terminal);
            roots.Release(terminal);
            roots.Release(neighbor);
            roots.WithdrawUnusedPages();
            Assert.Equal(1, roots.Pages);
            Assert.Equal(0, roots.CaptureExtent);
            Assert.Same(original, Backing<RootEntry[][]>(roots, "_pages"));
            Assert.False(roots.IsCurrent(terminal));
            var before = ExpectCapacity(node, allocator);
            using (node.HoldBudgetBacking(checked((int)(Limit - before.ChargedBytes - 64))))
            {
                var tight = ExpectCapacity(node, allocator);
                Assert.Equal(new RootHandle(2, neighbor.Slot), roots.Add(1));
                Assert.Equal(tight.ChargedBytes, ExpectCapacity(node, allocator).ChargedBytes);
                Assert.Null(Backing<Dictionary<uint, uint>?>(roots, "_generations"));
            }

            Assert.False(roots.IsCurrent(terminal));
            return true;
        });
    }
}
