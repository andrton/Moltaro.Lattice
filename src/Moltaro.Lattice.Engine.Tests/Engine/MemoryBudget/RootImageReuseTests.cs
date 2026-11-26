using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using Moltaro.Lattice.Budgeting;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using InstanceMemoryBudget = Moltaro.Lattice.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

// Ordinary placement holds the exact image under the writes lock. Only the weak
// owner remains charged between writers (milestone-1-engine-contract.md, Instance
// memory accounting). An image for forwarding belongs to its own working lease.
public sealed class RootImageReuseTests
{
    private const string PendingReuse = "An arena does not yet keep a weakly held root image.";

    [Fact(Skip = PendingReuse)]
    public void Construction_pays_for_the_actual_weak_owner_and_disposal_returns_its_capacity_once()
    {
        var budget = OpenBudget();
        var writes = new Lock();
        using var arena = OpenArena(budget, writes);
        var owner = Assert.Single(WeakOwners(arena));
        var ownerBytes = ManagedObjectSize(owner);
        var opened = ExpectCharge(budget, owner);

        var shortBudget = OpenBudget(opened - 1);
        var failure = Assert.Throws<BudgetRefusedException>(() =>
        {
            using var refused = OpenArena(shortBudget, new Lock());
        });
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Equal(opened - ownerBytes, shortBudget.Inspect((_, backing) => Assert.IsNotType<WeakReference<byte[]>>(backing)).Peak);

        arena.Dispose();
        Assert.Empty(WeakOwners(arena));
        Assert.Equal(opened - ownerBytes, budget.Inspect((_, backing) => Assert.NotSame(owner, backing)).Charged);
        arena.Dispose();
        Assert.Equal(opened - ownerBytes, ExpectCharge(budget));
    }

    [Fact(Skip = PendingReuse)]
    public void A_same_size_image_is_borrowed_once_and_each_open_writer_owns_its_own_exact_image()
    {
        var budget = OpenBudget();
        var writes = new Lock();
        var allocator = new CountingNativeAllocator();
        using var arena = OpenArena(budget, writes, allocator);
        var layout = Layout();
        var idle = Assert.Single(WeakOwners(arena));
        var baseline = ExpectCharge(budget, idle);
        lock (writes)
        {
            var seed = new RootWriter(arena, layout, RootArena.Handle);
            var seedImage = Assert.Single(Images(budget));
            var bytes = ExactImageBytes(seedImage, layout);
            seed.SetInteger(layout.EntityType.Key, 7);
            seed.Abandon();
            Assert.Empty(Images(budget));
            Assert.Equal(baseline, ExpectCharge(budget, idle));
            ExpectIdle(idle, seedImage);

            var first = new RootWriter(arena, layout, RootArena.Handle);
            Assert.Same(seedImage, Assert.Single(Images(budget)));
            Assert.False(idle.TryGetTarget(out _));
            Assert.Equal(baseline + bytes, ExpectCharge(budget, seedImage));

            var second = new RootWriter(arena, layout, RootArena.Handle);
            var other = Assert.Single(Images(budget), image => !ReferenceEquals(image, seedImage));
            Assert.Equal(bytes, ExactImageBytes(other, layout));
            Assert.Equal(baseline + 2 * bytes, ExpectCharge(budget, seedImage, other));
            Assert.False(idle.TryGetTarget(out _));

            WriterAssert.CompleteRefused(ref second, LatticeErrorEnum.ValidationFailed);
            Assert.Equal(baseline + bytes, ExpectCharge(budget, seedImage));
            ExpectIdle(idle, other);

            first.SetInteger(layout.EntityType.Key, 61);
            var root = first.Complete();
            RootStorage.Publish(arena, layout, root);
            var reader = new RootReader(arena, layout, root);
            Assert.Equal(61, reader.GetInteger(layout.EntityType.Key));
            Assert.True(allocator.OutstandingBytes > 0);
            Assert.Equal(allocator.OutstandingBytes, budget.Inspect(null).Native);
            _ = ExpectCharge(budget, idle);
            Assert.Empty(Images(budget));
            ExpectIdle(idle, seedImage);
        }
    }

    [Fact(Skip = PendingReuse)]
    public void Cached_images_obey_the_exact_budget_boundary_for_matching_and_different_sizes()
    {
        var budget = OpenBudget();
        var writes = new Lock();
        using var arena = OpenArena(budget, writes);
        var narrow = Layout();
        var wide = Layout("{ 'Id': 2, 'Name': 'Label', 'Type': 'String', 'Nullable': true }");
        var idle = Assert.Single(WeakOwners(arena));
        var baseline = ExpectCharge(budget, idle);
        lock (writes)
        {
            var seed = new RootWriter(arena, wide, RootArena.Handle);
            var wideImage = Assert.Single(Images(budget));
            var wideBytes = ExactImageBytes(wideImage, wide);
            seed.Abandon();

            ExpectRefusal(arena, budget, wide, baseline, wideBytes);
            ExpectIdle(idle, wideImage);
            budget.Open(baseline + wideBytes);
            var sameSize = new RootWriter(arena, wide, RootArena.Handle);
            Assert.Same(wideImage, Assert.Single(Images(budget)));
            Assert.Equal(baseline + wideBytes, ExpectCharge(budget, wideImage));
            sameSize.Abandon();

            var narrowBytes = ManagedObjectSize(new byte[ImageLength(narrow)]);
            Assert.True(narrowBytes < wideBytes);
            ExpectRefusal(arena, budget, narrow, baseline, narrowBytes);
            ExpectIdle(idle, wideImage);
            budget.Open(baseline + narrowBytes);
            var differentSize = new RootWriter(arena, narrow, RootArena.Handle);
            var narrowImage = Assert.Single(Images(budget));
            Assert.NotSame(wideImage, narrowImage);
            Assert.Equal(narrowBytes, ExactImageBytes(narrowImage, narrow));
            Assert.Equal(baseline + narrowBytes, ExpectCharge(budget, narrowImage));
            differentSize.Abandon();
            ExpectIdle(idle, narrowImage);
            Assert.Equal(baseline, ExpectCharge(budget, idle));
        }
    }

    [Fact(Skip = PendingReuse)]
    public void An_idle_image_can_be_collected_with_its_arena_open_and_its_replacement_is_paid()
    {
        var budget = OpenBudget();
        var writes = new Lock();
        using var arena = OpenArena(budget, writes);
        var layout = Layout();
        var idle = Assert.Single(WeakOwners(arena));
        var baseline = ExpectCharge(budget, idle);
        lock (writes)
        {
            var probe = IdleImage(arena, budget, layout, idle);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(probe.TryGetTarget(out _));
            Assert.False(idle.TryGetTarget(out _));

            var replacement = new RootWriter(arena, layout, RootArena.Handle);
            var image = Assert.Single(Images(budget));
            Assert.Equal(baseline + ExactImageBytes(image, layout), ExpectCharge(budget, image));
            replacement.Abandon();
            Assert.Equal(baseline, ExpectCharge(budget, idle));
            ExpectIdle(idle, image);
        }
    }

    [Fact(Skip = PendingReuse)]
    public void Independent_arenas_and_the_forwarded_image_keep_separate_ownership()
    {
        var budget = OpenBudget();
        var writes = new Lock();
        using var arena = OpenArena(budget, writes);
        var otherBudget = OpenBudget();
        var otherWrites = new Lock();
        using var otherArena = OpenArena(otherBudget, otherWrites);
        var idle = Assert.Single(WeakOwners(arena));
        var otherIdle = Assert.Single(WeakOwners(otherArena));
        Assert.NotSame(idle, otherIdle);

        var layout = Layout();
        byte[] original;
        lock (writes)
        {
            var ordinary = new RootWriter(arena, layout, RootArena.Handle);
            original = Assert.Single(Images(budget));
            ordinary.Abandon();
        }

        lock (otherWrites)
        {
            var independent = new RootWriter(otherArena, layout, RootArena.Handle);
            var otherImage = Assert.Single(Images(otherBudget));
            Assert.NotSame(original, otherImage);
            Assert.Empty(Images(budget));
            ExpectIdle(idle, original);
            independent.Abandon();
            ExpectIdle(otherIdle, otherImage);
        }

        var baseline = ExpectCharge(budget, idle);
        using (var lease = MemoryLease.Create(budget))
        {
            var forwarded = new RootWriter(layout, ExportedValues.OfRoot(layout, lease));
            var image = Assert.Single(Images(budget));
            Assert.NotSame(original, image);
            _ = ExactImageBytes(image, layout);
            var forwardedCharge = ExpectCharge(budget, idle, lease, image);

            forwarded.SetInteger(layout.EntityType.Key, 19);
            var output = new ArrayBufferWriter<byte>();
            var length = forwarded.ExportLength;
            forwarded.Export(output);
            Assert.Equal(length, output.WrittenCount);
            Assert.Same(image, Assert.Single(Images(budget)));
            Assert.Equal(forwardedCharge, ExpectCharge(budget, lease, image));
            ExpectIdle(idle, original);
        }

        Assert.Equal(baseline, ExpectCharge(budget, idle));
        Assert.Empty(Images(budget));
        lock (writes)
        {
            var ordinary = new RootWriter(arena, layout, RootArena.Handle);
            Assert.Same(original, Assert.Single(Images(budget)));
            ordinary.Abandon();
        }
    }

    private static void ExpectRefusal(Arena arena, InstanceMemoryBudget budget, RootLayout layout, long baseline, long bytes)
    {
        budget.Open(baseline + bytes - 1);
        budget.ResetPeak();
        var failure = Assert.Throws<BudgetRefusedException>(() =>
        {
            var writer = new RootWriter(arena, layout, RootArena.Handle);
            writer.Abandon();
        });
        Assert.Equal(LatticeLimitEnum.Memory, failure.Limit);
        Assert.Equal(baseline, ExpectCharge(budget));
        Assert.Equal(baseline, budget.Inspect(null).Peak);
        Assert.Empty(Images(budget));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<byte[]> IdleImage(Arena arena, InstanceMemoryBudget budget, RootLayout layout, WeakReference<byte[]> idle)
    {
        var writer = new RootWriter(arena, layout, RootArena.Handle);
        var image = Assert.Single(Images(budget));
        writer.Abandon();
        ExpectIdle(idle, image);
        return new WeakReference<byte[]>(image);
    }

    private static void ExpectIdle(WeakReference<byte[]> idle, byte[] image)
    {
        Assert.True(idle.TryGetTarget(out var target));
        Assert.Same(image, target);
    }

    private static int ImageLength(RootLayout layout) => layout.ImageCapacity + RootLayout.BitBytes(layout.FieldCount);

    private static long ExactImageBytes(byte[] image, RootLayout layout)
    {
        Assert.Equal(ImageLength(layout), image.Length);
        return ManagedObjectSize(image);
    }

    private static long ExpectCharge(InstanceMemoryBudget budget, params object[] required)
    {
        var owned = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var sample = budget.Inspect((_, backing) => Assert.True(owned.Add(backing)));
        foreach (var backing in required)
        {
            Assert.Contains(backing, owned);
        }

        Assert.Equal(owned.Sum(ManagedObjectSize) + sample.Native, sample.Charged);
        return sample.Charged;
    }

    private static byte[][] Images(InstanceMemoryBudget budget)
    {
        var images = new List<byte[]>();
        budget.Inspect((owner, backing) =>
        {
            if (owner == nameof(RootWriter))
            {
                images.Add(Assert.IsType<byte[]>(backing));
            }
        });
        return [.. images];
    }

    private static WeakReference<byte[]>[] WeakOwners(Arena arena) =>
        [.. typeof(Arena).GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(field => field.FieldType == typeof(WeakReference<byte[]>))
            .Select(field => field.GetValue(arena)).OfType<WeakReference<byte[]>>()];

    private static RootLayout Layout(string fields = "")
    {
        var schema = ItemSchema.Compile(fields);
        return RootLayout.Compile(Assert.Single(schema.EntityTypes), schema);
    }

    private static InstanceMemoryBudget OpenBudget(long limit = 2L * Segment)
    {
        var budget = new InstanceMemoryBudget(observesBackings: true);
        budget.Open(limit);
        return budget;
    }

    private static Arena OpenArena(InstanceMemoryBudget budget, Lock writes, CountingNativeAllocator? allocator = null) =>
        new(new ArenaOptions { MemoryLimit = Segment }, allocator ?? new CountingNativeAllocator(), 0, writes, budget: budget);
}
