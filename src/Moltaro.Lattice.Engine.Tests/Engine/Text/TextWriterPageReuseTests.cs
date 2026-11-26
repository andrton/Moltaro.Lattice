using System.Globalization;
using System.Text;
using System.Threading.Channels;
using Moltaro.Lattice.Engine.Tests.Engine.Replication;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// Actual writer capacity can serve the next write across a page boundary.
// When writes stop, bounded maintenance returns the unused capacity.
public sealed class TextWriterPageReuseTests
{
    private const int Boundary = 64;
    private const int Peak = 4 * Boundary;

    [Theory]
    [InlineData(LatticeRoleEnum.Master, false)]
    [InlineData(LatticeRoleEnum.Child, false)]
    [InlineData(LatticeRoleEnum.Master, true)]
    [InlineData(LatticeRoleEnum.Child, true)]
    public async Task Trailing_writer_capacity_can_serve_the_next_write_and_returns_when_quiet(LatticeRoleEnum role, bool sparse)
    {
        using var worker = new HeldEngineCallback();
        var parked = 0;
        var turns = Channel.CreateUnbounded<bool>();
        var activeInputs = new EngineTestInputs
        {
            IndexSeed = 153,
            BeforeMaintenanceWait = _ =>
            {
                if (Interlocked.Exchange(ref parked, 1) == 0)
                {
                    worker.Hold();
                }
            },
            ObserveMaintenanceTurn = sample =>
            {
                if (sample.Admitted)
                {
                    turns.Writer.TryWrite(true);
                }
            },
        };
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8,
            role == LatticeRoleEnum.Master ? activeInputs : new EngineTestInputs { DisableBackgroundMaintenance = true });
        await using var child = role == LatticeRoleEnum.Child ? await ReplicationNodes.OpenChildAsync(master.ListenUri!, activeInputs) : null;
        var node = child ?? master;
        var records = node.Set("Depot");
        var items = node.Set<TextSchema.Depot>();
        var expected = new Dictionary<long, string>();
        try
        {
            await worker.ReachedAsync();
            var empty = WriterBytes(node);
            await InsertAsync(1, Boundary);
            var fresh = WriterBytes(node);
            Assert.True(fresh > empty);
            Assert.Null(WriterPage(node, 1));

            object spare;
            long occupied;
            if (sparse)
            {
                await InsertAsync(Boundary + 1, Peak);
                var page = WriterPage(node, 1);
                Assert.NotNull(page);
                Assert.NotNull(WriterPage(node, 2));
                Assert.NotNull(WriterPage(node, 3));
                await DeleteAsync(expected.Keys.Where(key => key % 4 != 0).ToArray());
                Assert.Equal(Boundary, node.Counters.TextEntries);
                spare = page;
                occupied = WriterBytes(node);
            }
            else
            {
                var first = await ReplaceWhileHeldAsync(1, TextOf(Peak + 1));
                Assert.Same(first.Page, WriterPage(node, 1));
                Assert.Equal(first.Bytes, WriterBytes(node));
                var second = await ReplaceWhileHeldAsync(2, TextOf(Peak + 2));
                Assert.Same(first.Page, second.Page);
                Assert.Equal(first.Bytes, second.Bytes);
                spare = second.Page;
                occupied = second.Bytes;
            }

            var originalIds = TextIds(node, "Depot", "Area", expected.Keys);
            if (sparse)
            {
                Assert.True(originalIds.Values.Select(id => id / Boundary).Distinct().Count() >= 4);
            }

            Assert.Same(spare, WriterPage(node, 1));
            Assert.True(occupied > fresh);
            Assert.Null(WriterPage(node, 2));
            Assert.Null(WriterPage(node, 3));
            Assert.Equal(occupied, WriterBytes(node));
            var position = node.Position;
            worker.Release();
            using var deadline = new CancellationTokenSource(Bound);
            while (WriterBytes(node) != fresh)
            {
                await turns.Reader.ReadAsync(deadline.Token);
            }

            Assert.Null(WriterPage(node, 1));
            Assert.Equal(position, node.Position);
            Assert.Equal(Boundary, node.Counters.TextEntries);
            var currentIds = TextIds(node, "Depot", "Area", expected.Keys);
            foreach (var (key, value) in expected)
            {
                Assert.Equal(originalIds[key], currentIds[key]);
                Assert.Equal(value, items.Get(key)?.Area);
            }

            await DeleteAsync(expected.Keys.ToArray());
            Assert.Equal(0, node.Counters.TextEntries);
            Assert.Equal(empty, WriterBytes(node));
            Assert.Null(WriterPage(node, 0));
            Assert.Null(WriterPage(node, 1));
        }
        finally
        {
            worker.Release();
        }

        async Task InsertAsync(int first, int last)
        {
            var write = node.CreateWriteBatch();
            for (var key = first; key <= last; key++)
            {
                var text = TextOf(key);
                write.Insert(new TextSchema.Depot { Id = key, Area = text });
                expected.Add(key, text);
            }

            var inserted = await write.CommitAsync().AsTask().WaitAsync(Bound);
            Assert.True(inserted.Changed);
            await node.WaitForPositionAsync(inserted.Position);
        }

        async Task DeleteAsync(long[] keys)
        {
            var write = node.CreateWriteBatch();
            foreach (var key in keys)
            {
                write.Delete<TextSchema.Depot>(key);
                Assert.True(expected.Remove(key));
            }

            var removed = await write.CommitAsync().AsTask().WaitAsync(Bound);
            Assert.True(removed.Changed);
            await node.WaitForPositionAsync(removed.Position);
            node.DrainRetired();
        }

        async Task<(object Page, long Bytes)> ReplaceWhileHeldAsync(long key, string replacement)
        {
            var previousId = TextIds(node, "Depot", "Area", [key])[key];
            var previousBytes = Encoding.UTF8.GetBytes(expected[key]);
            Assert.True(records.TryGet(key, out _, out var previousRevision));
            using var pause = new ViewerPause();
            var reader = OwnThread.Run(() =>
            {
                Assert.True(records.TryView(key, view =>
                {
                    var bytes = view["Area"].GetPart(0);
                    Assert.True(bytes.SequenceEqual(previousBytes));
                    pause.Hold();
                    Assert.True(bytes.SequenceEqual(previousBytes));
                    return view.Revision;
                }, out var readRevision));
                return readRevision;
            });
            object page;
            long bytes;
            try
            {
                await pause.EnteredAsync(reader);
                var replaced = await items.ReplaceAsync(new TextSchema.Depot { Id = key, Area = replacement }).AsTask().WaitAsync(Bound);
                Assert.True(replaced.Changed);
                await node.WaitForPositionAsync(replaced.Position);
                node.DrainRetired();
                Assert.Equal(Boundary + 1, node.Counters.TextEntries);
                Assert.NotNull(Entry(node, previousId));
                var currentId = TextIds(node, "Depot", "Area", [key])[key];
                Assert.NotEqual(previousId, currentId);
                Assert.Equal(Boundary, Entry(node, currentId)!.Value.WriterOrdinal);
                Assert.Equal(replacement, items.Get(key)?.Area);
                expected[key] = replacement;
                var active = WriterPage(node, 1);
                Assert.NotNull(active);
                page = active;
                bytes = WriterBytes(node);
                Assert.Null(WriterPage(node, 2));
            }
            finally
            {
                pause.Release();
                Assert.Equal(previousRevision, await reader.WaitAsync(Bound));
            }

            node.DrainRetired();
            Assert.Null(Entry(node, previousId));
            Assert.Equal(Boundary, node.Counters.TextEntries);
            return (page, bytes);
        }
    }

    private static object? WriterPage(EngineDatabase node, uint page) =>
        UnderWrites(node, parts => parts.Arena.Texts.CaptureWriterPage(page));

    private static long WriterBytes(EngineDatabase node) => Bytes(Capacity(node).WriterMetadata);

    private static string TextOf(int key) => new string('p', 96) + key.ToString("D4", CultureInfo.InvariantCulture);
}
