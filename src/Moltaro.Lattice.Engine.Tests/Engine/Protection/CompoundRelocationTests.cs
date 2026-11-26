using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Access.Views;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Moving an owner repairs only its current owned edges. Old physical copies
// remain readable but never dispose the children that the current owner adopted.
// See docs/design/arena-protocol.md, "Deferred reclamation".
public sealed class CompoundRelocationTests
{
    private const int BigSlotBytes = 8_192;
    private const int DirectorySlotBytes = 64;
    private const int BigFillers = Arena.MaxBlockSize / BigSlotBytes;
    private const int SmallFillers = Arena.MaxBlockSize / DirectorySlotBytes;
    private const int FullChunkBytes = 65_504;
    private const int TailBytes = 4_496;
    private const long TargetKey = 1;
    private const sbyte OriginalCode = 27;
    private const sbyte ChangedCode = 28;

    private static readonly LatticeSchema Schema = MakeSchema();

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_raw_value_has_a_sparse_root_directory_and_tail_but_its_full_chunk_is_ineligible(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var staged = await StageAsync(master, child);
        var node = child ?? master;
        await ExpectGeometryAsync(node, staged);
        var revision = ExpectRecord(node.Set("Item"), OriginalCode);
        Assert.Equal(revision, ExpectRecord(master.Set("Item"), OriginalCode));
        Assert.Equal(ExpectedBody(staged.Source, OriginalCode), Export(node.Set("Item"), staged.Source));
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master, true, true)]
    [InlineData(LatticeRoleEnum.Child, true, true)]
    [InlineData(LatticeRoleEnum.Master, false, true)]
    [InlineData(LatticeRoleEnum.Child, false, true)]
    [InlineData(LatticeRoleEnum.Master, false, false)]
    [InlineData(LatticeRoleEnum.Child, false, false)]
    public async Task Old_root_and_chunk_aliases_stay_readable_while_current_edges_move(LatticeRoleEnum role, bool moveRoot, bool moveDirectory)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var stage = await StageAsync(master, child);
        var node = child ?? master;
        await ExpectGeometryAsync(node, stage);

        var original = stage.Source;
        var arena = original.Arena;
        var originalRootBytes = arena.Payload(original.Root).ToArray();
        var originalDirectoryBytes = arena.Payload(original.Directory).ToArray();
        var originalTailHeader = arena.Header(original.Tail);
        var originalRootHeader = arena.Header(original.Root);
        var data = Data();
        var items = node.Set("Item");
        var revision = ExpectRecord(items, OriginalCode);
        var position = node.Position;
        var canonical = ExpectedBody(original, OriginalCode);
        Assert.Equal(canonical, Export(items, original));
        var detached = new List<ArenaAddress>();
        Chain? finishedMoving = null;

        using var originalPause = new ViewerPause();
        var originalRead = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(TargetKey, view =>
            {
                ExpectView(view, original, revision, data);
                var first = view["Data"].GetPart(0);
                var oldTail = view["Data"].GetPart(1);
                var oldDirectory = arena.Payload(original.Directory);
                originalPause.Hold();
                if (finishedMoving is { } current)
                {
                    ExpectBorrowed(first, arena, original.First, data.AsSpan(0, FullChunkBytes));
                    ExpectBorrowed(oldTail, arena, original.Tail, data.AsSpan(FullChunkBytes));
                    if (moveDirectory)
                    {
                        Assert.True(oldDirectory.SequenceEqual(originalDirectoryBytes));
                        Assert.Equal(original.Tail, ReadChunk(oldDirectory, 1, TailBytes));
                    }

                    ExpectView(view, moveRoot ? original : current, revision, data);
                }

                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });
        await originalPause.EnteredAsync(originalRead);

        using var exportPause = new ViewerPause();
        var output = new PausedExportBuffer(canonical.Length, arena.Payload(original.Root), exportPause);
        var inFlightExport = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(TargetKey, _ =>
            {
                BlockExport.Write(output, arena, original.Layout, original.Root);
                return true;
            }, out _));
        });
        await exportPause.EnteredAsync(inFlightExport);

        var current = original;
        var initial = node.Counters.Arena;
        var requests = allocator.Requests;
        var returns = allocator.Returned;
        var retiredBytes = initial.RetiredBytes;
        if (moveRoot)
        {
            Assert.True(await MoveRootAsync(node, current, stage.BigAnchor));
            current = Capture(node);
            detached.Add(original.Root);
            retiredBytes += BigSlotBytes;
            Assert.Contains(current.Root, stage.BigHoles);
            Assert.Equal(original with { Root = current.Root }, current);
            Assert.Equal(originalRootHeader, arena.Header(original.Root));
            Assert.Equal(originalRootBytes, arena.Payload(original.Root).ToArray());
            Assert.Equal(initial with { RetiredBytes = retiredBytes }, node.Counters.Arena);
            Assert.Equal(canonical, Export(items, current));
        }

        if (moveDirectory)
        {
            var expectedOwner = current;
            var rootBeforeDirectoryMove = arena.Payload(current.Root).ToArray();
            var directoryHeader = arena.Header(current.Directory);
            Assert.True(await MoveCompoundAsync(node, current, current.Directory, stage.SmallAnchor));
            current = Capture(node);
            detached.Add(original.Directory);
            retiredBytes += DirectorySlotBytes;
            Assert.Equal(stage.DirectoryHole, current.Directory);
            Assert.Equal(expectedOwner with { Directory = current.Directory }, current);
            Assert.Equal(directoryHeader, arena.Header(original.Directory));
            Assert.Equal(originalDirectoryBytes, arena.Payload(original.Directory).ToArray());
            ExpectOnlyLocationChanged(rootBeforeDirectoryMove, arena.Payload(current.Root), current.DataOffset, current.Directory);
            Assert.Equal(initial with { RetiredBytes = retiredBytes }, node.Counters.Arena);
            Assert.Equal(canonical, Export(items, current));
        }

        Assert.False(await MoveCompoundAsync(node, current, current.First, stage.BigAnchor));
        var ownerBeforeTail = current;
        var directoryBeforeTailMove = arena.Payload(current.Directory).ToArray();
        var tailHeader = arena.Header(current.Tail);
        Assert.True(await MoveCompoundAsync(node, current, current.Tail, stage.BigAnchor));
        current = Capture(node);
        detached.Add(original.Tail);
        retiredBytes += BigSlotBytes;
        Assert.Contains(current.Tail, stage.BigHoles);
        Assert.Equal(ownerBeforeTail with { Tail = current.Tail }, current);
        ExpectOnlyLocationChanged(directoryBeforeTailMove, arena.Payload(current.Directory),
            BlockHeader.Size + ValueBlocks.DescriptorSize, current.Tail);
        Assert.Equal(tailHeader, arena.Header(original.Tail));
        Assert.Equal(originalTailHeader with { Parent = ownerBeforeTail.Directory }, tailHeader);
        Assert.Equal(data.AsSpan(FullChunkBytes).ToArray(), arena.Payload(original.Tail).ToArray());
        Assert.Equal(data.AsSpan(FullChunkBytes).ToArray(), arena.Payload(current.Tail).ToArray());
        Assert.Equal(initial with { RetiredBytes = retiredBytes }, node.Counters.Arena);
        Assert.Equal(position, node.Position);
        Assert.Equal(revision, ExpectRecord(items, OriginalCode));
        Assert.Equal(revision, ExpectRecord(master.Set("Item"), OriginalCode));
        Assert.Equal(canonical, Export(items, current));
        Assert.Equal(requests, allocator.Requests);
        Assert.Equal(returns, allocator.Returned);
        finishedMoving = current;

        exportPause.Release();
        await inFlightExport.WaitAsync(Bound);
        Assert.Equal(canonical, output.Written.ToArray());

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!,
            new EngineTestInputs { DisableBackgroundMaintenance = true });
        Assert.Equal(position, loaded.Position);
        var loadedItems = loaded.Set("Item");
        Assert.Equal(revision, ExpectRecord(loadedItems, OriginalCode));
        Assert.Equal(canonical, Export(loadedItems, Capture(loaded)));

        // A read entering after this horizon can reach only the current chain.
        // Keep it active while ordinary writes retire the logical owners.
        node.DrainRetired();
        Assert.Equal(returns, allocator.Returned);
        using var currentPause = new ViewerPause();
        var justMoved = current;
        var currentRead = OwnThread.Run(() =>
        {
            Assert.True(items.TryView(TargetKey, view =>
            {
                ExpectView(view, justMoved, revision, data);
                currentPause.Hold();
                ExpectView(view, justMoved, revision, data);
                return view.Revision;
            }, out var seen));
            Assert.Equal(revision, seen);
        });
        await currentPause.EnteredAsync(currentRead);

        var change = items.CreateUpdate(TargetKey);
        change.Assign("Code", ChangedCode);
        var update = await change.CommitAsync(revision).AsTask().WaitAsync(Bound);
        Assert.True(update.Changed);
        Assert.Equal(update.Position, master.Position);
        Assert.Equal(update.Position, node.Position);
        Assert.Equal(position.Sequence + 1, update.Position.Sequence);
        var updated = Capture(node);
        Assert.NotEqual(current.Root, updated.Root);
        Assert.Equal(current with { Root = updated.Root }, updated);
        Assert.Equal(updated.Root, arena.Header(updated.Directory).Parent);
        var updatedRevision = ExpectRecord(items, ChangedCode);
        Assert.NotEqual(revision, updatedRevision);
        Assert.Equal(updatedRevision, ExpectRecord(master.Set("Item"), ChangedCode));
        var canonicalUpdate = ExpectedBody(updated, ChangedCode);
        Assert.Equal(canonicalUpdate, Export(items, updated));
        await FollowAsync(master, loaded);
        Assert.Equal(updatedRevision, ExpectRecord(loadedItems, ChangedCode));
        Assert.Equal(canonicalUpdate, Export(loadedItems, Capture(loaded)));

        var deleted = await items.DeleteAsync(TargetKey).AsTask().WaitAsync(Bound);
        Assert.True(deleted.Changed);
        Assert.Equal(deleted.Position, master.Position);
        Assert.Equal(deleted.Position, node.Position);
        Assert.Equal(update.Position.Sequence + 1, deleted.Position.Sequence);
        await FollowAsync(master, loaded);
        ExpectAbsent(items);
        ExpectAbsent(master.Set("Item"));
        ExpectAbsent(loadedItems);

        var expected = detached.Select(address => (RetiredKindEnum.RelocatedBlock, address))
            .Append((RetiredKindEnum.Root, current.Root))
            .Append((RetiredKindEnum.DeletedRoot, updated.Root)).ToArray();
        Assert.Equal(expected, await RetirementOfAsync(node));
        node.DrainRetired();
        foreach (var address in detached)
        {
            _ = arena.Header(address);
        }

        var beforeSafeDrain = allocator.Returned;
        var segmentsBeforeSafeDrain = node.Counters.Arena.Segments;
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
        originalPause.Release();
        await originalRead.WaitAsync(Bound);
        node.DrainRetired();
        foreach (var address in detached)
        {
            ExpectFreed(arena, address);
        }

        // The directory occupied its own region. The root and tail share another
        // region, which empties in this drain only when the root also moved.
        var expectedReturns = (moveDirectory ? 1 : 0) + (moveRoot ? 1 : 0);
        Assert.Equal(expectedReturns, allocator.Returned - beforeSafeDrain);
        Assert.Equal(expectedReturns, segmentsBeforeSafeDrain - node.Counters.Arena.Segments);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);

        _ = arena.Header(current.Root);
        _ = arena.Header(updated.Directory);
        _ = arena.Header(updated.Tail);
        Assert.Equal(expected[^2..], await RetirementOfAsync(node));

        currentPause.Release();
        await currentRead.WaitAsync(Bound);
        node.DrainRetired();
        foreach (var address in new[] { current.Root, updated.Root, updated.Directory, updated.First, updated.Tail })
        {
            ExpectFreed(arena, address);
        }

        Assert.Empty(await RetirementOfAsync(node));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        ExpectAbsent(items);
        await node.CloseAsync().AsTask().WaitAsync(Bound);
        allocator.ExpectEverythingReturned();
    }


    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_directory_with_an_inconsistent_later_child_is_refused_before_any_child_is_repaired(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var node = child ?? master;
        var stage = await StageAsync(master, child);
        await ExpectGeometryAsync(node, stage);
        var source = stage.Source;
        var items = node.Set("Item");
        var revision = ExpectRecord(items, OriginalCode);
        var canonical = Export(items, source);
        var initial = node.Counters;
        var position = node.Position;
        var requests = allocator.Requests;
        var returned = allocator.Returned;

        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var arena = parts.Arena;
            var earlier = arena.Header(source.First);
            ref var later = ref arena.Header(source.Tail);
            var correct = later;
            later.DescriptorOffset = earlier.DescriptorOffset;
            try
            {
                var inconsistent = later;
                var destination = Arena.RegionOf(stage.SmallAnchor);
                Assert.True(arena.TryFindRelocationDestination(source.Directory, ref destination, out _));
                Assert.True(RootStorage.Owns(arena,
                    ResidentDescriptor.Read(arena.Payload(source.Directory)), source.Directory, BlockHeader.Size));
                Assert.False(RootStorage.Owns(arena,
                    ResidentDescriptor.Read(arena.Payload(source.Directory)[ValueBlocks.DescriptorSize..]), source.Directory,
                    BlockHeader.Size + ValueBlocks.DescriptorSize));
                ExpectRefusalWithoutChanges(parts, source, source.Directory, stage.SmallAnchor, [stage.DirectoryHole]);
                Assert.Equal(earlier, arena.Header(source.First));
                Assert.Equal(inconsistent, later);
                Assert.Equal(requests, allocator.Requests);
                Assert.Equal(returned, allocator.Returned);
                return true;
            }
            finally
            {
                later = correct;
            }
        }));

        Assert.Equal(source, Capture(node));
        Assert.Equal(initial, node.Counters);
        Assert.Equal(position, node.Position);
        Assert.True(await MoveCompoundAsync(node, source, source.Directory, stage.SmallAnchor));
        var moved = Capture(node);
        Assert.Equal(stage.DirectoryHole, moved.Directory);
        Assert.Equal(source with { Directory = moved.Directory }, moved);
        Assert.Equal(revision, ExpectRecord(items, OriginalCode));
        Assert.Equal(canonical, Export(items, moved));
        Assert.Equal(position, node.Position);
    }

    [Theory]
    [InlineData(LatticeRoleEnum.Master)]
    [InlineData(LatticeRoleEnum.Child)]
    public async Task A_tail_with_a_valid_local_edge_but_a_replaced_ancestor_edge_is_refused(LatticeRoleEnum role)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema, Inputs(role == LatticeRoleEnum.Master ? allocator : null));
        await using var child = role == LatticeRoleEnum.Child
            ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(allocator))
            : null;
        var node = child ?? master;
        var stage = await StageAsync(master, child, withPeerDirectory: true);
        await ExpectGeometryAsync(node, stage);
        var source = stage.Source;
        var items = node.Set("Item");
        var revision = ExpectRecord(items, OriginalCode);
        var canonical = Export(items, source);
        var initial = node.Counters;
        var position = node.Position;
        var requests = allocator.Requests;
        var returned = allocator.Returned;

        Assert.True(await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
        {
            var arena = parts.Arena;
            var root = arena.Payload(source.Root);
            var rootEdge = root[(source.DataOffset - BlockHeader.Size)..];
            var peer = stage.PeerDirectory;
            Assert.False(peer.IsNull);
            Assert.NotEqual(peer, source.Directory);
            Assert.Equal(BlockKindEnum.ChunkDirectory, arena.Header(peer).Kind);
            Assert.NotEqual(source.Handle, arena.Header(peer).Root);
            ResidentDescriptor.PublishLocation(rootEdge, peer);
            try
            {
                var incoming = ResidentDescriptor.Read(rootEdge);
                var localEntry = ResidentDescriptor.Read(arena.Payload(source.Directory)[ValueBlocks.DescriptorSize..]);
                Assert.Equal(DescriptorKindEnum.Chunked, incoming.Kind);
                Assert.Equal(peer, ValueBlocks.AddressOf(incoming));
                Assert.Equal(source.Root, arena.Header(source.Directory).Parent);
                Assert.Equal(source.Tail, ValueBlocks.AddressOf(localEntry));
                Assert.True(RootStorage.Owns(arena, localEntry, source.Directory, BlockHeader.Size + ValueBlocks.DescriptorSize));
                var destination = Arena.RegionOf(stage.BigAnchor);
                Assert.True(arena.TryFindRelocationDestination(source.Tail, ref destination, out _));
                ExpectRefusalWithoutChanges(parts, source, source.Tail, stage.BigAnchor, stage.BigHoles);
                Assert.Equal(incoming, ResidentDescriptor.Read(rootEdge));
                Assert.Equal(requests, allocator.Requests);
                Assert.Equal(returned, allocator.Returned);
                return true;
            }
            finally
            {
                ResidentDescriptor.PublishLocation(rootEdge, source.Directory);
            }
        }));

        Assert.Equal(source, Capture(node));
        Assert.Equal(initial, node.Counters);
        Assert.Equal(position, node.Position);
        Assert.True(await MoveCompoundAsync(node, source, source.Tail, stage.BigAnchor));
        var moved = Capture(node);
        Assert.Contains(moved.Tail, stage.BigHoles);
        Assert.Equal(source with { Tail = moved.Tail }, moved);
        Assert.Equal(revision, ExpectRecord(items, OriginalCode));
        Assert.Equal(canonical, Export(items, moved));
        Assert.Equal(position, node.Position);
    }

    private static void ExpectRefusalWithoutChanges(DatabaseParts parts, Chain source, ArenaAddress candidate,
        ArenaAddress liveNeighbor, ArenaAddress[] holes)
    {
        var arena = parts.Arena;
        var native = arena.Counters;
        var queue = RetiredItems(parts.Retired);
        var entries = queue.ToArray();
        var capacity = queue.EnsureCapacity(0);
        var reserved = RetirementReservations(parts.Retired);
        var addresses = new[] { source.Root, source.Directory, source.First, source.Tail };
        var headers = addresses.Select(address => arena.Header(address)).ToArray();
        var payloads = addresses.Select(address => arena.Payload(address).ToArray()).ToArray();
        var rootAddress = parts.Roots.Entry(source.Handle).Root;
        var destinations = holes.Select(hole => FreeSlotHeader(arena, liveNeighbor, hole)).ToArray();

        Assert.False(CompoundRelocation.TryMove(parts, source.Handle, candidate, Arena.RegionOf(liveNeighbor), out _, out _));

        Assert.Equal(native, arena.Counters);
        Assert.Equal(entries, queue.ToArray());
        Assert.Equal(capacity, queue.EnsureCapacity(0));
        Assert.Equal(reserved, RetirementReservations(parts.Retired));
        Assert.Equal(rootAddress, parts.Roots.Entry(source.Handle).Root);
        Assert.Equal(destinations, holes.Select(hole => FreeSlotHeader(arena, liveNeighbor, hole)).ToArray());
        for (var index = 0; index < addresses.Length; index++)
        {
            Assert.Equal(headers[index], arena.Header(addresses[index]));
            Assert.Equal(payloads[index], arena.Payload(addresses[index]).ToArray());
        }
    }


    // The adjacent live allocation keeps this one-region segment allocated.
    // Inspect the old initialized header bytes only, under Writes, so that even
    // a speculative copy followed by a free is visible to the test.
    private static BlockHeader FreeSlotHeader(Arena arena, ArenaAddress liveNeighbor, ArenaAddress hole)
    {
        Assert.Equal(liveNeighbor.Segment, hole.Segment);
        Assert.Throws<ArgumentException>("address", () => _ = arena.Header(hole));
        ref var neighbor = ref arena.Header(liveNeighbor);
        ref var first = ref Unsafe.As<BlockHeader, byte>(ref neighbor);
        ref var header = ref Unsafe.Add(ref first, (nint)((long)hole.Offset - liveNeighbor.Offset));
        return Unsafe.ReadUnaligned<BlockHeader>(ref header);
    }

    private static async Task<Stage> StageAsync(EngineDatabase master, EngineDatabase? child, bool withPeerDirectory = false)
    {
        var batch = master.CreateWriteBatch();
        var small = master.Set("SmallFiller");
        var big = master.Set("BigFiller");
        for (var key = 1L; key <= SmallFillers; key++)
        {
            batch.Insert(new LatticeRecord(small.EntityType)
            {
                ["Id"] = key,
                ["A"] = 1L,
                ["B"] = 2L,
                ["C"] = 3L,
            });
        }

        for (var key = 1L; key <= BigFillers; key++)
        {
            batch.Insert(new LatticeRecord(big.EntityType) { ["Id"] = key });
        }

        Assert.True((await batch.CommitAsync().AsTask().WaitAsync(Bound)).Changed);
        var items = master.Set("Item");
        Assert.True((await items.InsertAsync(ItemOf(items.EntityType, TargetKey, OriginalCode, Data())).AsTask().WaitAsync(Bound)).Changed);
        Assert.True((await items.InsertAsync(ItemOf(items.EntityType, 2, 31, new byte[FullChunkBytes])).AsTask().WaitAsync(Bound)).Changed);
        if (withPeerDirectory)
        {
            var peers = master.Set("Peer");
            var peer = new LatticeRecord(peers.EntityType) { ["Id"] = 1L, ["Tag"] = Guid.Empty, ["Data"] = Data() };
            Assert.True((await peers.InsertAsync(peer).AsTask().WaitAsync(Bound)).Changed);
        }

        await FollowAsync(master, child);

        var node = child ?? master;
        var smallSlots = FillerAddresses(node, "SmallFiller", SmallFillers, 2);
        var bigSlots = FillerAddresses(node, "BigFiller", BigFillers, 5);
        var bigHoles = bigSlots[1..];
        var original = Capture(node);
        Assert.True((await items.DeleteAsync(2L).AsTask().WaitAsync(Bound)).Changed);
        for (var key = BigFillers - bigHoles.Length + 1; key <= BigFillers; key++)
        {
            Assert.True((await big.DeleteAsync((long)key).AsTask().WaitAsync(Bound)).Changed);
        }

        Assert.True((await small.DeleteAsync((long)SmallFillers).AsTask().WaitAsync(Bound)).Changed);
        await FollowAsync(master, child);
        node.DrainRetired();
        Assert.Equal(original, Capture(node));
        return new Stage(original, smallSlots[0], smallSlots[1], bigSlots[0], bigHoles, PeerDirectoryOf(node));
    }

    private static ArenaAddress PeerDirectoryOf(EngineDatabase node)
    {
        ArenaAddress address = default;
        node.ForEachLiveRoot((arena, layout, _, root) =>
        {
            if (layout.EntityType.Name == "Peer")
            {
                var reader = new RootReader(arena, layout, root);
                var descriptor = reader.ReadDescriptor(layout.StoredFieldOf(layout.EntityType.GetField("Data")));
                Assert.Equal(DescriptorKindEnum.Chunked, descriptor.Kind);
                address = ValueBlocks.AddressOf(descriptor);
            }
        });
        return address;
    }

    private static async Task ExpectGeometryAsync(EngineDatabase node, Stage stage)
    {
        var original = stage.Source;
        Assert.Equal(6_491, original.DataOffset);
        Assert.Equal(BigSlotBytes, Arena.BlockSizeFor(original.Arena.Payload(original.Root).Length));
        Assert.Equal(DirectorySlotBytes, Arena.BlockSizeFor(original.Arena.Payload(original.Directory).Length));
        Assert.Equal(Arena.MaxBlockSize, Arena.BlockSizeFor(original.Arena.Payload(original.First).Length));
        Assert.Equal(BigSlotBytes, Arena.BlockSizeFor(original.Arena.Payload(original.Tail).Length));
        Assert.Equal(original.Root.Segment, original.Tail.Segment);
        Assert.True(stage.DirectoryHole.Segment < original.Directory.Segment);
        Assert.All(stage.BigHoles, hole => Assert.True(hole.Segment < original.Root.Segment));
        Assert.True(await EligibleAsync(node, original.Root, Arena.RegionOf(stage.BigAnchor)));
        Assert.True(await EligibleAsync(node, original.Directory, Arena.RegionOf(stage.SmallAnchor)));
        Assert.True(await EligibleAsync(node, original.Tail, Arena.RegionOf(stage.BigAnchor)));
        Assert.False(await EligibleAsync(node, original.First, Arena.RegionOf(stage.BigAnchor)));
        Assert.Equal(0, node.Counters.Arena.RetiredBytes);
        Assert.Equal(1, node.Counters.Arena.ReservedSegments);
    }

    private static ArenaAddress[] FillerAddresses(EngineDatabase node, string name, int count, int takeLast)
    {
        var addresses = new List<ArenaAddress>();
        node.ForEachLiveRoot((_, layout, _, root) =>
        {
            if (layout.EntityType.Name == name)
            {
                addresses.Add(root);
            }
        });
        Assert.Equal(count, addresses.Count);
        Assert.Single(addresses.Select(address => address.Segment).Distinct());
        return addresses.TakeLast(takeLast).ToArray();
    }

    private static Chain Capture(EngineDatabase node)
    {
        var chains = new List<Chain>();
        node.ForEachLiveRoot((arena, layout, handle, root) =>
        {
            if (layout.EntityType.Name != "Item")
            {
                return;
            }

            var keyOffset = layout.StoredFieldOf(layout.EntityType.Key).Offset;
            if (MemoryMarshal.Read<long>(arena.Payload(root)[(keyOffset - BlockHeader.Size)..]) != TargetKey)
            {
                return;
            }

            var offset = layout.StoredFieldOf(layout.EntityType.GetField("Data")).Offset;
            var descriptor = ResidentDescriptor.Read(arena.Payload(root)[(offset - BlockHeader.Size)..]);
            Assert.Equal(DescriptorKindEnum.Chunked, descriptor.Kind);
            Assert.Equal((uint)(FullChunkBytes + TailBytes), descriptor.Length);
            var directory = ValueBlocks.AddressOf(descriptor);
            var entries = arena.Payload(directory);
            Assert.Equal(2 * ValueBlocks.DescriptorSize, entries.Length);
            var first = ReadChunk(entries, 0, FullChunkBytes);
            var tail = ReadChunk(entries, 1, TailBytes);
            ExpectHeader(arena, root, BlockKindEnum.Root, handle, default, 0, layout.LayoutVersion);
            ExpectHeader(arena, directory, BlockKindEnum.ChunkDirectory, handle, root, offset, layout.LayoutVersion);
            ExpectHeader(arena, first, BlockKindEnum.Chunk, handle, directory, BlockHeader.Size, layout.LayoutVersion);
            ExpectHeader(arena, tail, BlockKindEnum.Chunk, handle, directory, BlockHeader.Size + ValueBlocks.DescriptorSize, layout.LayoutVersion);
            chains.Add(new Chain(arena, layout, handle, root, offset, directory, first, tail));
        });
        return Assert.Single(chains);
    }

    private static ArenaAddress ReadChunk(ReadOnlySpan<byte> directory, int index, int bytes)
    {
        var descriptor = ResidentDescriptor.Read(directory[(index * ValueBlocks.DescriptorSize)..]);
        Assert.Equal(DescriptorKindEnum.Block, descriptor.Kind);
        Assert.Equal((uint)bytes, descriptor.Length);
        return ValueBlocks.AddressOf(descriptor);
    }

    private static void ExpectHeader(Arena arena, ArenaAddress address, BlockKindEnum kind, RootHandle root,
        ArenaAddress parent, int offset, uint layoutVersion)
    {
        var header = arena.Header(address);
        Assert.Equal(kind, header.Kind);
        Assert.Equal(root, header.Root);
        Assert.Equal(parent, header.Parent);
        Assert.Equal((uint)offset, header.DescriptorOffset);
        Assert.Equal(layoutVersion, header.LayoutVersion);
    }

    private static LatticeRecord ItemOf(LatticeEntityType type, long key, sbyte code, byte[] data) => new(type)
    {
        ["Id"] = key,
        ["Code"] = code,
        ["Data"] = data,
        ["Label"] = "tri",
    };

    private static byte[] Data() =>
        Enumerable.Range(0, FullChunkBytes + TailBytes).Select(index => (byte)(index * 17 + index / 101)).ToArray();

    private static LatticeRevision ExpectRecord(ILatticeSet items, sbyte code)
    {
        Assert.True(items.TryGet(TargetKey, out var item, out var revision));
        Assert.Equal(TargetKey, item.Get<long>("Id"));
        Assert.Equal(code, item.Get<sbyte>("Code"));
        Assert.Equal(Data(), item.Get<byte[]>("Data"));
        Assert.Equal("tri", item.Get<string>("Label"));
        Assert.Null(item["Pad0"]);
        var range = IndexKey.All.Range(IndexBound.Inclusive(code), IndexBound.Inclusive(code));
        Assert.Single(items.Index("ByCode").Find(range), matched => matched.Get<long>("Id") == TargetKey);
        return revision;
    }

    private static byte[] Export(ILatticeSet items, Chain chain) =>
        ProtectedExport.Capture(items, TargetKey, chain.Arena, chain.Layout, chain.Root);

    private static byte[] ExpectedBody(Chain chain, sbyte code)
    {
        var type = chain.Layout.EntityType;
        var values = new object?[type.FieldsByOrdinal.Length];
        values[type.Key.Ordinal] = TargetKey;
        values[type.GetField("Code").Ordinal] = code;
        values[type.GetField("Data").Ordinal] = Data();
        values[type.GetField("Label").Ordinal] = "tri";
        return RootValues.Export(chain.Layout, type, values).ToArray();
    }

    private static Task<bool> MoveRootAsync(EngineDatabase node, Chain candidate, ArenaAddress destination) =>
        MaintenanceStep.RunAfterAdmissionAsync(node,
            parts => RootRelocation.TryMove(parts, candidate.Handle, candidate.Root, Arena.RegionOf(destination), out _));

    private static Task<bool> MoveCompoundAsync(EngineDatabase node, Chain candidate, ArenaAddress address, ArenaAddress destination) =>
        MaintenanceStep.RunAfterAdmissionAsync(node,
            parts => CompoundRelocation.TryMove(parts, candidate.Handle, address, Arena.RegionOf(destination), out _, out _));

    private static Task<(RetiredKindEnum, ArenaAddress)[]> RetirementOfAsync(EngineDatabase node) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => RetiredItems(parts.Retired)
            .Where(item => item.Kind is RetiredKindEnum.RelocatedBlock or RetiredKindEnum.Root or RetiredKindEnum.DeletedRoot)
            .Select(item => (item.Kind, item.Address)).ToArray());

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_items")]
    private static extern ref Queue<RetiredItem> RetiredItems(RetiredMemory retired);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_reserved")]
    private static extern ref int RetirementReservations(RetiredMemory retired);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_row")]
    private static extern ref ViewRecord ViewRow(ref LatticeRecordView view);

    private static void ExpectView(LatticeRecordView view, Chain chain, LatticeRevision revision, byte[] data)
    {
        Assert.Equal(revision, view.Revision);
        Assert.Equal(TargetKey, view["Id"].Get<long>());
        Assert.Equal(OriginalCode, view["Code"].Get<sbyte>());
        Assert.True(view["Pad0"].IsNull);
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(ViewRow(ref view).Bytes),
            ref MemoryMarshal.GetReference(chain.Arena.Payload(chain.Root))));

        var value = view["Data"];
        Assert.Equal(2, value.PartCount);
        ExpectBorrowed(value.GetPart(0), chain.Arena, chain.First, data.AsSpan(0, FullChunkBytes));
        ExpectBorrowed(value.GetPart(1), chain.Arena, chain.Tail, data.AsSpan(FullChunkBytes));
    }

    private static void ExpectBorrowed(ReadOnlySpan<byte> part, Arena arena, ArenaAddress address, ReadOnlySpan<byte> expected)
    {
        Assert.True(part.SequenceEqual(expected));
        Assert.True(Unsafe.AreSame(
            ref MemoryMarshal.GetReference(part),
            ref MemoryMarshal.GetReference(arena.Payload(address))));
    }

    private static void ExpectOnlyLocationChanged(byte[] before, ReadOnlySpan<byte> after, int descriptorOffset, ArenaAddress replacement)
    {
        // Align the address word down inside the resident slot, as specified by the native layout.
        var locationOffset = ((descriptorOffset + sizeof(ulong)) & -sizeof(ulong)) - BlockHeader.Size;
        var number = replacement.Value;
        MemoryMarshal.Write(before.AsSpan(locationOffset), in number);
        Assert.True(after.SequenceEqual(before));
    }

    private static void ExpectAbsent(ILatticeSet items)
    {
        Assert.Null(items.Get(TargetKey));
        foreach (var code in new[] { OriginalCode, ChangedCode })
        {
            var range = IndexKey.All.Range(IndexBound.Inclusive(code), IndexBound.Inclusive(code));
            Assert.DoesNotContain(items.Index("ByCode").Find(range), item => item.Get<long>("Id") == TargetKey);
        }
    }

    private static void ExpectFreed(Arena arena, ArenaAddress address) =>
        Assert.Throws<ArgumentException>("address", () => _ = arena.Header(address));


    private static Task<bool> EligibleAsync(EngineDatabase node, ArenaAddress address, long destination) =>
        MaintenanceStep.RunAfterAdmissionAsync(node, parts => parts.Arena.TryFindRelocationDestination(address, ref destination, out _));

    private static async Task FollowAsync(EngineDatabase source, EngineDatabase? child)
    {
        if (child is not null)
        {
            await child.WaitForPositionAsync(source.Position, Bound);
            Assert.Equal(source.Position, child.Position);
        }
    }

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator) => new()
    {
        Arena = new ArenaOptions { SegmentSize = Arena.MaxBlockSize, MemoryLimit = 32L * Arena.MaxBlockSize },
        Allocator = allocator,
        DisableBackgroundMaintenance = true,
    };

    private static LatticeSchema MakeSchema()
    {
        var pads = Enumerable.Range(0, 400).Select(index =>
            $$"""{"Id":{{index + 5}},"Name":"Pad{{index}}","Type":"Guid","Nullable":true}""");
        var padding = string.Join(",", pads);
        return LatticeSchema.Compile($$"""
            {"StringEncoding":"Utf8","EntityTypes":[
              {"Id":1,"Name":"Item","Fields":[
                {"Id":1,"Name":"Id","Type":"Int64"},{"Id":2,"Name":"Code","Type":"Int8"},
                {"Id":3,"Name":"Data","Type":"Binary"},{"Id":4,"Name":"Label","Type":"String"},{{padding}}],
                "PrimaryKey":[1],"Indexes":[{"Id":1,"Name":"ByCode","Fields":[2]}]},
              {"Id":2,"Name":"SmallFiller","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},
                {"Id":2,"Name":"A","Type":"Int64"},{"Id":3,"Name":"B","Type":"Int64"},{"Id":4,"Name":"C","Type":"Int64"}],
                "PrimaryKey":[1]},
              {"Id":3,"Name":"BigFiller","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},{{padding}}],
                "PrimaryKey":[1]},
              {"Id":4,"Name":"Peer","Fields":[{"Id":1,"Name":"Id","Type":"Int64"},
                {"Id":2,"Name":"Tag","Type":"Guid"},{"Id":3,"Name":"Data","Type":"Binary"}],"PrimaryKey":[1]}]}
            """);
    }

    private sealed record Stage(Chain Source, ArenaAddress SmallAnchor, ArenaAddress DirectoryHole,
        ArenaAddress BigAnchor, ArenaAddress[] BigHoles, ArenaAddress PeerDirectory);

    private sealed record Chain(Arena Arena, RootLayout Layout, RootHandle Handle, ArenaAddress Root, int DataOffset,
        ArenaAddress Directory, ArenaAddress First, ArenaAddress Tail);
}
