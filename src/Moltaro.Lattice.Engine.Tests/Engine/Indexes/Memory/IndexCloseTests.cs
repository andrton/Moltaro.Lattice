using System.Runtime.CompilerServices;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine.Protection;
using Moltaro.Lattice.Engine.Tests.Engine.Writes;
using Moltaro.Lattice.Engine.Tests.Storage;
using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;
using static Moltaro.Lattice.TestSupport.Concurrency.TestWait;

namespace Moltaro.Lattice.Engine.Tests.Engine.Indexes.Memory;

// A close completes only when the memory of the indexes is returned (docs/design/ordered-index.md: the
// nodes are blocks of the arena; SecondaryIndex: the close frees them with the arena). The database is
// opened with an allocator that counts the native memory its arena obtains and returns
// (CountingNativeAllocator, exception X3), and a test states what no counter of a closed database shows:
// once the close has completed, every piece of native memory was returned, exactly once. It holds
// - after writes that split, merge and empty the trees of three indexes, text keys longer than 64 bytes
//   among them;
// - for a close while a read is held across writes that replaced nodes, which completes only once the
//   read has left;
// - for an instance that a write changing indexed keys faulted, with a write prepared behind it that
//   holds the nodes it reserved (EngineDatabase.ArmFault).
// Every wait is bounded. And a closed instance that an application keeps alive holds nothing it gave out
// for its indexes (EngineDatabase, "The close": an instance kept alive after its close holds none of its
// parts): neither the index objects of its sets, through a class and by name, nor a search through one
// that took a step and was not disposed, which weak references show.
public sealed class IndexCloseTests
{
    private static readonly ArenaOptions Arena = new() { MemoryLimit = 64L * new ArenaOptions().SegmentSize };

    [Fact]
    public async Task Close_after_writes_that_split_merge_and_empty_the_trees_returns_every_piece_of_native_memory()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, Arena, allocator);
        var ranked = database.Set<IndexSchema.Ranked>();

        for (var id = 1L; id <= 2_000; id++)
        {
            await ranked.InsertAsync(StateOf(id, 0));
        }

        for (var id = 1L; id <= 2_000; id += 2)
        {
            await ranked.ReplaceAsync(StateOf(id, 1));
        }

        for (var id = 1L; id <= 1_500; id++)
        {
            await ranked.DeleteAsync(id);
        }

        await database.CloseAsync().AsTask().WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Close_while_a_read_is_held_across_writes_that_replaced_nodes_returns_every_piece_of_native_memory_once_the_read_has_left()
    {
        var allocator = new CountingNativeAllocator();
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, Arena, allocator);
        var ranked = database.Set<IndexSchema.Ranked>();

        for (var id = 1L; id <= 200; id++)
        {
            await ranked.InsertAsync(StateOf(id, 0));
        }

        using var held = await HeldRead.StartAsync(database, () => ranked.Get(1L)?.Number);

        await Task.Run(async () =>
        {
            for (var id = 1L; id <= 200; id++)
            {
                await ranked.ReplaceAsync(StateOf(id, 1));
            }
        }).WaitAsync(Bound);

        var closing = database.CloseAsync().AsTask();

        Assert.False(closing.IsCompleted, "The close completed while a read was in progress.");
        Assert.True(allocator.Outstanding > 0, "The native memory was returned while a read was in progress.");

        await held.ReleaseAsync();
        await closing.WaitAsync(Bound);

        allocator.ExpectEverythingReturned();
    }

    [Theory]
    [InlineData(nameof(EngineFaultPointEnum.BeforePublication))]
    [InlineData(nameof(EngineFaultPointEnum.DuringPublication))]
    public async Task Close_of_an_instance_that_a_write_changing_indexed_keys_faulted_returns_every_piece_of_native_memory(string point)
    {
        var allocator = new CountingNativeAllocator();
        var storage = new HeldCommitStorage(holding: false);
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8, new EngineTestInputs { Arena = Arena, Allocator = allocator, CommitStorage = storage });

        try
        {
            var ranked = database.Set<IndexSchema.Ranked>();

            for (var id = 1L; id <= 100; id++)
            {
                await ranked.InsertAsync(StateOf(id, 0));
            }

            storage.Hold();

            // The write that faults moves an entity in every index; the insert behind it has reserved
            // the nodes of its own, and ends in Unavailable without being published.
            var faulted = ranked.ReplaceAsync(StateOf(1, 1)).AsTask();
            await storage.NextAsync();
            var behind = ranked.InsertAsync(StateOf(101, 0)).AsTask();

            await CountersWait.UntilAsync(database, counters => counters.QueuedWrites == 2, "both writes are admitted");
            database.ArmFault(Enum.Parse<EngineFaultPointEnum>(point));
            storage.ReleaseAll();

            Assert.Equal(LatticeErrorEnum.IndeterminateOutcome, (await Assert.ThrowsAsync<LatticeException>(() => faulted.WaitAsync(Bound))).Error);
            Assert.Equal(LatticeDatabaseStateEnum.Faulted, database.State);
            Assert.Equal(LatticeErrorEnum.Unavailable, (await Assert.ThrowsAsync<LatticeException>(() => behind.WaitAsync(Bound))).Error);
        }
        finally
        {
            storage.ReleaseAll();
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }

        allocator.ExpectEverythingReturned();
    }

    [Fact]
    public async Task Closed_instance_that_is_kept_alive_holds_neither_the_index_objects_it_gave_out_nor_a_search_through_them()
    {
        var database = await EngineMaster.OpenAsync(IndexSchema.Utf8);
        var given = GiveOut(database);

        await database.CloseAsync().AsTask().WaitAsync(Bound);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var held = given.Where(part => part.Reference.IsAlive).Select(part => part.Name).ToList();

        Assert.True(held.Count == 0, $"The closed instance still holds {string.Join("; ", held)}.");
        GC.KeepAlive(database);
    }

    // In a frame of its own, so that nothing of the test's frame references them: the index objects of an
    // entity type through its class and by name, and a search through one that took a step, each with a
    // weak reference and its name.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<(string Name, WeakReference Reference)> GiveOut(EngineDatabase database)
    {
        var counted = database.Set<IndexSchema.Counted>();

        OwnThread.Wait(counted.InsertAsync(new IndexSchema.Counted { Id = 1, Number = 7 }));

        var byClass = counted.Index("ByNumber");
        var byName = database.Set("Counted").Index("ByNumber");
        var search = byClass.Find(IndexKey.Equal(7L)).GetEnumerator();

        Assert.True(search.MoveNext());

        return
        [
            ("the index ByNumber through the class", new WeakReference(byClass)),
            ("the index ByNumber by name", new WeakReference(byName)),
            ("a search through it that took a step", new WeakReference(search)),
        ];
    }

    // The state of the number of an entity: keys of its own in each index, a Login longer than 64 bytes for
    // every third entity, a Region of a few shared ones and one longer than 64 bytes for every fifth.
    private static IndexSchema.Ranked StateOf(long id, long number) => new()
    {
        Id = id,
        Number = (id * 7_919) + number,
        Login = id % 3 == 0 ? $"{id:D6}-{number}".PadRight(100, 'l') : $"login {id} {number}",
        Region = id % 5 == 0 ? $"region {number}".PadRight(80, 'r') : $"region {id % 4}",
    };
}
