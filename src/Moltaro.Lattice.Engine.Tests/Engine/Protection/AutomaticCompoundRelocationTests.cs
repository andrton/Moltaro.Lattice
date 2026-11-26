using System.Buffers;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Ownership;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// The budget covers old physical copies regardless of the shape at the next
// source. Every vacancy and source here comes from application commits.
public sealed class AutomaticCompoundRelocationTests(ITestOutputHelper output)
{
    private const int CapSlot = 16_384;
    private const int CapRegionCount = 7;
    private const long TargetKey = 1;
    private const long Stamp = 64;
    private const int Turns = 256;
    private const int TailBytes = 1_000;
    private const int TailSlot = 1_280;
    private const int PackedRecords = 15;
    private const int LookupEntries = 5_800;

    public static TheoryData<LatticeRoleEnum, string> CompoundCases
    {
        get
        {
            var cases = new TheoryData<LatticeRoleEnum, string>();
            foreach (var kind in Enum.GetValues<SourceEnum>().Where(kind => kind != SourceEnum.DirectValue))
            {
                cases.Add(LatticeRoleEnum.Master, kind.ToString());
                cases.Add(LatticeRoleEnum.Child, kind.ToString());
            }

            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(CompoundCases))]
    public Task The_first_eligible_compound_move_waits_for_space_in_the_reader_held_budget(LatticeRoleEnum role, string kind) =>
        RunAsync(role, Enum.Parse<SourceEnum>(kind));

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public Task A_direct_leaf_already_obeys_the_same_reader_held_budget(LatticeRoleEnum role) =>
        RunAsync(role, SourceEnum.DirectValue);

    public static TheoryData<LatticeRoleEnum, string> SourceReports
    {
        get
        {
            var cases = CompoundCases;
            cases.Add(LatticeRoleEnum.Master, nameof(SourceEnum.DirectValue));
            cases.Add(LatticeRoleEnum.Child, nameof(SourceEnum.DirectValue));
            return cases;
        }
    }

    [Theory(Skip = AutomaticRelocationTests.SourceReportPending)]
    [MemberData(nameof(SourceReports))]
    public Task A_worker_reports_the_verified_native_source_kind_and_owner_edge(LatticeRoleEnum role, string kind) =>
        RunAsync(role, Enum.Parse<SourceEnum>(kind), reportSource: true);

    private async Task RunAsync(LatticeRoleEnum role, SourceEnum kind, bool reportSource = false)
    {
        var input = InputFor(kind);
        var allocator = new CountingNativeAllocator();
        var samples = new List<MaintenanceTurnSample>();
        await using var master = await ReplicationNodes.OpenMasterAsync(SchemaFor(input),
            Inputs(role == LatticeRoleEnum.Master ? allocator : null, role == LatticeRoleEnum.Master ? samples.Add : null));

        await FillAsync(master, "Cap", CapRegionCount * Arena.MaxBlockSize / CapSlot);
        await FillAsync(master, "Hole", Arena.MaxBlockSize / input.SlotBytes);
        var writer = master.Set("Target");
        var target = new LatticeRecord(writer.EntityType)
        {
            ["Id"] = TargetKey,
            ["Stamp"] = Stamp,
            ["Pad"] = -1L,
            ["Values"] = input.Value,
        };
        if (input.Label is not null)
        {
            target["Label"] = input.Label;
        }

        Assert.True((await writer.InsertAsync(target).AsTask().WaitAsync(Bound)).Changed);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            Inputs(role == LatticeRoleEnum.Child ? allocator : null, role == LatticeRoleEnum.Child ? samples.Add : null));
        var node = role == LatticeRoleEnum.Master ? master : child;
        var items = node.Set("Target");
        var before = Capture(node, input);
        var (copies, destination) = await FreeHolesAsync(master, child, node, input, before);
        var revision = ExpectRecord(items, input);
        Assert.Equal(revision, ExpectRecord(writer, input));
        Assert.Equal(revision, ExpectRecord(child.Set("Target"), input));
        var bytes = Export(items, before);
        var position = node.Position;
        var requests = allocator.Requests;
        var returned = allocator.Returned;
        output.WriteLine($"{role} {kind}: root {before.Root}; candidate {before.Source}, destination {destination}, class {input.SlotBytes}; cap {copies.Sum(copy => copy.SlotBytes)} bytes.");

        using var holdBudget = new ViewerPause();
        var budgetRead = Hold(items, input, revision, holdBudget);
        await holdBudget.EnteredAsync(budgetRead);
        foreach (var planned in copies)
        {
            Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node,
                parts => RootRelocation.TryMove(parts, planned.Handle, planned.Root, CapVacancy(parts.Arena, planned), out _)));
        }

        var pinned = node.Counters.Arena;
        Assert.Equal(Arena.MaxBlockSize, pinned.RetiredBytes);
        Assert.Equal(0, pinned.PreparedBytes - node.ReservedNodeBytes());
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returned, allocator.Returned);
        await ExpectVacancyAsync(node, before.Source, destination);
        var witnesses = RootsOf(node, "Hole").Concat(RootsOf(node, "Cap")).Append(before)
            .Select(root => root.Root).ToArray();
        var vacantBytes = AutomaticRelocationTests.SnapshotEarlierVacancies(node, allocator, before.Source, input.SlotBytes, witnesses);
        Assert.Contains(destination, vacantBytes.Keys);
        for (var count = 0; count < 3; count++)
        {
            var quiet = RunTurn(node, samples);
            Assert.Equal((default(ArenaAddress), default(ArenaAddress), 0), (quiet.Source, quiet.Destination, quiet.SlotBytes));
            Assert.Equal(Arena.MaxBlockSize, quiet.RelocationBytes);
            Assert.Equal(0, quiet.ReclamationWork);
            if (reportSource)
            {
                Assert.Equal((default(BlockKindEnum), (ushort)0, default(DescriptorKindEnum)),
                    (quiet.SourceKind, quiet.SourceFlags, quiet.SourceDescriptorKind));
            }

            Assert.Equal(pinned, node.Counters.Arena);
            var after = AutomaticRelocationTests.SnapshotEarlierVacancies(node, allocator, before.Source, input.SlotBytes, witnesses);
            Assert.Equal(vacantBytes.Keys, after.Keys);
            Assert.All(vacantBytes, slot => Assert.Equal(slot.Value, after[slot.Key]));
            Assert.Equal(before, Capture(node, input));
            Assert.Equal(requests, allocator.Requests);
            Assert.Equal(returned, allocator.Returned);
        }

        Assert.Equal(position, node.Position);
        holdBudget.Release();
        await budgetRead.WaitAsync(Bound);
        node.DrainRetired();
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        foreach (var copy in copies)
        {
            ExpectFreed(copy.Arena, copy.Root);
        }

        using var holdTarget = new ViewerPause();
        var oldRead = Hold(items, input, revision, holdTarget);
        await holdTarget.EnteredAsync(oldRead);
        var header = reportSource
            ? await MaintenanceStep.RunAfterAdmissionAsync(node, parts => parts.Arena.Header(before.Source))
            : default;
        var first = FirstMove(node, samples);
        Assert.Equal(before.Source, first.Source);
        Assert.Equal(destination, first.Destination);
        Assert.Equal(input.SlotBytes, first.SlotBytes);
        Assert.Equal(input.SlotBytes, first.RelocationBytes);
        if (reportSource)
        {
            var expected = (header.Kind, header.Flags, ExpectedSourceDescriptor(kind));
            var reported = (first.SourceKind, first.SourceFlags, first.SourceDescriptorKind);
            output.WriteLine($"Source report: expected {expected}, observed {reported}.");
            Assert.Equal(expected, reported);
        }

        Assert.Equal(position, node.Position);
        Assert.Equal(requests, allocator.Requests);
        var moved = Capture(node, input);
        Assert.Equal(before.Handle, moved.Handle);
        Assert.Equal(destination, moved.Source);
        Assert.Equal(revision, ExpectRecord(items, input));
        Assert.Equal(bytes, Export(items, moved));
        Assert.Equal(before.Descriptor, moved.Descriptor);
        if (kind == SourceEnum.DictionaryCollectionRoot)
        {
            Assert.Equal(DescriptorAt(before, "Label"), DescriptorAt(moved, "Label"));
            Assert.Equal(DescriptorAt(before, "Values"), DescriptorAt(moved, "Values"));
        }

        ExpectText(node, input);
        node.DrainRetired();
        Assert.Equal(before.Handle, before.Arena.Header(before.Source).Root);

        var deleted = await items.DeleteAsync(TargetKey, revision).AsTask().WaitAsync(Bound);
        Assert.True(deleted.Changed);
        await child.WaitForPositionAsync(deleted.Position, Bound);
        Assert.Equal(new LatticePosition(position.History, position.Sequence + 1), master.Position);
        Assert.Equal(master.Position, child.Position);
        Assert.Null(writer.Get(TargetKey));
        Assert.Null(child.Set("Target").Get(TargetKey));
        Assert.Empty(items.Index("ByStamp").Find(IndexKey.All));
        node.DrainRetired();
        Assert.Equal(before.Handle, before.Arena.Header(before.Source).Root);
        ExpectText(node, input);

        holdTarget.Release();
        await oldRead.WaitAsync(Bound);
        node.DrainRetired();
        foreach (var address in new[] { before.Source, moved.Root, moved.Source }.Distinct())
        {
            ExpectFreed(before.Arena, address);
        }

        Assert.Empty(node.TextReferences());
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        OwnershipAudit.Expect(master);
        OwnershipAudit.Expect(child);
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    private static async Task<(LocatedRoot[] Copies, ArenaAddress Hole)> FreeHolesAsync(EngineDatabase master, EngineDatabase child,
        EngineDatabase node, Input input, LocatedRoot target)
    {
        var caps = RootsOf(node, "Cap").GroupBy(root => root.Root.Segment).OrderBy(group => group.Key)
            .Select(group => group.OrderBy(root => root.Root.Offset).ToArray()).ToArray();
        Assert.Equal(CapRegionCount, caps.Length);
        Assert.All(caps, group =>
        {
            Assert.Equal(Arena.MaxBlockSize / CapSlot, group.Length);
            Assert.All(group, root => Assert.Equal(CapSlot, root.SlotBytes));
        });

        var copies = caps.Skip(3).Select(group => group[0]).ToArray();
        Assert.Equal(Arena.MaxBlockSize, copies.Sum(copy => copy.SlotBytes));
        var hole = RootsOf(node, "Hole").OrderBy(root => root.Root.Offset).ToArray();
        Assert.Equal(Arena.MaxBlockSize / input.SlotBytes, hole.Length);
        Assert.Single(hole.Select(root => root.Root.Segment).Distinct());
        Assert.All(hole, root => Assert.Equal(input.SlotBytes, root.SlotBytes));
        var destination = hole[^1];
        var batch = master.CreateWriteBatch();
        foreach (var retired in caps.Take(3).SelectMany(group => group.Skip(2)).Concat(caps.Skip(3).SelectMany(group => group.Skip(1))))
        {
            batch.Delete("Cap", retired.Key);
        }

        batch.Delete("Hole", destination.Key);
        var result = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(result.Position, Bound);
        node.DrainRetired();
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(target, Capture(node, input));
        ExpectFreed(target.Arena, destination.Root);
        await ExpectVacancyAsync(node, target.Source, destination.Root);
        foreach (var copy in copies)
        {
            Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                var region = CapVacancy(parts.Arena, copy);
                return parts.Arena.TryFindRelocationDestination(copy.Source, ref region, out _);
            }));
        }

        return (copies, destination.Root);
    }

    private static long CapVacancy(Arena arena, LocatedRoot copy)
    {
        var sizeClass = SizeClasses.IndexFor(copy.SlotBytes - BlockHeader.Size);
        for (var coordinate = 0L; coordinate < arena.EarlierRelocationExtent(copy.Source); coordinate++)
        {
            var vacancy = arena.RelocationVacancyAt(coordinate, sizeClass);
            if (vacancy != 0)
            {
                return vacancy;
            }
        }

        Assert.Fail("No earlier vacancy is available for the budget setup.");
        return 0;
    }

    private static async Task ExpectVacancyAsync(EngineDatabase node, ArenaAddress source, ArenaAddress destination)
    {
        Assert.True(source.Segment > destination.Segment);
        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            long region = 0;
            return parts.Arena.TryFindRelocationDestination(source, ref region, out _);
        }));
    }

    private static DescriptorKindEnum ExpectedSourceDescriptor(SourceEnum source) => source switch
    {
        SourceEnum.InlineNullRoot or SourceEnum.DictionaryCollectionRoot or SourceEnum.LookupSlots => default,
        SourceEnum.RawDirectory => DescriptorKindEnum.Chunked,
        SourceEnum.RecordsBlock or SourceEnum.RecordsDirectory => DescriptorKindEnum.Records,
        SourceEnum.Lookup => DescriptorKindEnum.Lookup,
        _ => DescriptorKindEnum.Block,
    };

    private static MaintenanceTurnSample FirstMove(EngineDatabase node, List<MaintenanceTurnSample> samples)
    {
        for (var count = 0; count < Turns; count++)
        {
            var sample = RunTurn(node, samples);
            if (sample.SlotBytes != 0)
            {
                return sample;
            }
        }

        Assert.Fail("The worker did not move the eligible source in a bounded sweep.");
        return default;
    }

    private static MaintenanceTurnSample RunTurn(EngineDatabase node, List<MaintenanceTurnSample> samples)
    {
        Assert.True(SpinWait.SpinUntil(node.TryRunMaintenanceTurn, Bound));
        var sample = samples[^1];
        Assert.True(sample.Admitted);
        Assert.InRange(sample.SelectionWork, 0, AutomaticMaintenance.SelectionWorkLimit);
        Assert.InRange(sample.ReclamationWork, 0, AutomaticMaintenance.ReclamationWorkLimit);
        return sample;
    }

    private static async Task FillAsync(EngineDatabase master, string type, int count)
    {
        var set = master.Set(type);
        var batch = master.CreateWriteBatch();
        for (var key = 1L; key <= count; key++)
        {
            batch.Insert(new LatticeRecord(set.EntityType) { ["Id"] = key });
        }

        await batch.CommitAsync().AsTask().WaitAsync(Bound);
    }

    private static List<LocatedRoot> RootsOf(EngineDatabase node, string type)
    {
        var found = new List<LocatedRoot>();
        node.ForEachLiveRoot((arena, layout, handle, address) =>
        {
            if (layout.EntityType.Name == type)
            {
                var key = new RootReader(arena, layout, address).GetInteger(layout.EntityType.Key);
                var slot = Arena.BlockSizeFor((int)arena.Header(address).PayloadLength);
                found.Add(new LocatedRoot(arena, layout, handle, address, address, key, slot, default));
            }
        });

        return found;
    }

    private static LocatedRoot Capture(EngineDatabase node, Input input)
    {
        var root = Assert.Single(RootsOf(node, "Target"));
        var value = DescriptorAt(root, "Values");
        var address = ValueBlocks.AddressOf(value);
        var kind = input.Kind;
        var source = kind switch
        {
            SourceEnum.InlineNullRoot or SourceEnum.DictionaryCollectionRoot => root.Root,
            SourceEnum.RawTail or SourceEnum.RecordsTail => ValueBlocks.AddressOf(
                ResidentDescriptor.ReadAligned(root.Arena.Payload(address)[ValueBlocks.DescriptorSize..])),
            SourceEnum.NestedValue => ValueBlocks.AddressOf(ResidentDescriptor.Read(root.Arena.Payload(address)[ValueBlocks.DescriptorSize..])),
            SourceEnum.LookupSlots => CollectionLookup.ReadChunk(CollectionLookup.Chunks(root.Arena, address), 1),
            _ => address,
        };
        var header = root.Arena.Header(source);
        var slot = Arena.BlockSizeFor((int)header.PayloadLength);
        Assert.Equal(input.SlotBytes, slot);
        var expected = kind switch
        {
            SourceEnum.InlineNullRoot or SourceEnum.DictionaryCollectionRoot => (BlockKindEnum.Root, (ushort)0),
            SourceEnum.RawDirectory or SourceEnum.RecordsDirectory => (BlockKindEnum.ChunkDirectory, (ushort)0),
            SourceEnum.RawTail => (BlockKindEnum.Chunk, (ushort)0),
            SourceEnum.RecordsBlock => (BlockKindEnum.Value, BlockHeader.RecordBlockFlag),
            SourceEnum.RecordsTail => (BlockKindEnum.Chunk, BlockHeader.RecordBlockFlag),
            SourceEnum.Lookup => (BlockKindEnum.Lookup, (ushort)0),
            SourceEnum.LookupSlots => (BlockKindEnum.Chunk, BlockHeader.LookupSlotsFlag),
            _ => (BlockKindEnum.Value, (ushort)0),
        };
        Assert.Equal(expected, (header.Kind, header.Flags));
        Assert.Equal(root.Handle, header.Root);
        if (kind == SourceEnum.InlineNullRoot)
        {
            Assert.Equal(DescriptorKindEnum.Null, value.Kind);
            Assert.Equal(DescriptorKindEnum.Inline, DescriptorAt(root, "Label").Kind);
        }
        else if (kind == SourceEnum.DictionaryCollectionRoot)
        {
            Assert.Equal(DescriptorKindEnum.Text, DescriptorAt(root, "Label").Kind);
            Assert.Equal(DescriptorKindEnum.Records, value.Kind);
            Assert.Equal(root.Root, root.Arena.Header(address).Parent);
        }

        return root with { Source = source, SlotBytes = slot, Descriptor = (value.Kind, value.Length) };
    }

    private static ValueDescriptor DescriptorAt(LocatedRoot root, string field)
    {
        var offset = root.Layout.StoredFieldOf(root.Layout.EntityType.GetField(field)).Offset - BlockHeader.Size;
        return ResidentDescriptor.Read(root.Arena.Payload(root.Root)[offset..]);
    }

    private static LatticeRevision ExpectRecord(ILatticeSet items, Input input)
    {
        Assert.True(items.TryGet(TargetKey, out var record, out var revision));
        ExpectOwned(record, input);
        ExpectOwned(Assert.Single(items.Index("ByStamp").Find(IndexKey.All)), input);
        return revision;
    }

    private static void ExpectOwned(LatticeRecord record, Input input)
    {
        Assert.Equal(TargetKey, record.Get<long>("Id"));
        Assert.Equal(Stamp, record.Get<long>("Stamp"));
        Assert.Equal(-1L, record.Get<long>("Pad"));
        if (input.Label is not null)
        {
            Assert.Equal(input.Label, record.Get<string>("Label"));
        }

        switch (input.Value)
        {
            case byte[] bytes:
                Assert.Equal(bytes, record.Get<byte[]>("Values"));
                break;
            case List<string> strings:
                Assert.Equal(strings, record.Get<List<string>>("Values"));
                break;
            case List<byte[]> parts:
                var values = record.Get<List<byte[]>>("Values")!;
                Assert.Equal(parts.Count, values.Count);
                for (var index = 0; index < parts.Count; index++)
                {
                    Assert.Equal(parts[index], values[index]);
                }

                break;
            case Dictionary<int, int> map:
                Assert.Equal(map.OrderBy(entry => entry.Key), record.Get<Dictionary<int, int>>("Values")!.OrderBy(entry => entry.Key));
                break;
            default:
                Assert.Null(record["Values"]);
                break;
        }
    }

    private static Task Hold(ILatticeSet items, Input input, LatticeRevision revision, ViewerPause pause) =>
        OwnThread.Run(() =>
        {
            Assert.True(items.TryView(TargetKey, view =>
            {
                ExpectView(view, input, revision);
                pause.Hold();
                ExpectView(view, input, revision);
                return view.Revision;
            }, out var observed));
            Assert.Equal(revision, observed);
        });

    private static void ExpectView(LatticeRecordView view, Input input, LatticeRevision revision)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(TargetKey, view["Id"].Get<long>());
        Assert.Equal(Stamp, view["Stamp"].Get<long>());
        if (input.Label is not null)
        {
            Assert.Equal(input.Label, view["Label"].GetString());
        }

        var value = view["Values"];
        switch (input.Value)
        {
            case byte[] bytes:
                ExpectBytes(value, bytes);
                break;
            case List<string> strings:
                Assert.Equal(strings.Count, value.List.Count);
                for (var index = 0; index < strings.Count; index++)
                {
                    Assert.Equal(strings[index], value.List[index].GetString());
                }

                break;
            case List<byte[]> parts:
                Assert.Equal(parts.Count, value.List.Count);
                for (var index = 0; index < parts.Count; index++)
                {
                    ExpectBytes(value.List[index], parts[index]);
                }

                break;
            case Dictionary<int, int> map:
                Assert.Equal(map.Count, value.Map.Count);
                foreach (var (key, expected) in map)
                {
                    Assert.True(value.Map.TryGetValue(key, out var observed));
                    Assert.Equal(expected, observed.Get<int>());
                }

                Assert.False(value.Map.TryGetValue(-1, out _));
                break;
            default:
                Assert.True(value.IsNull);
                break;
        }
    }

    private static void ExpectBytes(LatticeValueView value, byte[] expected)
    {
        var offset = 0;
        for (var index = 0; index < value.PartCount; index++)
        {
            var bytes = value.GetPart(index);
            Assert.True(bytes.SequenceEqual(expected.AsSpan(offset, bytes.Length)));
            offset += bytes.Length;
        }

        Assert.Equal(expected.Length, offset);
    }

    private static byte[] Export(ILatticeSet items, LocatedRoot root)
    {
        Assert.True(items.TryView(TargetKey, _ =>
        {
            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, root.Arena, root.Layout, root.Root);
            Assert.Equal(output.WrittenCount, BlockExport.Length(root.Arena, root.Layout, root.Root));
            return output.WrittenSpan.ToArray();
        }, out var bytes));
        return bytes;
    }

    private static void ExpectText(EngineDatabase node, Input input)
    {
        if (input.Kind == SourceEnum.DictionaryCollectionRoot)
        {
            var text = Assert.Single(node.TextReferences());
            Assert.Equal(1, text.References);
            Assert.Equal(Encoding.UTF8.GetBytes(input.Label!), text.Text);
        }
        else
        {
            Assert.Empty(node.TextReferences());
        }
    }

    private static void ExpectFreed(Arena arena, ArenaAddress address) =>
        Assert.Throws<ArgumentException>("address", () => _ = arena.Header(address));

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator, Action<MaintenanceTurnSample>? observe) => new()
    {
        Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 64L * Arena.MaxBlockSize },
        Allocator = allocator,
        DisableBackgroundMaintenance = true,
        ObserveMaintenanceTurn = observe,
        IndexSeed = 39,
    };

    private static LatticeSchema SchemaFor(Input input) => LatticeSchema.Compile($$"""
        {"StringEncoding":"Utf8","EntityTypes":[
          {{FixedEntity(1, "Cap", CapSlot)}},{{FixedEntity(2, "Hole", input.SlotBytes)}},
          {"Id":3,"Name":"Target","Fields":[
            {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Stamp","Type":"Int64"},{"Id":3,"Name":"Pad","Type":"Int64"},
            {"Id":4,"Name":"Values",{{input.Declaration}}}{{input.LabelDeclaration}}],
           "PrimaryKey":[1],"Indexes":[{"Id":1,"Name":"ByStamp","Fields":[2]}]}]}
        """);

    private static string FixedEntity(int id, string name, int slot)
    {
        var pads = Enumerable.Range(0, 1_024).First(count => Arena.BlockSizeFor(sizeof(long) + count * 16 + (count + 7) / 8) == slot);
        var fields = Enumerable.Range(2, pads).Select(field => $$""",{"Id":{{field}},"Name":"Pad{{field}}","Type":"Guid","Nullable":true}""");
        return $$"""
            {"Id":{{id}},"Name":"{{name}}","Fields":[{"Id":1,"Name":"Id","Type":"Int64"}{{string.Concat(fields)}}],"PrimaryKey":[1]}
            """;
    }

    private static Input InputFor(SourceEnum kind)
    {
        const string binary = """ "Type":"Binary" """;
        const string list = """ "Type":"List","Element":{"Type":"String"} """;
        const string map = """ "Type":"Map","Key":{"Type":"Int32"},"Value":{"Type":"Int32"} """;
        const int viaRecords = ValueBlocks.ChunkPayload / (ValueBlocks.DescriptorSize + ValueBlocks.InlineLimit) + PackedRecords;

        return kind switch
        {
            SourceEnum.InlineNullRoot => new(kind, 96, """ "Type":"Binary","Nullable":true """, null, "tiny",
                """,{"Id":5,"Name":"Label","Type":"String"}"""),
            SourceEnum.DictionaryCollectionRoot => new(kind, 96, list, Strings(PackedRecords), new string('d', 120),
                """,{"Id":5,"Name":"Label","Type":"String","StringStorage":"Dictionary"}"""),
            SourceEnum.DirectValue => new(kind, TailSlot, binary, Data(TailBytes)),
            SourceEnum.NestedValue => new(kind, TailSlot, """ "Type":"List","Element":{"Type":"Binary"} """,
                (List<byte[]>)[[1, 2], Data(TailBytes), [9]]),
            SourceEnum.RawDirectory => new(kind, 64, binary, Data(ValueBlocks.ChunkPayload + TailBytes)),
            SourceEnum.RawTail => new(kind, TailSlot, binary, Data(ValueBlocks.ChunkPayload + TailBytes)),
            SourceEnum.RecordsBlock => new(kind, TailSlot, list, Strings(PackedRecords)),
            SourceEnum.RecordsDirectory => new(kind, 64, list, Strings(viaRecords)),
            SourceEnum.RecordsTail => new(kind, TailSlot, list, Strings(viaRecords)),
            SourceEnum.Lookup => new(kind, 64, map, Entries()),
            SourceEnum.LookupSlots => new(kind, 816, map, Entries()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static Dictionary<int, int> Entries() => Enumerable.Range(0, LookupEntries).ToDictionary(key => key, key => key * -11 + 3);

    private static List<string> Strings(int count) => Enumerable.Range(0, count).Select(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, 'r')).ToList();

    private static byte[] Data(int length) => Enumerable.Range(0, length).Select(index => (byte)(index * 17 + 7)).ToArray();

    private sealed record Input(SourceEnum Kind, int SlotBytes, string Declaration, object? Value, string? Label = null, string LabelDeclaration = "");

    private sealed record LocatedRoot(Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Root,
        ArenaAddress Source, long Key, int SlotBytes, (DescriptorKindEnum Kind, uint Length) Descriptor);

    private enum SourceEnum
    {
        InlineNullRoot,
        DictionaryCollectionRoot,
        DirectValue,
        NestedValue,
        RawDirectory,
        RawTail,
        RecordsBlock,
        RecordsDirectory,
        RecordsTail,
        Lookup,
        LookupSlots,
    }
}
