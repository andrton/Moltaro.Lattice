using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.TestSupport.Positions;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Writes;

// The commit stage of the write coordinator (brief of Epic 1, sprint S09, Acceptance, "No wait to fill
// a group in MemoryOnly", "A write larger than a group", "What is counted" and the answers of the
// planner on the counters and the positions). The persistence step of the test holds a commit until the
// test releases it (HeldPersistence), so the commit stage is busy while the test submits more
// writes, and the test waits until they are all prepared (CountersWait) before it lets the stage go
// on. So:
// - While one commit is held, later writes are admitted and prepared at once and wait for the commit
//   stage: QueuedWrites counts every write admitted and not completed, and PreparedBytes holds the
//   prepared capacity of each, the slots of its root and of its values (PieceTally.SlotBytesOf). Once
//   the stage is free it takes them into the next group without waiting to fill it, and when every
//   call has returned QueuedWrites is 0.
// - A group holds at most CommitGroup.MaxOperations writes, 32: of 40 writes prepared together the
//   next group takes 32 and the one after it 8. The writes of a group take consecutive positions from
//   CommitOutcome.FirstSequence in the order of the group, which the storage of the test assigns as the
//   storage of MemoryOnly does; each write is known by its encoded size, which differs from every
//   other's. LargestCommitGroup and LargestCommitGroupBytes are the maxima of the groups.
// - A group holds at most CommitGroup.MaxEncodedSize bytes, 8 MiB, of the encoded sizes of its writes;
//   a write larger than that, an entity of 32 MiB here, forms a group alone and completes with the
//   small writes around it. It counts in both maxima, which are taken independently of each other.
// - Publication only installs what was prepared (brief of S09, "Gates"; Acceptance: publication
//   performs no allocation of pool capacity): from the moment the persistence step holds a write, which
//   is prepared then with everything its publication needs, to the moment its call has returned, the
//   arena obtains no segment and holds no more capacity, the primary index does not grow, and the
//   table of root entries gains no entry. Inserts that grow the primary index several times, replaces
//   and deletes.
// - No prepared write is left without a commit stage (Acceptance: writes submitted at once all
//   complete; milestone contract, "Write coordinator and revision semantics": whoever completes the
//   persistence step publishes its group and hands over the next). While the persistence step holds the group
//   of one write, eight writes of other entities are prepared and their calls return their tasks; no
//   call of the test runs anything after that. The test completes the persistence step from a thread of
//   its own: the first write is published, and the eight are dispatched as the next group, which the
//   storage holds in turn; completing that step publishes the eight. The storage of the test only takes
//   a group in its call and never blocks there, as a persistence step has to (IPersistence.CommitAsync).
//   Its completion runs what waits for it on a thread of the pool, and, as the durable persistence step of
//   S27 will, on the thread that completes its task (review round 1 of S09, part 2: a storage of the
//   tests that completes only on the pool hid that form).
// Every wait is bounded; the storage releases every commit when a test ends.
public sealed class CommitGroupTests
{
    private const int Mebibyte = 1024 * 1024;

    // Where the completion of a held persistence step runs what waits for it.
    private const string OnThePool = "on a thread of the pool";

    private const string OnTheReleasingThread = "on the thread that completes its task";

    [Fact]
    public async Task Writes_submitted_while_a_commit_is_held_are_admitted_and_prepared_and_wait_for_the_commit_stage()
    {
        var storage = new HeldPersistence();
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            var first = PieceSchema.Of(0, name: 10);
            var firstWrite = pieces.InsertAsync(first).AsTask();
            var held = await storage.NextAsync();

            PieceSchema.Piece[] later = [.. Enumerable.Range(1, 3).Select(SmallOf)];
            var writes = later.Select(piece => pieces.InsertAsync(piece).AsTask()).ToArray();

            var counters = await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf([first, .. later]));

            Assert.Equal(4, counters.QueuedWrites);
            Assert.Equal(1, held.Count);
            Assert.False(firstWrite.IsCompleted, "The write whose commit is held completed.");
            Assert.DoesNotContain(writes, write => write.IsCompleted);

            storage.ReleaseAll();
            await Task.WhenAll([firstWrite, .. writes]).WaitAsync(Bound);

            Assert.Equal(new[] { 1, 3 }, storage.Commits.Select(commit => commit.Count));
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of([first, .. later]).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Group_holds_at_most_32_writes_and_its_writes_take_consecutive_positions_in_its_order()
    {
        var storage = new HeldPersistence();
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            var first = PieceSchema.Of(0, name: 10);
            var firstWrite = pieces.InsertAsync(first).AsTask();
            await storage.NextAsync();

            PieceSchema.Piece[] later = [.. Enumerable.Range(1, 40).Select(SmallOf)];
            var writes = later.Select(piece => pieces.InsertAsync(piece).AsTask()).ToArray();
            PieceSchema.Piece[] all = [first, .. later];

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(all));
            storage.ReleaseAll();
            var results = await Task.WhenAll([firstWrite, .. writes]).WaitAsync(Bound);

            var commits = storage.Commits;
            var sizes = all.Select(PieceTally.SlotBytesOf).ToArray();
            var sequenceOf = sizes.Zip(results).ToDictionary(pair => pair.First, pair => PositionSequence.Of(pair.Second.Position));

            Assert.Equal(all.Length, sequenceOf.Count);
            Assert.Equal(new[] { 1, 32, 8 }, commits.Select(commit => commit.Count));
            Assert.Equal(sizes.Order(), commits.SelectMany(commit => commit.Sizes).Order());

            foreach (var commit in commits)
            {
                for (var place = 0; place < commit.Count; place++)
                {
                    Assert.Equal(commit.FirstSequence + (ulong)place, sequenceOf[commit.Sizes[place]]);
                }
            }

            var counters = database.Counters;

            Assert.Equal(32, counters.LargestCommitGroup);
            Assert.Equal(commits.Max(commit => commit.Bytes), counters.LargestCommitGroupBytes);
            Assert.Equal(all.Length, counters.PublishedSequence);
            Assert.Equal(0, counters.QueuedWrites);
            PieceTally.Of(all).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Group_holds_at_most_8_MiB_and_a_write_larger_than_that_forms_a_group_alone_and_completes()
    {
        var storage = new HeldPersistence();
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            var first = PieceSchema.Of(0, name: 10);
            var firstWrite = pieces.InsertAsync(first).AsTask();
            await storage.NextAsync();

            // Three writes of 3 MiB, of which two fit a group and three do not, an entity of 32 MiB, and
            // small writes between them.
            var large = PieceSchema.Of(4, name: 10, note: NoteOf(4), data: 32 * Mebibyte);
            PieceSchema.Piece[] later =
            [
                PieceSchema.Of(1, name: 10, note: NoteOf(1), data: 3 * Mebibyte),
                SmallOf(5),
                large,
                PieceSchema.Of(2, name: 10, note: NoteOf(2), data: 3 * Mebibyte),
                SmallOf(6),
                PieceSchema.Of(3, name: 10, note: NoteOf(3), data: 3 * Mebibyte),
                SmallOf(7),
                SmallOf(8),
            ];
            var writes = later.Select(piece => pieces.InsertAsync(piece).AsTask()).ToArray();
            PieceSchema.Piece[] all = [first, .. later];

            await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf(all));
            storage.ReleaseAll();
            await Task.WhenAll([firstWrite, .. writes]).WaitAsync(Bound);

            var commits = storage.Commits;
            var largeBytes = PieceTally.SlotBytesOf(large);

            Assert.True(largeBytes > CommitGroup.MaxEncodedSize, "The large write is not larger than a group.");
            Assert.Equal(all.Length, commits.Sum(commit => commit.Count));
            Assert.All(commits, commit => Assert.True(commit.Count <= CommitGroup.MaxOperations, $"A group held {commit.Count} writes."));
            Assert.All(commits, commit => Assert.True(commit.Bytes <= CommitGroup.MaxEncodedSize || commit.Count == 1, $"A group of {commit.Count} writes held {commit.Bytes} bytes."));
            Assert.Contains(commits, commit => commit.Count == 1 && commit.Sizes[0] == largeBytes);

            var counters = database.Counters;

            Assert.Equal(largeBytes, counters.LargestCommitGroupBytes);
            Assert.Equal(commits.Max(commit => commit.Count), counters.LargestCommitGroup);
            Assert.True(counters.LargestCommitGroup > 1, "No group held more than one write, so the two maxima were not taken from two groups.");
            Assert.Equal(0, counters.QueuedWrites);
            PieceTally.Of(all).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
        }
    }

    [Fact]
    public async Task Publication_obtains_no_memory_grows_no_index_and_adds_no_root_entry()
    {
        var storage = new HeldPersistence();
        await using var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();
        var tally = new PieceTally();

        try
        {
            // 64 inserts, which grow the primary index from its first table several times.
            for (var id = 1; id <= 64; id++)
            {
                var piece = PieceSchema.Of(id, name: 10, note: 100);

                await ExpectNothingObtainedAsync($"the insert of {id}", pieces.InsertAsync(piece).AsTask());
                tally.Written(piece);
            }

            for (var id = 1; id <= 8; id++)
            {
                var piece = PieceSchema.Of(id, name: 10, note: 2_000, data: 70_000);

                await ExpectNothingObtainedAsync($"the replace of {id}", pieces.ReplaceAsync(piece).AsTask());
                tally.Written(piece);
            }

            for (var id = 9; id <= 16; id++)
            {
                await ExpectNothingObtainedAsync($"the delete of {id}", pieces.DeleteAsync((long)id).AsTask());
                tally.Deleted(id);
            }

            tally.ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
        }

        async Task ExpectNothingObtainedAsync(string write, Task<LatticeWriteResult> submitted)
        {
            var commit = await storage.NextAsync();
            var before = database.Counters;

            commit.Release();
            Assert.True((await submitted.WaitAsync(Bound)).Changed);

            var after = database.Counters;

            Assert.True(after.Arena.Segments <= before.Arena.Segments, $"The publication of {write} obtained a segment.");
            Assert.True(CapacityOf(after) <= CapacityOf(before), $"The arena held more capacity after the publication of {write}.");
            Assert.True(after.PrimaryIndexBytes <= before.PrimaryIndexBytes, $"The primary index grew during the publication of {write}.");
            Assert.True(after.RootEntries <= before.RootEntries, $"The publication of {write} added a root entry.");
        }
    }

    [Theory]
    [InlineData(OnThePool)]
    [InlineData(OnTheReleasingThread)]
    public async Task Writes_prepared_while_the_persistence_step_holds_a_group_are_dispatched_and_published_by_whoever_completes_the_persistence_step(string completion)
    {
        var storage = new HeldPersistence(inline: completion == OnTheReleasingThread);
        var database = await OpenAsync(storage);
        var pieces = database.Set<PieceSchema.Piece>();

        try
        {
            // The group of the first write is in the persistence step; its call has returned its task.
            var first = PieceSchema.Of(0, name: 10);
            var firstWrite = pieces.InsertAsync(first).AsTask();
            var held = await storage.NextAsync();

            // Prepared while the persistence step holds that group; each call returns its task.
            PieceSchema.Piece[] later = [.. Enumerable.Range(1, 8).Select(SmallOf)];
            var writes = later.Select(piece => pieces.InsertAsync(piece).AsTask()).ToArray();
            var counters = await CountersWait.PreparedAsync(database, PieceTally.SlotBytesOf([first, .. later]));

            Assert.Equal(9, counters.QueuedWrites);
            Assert.Equal(1, held.Count);
            Assert.False(firstWrite.IsCompleted, "The write whose commit is held completed.");
            Assert.DoesNotContain(writes, write => write.IsCompleted);

            // Completing the persistence step publishes the first write and dispatches the eight.
            held.Release();

            Assert.True((await firstWrite.WaitAsync(Bound)).Changed);

            var next = await storage.NextAsync();

            Assert.Equal(later.Select(PieceTally.SlotBytesOf).Order(), next.Sizes.Order());
            Assert.DoesNotContain(writes, write => write.IsCompleted);

            // Completing that one publishes them.
            next.Release();

            var results = await Task.WhenAll(writes).WaitAsync(Bound);

            Assert.All(results, result => Assert.True(result.Changed));
            Assert.Equal(0, database.Counters.QueuedWrites);
            PieceTally.Of([first, .. later]).ExpectHeldBy(database);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    private static async Task<EngineDatabase> OpenAsync(HeldPersistence storage) =>
        await EngineMaster.OpenAsync(PieceSchema.Schema, new EngineTestInputs { Persistence = storage });

    // A small piece whose Note, in a block of its own, makes its encoded size differ from that of
    // every other piece of another id up to 55: blocks of 16-byte steps.
    private static PieceSchema.Piece SmallOf(int id) => PieceSchema.Of(id, name: 10, note: NoteOf(id));

    private static int NoteOf(int id) => 100 + (16 * id);

    // The capacity of every slot in use: live, slack, prepared and retired.
    private static long CapacityOf(EngineCounters counters) =>
        counters.Arena.LiveBytes + counters.Arena.ClassSlackBytes + counters.Arena.PreparedBytes + counters.Arena.RetiredBytes;
}
