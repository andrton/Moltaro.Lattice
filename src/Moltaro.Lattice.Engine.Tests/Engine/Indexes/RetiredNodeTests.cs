using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes.Reservation;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes;

// A node a write replaces is retired, counted as retired bytes, and freed after the reads that could reach
// it have left, by the same protection of reads as every other read (docs/design/ordered-index.md, "The
// retired node"; docs/decisions/index-structures.md, "Secondary index": a write copies one node of
// each index it changes and retires the old one, so retired memory grows by about one node for each index
// write while a read is held).
// - A read held inside the protection (HeldRead) keeps every node the writes after it replace: each
//   replace that moves an entity to another key of ByNumber retires at least one node of 1 KiB beside its
//   old root, and the retired bytes are those roots and whole nodes.
// - Once the read has left, a drain frees them all.
// - Without a drain, the writes after the read has left free them on their own path: within as many
//   further writes as the epoch table has slots, the look of the writer frees what the read kept (arena
//   protocol, "Deferred reclamation").
// Counted has the fixed part of 48 bytes, the slot of its root.
public sealed class RetiredNodeTests
{
    private const int Entities = 10;

    private const int HeldWrites = 20;

    [Fact]
    public async Task Nodes_replaced_under_a_held_read_are_retired_bytes_until_the_read_leaves_and_a_drain_then_frees_them()
    {
        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();
        await InsertAsync(database, counted);

        using var held = await HeldRead.StartAsync(database, () => counted.Get(1L)?.Number);

        await Task.Run(() => MoveAsync(counted, HeldWrites)).WaitAsync(TimeSpan.FromMinutes(1));

        database.DrainRetired();
        var retiredNodes = database.Counters.Arena.RetiredBytes - (HeldWrites * IndexSchema.CountedFixedSize);

        Assert.True(retiredNodes >= HeldWrites * IndexSchema.NodeBytes, $"{HeldWrites} writes that moved keys retired {retiredNodes} bytes of nodes.");
        Assert.Equal(0, retiredNodes % IndexSchema.NodeBytes);

        await held.ReleaseAsync();

        database.DrainRetired();

        Assert.Equal(0, database.Counters.Arena.RetiredBytes);
        Assert.True(database.Counters.Arena.PreparedBytes <= IndexReserve.BytesOf(database, "Counted"), "The arena holds prepared bytes beyond the reserve of ByNumber.");
    }

    [Fact]
    public async Task Nodes_a_held_read_kept_are_freed_without_a_drain_within_a_window_of_writes_after_it_leaves()
    {
        var slots = new ReadEpochs(new object()).SlotCount;

        await using var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var counted = database.Set<IndexSchema.Counted>();
        await InsertAsync(database, counted);

        using var held = await HeldRead.StartAsync(database, () => counted.Get(1L)?.Number);

        await Task.Run(() => MoveAsync(counted, HeldWrites)).WaitAsync(TimeSpan.FromMinutes(1));
        var kept = database.Counters.Arena.RetiredBytes;

        await held.ReleaseAsync();

        var freed = false;

        for (var write = 0; write < slots && !freed; write++)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = 2, Number = 2_000 + write });
            freed = database.Counters.Arena.RetiredBytes < kept;
        }

        Assert.True(freed, $"The retired bytes did not fall below the {kept} the held read kept within {slots} writes after it left.");
    }

    // The entities, and a drain of what their inserts retired, so that what is retired afterwards is what
    // the held read keeps.
    private static async Task InsertAsync(EngineDatabase database, ILatticeSet<IndexSchema.Counted> counted)
    {
        for (var id = 1L; id <= Entities; id++)
        {
            await counted.InsertAsync(new IndexSchema.Counted { Id = id, Number = id });
        }

        database.DrainRetired();
    }

    // Moves the entities in turn to keys from 1,001 on: each write changes the key of ByNumber.
    private static async Task MoveAsync(ILatticeSet<IndexSchema.Counted> counted, int writes)
    {
        for (var write = 0; write < writes; write++)
        {
            await counted.ReplaceAsync(new IndexSchema.Counted { Id = (write % Entities) + 1, Number = 1_001 + write });
        }
    }
}
