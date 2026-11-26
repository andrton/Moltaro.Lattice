using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Batches;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Storage.Roots;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class OwnedSnapshotBackingsTests
{
    [Fact]
    public async Task A_true_typed_key_box_and_update_input_remain_owned_until_their_batch_finishes()
    {
        var allocator = new CountingNativeAllocator();
        await using var node = await OpenAsync(8L * Segment, allocator);
        var pieces = node.Set<PieceSchema.Piece>();
        var layout = RootLayout.Compile(pieces.EntityType, node.Schema);
        var images = new List<byte[]>();
        allocator.HoldNextAllocation(() => node.InspectBudget((owner, backing) =>
        {
            if (owner == nameof(RootWriter))
            {
                images.Add(Assert.IsType<byte[]>(backing));
            }
        }));
        await pieces.InsertAsync(PieceSchema.Of(1L, 8));
        var placedImage = Assert.Single(images);
        Assert.Equal(layout.ImageCapacity + RootLayout.BitBytes(layout.FieldCount), placedImage.Length);
        node.InspectBudget((_, backing) => Assert.NotSame(placedImage, backing));

        var batch = node.CreateWriteBatch();
        batch.Insert(PieceSchema.Of(2L, 8));
        var update = pieces.CreateUpdate(1L);
        update.Assign("Name", "updated");
        batch.Update(update);
        var operations = Backing<List<WriteBatchOperation>>(batch, "_operations");
        var key = operations[0].Key;
        var input = operations[1].Update;
        Assert.Equal(2L, Assert.IsType<long>(key));
        Assert.NotNull(input);
        Assert.Equal(24, ManagedObjectSize(key));
        Assert.Equal(32, ManagedObjectSize(input));

        var byName = PieceSchema.RecordOf(pieces.EntityType, 3L, 8);
        var callerKey = Backing<object?[]>(byName, "_values")[pieces.EntityType.Key.Ordinal];
        batch.Insert(byName);
        Assert.Same(callerKey, operations[2].Key);
        _ = ExpectCapacity(node, allocator, out _, key, input);
        node.InspectBudget((_, backing) => Assert.NotSame(callerKey, backing));
        var committed = await batch.CommitAsync();
        Assert.Equal(committed.Position, node.Position);
        Assert.Equal("updated", pieces.Get(1L)?.Name);
        Assert.Equal(2L, pieces.Get(2L)?.Id);
        Assert.Equal(3L, pieces.Get(3L)?.Id);
        ExpectCapacity(node, allocator);
        node.InspectBudget((_, backing) =>
        {
            Assert.NotSame(key, backing);
            Assert.NotSame(input, backing);
        });
    }
}
