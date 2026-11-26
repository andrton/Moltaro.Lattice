using System.Globalization;
using Moltaro.Lattice.Engine.Maintenance;
using Moltaro.Lattice.Engine.Tests.Engine.Views;
using Moltaro.Lattice.Storage.Values;
using static Moltaro.Lattice.Engine.Tests.Engine.Text.TextMetadataTestAccess;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// A saturated raw retirement prefix cannot starve a pending text resize.
public sealed class TextMetadataMaintenanceTests
{
    [Fact]
    public async Task Text_maintenance_shares_the_bound_with_a_saturated_retirement_prefix()
    {
        const int Count = 200;
        var last = default(MaintenanceTurnSample);
        await using var node = await EngineMaster.OpenAsync(TextSchema.Utf8, new EngineTestInputs
        {
            DisableBackgroundMaintenance = true,
            ObserveMaintenanceTurn = sample => last = sample,
        });

        var batch = node.CreateWriteBatch();
        for (var id = 1; id <= Count; id++)
        {
            batch.Insert(new TextSchema.Depot { Id = id, Area = "region-" + id.ToString(CultureInfo.InvariantCulture) });
        }

        await batch.CommitAsync();
        for (var turn = 0; Maintain(node, 64) != 0; turn++)
        {
            Assert.True(turn < Count, "The populated table did not settle.");
        }

        var grown = Capacity(node);
        Assert.Empty(grown.OldBuckets);

        var deletes = node.CreateWriteBatch();
        for (var id = 2; id <= Count; id++)
        {
            deletes.Delete<TextSchema.Depot>((long)id);
        }

        await deletes.CommitAsync();
        node.DrainRetired();
        Assert.Equal(1, node.Counters.TextEntries);
        Assert.Equal(Bytes(grown.ActiveBuckets), Bytes(Capacity(node).ActiveBuckets));

        var text = new string('p', 48 * ValueBlocks.ChunkPayload);
        var place = node.Set<TextSchema.Place>();
        await place.InsertAsync(new TextSchema.Place { Id = 1, Label = text });
        var records = node.Set("Place");
        using var pause = new ViewerPause();
        var held = OwnThread.Run(() =>
        {
            Assert.True(records.TryView(1L, view =>
            {
                var value = view["Label"];
                pause.Hold();
                return value.ByteLength;
            }, out var bytes));
            return bytes;
        });
        await pause.EnteredAsync(held);
        await place.DeleteAsync(1L);
        Assert.True(node.Counters.Arena.RetiredBytes > text.Length);
        pause.Release();
        Assert.Equal(text.Length, await held.WaitAsync(Bound));
        Assert.Empty(Capacity(node).OldBuckets);

        var retiredBefore = node.Counters.Arena.RetiredBytes;
        Assert.True(node.TryRunMaintenanceTurn());
        Assert.True(last.Admitted);
        Assert.Equal(1, last.TextMetadataWork);
        Assert.Equal(AutomaticMaintenance.ReclamationWorkLimit, last.ReclamationWork);
        Assert.InRange(node.Counters.Arena.RetiredBytes, 1, retiredBefore - 1);
        Assert.NotEmpty(Capacity(node).OldBuckets);
    }
}
