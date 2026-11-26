using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Text;

// A master places the root a child forwards a value at a time, as a child places a root it receives, and
// each Dictionary text takes its reference as it is placed. Whatever the master frees of such a root, it
// gives back the references the root took: a forwarded write leaks no count (decision on strings, Q04.4),
// and the counts of the master and of the child equal the independent tally of what they hold.
// - A forwarded write whose first Dictionary text is one the master holds, and whose later Plain value the
//   arena of the master cannot hold beside what it stores, is refused for memory once the master has placed
//   its texts: the child's call ends in WriteRejected with Limit Memory, the entry keeps the count it had on
//   both nodes, and the same write with a value that fits succeeds. For an insert and for a replace. The
//   master has an arena of two segments, 8 MiB, and stores a Plain value of 5 MiB; the forwarded value takes
//   5 MiB as well, below the memory limit, so the master receives the request whole and places it rather
//   than refusing it from the length it announces.
// - A replace or an upsert forwarded under another spelling of a Dictionary key compared without case keeps
//   the spelling the entity was created with: the master places the root the child sent, reads it, frees it
//   and writes the entity again with the stored key. The spelling the child sent leaves no entry, and the
//   text the write brought has one reference, on the master and on the child.
public sealed class ForwardedTextTests
{
    private const int Mebibyte = 1024 * 1024;

    // The fixed part of a Code: the header and a descriptor of 16 bytes for each of its two Strings.
    private const int CodeFixedSize = 32 + (2 * 16);

    private static readonly ArenaOptions TwoSegments = new() { MemoryLimit = 15L * Mebibyte };

    // Code, bound to the class Code: the key Name, a String with Dictionary storage compared without case,
    // and Body, a String with Dictionary storage.
    private static readonly LatticeSchema CodeSchema = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Code", "ClrType": "{{typeof(Code).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Name", "Type": "String", "Comparison": "OrdinalIgnoreCase", "StringStorage": "Dictionary" },
                { "Id": 2, "Name": "Body", "Type": "String", "StringStorage": "Dictionary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Theory]
    [InlineData("insert")]
    [InlineData("replace")]
    public async Task Forwarded_write_refused_for_memory_after_it_took_a_reference_to_a_text_the_master_holds_leaves_the_counts_as_they_were(string kind)
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, new EngineTestInputs { Arena = TwoSegments });
        var tally = new TextTally("Utf8");

        foreach (var stored in new[]
        {
            new TextSchema.Place { Id = 1, Region = "North", Label = "label" },
            new TextSchema.Place { Id = 2, Region = "South", Label = new string('m', 5 * Mebibyte) },
        })
        {
            await master.Set<TextSchema.Place>().InsertAsync(stored);
            tally.Written(stored);
        }

        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var places = child.Set<TextSchema.Place>();
        var id = kind == "insert" ? 3 : 1;
        var refusedValues = new TextSchema.Place { Id = id, Number = 1, Region = "North", Zone = "forwarded", Label = new string('x', 5 * Mebibyte) };

        var refused = await Assert.ThrowsAsync<LatticeException>(() => (kind == "insert" ? places.InsertAsync(refusedValues) : places.ReplaceAsync(refusedValues)).AsTask());

        Assert.Equal(LatticeErrorEnum.WriteRejected, refused.Error);
        Assert.Equal(LatticeLimitEnum.Memory, refused.Limit);

        foreach (var node in new[] { master, child })
        {
            Assert.Equal(1, EntryCounts.Of(node, "Utf8")["North"]);
            EntryCounts.ExpectEqual(tally, node);
            tally.ExpectHeldBy(node);
        }

        // The same write, its Plain value small enough; it returns once the child has applied it.
        var fitting = new TextSchema.Place { Id = id, Number = 2, Region = "North", Zone = "forwarded", Label = "label" };

        await (kind == "insert" ? places.InsertAsync(fitting) : places.ReplaceAsync(fitting));
        tally.Written(fitting);

        foreach (var node in new[] { master, child })
        {
            EntryCounts.ExpectEqual(tally, node);
            tally.ExpectHeldBy(node);
        }
    }

    [Fact]
    public async Task Replace_and_upsert_forwarded_under_another_spelling_of_a_Dictionary_key_leave_no_entry_of_that_spelling()
    {
        await using var master = await ReplicationNodes.OpenMasterAsync(CodeSchema);
        await master.Set<Code>().InsertAsync(new Code { Name = "Main", Body = "first" });
        await using var child = await ReplicationNodes.OpenChildAsync(master.ListenUri!);
        var codes = child.Set<Code>();

        await codes.ReplaceAsync(new Code { Name = "MAIN", Body = "second" });

        await ExpectOneCodeAsync(master, child, "second");

        await codes.UpsertAsync(new Code { Name = "mAIN", Body = "third" });

        await ExpectOneCodeAsync(master, child, "third");
    }

    // Each node holds the one Code, Main with the body given, read under another spelling: the entries Main
    // and the body, each with one reference, and in the arena the root and the two entries and nothing else.
    // A write through the child has returned once the child applied it, so the child is at the position of
    // the master.
    private static async Task ExpectOneCodeAsync(EngineDatabase master, EngineDatabase child, string body)
    {
        await child.WaitForPositionAsync(master.Position);

        foreach (var node in new[] { master, child })
        {
            var read = node.Set<Code>().Get("main");

            Assert.Equal(("Main", body), (read?.Name, read?.Body));
            Assert.Equal(
                new[] { KeyValuePair.Create("Main", 1L), KeyValuePair.Create(body, 1L) }.OrderBy(entry => entry.Key, StringComparer.Ordinal),
                EntryCounts.Of(node, "Utf8").OrderBy(entry => entry.Key, StringComparer.Ordinal));

            var counters = node.Counters;
            var expected = StoredBytes.Of(CodeFixedSize) + StoredBytes.OutOfLine("Main".Length) + StoredBytes.OutOfLine(body.Length);

            Assert.Equal(2, counters.TextEntries);
            Assert.Equal(1, counters.RootEntries);
            Assert.Equal((0L, 0L), (counters.Arena.PreparedBytes, counters.Arena.RetiredBytes));
            Assert.Equal(expected, new StoredBytes(counters.Arena.LiveBytes, counters.Arena.ClassSlackBytes));
        }
    }

    public sealed class Code
    {
        public string Name { get; set; } = "";

        public string Body { get; set; } = "";
    }
}
