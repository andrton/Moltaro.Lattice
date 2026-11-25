using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class WideCompactMapCursorTests
{
    [Theory]
    [InlineData("Utf8", false)]
    [InlineData("Utf16", false)]
    [InlineData("Utf8", true)]
    [InlineData("Utf16", true)]
    public Task Copies_of_a_held_compact_Map_iterator_cross_its_actual_entry_join(string encoding, bool records)
    {
        var profile = WideMapProfile.All.Single(profile => profile.UsesRecords == records);
        return records
            ? ExpectAsync(encoding, profile, ordinal => ordinal != 0, key => key ? 1 : 0)
            : ExpectAsync(encoding, profile, ordinal => ordinal, key => key);
    }

    private static async Task ExpectAsync<TMember>(
        string encoding, WideMapProfile profile, Func<int, TMember> member, Func<TMember, int> ordinalOf)
        where TMember : notnull
    {
        var schema = profile.Schema(LatticeFieldTypeEnum.UInt64, encoding);
        var layout = profile.EntryLayout(schema);
        var crossing = profile.UsesRecords ? layout.RecordsPerChunk : ValueBlocks.ChunkPayload / layout.Size;
        var count = crossing + 1;
        const ulong key = 0xF038_924D_0000_0187;
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var initial = await master.Set("Item").InsertAsync(Record(master, stamp: 1));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(initial.Position, child.Position);
        AssertPhysicalJoin(master, profile, layout, count);
        AssertPhysicalJoin(child, profile, layout, count);
        Assert.True(master.Set("Item").TryGet(key, out _, out var revision));

        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = OwnThread.Run(() => ReadHeld(master.Set("Item"), masterPause));
        var childRead = OwnThread.Run(() => ReadHeld(child.Set("Item"), childPause));
        try
        {
            await masterPause.EnteredAsync(masterRead);
            await childPause.EnteredAsync(childRead);
            var replaced = await child.Set("Item").ReplaceAsync(Record(child, stamp: 2)).AsTask().WaitAsync(Bound);
            Assert.Equal(new LatticePosition(initial.Position.History, initial.Position.Sequence + 1), replaced.Position);
            foreach (var node in new[] { master, child })
            {
                Assert.Equal(replaced.Position, node.Position);
                Assert.True(node.Counters.Arena.RetiredBytes > 0);
                var items = node.Set("Item");
                Assert.True(items.TryView(key, view =>
                {
                    Assert.Equal(2, view["Stamp"].Get<int>());
                    Assert.Equal(count, view["Values"].Map.Count);
                    Assert.True(view["Values"].Map.TryGetValue(member(count - 1), out var value));
                    AssertCell(value.Owned, count - 1, stamp: 2);
                    return view.Revision;
                }, out var now));
                Assert.NotEqual(revision, now);
            }
        }
        finally
        {
            masterPause.Release();
            childPause.Release();
            await Task.WhenAll(masterRead, childRead).WaitAsync(Bound);
        }

        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);

        LatticeRecord Record(EngineDatabase node, int stamp)
        {
            var type = node.Set("Item").EntityType;
            Assert.True(node.Schema.TryGetOwnedType("Wide", out var cellType));
            var values = new Dictionary<TMember, LatticeOwnedValue>();
            for (var index = 0; index < count; index++)
            {
                var value = new LatticeOwnedValue(cellType);
                for (var field = 1; field <= profile.FieldCount; field++)
                {
                    value[FieldName(field)] = Expected(field, index, stamp);
                }

                values.Add(member(index), value);
            }

            return new LatticeRecord(type) { ["Id"] = key, ["Case"] = 0, ["Values"] = values, ["Stamp"] = stamp };
        }

        void ReadHeld(ILatticeSet set, ViewerPause pause)
        {
            Assert.True(set.TryView(key, view =>
            {
                Assert.Equal(revision, view.Revision);
                var map = view["Values"].Map;
                Assert.Equal(count, map.Count);
                var left = map.GetEnumerator();
                for (var index = 0; index < crossing; index++)
                {
                    Assert.True(left.MoveNext());
                }

                // Both cursors are one logical entry before the physical join.
                // Their relative order is meaningful even though Map iteration
                // does not promise any particular order for separate snapshots.
                var right = left;
                var held = left.Current;
                pause.Hold();
                Assert.Equal(1, view["Stamp"].Get<int>());
                AssertCell(held.Value.Owned, ordinalOf((TMember)TestScalarView.Read(held.Key)!), stamp: 1);

                var visited = new HashSet<TMember>();
                for (var index = crossing - 1; index < count; index++)
                {
                    var leftEntry = left.Current;
                    var rightEntry = right.Current;
                    var current = (TMember)TestScalarView.Read(leftEntry.Key)!;
                    Assert.Equal(current, TestScalarView.Read(rightEntry.Key));
                    Assert.True(visited.Add(current));
                    AssertCell(leftEntry.Value.Owned, ordinalOf(current), stamp: 1);
                    AssertCell(rightEntry.Value.Owned, ordinalOf(current), stamp: 1);

                    var more = index + 1 < count;
                    Assert.Equal(more, left.MoveNext());
                    Assert.Equal(more, right.MoveNext());
                }

                Assert.Equal(2, visited.Count);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        }

        void AssertCell(LatticeOwnedView value, int row, int stamp)
        {
            for (var field = 1; field <= profile.FieldCount; field++)
            {
                Assert.Equal(Expected(field, row, stamp), TestScalarView.Read(value[FieldName(field)]));
            }
        }

        object Expected(int field, int row, int stamp) => profile.UsesRecords
            ? string.Create(CultureInfo.InvariantCulture, $"{stamp}-{row}-{field}-é")
            : new Guid(row, (short)field, (short)stamp, 1, 2, 3, 4, 5, 6, 7, 8);
    }

    private static void AssertPhysicalJoin(EngineDatabase database, WideMapProfile profile, StructureLayout layout, int count)
    {
        var observed = 0;
        database.ForEachLiveRoot((arena, rootLayout, _, root) =>
        {
            var field = rootLayout.StoredFieldOf(rootLayout.EntityType.GetField("Values"));
            var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(field.Offset - BlockHeader.Size)..]);
            Assert.Equal(profile.UsesRecords ? DescriptorKindEnum.Records : DescriptorKindEnum.Chunked, descriptor.Kind);
            var directory = ValueBlocks.AddressOf(descriptor);
            var entries = arena.Payload(directory);
            Assert.Equal(2 * ValueBlocks.DescriptorSize, entries.Length);
            Assert.Equal(root, arena.Header(directory).Parent);
            var first = ResidentDescriptor.Read(entries);
            var second = ResidentDescriptor.Read(entries[ValueBlocks.DescriptorSize..]);
            Assert.Equal(directory, arena.Header(ValueBlocks.AddressOf(first)).Parent);
            Assert.Equal(directory, arena.Header(ValueBlocks.AddressOf(second)).Parent);
            if (profile.UsesRecords)
            {
                Assert.Equal((uint)count, descriptor.Length);
                Assert.Equal(1, layout.RecordsPerChunk);
                Assert.True(first.Length >= layout.Size && second.Length >= layout.Size);
            }
            else
            {
                Assert.Equal((uint)(count * layout.Size), descriptor.Length);
                Assert.Equal((uint)ValueBlocks.ChunkPayload, first.Length);
                Assert.Equal(descriptor.Length - first.Length, second.Length);
                Assert.True((count - 1) * layout.Size < first.Length && first.Length < descriptor.Length);
            }

            observed++;
        });
        Assert.Equal(1, observed);
    }

    private static string FieldName(int field) => "Value" + field.ToString(CultureInfo.InvariantCulture);
}
