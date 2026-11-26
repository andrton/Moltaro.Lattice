using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Collections;

public sealed class FiniteCollectionDomainTests
{
    private static readonly bool?[] Members = [null, false, true];

    // Zero is a missing key, one is a present key with a null owned value.
    // All other states have a record. Its Binary distinguishes empty, inline,
    // single-block, one-chunk directory and multi-chunk directory.
    private static readonly int[] ByteLengths = [0, 0, 0, 64, 65, 4_097, 65_505];

    private const string Shared = "the same dictionary text from every entry á€𐐨";
    private const ulong Reference = 0xFFFF_FFFF_0000_003BUL;

    private static LatticeSchema Compile(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          "OwnedTypes": [
            { "Id": 1, "Name": "Cell", "Fields": [
              { "Id": 1, "Name": "Bytes", "Type": "Binary" },
              { "Id": 2, "Name": "Shared", "Type": "String", "StringStorage": "Dictionary" },
              { "Id": 3, "Name": "Reference", "Type": "Reference", "EntityType": "Target" },
              { "Id": 4, "Name": "Labels", "Type": "List", "Element": { "Type": "String", "Nullable": true } }
            ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Row",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Case", "Type": "Int32" },
                { "Id": 3, "Name": "Options", "Type": "Map", "Key": { "Type": "Boolean" },
                  "Value": { "Type": "Owned", "OwnedType": "Cell", "Nullable": true } },
                { "Id": 4, "Name": "Members", "Type": "Set", "Element": { "Type": "Boolean", "Nullable": true } }
              ],
              "PrimaryKey": [1],
              "Indexes": [{ "Id": 1, "Name": "ByCase", "Fields": [2] }]
            },
            {
              "Id": 2, "Name": "Target",
              "Fields": [{ "Id": 1, "Name": "Id", "Type": "UInt64" }],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public async Task Every_Boolean_membership_and_independent_Map_value_form_survives_replication(string encoding)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(Compile(encoding));
        var source = master.Set("Row");
        var cases =
            (from members in Enumerable.Range(0, 1 << Members.Length)
             from falseState in Enumerable.Range(0, ByteLengths.Length)
             from trueState in Enumerable.Range(0, ByteLengths.Length)
             select (members, falseState, trueState)).ToArray();
        Assert.Equal(392, cases.Length);

        for (var index = 0; index < cases.Length; index++)
        {
            var (members, falseState, trueState) = cases[index];
            await source.InsertAsync(Record(master, index, members, falseState, trueState));
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        Assert.Equal(master.Position, child.Position);
        var replicas = child.Set("Row");
        var lastPosition = master.Position;
        for (var index = 0; index < cases.Length; index++)
        {
            var (members, falseState, trueState) = cases[index];
            var loaded = AssertBoth(index, members, falseState, trueState);

            var nextFalse = (falseState + 1) % ByteLengths.Length;
            var nextTrue = (trueState + 1) % ByteLengths.Length;
            var replacement = await replicas.ReplaceAsync(Record(child, index, members, nextFalse, nextTrue));
            AssertPosition(replacement.Position);
            await child.WaitForPositionAsync(replacement.Position, Bound);
            var replaced = AssertBoth(index, members, nextFalse, nextTrue);
            Assert.NotEqual(loaded, replaced);

            var editedFalse = (nextFalse + 1) % ByteLengths.Length;
            var edit = replicas.CreateUpdate((long)index);
            var command = editedFalse == 0
                ? edit.MapRemove("Options", false)
                : edit.MapSet<bool, LatticeOwnedValue?>("Options", false, Cell(child.Schema, editedFalse));
            var committed = await edit.CommitAsync(replaced);
            Assert.True(command.Changed);
            AssertPosition(committed.Position);
            Assert.Equal(committed.Position, child.Position);
            Assert.NotEqual(replaced, AssertBoth(index, members, editedFalse, nextTrue));
        }

        // Neither equality-bearing Boolean declaration can produce a lookup.
        // The independently variable Map values can still own nested chunks.
        foreach (var node in new[] { master, child })
        {
            var roots = 0;
            node.ForEachLiveRoot((arena, layout, _, root) =>
            {
                Assert.Equal("Row", layout.EntityType.Name);
                var reader = new RootReader(arena, layout, root);
                var id = (int)reader.GetInteger(layout.EntityType.GetField("Case"));
                var (members, falseState, trueState) = cases[id];
                var finalFalse = (falseState + 2) % ByteLengths.Length;
                var finalTrue = (trueState + 1) % ByteLengths.Length;
                var entries = Inspect("Options");
                var set = Inspect("Members");
                var count = (finalFalse > 0 ? 1U : 0U) + (finalTrue > 0 ? 1U : 0U);
                Assert.Equal(DescriptorKindEnum.Records, entries.Kind);
                Assert.Equal(count, entries.Length);
                Assert.Equal(count == 0, entries.Location == 0);
                AssertEntryBlocks(arena, layout, entries, finalFalse, finalTrue);
                Assert.Equal(DescriptorKindEnum.Inline, set.Kind);
                Assert.Equal((uint)Members.Where((_, bit) => (members & (1 << bit)) != 0).Count() * 2, set.Length);
                roots++;

                ValueDescriptor Inspect(string name) => ResidentDescriptor.Read(
                    arena.Payload(root)[(layout.StoredFieldOf(layout.EntityType.GetField(name)).Offset - BlockHeader.Size)..]);
            });
            Assert.Equal(cases.Length, roots);
            OwnershipAudit.Expect(node);
        }

        void AssertPosition(LatticePosition position)
        {
            Assert.Equal(new LatticePosition(lastPosition.History, lastPosition.Sequence + 1), position);
            Assert.Equal(position, master.Position);
            lastPosition = position;
        }

        LatticeRevision AssertBoth(int index, int members, int falseState, int trueState)
        {
            var primary = AssertNode(source, index, members, falseState, trueState);
            Assert.Equal(primary, AssertNode(replicas, index, members, falseState, trueState));
            return primary;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task A_byte_wide_Map_and_nullable_Set_cover_their_entire_member_domain(bool unsigned) =>
        unsigned
            ? EveryByteAsync("UInt8", Enumerable.Range(0, 256).Select(value => (byte)value).ToArray())
            : EveryByteAsync("Int8", Enumerable.Range(sbyte.MinValue, 256).Select(value => (sbyte)value).ToArray());

    private static async Task EveryByteAsync<T>(string type, T[] domain)
        where T : struct
    {
        var schema = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [{
                "Id": 1, "Name": "Item",
                "Fields": [
                  { "Id": 1, "Name": "Id", "Type": "Int64" },
                  { "Id": 2, "Name": "Keys", "Type": "Map", "Key": { "Type": "{{type}}" }, "Value": { "Type": "Int32" } },
                  { "Id": 3, "Name": "Members", "Type": "Set", "Element": { "Type": "{{type}}", "Nullable": true } }
                ],
                "PrimaryKey": [1]
              }]
            }
            """);
        await using var master = await ReplicationNodes.OpenMasterAsync(schema);
        var source = master.Set("Item");
        var key = domain[^1];
        await source.InsertAsync(new LatticeRecord(source.EntityType)
        {
            ["Id"] = 1L,
            ["Keys"] = domain[..^1].Select((entry, ordinal) => (entry, ordinal)).ToDictionary(pair => pair.entry, pair => pair.ordinal),
            ["Members"] = domain[..^1].Select(entry => (T?)entry).ToHashSet(),
        });
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var replicas = child.Set("Item");
        var revision = Expect(source, full: false);
        Assert.Equal(revision, Expect(replicas, full: false));

        var update = replicas.CreateUpdate(1L);
        var map = update.MapSet("Keys", key, domain.Length - 1);
        var set = update.SetAdd("Members", key);
        var empty = update.SetAdd<T?>("Members", null);
        var duplicate = update.SetAdd("Members", key);
        var written = await update.CommitAsync(revision);
        Assert.True(map.Changed);
        Assert.True(set.Changed);
        Assert.True(empty.Changed);
        Assert.False(duplicate.Changed);
        Assert.Equal(written.Position, master.Position);
        Assert.Equal(written.Position, child.Position);
        var next = Expect(source, full: true);
        Assert.NotEqual(revision, next);
        Assert.Equal(next, Expect(replicas, full: true));

        foreach (var node in new[] { master, child })
        {
            node.ForEachLiveRoot((arena, layout, _, root) =>
            {
                foreach (var name in new[] { "Keys", "Members" })
                {
                    var field = layout.StoredFieldOf(layout.EntityType.GetField(name));
                    var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(field.Offset - BlockHeader.Size)..]);
                    Assert.Equal("Slots/Block", CollectionMatrixProfile.ActualShape(arena, descriptor));
                }
            });

            OwnershipAudit.Expect(node);
        }

        LatticeRevision Expect(ILatticeSet set, bool full)
        {
            Assert.True(set.TryGet(1L, out var record, out var observed));
            var map = record.Get<Dictionary<T, int>>("Keys")!;
            var members = record.Get<HashSet<T?>>("Members")!;
            var count = full ? domain.Length : domain.Length - 1;
            Assert.Equal(count, map.Count);
            Assert.Equal(count + (full ? 1 : 0), members.Count);
            for (var ordinal = 0; ordinal < count; ordinal++)
            {
                Assert.Equal(ordinal, map[domain[ordinal]]);
                Assert.Contains(domain[ordinal], members);
            }

            Assert.Equal(full, map.ContainsKey(key));
            Assert.Equal(full, members.Contains(key));
            Assert.Equal(full, members.Contains(null));
            Assert.True(set.TryView(1L, view =>
            {
                var liveMap = view["Keys"].Map;
                var liveSet = view["Members"].Set;
                Assert.Equal(count, liveMap.Count);
                Assert.Equal(members.Count, liveSet.Count);
                for (var ordinal = 0; ordinal < count; ordinal++)
                {
                    Assert.True(liveMap.TryGetValue(domain[ordinal], out var value));
                    Assert.Equal(ordinal, value.Get<int>());
                    Assert.True(liveSet.Contains(domain[ordinal]));
                }

                Assert.Equal(full, liveMap.TryGetValue(key, out _));
                Assert.Equal(full, liveSet.Contains(key));
                Assert.Equal(full, liveSet.Contains<T?>(null));
                return view.Revision;
            }, out var seen));
            Assert.Equal(observed, seen);
            return observed;
        }
    }

    private static void AssertEntryBlocks(Arena arena, RootLayout layout, in ValueDescriptor entries, int falseState, int trueState)
    {
        if (entries.Length == 0)
        {
            return;
        }

        var options = layout.EntityType.GetField("Options");
        var element = layout.Structure.ElementOf(options.Ordinal);
        Assert.True(options.Declaration.Value!.OwnedType!.TryGetField("Bytes", out var bytes));
        var bytesPlace = element.Place(element.ChildOf(position: 1, bytes));
        var keyPlace = element.Place(position: 0);
        var records = ValueBlocks.AddressOf(entries);
        Assert.Equal(BlockKindEnum.Value, arena.Header(records).Kind);
        var payload = arena.Payload(records);
        for (var ordinal = 0; ordinal < entries.Length; ordinal++)
        {
            var row = payload[(ordinal * element.Size)..];
            var key = row[keyPlace.Offset];
            Assert.InRange(key, (byte)0, (byte)1);
            var state = key == 0 ? falseState : trueState;
            Assert.NotEqual(0, state);
            var content = ResidentDescriptor.Read(row[bytesPlace.Offset..]);
            var kind = state switch
            {
                1 => DescriptorKindEnum.Null,
                2 or 3 => DescriptorKindEnum.Inline,
                4 => DescriptorKindEnum.Block,
                5 or 6 => DescriptorKindEnum.Chunked,
                _ => throw new InvalidOperationException("The model has no such Binary state."),
            };
            Assert.Equal(kind, content.Kind);
            Assert.Equal((uint)ByteLengths[state], content.Length);
            if (kind == DescriptorKindEnum.Chunked)
            {
                var directory = arena.Payload(ValueBlocks.AddressOf(content));
                Assert.Equal((state == 6 ? 2 : 1) * ValueBlocks.DescriptorSize, directory.Length);
            }
        }
    }

    private static LatticeRecord Record(EngineDatabase database, int index, int members, int falseState, int trueState)
    {
        var set = database.Set("Row");
        var options = new Dictionary<bool, LatticeOwnedValue?>();
        if (falseState > 0)
        {
            options.Add(false, Cell(database.Schema, falseState));
        }

        if (trueState > 0)
        {
            options.Add(true, Cell(database.Schema, trueState));
        }

        return new LatticeRecord(set.EntityType)
        {
            ["Id"] = (long)index,
            ["Case"] = index,
            ["Options"] = options,
            ["Members"] = Members.Where((_, bit) => (members & (1 << bit)) != 0).ToHashSet(),
        };
    }

    private static LatticeOwnedValue? Cell(LatticeSchema schema, int state)
    {
        if (state == 1)
        {
            return null;
        }

        Assert.True(schema.TryGetOwnedType("Cell", out var type));
        return new LatticeOwnedValue(type)
        {
            ["Bytes"] = ExpectedData[state],
            ["Shared"] = Shared,
            ["Reference"] = Reference,
            ["Labels"] = new List<string?> { null, "", "é€𐐨" },
        };
    }

    private static readonly byte[][] ExpectedData =
        [.. ByteLengths.Select((length, state) => Enumerable.Range(0, length).Select(index => (byte)(index * 47 + state)).ToArray())];

    private static LatticeRevision AssertNode(ILatticeSet rows, int index, int members, int falseState, int trueState)
    {
        Assert.True(rows.TryGet((long)index, out var record, out var revision));
        Assert.Equal(index, record.Get<int>("Case"));
        var hit = Assert.Single(rows.Index("ByCase").Find(IndexKey.Equal(index)));
        Assert.Equal((long)index, hit.Get<long>("Id"));
        var set = record.Get<HashSet<bool?>>("Members")!;
        var map = record.Get<Dictionary<bool, LatticeOwnedValue?>>("Options")!;
        var expectedMembers = Members.Where((_, bit) => (members & (1 << bit)) != 0).ToHashSet();
        Assert.True(expectedMembers.SetEquals(set));
        var expectedEntries = (falseState > 0 ? 1 : 0) + (trueState > 0 ? 1 : 0);
        Assert.Equal(expectedEntries, map.Count);
        AssertCell(false, falseState);
        AssertCell(true, trueState);

        Assert.True(rows.TryView((long)index, view =>
        {
            var membersView = view["Members"].Set;
            var optionsView = view["Options"].Map;
            Assert.Equal(expectedMembers.Count, membersView.Count);
            Assert.Equal(expectedEntries, optionsView.Count);
            foreach (var member in Members)
            {
                Assert.Equal(expectedMembers.Contains(member), membersView.Contains(member));
            }

            AssertCellView(optionsView, false, falseState);
            AssertCellView(optionsView, true, trueState);
            return view.Revision;
        }, out var viewed));
        Assert.Equal(revision, viewed);
        return revision;

        void AssertCell(bool key, int state)
        {
            Assert.Equal(state != 0, map.TryGetValue(key, out var owned));
            if (state <= 1)
            {
                Assert.Null(owned);
                return;
            }

            Assert.NotNull(owned);
            Assert.Equal(ExpectedData[state], owned.Get<byte[]>("Bytes"));
            Assert.Equal(Shared, owned.Get<string>("Shared"));
            Assert.Equal(Reference, owned.Get<ulong>("Reference"));
            Assert.Equal(new string?[] { null, "", "é€𐐨" }, owned.Get<List<string?>>("Labels"));
        }
    }

    private static void AssertCellView(LatticeMapView map, bool key, int state)
    {
        Assert.Equal(state != 0, map.TryGetValue(key, out var value));
        if (state == 0)
        {
            return;
        }

        Assert.Equal(state == 1, value.IsNull);
        if (state == 1)
        {
            return;
        }

        var owned = value.Owned;
        var bytes = owned["Bytes"];
        var expected = ExpectedData[state];
        Assert.Equal(expected.Length, bytes.ByteLength);
        Assert.Equal(state == 6 ? 2 : 1, bytes.PartCount);
        var offset = 0;
        for (var part = 0; part < bytes.PartCount; part++)
        {
            var slice = bytes.GetPart(part);
            Assert.True(expected.AsSpan(offset, slice.Length).SequenceEqual(slice));
            offset += slice.Length;
        }

        Assert.Equal(expected.Length, offset);
        Assert.Equal(Shared, owned["Shared"].GetString());
        Assert.Equal(Reference, owned["Reference"].Get<ulong>());
        var labels = owned["Labels"].List;
        Assert.Equal(3, labels.Count);
        Assert.True(labels[0].IsNull);
        Assert.Equal("", labels[1].GetString());
        Assert.Equal("é€𐐨", labels[2].GetString());
    }
}
