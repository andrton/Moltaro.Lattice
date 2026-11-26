using System.Net;
using System.Net.Sockets;
using Moltaro.Lattice.Engine.Child.Following;
using Moltaro.Lattice.Engine.Children;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Tests.Engine.Indexes;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Replication;
using Moltaro.Lattice.Storage.Text;
using static Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget.InstanceBudget;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.MemoryBudget;

public sealed class ManagedBudgetOwnersTests
{
    [Theory(Skip = Pending)]
    [InlineData(10_000, false)]
    [InlineData(200_000, true)]
    public async Task A_batch_charges_the_two_export_generations_and_releases_the_final_pieces_after_commit(int length, bool multiplePieces)
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, allocator);
        var before = ExpectCapacity(master, allocator);
        var batch = master.CreateWriteBatch();
        batch.Insert(PieceSchema.Of(1, 8, data: length));
        var parts = BatchPieceArrays(batch);
        Assert.Equal(multiplePieces, parts.Length > 1);
        Assert.True(parts.Sum(part => part.Length) >= length);
        var built = ExpectCapacity(master, allocator, out _, parts);
        Assert.Equal(before.NativeBytes, built.NativeBytes);
        Assert.True(built.ChargedBytes - before.ChargedBytes >= parts.Sum(ManagedObjectSize));
        Assert.True(built.PeakBytes >= before.ChargedBytes + parts.Sum(ManagedObjectSize) + length);
        var commit = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        Assert.Equal(commit.Position, master.Position);
        Assert.Equal(length, master.Set<PieceSchema.Piece>().Get(1L)?.Data?.Length);

        _ = master.InspectBudget((_, backing) =>
        {
            if (backing is byte[] bytes)
            {
                Assert.DoesNotContain(bytes, parts);
            }
        });
        ExpectCapacity(master, allocator);
        GC.KeepAlive(batch);
    }

    [Fact(Skip = Pending)]
    public async Task A_connected_peer_owns_charged_frame_buffers_before_it_sends_hello_and_returns_them_on_close()
    {
        var allocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, allocator, listen: true);
        var before = ExpectCapacity(master, allocator);
        var listener = Backing<ChildListener>(master, "_listener");
        var gate = Backing<Lock>(listener, "_lock");
        var connections = Backing<Dictionary<ChildConnection, Task>>(listener, "_connections");
        object[]? frames = null;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, master.ListenUri!.Port)).WaitAsync(Bound);
            await Eventually.HoldsAsync(() =>
            {
                lock (gate)
                {
                    if (connections.Count == 1)
                    {
                        frames = FrameBackings(Backing<FrameConnection>(connections.Keys.Single(), "_connection"));
                    }
                }

                return frames is not null;
            }, "The master did not accept the silent peer.");

            var expected = Assert.IsType<object[]>(frames);
            var connected = ExpectCapacity(master, allocator, out _, expected);
            Assert.True(connected.ChargedBytes >= before.ChargedBytes + expected.Sum(ManagedObjectSize));
            Assert.Equal(before.NativeBytes, connected.NativeBytes);
            Assert.Equal(0, master.Counters.ActiveCaptures);
        }

        await Eventually.HoldsAsync(() =>
        {
            lock (gate)
            {
                return connections.Count == 0;
            }
        }, "The closed peer still runs on the master.");
        var all = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var after = master.InspectBudget((_, backing) => all.Add(backing));
        foreach (var frame in Assert.IsType<object[]>(frames))
        {
            Assert.DoesNotContain(frame, all);
        }
        Assert.InRange(after.ChargedBytes, before.ChargedBytes, before.ChargedBytes + 1_024);
    }

    [Fact(Skip = Pending)]
    public async Task Both_nodes_charge_owned_publication_and_text_capacity_and_the_child_charges_its_transfer_storage()
    {
        var masterAllocator = new CountingNativeAllocator();
        await using var master = await OpenAsync(64L * Segment, masterAllocator, listen: true, schema: IndexSchema.Utf8);
        var source = master.Set<IndexSchema.Ranked>();
        var batch = master.CreateWriteBatch();
        for (var key = 1; key <= 16; key++)
        {
            batch.Insert(new IndexSchema.Ranked { Id = key, Number = key, Region = $"Region {key}", Login = new string('a', 2_048) });
        }

        var committed = await batch.CommitAsync().AsTask().WaitAsync(Bound);
        var childAllocator = new CountingNativeAllocator();
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!, new EngineTestInputs
        {
            Allocator = childAllocator,
            DisableBackgroundMaintenance = true,
        });
        Assert.Equal(committed.Position, child.Position);

        var later = await source.InsertAsync(new IndexSchema.Ranked { Id = 40, Number = 40, Region = "New", Login = new string('b', 150_000) });
        await child.WaitForPositionAsync(later.Position, Bound);
        var changes = Backing<object>(Backing<Follower>(child, "_follower"), "_changes");
        var import = Backing<object>(changes, "_import");
        var keys = Backing<object>(Backing<PublicationKeys>(changes, "_keys"), "_keys");
        var link = Backing<object>(child, "_master");
        var transfer = Backing<object>(Backing<object>(Backing<Follower>(child, "_follower"), "_assembler"), "_large");
        var pieces = Backing<System.Collections.IList>(transfer, "_pieces");
        Assert.NotEmpty(pieces);
        var retained = pieces.Cast<object>().Select(piece =>
            Assert.IsType<byte[]>(piece.GetType().GetProperty("Bytes")!.GetValue(piece))).ToArray();
        object[] receiving =
        [
            .. FrameBackings(Backing<FrameConnection>(link, "_connection")),
            Backing<Array>(import, "_payload"), Backing<Array>(import, "_record"),
            Backing<Array>(import, "_inline"), Backing<Array>(import, "_starts"),
            Backing<Array>(Backing<object>(changes, "_prepared"), "_items"),
            Backing<Array>(keys, "_buckets"), Backing<Array>(keys, "_entries"), .. retained,
        ];
        Assert.All(receiving.OfType<Array>(), array => Assert.NotEmpty(array));
        _ = ExpectCapacity(child, childAllocator, out _, receiving);

        var changed = await source.ReplaceAsync(new IndexSchema.Ranked { Id = 40, Number = 40, Region = "New", Login = new string('c', 150_000) });
        await child.WaitForPositionAsync(changed.Position, Bound);
        Assert.Equal(retained.Length, pieces.Count);
        for (var index = 0; index < retained.Length; index++)
        {
            var piece = Assert.IsAssignableFrom<object>(pieces[index]);
            Assert.Same(retained[index], piece.GetType().GetProperty("Bytes")!.GetValue(piece));
        }

        _ = ExpectCapacity(child, childAllocator, out _, [.. retained]);

        foreach (var (node, allocator) in new[] { (master, masterAllocator), (child, childAllocator) })
        {
            var owned = await MaintenanceStep.RunAfterAdmissionAsync(node, parts =>
            {
                var group = Backing<CommitGroup>(parts.Coordinator, "_group");
                var writes = Backing<object>(group, "_writes");
                var texts = parts.Arena.Texts;
                return new object[]
                {
                    Backing<Array>(writes, "_items"),
                    Backing<TextLocator>(texts, "_locator"), Backing<TextWriterSlots>(texts, "_slots"),
                    Backing<TextBucketSet>(texts, "_active"),
                    Backing<Array>(parts.Publisher, "_tablesOf"),
                };
            });
            var text = TextMetadataTestAccess.Capacity(node);
            Assert.NotEmpty(text.LocatorLeaves);
            Assert.NotEmpty(text.WriterMetadata);
            Assert.NotEmpty(text.ActiveBuckets);
            TextMetadataTestAccess.ExpectAccounting(node, text);
            _ = ExpectCapacity(node, allocator, out _, owned);
        }
    }

    private static object[] FrameBackings(FrameConnection connection)
    {
        var receive = Backing<byte[]>(connection, "_received");
        var send = Backing<byte[]>(Backing<object>(connection, "_outgoing"), "_buffer");
        Assert.True(receive.Length >= 128 * 1024);
        Assert.True(send.Length >= 64 * 1024);
        return [receive, send];
    }
}
