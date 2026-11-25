using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class CollectionInlineRoomTests
{
    private const int PackedBytes = 64;
    private const int ValuesCount = 8;

    [Theory]
    [InlineData(LatticeFieldTypeEnum.List)]
    [InlineData(LatticeFieldTypeEnum.Map)]
    [InlineData(LatticeFieldTypeEnum.Set)]
    public async Task The_same_packed_collection_spills_when_an_earlier_value_occupies_the_inline_room(LatticeFieldTypeEnum kind)
    {
        var schema = Compile(kind);
        var type = Assert.Single(schema.EntityTypes);
        var layout = RootLayout.Compile(type, schema);
        var entry = layout.Structure.ElementOf(type.GetField("Values").Ordinal);
        Assert.False(entry.HoldsDescriptors);
        Assert.Equal(PackedBytes, ValuesCount * entry.Size);
        Assert.Equal(Arena.MaxBlockSize, layout.ImageCapacity);
        var room = layout.ImageCapacity - layout.FixedSize;
        Assert.Equal(PackedBytes, room);
        Assert.Equal(ValuesCount, CollectionLookup.MostCompactEntries);

        var occupying = new byte[room - PackedBytes + 1];
        occupying[0] = 0xB1;

        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var source = master.Set("Item");
        await source.InsertAsync(Record(master, 1L, null));
        await source.InsertAsync(Record(master, 2L, occupying));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var replicas = child.Set("Item");
        Assert.Equal(master.Position, child.Position);

        var inline = ExpectBoth(1L, null, DescriptorKindEnum.Inline);
        ExpectBoth(2L, occupying, DescriptorKindEnum.Block);

        var fill = replicas.CreateUpdate(1L);
        var filled = fill.Assign("Filler", occupying);
        await ReplayAsync(fill, inline.Revision);
        Assert.True(filled.Changed);
        var outside = ExpectBoth(1L, occupying, DescriptorKindEnum.Block);
        Assert.NotEqual(inline.Revision, outside.Revision);

        var clear = replicas.CreateUpdate(1L);
        var cleared = clear.Assign<byte[]?>("Filler", null);
        await ReplayAsync(clear, outside.Revision);
        Assert.True(cleared.Changed);
        var adopted = ExpectBoth(1L, null, DescriptorKindEnum.Block);
        Assert.NotEqual(outside.Revision, adopted.Revision);
        Assert.Equal((outside.Master, outside.Child), (adopted.Master, adopted.Child));

        var rebuild = replicas.CreateUpdate(1L);
        var rebuilt = rebuild.Assign("Values", Values());
        await ReplayAsync(rebuild, adopted.Revision);
        Assert.True(rebuilt.Changed);
        var reparsed = ExpectBoth(1L, null, DescriptorKindEnum.Inline);
        Assert.NotEqual(adopted.Revision, reparsed.Revision);

        var beforeReplace = master.Position;
        var replaced = await replicas.ReplaceAsync(Record(child, 2L, null)).AsTask().WaitAsync(Bound);
        AssertPosition(beforeReplace, replaced.Position);
        ExpectBoth(2L, null, DescriptorKindEnum.Inline);
        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);

        async Task ReplayAsync(LatticeUpdate update, LatticeRevision revision)
        {
            var before = master.Position;
            var written = await update.CommitAsync(revision).AsTask().WaitAsync(Bound);
            Assert.True(written.Changed);
            AssertPosition(before, written.Position);
        }

        void AssertPosition(LatticePosition from, LatticePosition at)
        {
            Assert.Equal(new LatticePosition(from.History, from.Sequence + 1), at);
            Assert.Equal(at, master.Position);
            Assert.Equal(at, child.Position);
        }

        (LatticeRevision Revision, ulong Master, ulong Child) ExpectBoth(long key, byte[]? filler, DescriptorKindEnum storage)
        {
            var (left, masterLocation) = Expect(master, source);
            var (right, childLocation) = Expect(child, replicas);
            Assert.Equal(left, right);
            return (left, masterLocation, childLocation);

            (LatticeRevision, ulong) Expect(EngineDatabase database, ILatticeSet set)
            {
                Assert.True(set.TryGet(key, out var copy, out var revision));
                Assert.Equal(filler, copy.Get<byte[]>("Filler"));
                AssertOwned(copy, kind);
                Assert.True(set.TryView(key, view =>
                {
                    var fillerView = view["Filler"];
                    Assert.Equal(filler is null, fillerView.IsNull);
                    if (filler is not null)
                    {
                        Assert.True(filler.AsSpan().SequenceEqual(fillerView.GetPart(0)));
                    }

                    AssertView(view["Values"], kind);
                    return view.Revision;
                }, out var seen));
                Assert.Equal(revision, seen);

                var locations = new List<ulong>();
                database.ForEachLiveRoot((arena, stored, _, root) =>
                {
                    var reader = new RootReader(arena, stored, root);
                    if (reader.GetInteger(stored.EntityType.Key) != key)
                    {
                        return;
                    }

                    var payload = arena.Payload(root);
                    var values = stored.StoredFieldOf(stored.EntityType.GetField("Values"));
                    var first = stored.StoredFieldOf(stored.EntityType.GetField("Filler"));
                    Assert.True(first.Offset < values.Offset);
                    var target = ResidentDescriptor.Read(payload[(values.Offset - BlockHeader.Size)..]);
                    var occupied = ResidentDescriptor.Read(payload[(first.Offset - BlockHeader.Size)..]);
                    Assert.Equal(filler is null ? DescriptorKindEnum.Null : DescriptorKindEnum.Inline, occupied.Kind);
                    Assert.Equal((uint)(filler?.Length ?? 0), occupied.Length);
                    Assert.Equal(storage, target.Kind);
                    Assert.Equal((uint)PackedBytes, target.Length);
                    var inlineBytes = filler?.Length ?? 0;
                    if (storage == DescriptorKindEnum.Inline)
                    {
                        Assert.Equal((ulong)(stored.FixedSize + inlineBytes), target.Location);
                        inlineBytes += PackedBytes;
                    }
                    else
                    {
                        var block = ValueBlocks.AddressOf(target);
                        Assert.Equal(root, arena.Header(block).Parent);
                        Assert.Equal((uint)values.Offset, arena.Header(block).DescriptorOffset);
                        Assert.Equal(PackedBytes, arena.Payload(block).Length);
                    }

                    Assert.Equal(stored.FixedSize - BlockHeader.Size + inlineBytes, payload.Length);
                    locations.Add(target.Location);
                });
                return (revision, Assert.Single(locations));
            }
        }

        LatticeRecord Record(EngineDatabase database, long key, byte[]? filler)
        {
            Assert.True(database.Schema.TryGetOwnedType("Page", out var pageType));
            Assert.True(database.Schema.TryGetOwnedType("Pad", out var padType));
            var page = new LatticeOwnedValue(pageType);
            foreach (var field in pageType.Fields)
            {
                page[field.Name] = Guid.Empty;
            }

            var pad = new LatticeOwnedValue(padType);
            foreach (var field in padType.Fields)
            {
                pad[field.Name] = field.Declaration.Type switch
                {
                    LatticeFieldTypeEnum.Owned => page,
                    LatticeFieldTypeEnum.Guid => Guid.Empty,
                    _ => (byte)0,
                };
            }

            var set = database.Set("Item");
            return new LatticeRecord(set.EntityType) { ["Id"] = key, ["Filler"] = filler, ["Values"] = Values(), ["Padding"] = pad };
        }

        object Values() => kind switch
        {
            LatticeFieldTypeEnum.List => Enumerable.Range(0, ValuesCount).Select(Number).ToList(),
            LatticeFieldTypeEnum.Map => Enumerable.Range(0, ValuesCount).ToDictionary(index => (uint)index, index => (int)Number(index)),
            _ => Enumerable.Range(0, ValuesCount).Select(Number).ToHashSet(),
        };
    }

    private static long Number(int index) => 103L + index * 97L;

    private static void AssertOwned(LatticeRecord record, LatticeFieldTypeEnum kind)
    {
        var expected = Enumerable.Range(0, ValuesCount).Select(Number);
        switch (kind)
        {
            case LatticeFieldTypeEnum.List:
                Assert.Equal(expected, record.Get<List<long>>("Values"));
                break;
            case LatticeFieldTypeEnum.Map:
                var map = record.Get<Dictionary<uint, int>>("Values")!;
                Assert.Equal(ValuesCount, map.Count);
                for (var index = 0; index < ValuesCount; index++)
                {
                    Assert.Equal((int)Number(index), map[(uint)index]);
                }

                break;
            default:
                Assert.True(expected.ToHashSet().SetEquals(record.Get<HashSet<long>>("Values")!));
                break;
        }
    }

    private static void AssertView(LatticeValueView value, LatticeFieldTypeEnum kind)
    {
        switch (kind)
        {
            case LatticeFieldTypeEnum.List:
                var list = value.List;
                Assert.Equal(ValuesCount, list.Count);
                for (var index = 0; index < ValuesCount; index++)
                {
                    Assert.Equal(Number(index), list[index].Get<long>());
                }

                break;
            case LatticeFieldTypeEnum.Map:
                var map = value.Map;
                Assert.Equal(ValuesCount, map.Count);
                for (var index = 0; index < ValuesCount; index++)
                {
                    Assert.True(map.TryGetValue((uint)index, out var found));
                    Assert.Equal((int)Number(index), found.Get<int>());
                }

                Assert.False(map.TryGetValue(uint.MaxValue, out _));
                break;
            default:
                var set = value.Set;
                Assert.Equal(ValuesCount, set.Count);
                for (var index = 0; index < ValuesCount; index++)
                {
                    Assert.True(set.Contains(Number(index)));
                }

                Assert.False(set.Contains(long.MinValue));
                break;
        }
    }

    private static LatticeSchema Compile(LatticeFieldTypeEnum kind)
    {
        // The required padding is 63 pages (1,024 bytes each), 55 Guid
        // fields and seven byte fields. The remaining fixed bytes are a
        // header, one null bit, the Int64 key and two descriptors.
        var page = string.Join(", ", Enumerable.Range(1, 64).Select(index => Field(index, "Guid")));
        var children = Enumerable.Range(1, 63).Select(index => $$"""
            { "Id": {{index}}, "Name": "Field{{index}}", "Type": "Owned", "OwnedType": "Page" }
            """);
        var guids = Enumerable.Range(64, 55).Select(index => Field(index, "Guid"));
        var bytes = Enumerable.Range(119, 7).Select(index => Field(index, "UInt8"));
        var pad = string.Join(", ", children.Concat(guids).Concat(bytes));
        var values = kind == LatticeFieldTypeEnum.Map
            ? """ "Key": { "Type": "UInt32" }, "Value": { "Type": "Int32" } """
            : """ "Element": { "Type": "Int64" } """;
        return LatticeSchema.Compile($$"""
            {
              "OwnedTypes": [
                { "Id": 1, "Name": "Page", "Fields": [{{page}}] },
                { "Id": 2, "Name": "Pad", "Fields": [{{pad}}] }
              ],
              "EntityTypes": [{
                "Id": 1, "Name": "Item",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "Int64" },
                  { "Id": 2, "Name": "Filler", "Type": "Binary", "Nullable": true },
                  { "Id": 3, "Name": "Values", "Type": "{{kind}}", {{values}} },
                  { "Id": 4, "Name": "Padding", "Type": "Owned", "OwnedType": "Pad" }
                ],
                "PrimaryKey": [1]
              }]
            }
            """);

        static string Field(int ordinal, string type) =>
            $$""" { "Id": {{ordinal}}, "Name": "Field{{ordinal}}", "Type": "{{type}}" } """;
    }
}
