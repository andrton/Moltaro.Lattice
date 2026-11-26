using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Protection.ResumableReclamationTests;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A shared directory is reached through its stable ID. Its short tail is
// reached through a descriptor in that directory. Both keep old readers safe.
public sealed class SharedTextChunkRelocationTests
{
    private const long Target = 10_000;
    private const int RegionBytes = 65_536;
    private const int DirectorySlot = 48;
    private const int TailSlot = 512;
    private const int TailBytes = TailSlot - 32;
    private const int DirectoryTextBytes = 4_500;

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public Task A_shared_directory_moves_without_changing_its_chunk_or_transfer(string encoding) =>
        ExpectMoveAsync(encoding, movingTail: false);

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public Task A_sparse_shared_short_tail_moves_while_the_full_chunk_and_wire_stay_valid(string encoding) =>
        ExpectMoveAsync(encoding, movingTail: true);

    [Theory]
    [MemberData(nameof(TextSchema.Encodings), MemberType = typeof(TextSchema))]
    public async Task A_half_full_shared_chunk_region_cannot_supply_a_move_even_with_an_earlier_vacancy(string encoding)
    {
        const int Half = RegionBytes / 2;
        const int TextBytes = 20_000;
        var chars = TextBytes / (encoding == "Utf8" ? 1 : 2);
        var text = new string('z', chars);
        await using var master = await ReplicationNodes.OpenMasterAsync(Schema(encoding), Inputs());
        await master.Set<TextSchema.Depot>().InsertAsync(Depot(1, TextOf(1, chars)));
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        var write = child.CreateWriteBatch();
        write.Insert(Depot(2, TextOf(2, chars)));
        write.Insert(Depot(Target, text));
        write.Insert(new IndexSchema.Ranked { Id = Target, Number = Target, Login = text, Region = "" });
        var written = await write.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(written.Position);
        var removed = await child.Set<TextSchema.Depot>().DeleteAsync(1L).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(removed.Position);

        foreach (var node in new[] { master, child })
        {
            node.DrainRetired();
            var id = TextIds(node, "Depot", "Area", [Target])[Target];
            var chunk = Inspect(node, id, movingTail: false).Full;
            UnderWrites(node, parts =>
            {
                var arena = parts.Arena;
                ref readonly var header = ref arena.Header(chunk);
                var sizeClass = SizeClasses.IndexFor((int)header.PayloadLength);
                Assert.Equal(Half, SizeClasses.SizeOf(sizeClass));
                Assert.False(IsAllocated(arena, new ArenaAddress(chunk.Segment, chunk.Offset ^ Half)));
                Assert.Contains(Enumerable.Range(0, (int)arena.EarlierRelocationExtent(chunk)),
                    region => arena.RelocationVacancyAt(region, sizeClass) != 0);
                Assert.False(arena.IsRelocationSource(chunk, sizeClass, header.Root, header.Kind));
                return true;
            });

            var wire = Export(node);
            var revision = ExpectedCopy(node, text, encoding);
            for (var turn = 0; turn < 256; turn++)
            {
                RunAdmittedTurn(node);
            }

            Assert.Equal(chunk, Inspect(node, id, movingTail: false).Full);
            Assert.Equal(wire, Export(node));
            Assert.Equal(revision, ExpectedCopy(node, text, encoding));
        }
    }

    private static async Task ExpectMoveAsync(string encoding, bool movingTail)
    {
        var schema = Schema(encoding);
        var bytesPerChar = encoding == "Utf8" ? 1 : 2;
        var slot = movingTail ? TailSlot : DirectorySlot;
        var fillerBytes = movingTail ? TailBytes : DirectoryTextBytes;
        var textBytes = movingTail ? ValueBlocks.ChunkPayload + TailBytes : DirectoryTextBytes;
        var text = new string('z', textBytes / bytesPerChar);
        var expected = TextTally.EncodingOf(encoding).GetBytes(text);
        var filler = Enumerable.Range(1, RegionBytes / slot).Select(id =>
            Depot(id, TextOf(id, fillerBytes / bytesPerChar))).ToArray();

        var masterAllocator = new CountingNativeAllocator();
        var childAllocator = new CountingNativeAllocator();
        await using var master = await ReplicationNodes.OpenMasterAsync(schema, Inputs(masterAllocator));
        await master.Set<TextSchema.Depot>().InsertAsync(filler[0]);
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs(childAllocator));
        var fill = master.CreateWriteBatch();
        foreach (var entry in filler[1..])
        {
            fill.Insert(entry);
        }

        var filled = await fill.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(filled.Position);
        var publish = child.CreateWriteBatch();
        publish.Insert(Depot(Target, text));
        publish.Insert(new IndexSchema.Ranked { Id = Target, Number = Target, Login = text, Region = "" });
        var published = await publish.CommitAsync().AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(published.Position);
        var removed = await child.Set<TextSchema.Depot>().DeleteAsync(1L).AsTask().WaitAsync(Bound);
        await child.WaitForPositionAsync(removed.Position);
        Assert.Equal(removed.Position, master.Position);

        var nodes = new[] { master, child };
        var allocators = new[] { masterAllocator, childAllocator };
        var ids = nodes.Select(node => TextIds(node, "Depot", "Area", [Target])[Target]).ToArray();
        var originals = new Placement[nodes.Length];
        var transfers = new byte[nodes.Length][];
        var revisions = new LatticeRevision[nodes.Length];
        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            node.DrainRetired();
            var placement = Inspect(node, ids[index], movingTail);
            originals[index] = placement;
            transfers[index] = Export(node);
            revisions[index] = ExpectedCopy(node, text, encoding);
            AssertGeometry(node, placement, movingTail, slot);
        }

        Assert.Equal(transfers[0], transfers[1]);
        Assert.Equal(revisions[0], revisions[1]);
        using var masterPause = new ViewerPause();
        using var childPause = new ViewerPause();
        var masterRead = OwnThread.Run(() => Hold(master, masterPause, originals[0], revisions[0], expected, text, movingTail));
        var childRead = OwnThread.Run(() => Hold(child, childPause, originals[1], revisions[1], expected, text, movingTail));
        try
        {
            await masterPause.EnteredAsync(masterRead);
            await childPause.EnteredAsync(childRead);
            for (var index = 0; index < nodes.Length; index++)
            {
                var node = nodes[index];
                var original = originals[index];
                var allocator = allocators[index];
                var requests = allocator.Requests;
                var current = original;
                for (var turn = 0; current.Source == original.Source; turn++)
                {
                    Assert.True(turn < 40_000, "The sparse shared block did not move.");
                    RunAdmittedTurn(node);
                    current = Inspect(node, ids[index], movingTail);
                }

                Assert.True(current.Source.Segment < original.Source.Segment, "The shared block did not move into an earlier native segment.");
                Assert.Equal(requests, allocator.Requests);
                Assert.Equal(original.Other, current.Other);
                Assert.Equal(original.Full, current.Full);
                Assert.True(UnderWrites(node, parts => IsAllocated(parts.Arena, original.Source)));
                Assert.Equal(revisions[index], ExpectedCopy(node, text, encoding));
                Assert.Equal(transfers[index], Export(node));
                Assert.Equal(removed.Position, node.Position);
            }
        }
        finally
        {
            masterPause.Release();
            childPause.Release();
            await Task.WhenAll(masterRead, childRead).WaitAsync(Bound);
        }

        for (var index = 0; index < nodes.Length; index++)
        {
            var node = nodes[index];
            node.DrainRetired();
            Assert.False(UnderWrites(node, parts => IsAllocated(parts.Arena, originals[index].Source)));
            Assert.Equal(transfers[index], Export(node));
        }

        await using var loaded = await ReplicationNodes.OpenChildAsync(master.ListenUri!, Inputs());
        Assert.Equal(removed.Position, loaded.Position);
        Assert.Equal(revisions[0], ExpectedCopy(loaded, text, encoding));
        Assert.Equal(transfers[0], Export(loaded));
    }

    private static void RunAdmittedTurn(EngineDatabase node)
    {
        var started = Stopwatch.GetTimestamp();
        var wait = new SpinWait();
        while (!node.TryRunMaintenanceTurn())
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < Bound, "The engine did not admit the maintenance turn.");
            wait.SpinOnce();
        }
    }

    private static void AssertGeometry(EngineDatabase node, Placement placement, bool movingTail, int slot)
    {
        UnderWrites(node, parts =>
        {
            var arena = parts.Arena;
            ref readonly var source = ref arena.Header(placement.Source);
            var sizeClass = SizeClasses.IndexFor((int)source.PayloadLength);
            Assert.Equal(slot, SizeClasses.SizeOf(sizeClass));
            Assert.True(arena.IsRelocationSource(placement.Source, sizeClass, source.Root, source.Kind));
            Assert.Contains(Enumerable.Range(0, (int)arena.EarlierRelocationExtent(placement.Source)),
                region => arena.RelocationVacancyAt(region, sizeClass) != 0);

            if (movingTail)
            {
                ref readonly var full = ref arena.Header(placement.Full);
                var fullClass = SizeClasses.IndexFor((int)full.PayloadLength);
                Assert.Equal(65_536, SizeClasses.SizeOf(fullClass));
                Assert.False(arena.IsRelocationSource(placement.Full, fullClass, full.Root, full.Kind));
            }

            return true;
        });
    }

    private static LatticeRevision ExpectedCopy(EngineDatabase node, string text, string encoding)
    {
        Assert.True(node.Set("Depot").TryGet(Target, out var record, out var revision));
        Assert.Equal(text, record.Get<string>("Area"));
        Assert.Equal(text, node.Set<TextSchema.Depot>().Get(Target)?.Area);
        var indexed = Assert.Single(node.Set<IndexSchema.Ranked>().Index("ByLogin").Find(IndexKey.Equal(text)));
        Assert.Equal(Target, indexed.Id);
        Assert.Equal(text, indexed.Login);
        Assert.Equal(3L, References(node, text, encoding));
        return revision;
    }

    private static void Hold(EngineDatabase node, ViewerPause pause, Placement initial, LatticeRevision revision,
        byte[] expected, string text, bool movingTail)
    {
        Assert.True(node.Set("Depot").TryView(Target, view =>
        {
            var value = view["Area"];
            Assert.Equal(movingTail ? 2 : 1, value.PartCount);
            var old = movingTail ? value.GetPart(1) : initial.Arena.Payload(initial.Source);
            var original = movingTail ? expected[^TailBytes..] : old.ToArray();
            Assert.True(old.SequenceEqual(original));
            pause.Hold();
            Assert.True(old.SequenceEqual(original));
            Assert.Equal(text, value.GetString());
            return view.Revision;
        }, out var seen));
        Assert.Equal(revision, seen);
    }

    private static Placement Inspect(EngineDatabase node, uint id, bool movingTail) =>
        UnderWrites(node, parts =>
        {
            Assert.True(parts.Arena.Texts.TryMeasureEntry(id, out var entry));
            var directory = entry.Address;
            Assert.Equal(id, parts.Arena.Header(directory).Root.Slot);
            var first = ValueBlocks.ChunkAddressAt(parts.Arena, directory, 0);
            ExpectChunkOwner(parts.Arena, first, id, 0);
            if (movingTail)
            {
                var tail = ValueBlocks.ChunkAddressAt(parts.Arena, directory, 1);
                ExpectChunkOwner(parts.Arena, tail, id, 1);
                return new Placement(parts.Arena, tail, directory, first);
            }

            return new Placement(parts.Arena, directory, first, first);
        });

    private static void ExpectChunkOwner(Arena arena, ArenaAddress chunk, uint id, int index)
    {
        ref readonly var header = ref arena.Header(chunk);
        Assert.Equal(new RootHandle(0, id), header.Root);
        Assert.True(header.Parent.IsNull);
        Assert.Equal((uint)(BlockHeader.Size + index * ValueBlocks.DescriptorSize), header.DescriptorOffset);
    }

    private static byte[] Export(EngineDatabase node)
    {
        byte[]? bytes = null;
        node.ForEachLiveRoot((arena, layout, _, root) =>
        {
            if (layout.EntityType.Name != "Depot" || new RootReader(arena, layout, root).GetInteger(layout.EntityType.Key) != Target)
            {
                return;
            }

            var output = new ArrayBufferWriter<byte>();
            BlockExport.Write(output, arena, layout, root);
            Assert.Equal(BlockExport.Length(arena, layout, root), output.WrittenCount);
            bytes = output.WrittenSpan.ToArray();
        });
        Assert.NotNull(bytes);
        return bytes;
    }

    private static LatticeSchema Schema(string encoding) => IndexSchema.With(encoding, $$"""
        {
            "Id": 5, "Name": "Depot", "ClrType": "{{typeof(TextSchema.Depot).FullName}}",
            "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Area", "Type": "String", "StringStorage": "Dictionary" }
            ],
            "PrimaryKey": [1]
        },
        """);

    private static TextSchema.Depot Depot(long id, string text) => new() { Id = id, Area = text };

    private static string TextOf(int id, int characters) =>
        new string('t', characters - 6) + id.ToString("D6", CultureInfo.InvariantCulture);

    private static EngineTestInputs Inputs(CountingNativeAllocator? allocator = null) => new()
    {
        Allocator = allocator,
        IndexSeed = 243,
        Arena = new ArenaOptions { SegmentSize = RegionBytes, MemoryLimit = 32 * 1024 * 1024 },
        DisableBackgroundMaintenance = true,
    };

    private readonly record struct Placement(Arena Arena, ArenaAddress Source, ArenaAddress Other, ArenaAddress Full);
}
