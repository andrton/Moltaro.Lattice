using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Measurements;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage.Allocation;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;
using InstanceMemoryBudget = Moltaro.Lattice.Storage.Budgeting.MemoryBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The sizes the budget states, held against what the running runtime allocates: the array header
// of ArrayBytes, and the per-builder and per-member constants of the estimate, measured on the
// reference fixture for the members of a batch and for ordinary and partial writes. A constant
// below what it measures fails the test.
[Collection<ManagedAllocationCollection>]
public sealed class MeasuredSizesTests
{
    private const int Members = 1_024;

    [Fact]
    public void ArrayBytes_is_what_the_runtime_allocates_for_an_array_with_its_header_of_24_bytes()
    {
        Assert.Equal(24, Allocated(() => new byte[0]));
        Assert.Equal(24, InstanceMemoryBudget.ArrayBytes<byte>(0));
        Expect<byte>(1, 32);
        Expect<byte>(9, 40);
        Expect<long>(3, 48);
        Expect<object>(5, 64);
        Expect<ArenaRegion>(64, 24 + 64 * 24);

        static void Expect<T>(int length, long bytes)
        {
            Assert.Equal(bytes, Allocated(() => new T[length]));
            Assert.Equal(bytes, InstanceMemoryBudget.ArrayBytes<T>(length));
        }
    }

    [Fact]
    public async Task The_builder_and_member_constants_cover_what_a_batch_of_the_reference_fixture_allocates_beside_its_input()
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        var records = ReferenceFixture.Generate(schema).Take(Members + 32).ToList();
        await using var node = await OpenAsync(64L * Segment, new CountingNativeAllocator(), schema: schema);
        var warm = node.CreateWriteBatch();
        foreach (var record in records.Take(32))
        {
            warm.Insert(record);
        }

        var measured = records.Skip(32).ToList();
        var encoded = measured.Select(record => Encoded(node, record)).ToArray();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var batch = node.CreateWriteBatch();
        batch.Insert(measured[0]);
        var first = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(first - encoded[0], 0, BatchInputCharge.PerBuilderBytes + BatchInputCharge.PerMemberBytes);

        before = GC.GetAllocatedBytesForCurrentThread();
        for (var member = 1; member < measured.Count; member++)
        {
            batch.Insert(measured[member]);
        }

        var members = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(members - encoded.Skip(1).Sum(), 0, (measured.Count - 1) * BatchInputCharge.PerMemberBytes);

        var deletes = node.CreateWriteBatch();
        deletes.Delete("User", -1L);
        before = GC.GetAllocatedBytesForCurrentThread();
        for (var key = 0L; key < Members; key++)
        {
            deletes.Delete("User", -2L - key);
        }

        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, Members * BatchInputCharge.PerMemberBytes);
        GC.KeepAlive(warm);
        GC.KeepAlive(batch);
    }

    [Fact]
    public async Task The_member_constant_covers_what_an_ordinary_and_a_partial_write_of_the_reference_fixture_allocate()
    {
        var schema = LatticeSchema.Compile(ReferenceFixture.SchemaJson);
        var users = ReferenceFixture.Generate(schema).Where(record => record.EntityType.Name == "User").Take(2).ToList();
        await using var node = await OpenAsync(64L * Segment, new CountingNativeAllocator(), schema: schema);
        var set = node.Set("User");
        foreach (var user in users)
        {
            await set.InsertAsync(user).AsTask().WaitAsync(Bound);
        }

        var key = users[0][set.EntityType.Key.Name]!;
        for (var warm = 0; warm < 64; warm++)
        {
            await set.ReplaceAsync(users[0]).AsTask().WaitAsync(Bound);
        }

        var before = GC.GetTotalAllocatedBytes(precise: true);
        for (var write = 0; write < Members; write++)
        {
            await set.ReplaceAsync(users[0]).AsTask().WaitAsync(Bound);
        }

        var whole = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.InRange(whole, 0, Members * BatchInputCharge.PerMemberBytes);

        var updates = Enumerable.Range(0, Members + 64).Select(index =>
        {
            var update = set.CreateUpdate(key);
            update.Assign("DisplayName", $"display name {index % 2}");
            return update;
        }).ToList();
        var commands = HeldInventory.UpdateEstimate(node, updates[0]) - BatchInputCharge.PerMemberBytes;
        foreach (var update in updates.Take(64))
        {
            await update.CommitAsync().AsTask().WaitAsync(Bound);
        }

        before = GC.GetTotalAllocatedBytes(precise: true);
        foreach (var update in updates.Skip(64))
        {
            await update.CommitAsync().AsTask().WaitAsync(Bound);
        }

        var partial = GC.GetTotalAllocatedBytes(precise: true) - before;
        Assert.InRange(partial, 0, Members * (commands + BatchInputCharge.PerMemberBytes));
    }

    private static long Allocated(Func<object> allocate)
    {
        _ = allocate();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var value = allocate();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(value);
        return bytes;
    }

    private static long Encoded(EngineDatabase node, LatticeRecord record) =>
        HeldInventory.MemberEstimate(node, record) - BatchInputCharge.PerMemberBytes;
}
