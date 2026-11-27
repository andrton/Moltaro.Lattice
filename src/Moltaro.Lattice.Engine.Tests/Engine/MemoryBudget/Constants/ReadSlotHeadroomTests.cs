using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Budgeting;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Constants;

// The array of the slots of the protection of reads depends on the processors of the host alone
// (ReadEpochs.SlotCount), so the headroom holds it as its bytes beside the parts measured on one host,
// and covers it on every host (docs/design/storage-and-maintenance.md, the memory budget).
public sealed class ReadSlotHeadroomTests
{
    [Fact]
    public async Task The_headroom_holds_the_bytes_of_the_read_slots_of_the_host_beside_its_measured_parts()
    {
        var schema = IndexSchema.Utf8;
        await using var node = await OpenAsync(16L * Segment, new CountingNativeAllocator(), schema: schema);

        var types = schema.EntityTypes.Count;
        var indexes = types + schema.EntityTypes.Sum(entityType => entityType.Indexes.Count);
        var parts = Headroom.InstanceBytes + types * Headroom.EntityTypeBytes + indexes * Headroom.IndexBytes;
        var slots = new ReadEpochs(new object()).SlotCount;
        Assert.Equal(parts + ArrayBytes(typeof(EpochSlot), slots), node.InspectHeld().Headroom);
    }
}
