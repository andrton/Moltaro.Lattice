using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class StringPrimaryKeyPlacementTests
{
    [Theory]
    [InlineData("Utf8", 1, 9)]
    [InlineData("Utf16", 2, 8)]
    public async Task Generated_String_keys_reach_each_encoded_size_tier_and_its_native_placement(
        string encoding, int digitBytes, int groupBytes)
    {
        var profile = CollectionMatrixProfile.All.Single(profile => profile.Name == "PackedList");
        var schema = profile.Schema(LatticeFieldTypeEnum.String, encoding);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var items = master.Set("Item");
        int[] ordinals = [0, 1, 2, 3, 6];

        foreach (var ordinal in ordinals)
        {
            await items.InsertAsync(new LatticeRecord(items.EntityType)
            {
                ["Id"] = ReplicatedCollectionShapeTests.KeyOf(LatticeFieldTypeEnum.String, ordinal),
                ["Case"] = ordinal,
                ["Values"] = new List<long?>(),
                ["Stamp"] = 1,
            });
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(master.Position, child.Position);

        // A nonempty fixture key consists of one ASCII digit and repeated
        // "é€𝌆". Its independent group width is 2+3+4 bytes in UTF-8, or
        // four UTF-16 code units. The sizes straddle the native placement limits.
        (int Ordinal, int Bytes, DescriptorKindEnum Kind, int Chunks)[] expected =
        [
            (0, 0, DescriptorKindEnum.Inline, 0),
            (1, digitBytes + groupBytes, DescriptorKindEnum.Inline, 0),
            (2, digitBytes + 20 * groupBytes, DescriptorKindEnum.Block, 0),
            (3, digitBytes + 610 * groupBytes, DescriptorKindEnum.Chunked, 1),
            (6, digitBytes + 8_250 * groupBytes, DescriptorKindEnum.Chunked, 2),
        ];
        Assert.InRange(expected[1].Bytes, 1, ValueBlocks.InlineLimit);
        Assert.InRange(expected[2].Bytes, ValueBlocks.InlineLimit + 1, ValueBlocks.SingleBlockLimit);
        Assert.InRange(expected[3].Bytes, ValueBlocks.SingleBlockLimit + 1, ValueBlocks.ChunkPayload);
        Assert.InRange(expected[4].Bytes, ValueBlocks.ChunkPayload + 1, 2 * ValueBlocks.ChunkPayload);

        foreach (var database in new[] { master, child })
        {
            var actual = new List<(int Ordinal, int Bytes, DescriptorKindEnum Kind, int Chunks)>();
            database.ForEachLiveRoot((arena, layout, _, root) =>
            {
                var reader = new RootReader(arena, layout, root);
                var ordinal = (int)reader.GetInteger(layout.EntityType.GetField("Case"));
                var payload = arena.Payload(root);
                var key = layout.StoredFieldOf(layout.EntityType.Key);
                var descriptor = ResidentDescriptor.Read(payload[(key.Offset - BlockHeader.Size)..]);
                var chunks = 0;
                if (descriptor.Kind == DescriptorKindEnum.Block)
                {
                    var value = ValueBlocks.AddressOf(descriptor);
                    Assert.Equal(root, arena.Header(value).Parent);
                    Assert.Equal((int)descriptor.Length, arena.Payload(value).Length);
                }

                if (descriptor.Kind == DescriptorKindEnum.Chunked)
                {
                    var directory = ValueBlocks.AddressOf(descriptor);
                    Assert.Equal(root, arena.Header(directory).Parent);
                    var body = arena.Payload(directory);
                    Assert.Equal(0, body.Length % ValueBlocks.DescriptorSize);
                    chunks = body.Length / ValueBlocks.DescriptorSize;
                    var bytes = 0;
                    for (var part = 0; part < chunks; part++)
                    {
                        var entry = ResidentDescriptor.Read(body[(part * ValueBlocks.DescriptorSize)..]);
                        Assert.Equal(DescriptorKindEnum.Block, entry.Kind);
                        var physical = ValueBlocks.AddressOf(entry);
                        Assert.Equal(directory, arena.Header(physical).Parent);
                        Assert.Equal((int)entry.Length, arena.Payload(physical).Length);
                        bytes += (int)entry.Length;
                    }

                    Assert.Equal((int)descriptor.Length, bytes);
                }

                actual.Add((ordinal, (int)descriptor.Length, descriptor.Kind, chunks));
            });

            Assert.Equal(expected, actual.OrderBy(entry => entry.Ordinal));
        }
    }
}
