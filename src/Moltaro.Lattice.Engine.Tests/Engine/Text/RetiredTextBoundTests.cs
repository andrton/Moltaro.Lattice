using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine.Text;

// What waits between two looks of the drain is bounded by the two thresholds of a look, the items and the
// bytes retired since the last one (docs/design/arena-protocol.md, "Deferred reclamation"), and that holds
// for the text of a Dictionary field as for a Plain value. A text whose last reference lies in a retired
// root is freed with that root, so its bytes wait for the same look; they count toward it as the bytes of
// the root's own blocks do, or a database whose texts change keeps the dead ones live until the item count
// alone makes a look.
//
// One entity has its text of 1 MiB replaced by a distinct text of 1 MiB, fewer times than the slots of the
// protection of reads, which is the smallest count of items that makes a look, with no read in progress and
// no drain asked for. The same sequence runs with the field Plain and with the field Dictionary, each in a
// database of its own, and after every write the memory the arena holds for the entity is read: its live
// and retired bytes with their slack. With Dictionary storage the arena never holds more than twice what it
// holds at most with Plain storage; the text and its blocks are placed alike in both.
public sealed class RetiredTextBoundTests
{
    private const int Mebibyte = 1024 * 1024;

    // Fewer than the 128 slots the protection of reads has at least.
    private const int Replaces = 100;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Plain",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            },
            {
              "Id": 2, "Name": "Shared",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String", "StringStorage": "Dictionary" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact]
    public async Task Text_whose_last_reference_lies_in_a_retired_root_waits_for_a_look_no_longer_than_a_Plain_value()
    {
        var plain = await MostHeldAsync("Plain");
        var shared = await MostHeldAsync("Shared");

        Assert.True(
            shared <= 2 * plain,
            $"Replacing a text of 1 MiB {Replaces} times, the arena held at most {shared} bytes with Dictionary storage, more than twice the {plain} bytes it held at most with Plain storage.");
    }

    // The most bytes the arena holds after any write of the sequence: live and retired, with their slack.
    private static async Task<long> MostHeldAsync(string entityType)
    {
        await using var database = await EngineMaster.OpenAsync(Schema, new EngineTestInputs { DisableBackgroundMaintenance = true });
        var set = database.Set(entityType);
        var most = 0L;

        for (var write = 0; write <= Replaces; write++)
        {
            var record = new LatticeRecord(set.EntityType) { ["Id"] = 1L, ["Text"] = TextOf(write) };

            await (write == 0 ? set.InsertAsync(record) : set.ReplaceAsync(record));

            var arena = database.Counters.Arena;

            most = Math.Max(most, arena.LiveBytes + arena.ClassSlackBytes + arena.RetiredBytes + arena.PreparedBytes);
        }

        return most;
    }

    // A text of 1 MiB of UTF-8 that begins with the number of the write, so that no two are equal.
    private static string TextOf(int write) => string.Create(Mebibyte, write, static (span, write) =>
    {
        span.Fill('t');
        write.TryFormat(span, out _, "D3", CultureInfo.InvariantCulture);
    });
}
