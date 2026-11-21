using Moltaro.Lattice.Engine.Tests.Engine.Text;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Engine.Writes;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication.Text;

// The encoded size of a write (PreparedWrite.EncodedSize) is the bytes of the slots it prepared in the
// arena: its root and every block of its values, a new entry of the text table among them. It is what
// bounds a commit group, at most CommitGroup.MaxEncodedSize bytes (docs/design/milestone-1-engine-contract.md,
// the table of limits, "Internal group"), and nothing there sets a forwarded root apart: the same write
// counts the same whoever sent it. A master places the root a child forwards from the bytes the child sent,
// and a Dictionary text the master does not hold becomes a new entry there as it does for a write made on
// the master.
// - On a master that holds no text, an insert with a new Dictionary text of 1,000 characters makes its one
//   commit group, LargestCommitGroupBytes, the slots of its root and of the entry of the text: the same
//   figure whether it was made on that master or forwarded by a child of another master that holds no text
//   either. In a UTF-8 and a UTF-16 database, where the entry holds the text in the encoding of the database.
// - While the storage step of a master holds a first write (HeldPersistence), a child forwards an insert
//   with a new Dictionary text of 5 MiB, and the master then takes an insert with another new text of 5 MiB.
//   Together they take more than a group, so each forms a group of its own, which holds the size of its root
//   and of the entry of its text. The test knows that each write is prepared before it submits the next by
//   the prepared bytes of the arena (CountersWait), which count every block the arena allocated, an entry
//   among them, whoever sent the write.
public sealed class ForwardedTextSizeTests
{
    private const int Mebibyte = 1024 * 1024;

    public static IEnumerable<TheoryDataRow<string>> Encodings => TextSchema.Encodings;

    [Theory]
    [MemberData(nameof(Encodings))]
    public async Task Write_with_a_new_Dictionary_text_counts_the_same_in_the_bytes_of_its_commit_group_whether_made_on_the_master_or_forwarded_by_a_child(string encoding)
    {
        var schema = TextSchema.Of(encoding);
        var place = new TextSchema.Place { Id = 1, Number = 1, Region = new string('r', 1_000), Label = "label" };

        await using var master = await EngineMaster.OpenAsync(schema);
        await master.Set<TextSchema.Place>().InsertAsync(place);

        // A write through a child returns once the child has applied it, so its master has published it.
        await using var forwardedTo = await ReplicationNodes.OpenMasterAsync(schema);
        await using var child = await ReplicationNodes.OpenChildAsync(forwardedTo.ListenUri!);
        await child.Set<TextSchema.Place>().InsertAsync(place);

        var made = master.Counters.LargestCommitGroupBytes;
        var forwarded = forwardedTo.Counters.LargestCommitGroupBytes;

        Assert.Equal(EncodedSizeOf(encoding, place), made);
        Assert.Equal(made, forwarded);
    }

    [Fact]
    public async Task Write_forwarded_by_a_child_with_a_new_Dictionary_text_of_5_MiB_takes_a_commit_group_apart_from_a_write_of_5_MiB_made_on_the_master()
    {
        var storage = new HeldPersistence(holding: false);
        await using var master = await ReplicationNodes.OpenMasterAsync(TextSchema.Utf8, new EngineTestInputs { Persistence = storage });

        try
        {
            // The child waits beyond the bound for the write it forwards, which the test holds.
            await using var child = await ReplicationNodes.OpenChildAsync(ReplicationNodes.ChildWaitingBeyondTheBound(master.ListenUri!), new EngineTestInputs());
            var first = new TextSchema.Place { Id = 1, Region = "North", Label = "label" };
            var forwarded = new TextSchema.Place { Id = 2, Region = new string('f', 5 * Mebibyte), Label = "label" };
            var made = new TextSchema.Place { Id = 3, Region = new string('m', 5 * Mebibyte), Label = "label" };

            Assert.True(EncodedSizeOf("Utf8", forwarded) + EncodedSizeOf("Utf8", made) > CommitGroup.MaxEncodedSize, "The two writes of 5 MiB fit one group.");

            storage.Hold();
            var firstWrite = master.Set<TextSchema.Place>().InsertAsync(first).AsTask();
            await storage.NextAsync();

            var forwardedWrite = child.Set<TextSchema.Place>().InsertAsync(forwarded).AsTask();
            await CountersWait.PreparedAsync(master, EncodedSizeOf("Utf8", first) + EncodedSizeOf("Utf8", forwarded));

            var madeWrite = master.Set<TextSchema.Place>().InsertAsync(made).AsTask();
            await CountersWait.PreparedAsync(master, EncodedSizeOf("Utf8", first) + EncodedSizeOf("Utf8", forwarded) + EncodedSizeOf("Utf8", made));

            storage.ReleaseAll();
            await Task.WhenAll(firstWrite, forwardedWrite, madeWrite).WaitAsync(Bound);

            var commits = storage.Commits;

            Assert.Equal(new[] { 1, 1, 1 }, commits.Select(commit => commit.Count));
            Assert.Equal(new[] { first, forwarded, made }.Select(place => EncodedSizeOf("Utf8", place)), commits.Select(commit => commit.Bytes));
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    // The encoded size of an insert of the place on a database that holds none of its texts: the slots of
    // its root, with its Plain Label, and of a new entry for each of its Dictionary texts that is not null
    // or empty, which differ from each other, in the encoding of the database.
    private static long EncodedSizeOf(string encoding, TextSchema.Place place)
    {
        var bytes = TextTally.EncodingOf(encoding);
        var stored = StoredBytes.Of(TextSchema.PlaceFixedSize, bytes.GetByteCount(place.Label));

        foreach (var text in new[] { place.Region, place.Zone })
        {
            if (!string.IsNullOrEmpty(text))
            {
                stored += StoredBytes.OutOfLine(bytes.GetByteCount(text));
            }
        }

        return stored.LiveBytes + stored.ClassSlackBytes;
    }
}
