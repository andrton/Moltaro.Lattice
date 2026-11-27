using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.HeldInventory;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.Boundaries;

// A native segment is charged at its grant with its segment object and its metadata arrays, before
// it is obtained. The next grant is admitted exactly when the limit holds what the instance holds,
// its headroom, the grant, the segment object and the metadata, derived from the layout.
public sealed class NativeGrantBoundaryTests
{
    private const int Region = 64 * 1024;
    private static readonly RootHandle Owner = new(1, 1);

    [Theory(Skip = Pending)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_next_native_grant_is_admitted_exactly_when_its_segment_and_metadata_fit_beside_the_headroom(int difference)
    {
        long required;
        long filled;
        var witness = new CountingNativeAllocator();
        await using (var control = await OpenAsync(32L * Segment, witness))
        {
            var opened = ExpectAtRest(control, witness);
            Assert.Equal(0, opened.GrantedSegments);
            await FillOneSegmentAsync(control);
            var first = ExpectAtRest(control, witness);
            Assert.Equal(1, first.GrantedSegments);
            Assert.Equal(GrantBytes(Segment) + SegmentMetadataBytes(Segment), first.Charged - opened.Charged);
            filled = first.Charged;
            required = filled + GrantBytes(Segment) + SegmentMetadataBytes(Segment);
            Assert.NotEqual(default, await AllocateAsync(control));
            var second = ExpectAtRest(control, witness);
            Assert.Equal(2, second.GrantedSegments);
            Assert.Equal(required, second.Charged);
        }

        witness.ExpectEverythingReturned();
        var allocator = new CountingNativeAllocator();
        await using (var node = await OpenAsync(required + difference, allocator))
        {
            await FillOneSegmentAsync(node);
            Assert.Equal(filled, ExpectAtRest(node, allocator).Charged);
            var next = await AllocateAsync(node);
            var after = ExpectAtRest(node, allocator);
            if (difference < 0)
            {
                Assert.Equal(default, next);
                Assert.Equal(1, allocator.Requests);
                Assert.Equal(filled, after.Charged);
            }
            else
            {
                Assert.NotEqual(default, next);
                Assert.Equal(2, allocator.Requests);
                Assert.Equal(required, after.Charged);
                Assert.Equal(required, after.Peak);
            }
        }

        allocator.ExpectEverythingReturned();
    }

    private static Task<ArenaAddress> AllocateAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            _ = parts.Arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out var address);
            return address;
        });

    private static Task<bool> FillOneSegmentAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            for (var index = 0; index < Segment / Region; index++)
            {
                Assert.True(parts.Arena.TryAllocatePrepared(Region - 32, BlockKindEnum.Value, Owner, out _));
            }

            return true;
        });
}
