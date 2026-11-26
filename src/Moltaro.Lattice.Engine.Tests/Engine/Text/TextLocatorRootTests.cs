using System.Globalization;
using System.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// The same native bytes are available while the published locator root
// changes between a direct leaf and a radix directory.
public sealed class TextLocatorRootTests
{
    [Fact]
    public async Task Borrowed_text_survives_locator_root_growth_and_contraction()
    {
        const long Anchor = 1;
        const int Extra = 70;
        var text = new string('x', 128);
        var bytes = Encoding.UTF8.GetBytes(text);
        await using var node = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs { DisableBackgroundMaintenance = true });
        var items = node.Set<TextSchema.Depot>();
        await items.InsertAsync(new TextSchema.Depot { Id = Anchor, Area = text });
        var original = Capacity(node);
        var records = node.Set("Depot");

        using (var sparseReader = new ViewerPause())
        {
            var beforeGrowth = Read(sparseReader);
            await sparseReader.EnteredAsync(beforeGrowth);
            var insert = node.CreateWriteBatch();
            for (var id = Anchor + 1; id <= Anchor + Extra; id++)
            {
                insert.Insert(new TextSchema.Depot { Id = id, Area = text + id.ToString(CultureInfo.InvariantCulture) });
            }

            await insert.CommitAsync();
            var grown = Capacity(node);
            Assert.True(Bytes(grown.LocatorDirectory) > Bytes(original.LocatorDirectory));
            Assert.True(Bytes(grown.WriterMetadata) > Bytes(original.WriterMetadata));
            Assert.True(Bytes(grown.ActiveBuckets) + Bytes(grown.OldBuckets) > Bytes(original.ActiveBuckets));
            sparseReader.Release();
            Assert.Equal(text, await beforeGrowth.WaitAsync(Bound));
        }

        using var older = new ViewerPause();
        var guard = Read(older);
        await older.EnteredAsync(guard);
        var remove = node.CreateWriteBatch();
        for (var id = Anchor + 1; id <= Anchor + Extra; id++)
        {
            remove.Delete<TextSchema.Depot>(id);
        }

        await remove.CommitAsync();
        Assert.Equal(0, RetireOne(node));
        using var newer = new ViewerPause();
        var beforeContraction = Read(newer);
        await newer.EnteredAsync(beforeContraction);
        older.Release();
        Assert.Equal(text, await guard.WaitAsync(Bound));

        node.DrainRetired();
        for (var turn = 0; Maintain(node, 64) != 0; turn++)
        {
            Assert.True(turn <= Extra, "The locator did not settle.");
        }

        var settled = Capacity(node);
        Assert.Equal(Bytes(original.LocatorDirectory), Bytes(settled.LocatorDirectory));
        Assert.Equal(Bytes(original.LocatorLeaves), Bytes(settled.LocatorLeaves));
        Assert.Equal(Bytes(original.WriterMetadata), Bytes(settled.WriterMetadata));
        Assert.Equal(Bytes(original.ActiveBuckets), Bytes(settled.ActiveBuckets));
        Assert.Empty(settled.OldBuckets);
        ExpectAccounting(node, settled);
        Assert.Equal(1, node.Counters.TextEntries);
        newer.Release();
        Assert.Equal(text, await beforeContraction.WaitAsync(Bound));

        Task<string> Read(ViewerPause pause) => OwnThread.Run(() =>
        {
            Assert.True(records.TryView(Anchor, view =>
            {
                var value = view["Area"];
                var old = value.GetPart(0);
                Assert.True(old.SequenceEqual(bytes));
                pause.Hold();
                Assert.True(old.SequenceEqual(bytes));
                return value.GetString();
            }, out var captured));
            return captured;
        });
    }
}
