using Moltaro.Lattice.Access;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Reads protected by epochs, with deferred reclamation (the owner's decision of 16 November 2026;
// arena protocol, "Deferred reclamation, 16 November 2026"; brief of Epic 1, sprint S08, "Acceptance,
// part 1"). A test holds one read by key inside the protection, after it has found its root and
// before it copies it (HeldRead), and states what the one writer and the counters do meanwhile:
// - The writer never waits for the read: an insert, a replace and a delete, of the entity being read
//   and of others, return while the read is held, and so does a growth of the primary index.
// - What the read could still reach is retired, not freed, and stays retired through a drain while
//   the read is held: the old root of a replaced or deleted entity in RetiredBytes, the entry of a
//   deleted one in RootEntries, the old table of a grown index in RetiredIndexBytes, beside its
//   replacement in PrimaryIndexBytes. Once the read has left, a drain frees all of it. While the
//   replacement is built, inside the insert that grows the index, it is counted in
//   ReplacementIndexBytes; a test cannot stop the writer there, so it states only that nothing is
//   left being built once the replacement is the index.
// - The entry of a deleted entity is not given to another while the read is held; once the read has
//   left and a drain has released it, it is given out again with the next generation of its slot.
// - The held read returns one written state of its own key: the state it found or one written while
//   it was held, with the revision of that same write, or absence when its entity was deleted
//   meanwhile, never the entity of a key created again; other reads go on, during a growth too.
// - A close waits for the held read and completes once it has left. That it frees everything retired
//   is not visible once the database is closed; the test states that the held read was served whole
//   and that the close completes.
// Every value of a state is derived from its key and its number (StampSchema), so all roots of a test
// have the same size, and one root is what a database of one entity holds after a drain. Every wait
// is bounded, and each write runs on another thread, so that a writer that waited for the held read
// fails the test instead of hanging it.
public sealed class HeldReadTests
{
    private const int Readers = 4;

    // The most keys the growth test inserts before it gives up: the main table grows by the count, so
    // the first growth comes after a few inserts.
    private const int MostKeys = 10_000;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    [Fact]
    public async Task Replace_while_a_read_of_the_entity_is_held_returns_and_the_old_root_stays_retired_until_the_read_left_and_a_drain()
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            await stamps.InsertAsync(StampSchema.StampOf(1, 0));
            var root = HeldBytes(database);
            Assert.True(stamps.TryGet(1L, out _, out var first));

            // The held read returns the revision it read beside the state.
            var heldRevision = default(LatticeRevision);
            using var held = await HeldRead.StartAsync(
                database,
                () => stamps.TryGet(1L, out var stamp, out heldRevision) ? StampSchema.NumberOf(1, stamp) : null);

            await WrittenAsync(() => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)));
            Assert.True(stamps.TryGet(1L, out _, out var second));
            database.DrainRetired();

            // The new root is live, and the old one is retired, whole, while the held read may reach it.
            var during = database.Counters;

            Assert.Equal(root, during.Arena.LiveBytes + during.Arena.ClassSlackBytes);
            Assert.Equal(root, during.Arena.RetiredBytes);
            Assert.Equal(1, during.RootEntries);

            // The held read returns the state it found or the one that replaced it, with the revision of
            // the write of that state; afterwards a drain frees the old root.
            var number = await held.ReleaseAsync();

            Assert.Contains(number, new long?[] { 0, 1 });
            Assert.Equal(number == 0 ? first : second, heldRevision);
            Assert.Equal(root, HeldBytes(database));
            Assert.Equal(0, database.Counters.Arena.RetiredBytes);
            Assert.Equal<long?>(1, StampSchema.NumberOf(1, stamps.Get(1L)));
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Entry_of_an_entity_deleted_while_a_read_of_it_is_held_is_given_out_again_only_after_the_read_left_and_a_drain()
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            await stamps.InsertAsync(StampSchema.StampOf(1, 0));
            await stamps.InsertAsync(StampSchema.StampOf(2, 0));
            var root = HeldBytes(database) / 2;

            using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(1, stamps.Get(1L)));

            // Deleted, and while the read is held another entity is inserted and the deleted one is
            // created again.
            await WrittenAsync(() => stamps.DeleteAsync(1L));
            await WrittenAsync(() => stamps.InsertAsync(StampSchema.StampOf(3, 0)));
            await WrittenAsync(() => stamps.InsertAsync(StampSchema.StampOf(1, 2)));
            database.DrainRetired();

            // The entry of the deleted entity is counted beside the three live ones, and its old root is
            // retired; neither of the two inserted entities took its slot.
            var during = database.Counters;

            Assert.Equal(4, during.RootEntries);
            Assert.Equal(root, during.Arena.RetiredBytes);
            Assert.NotEqual(held.Handle.Slot, (await HandleOfAsync(database, 3, stamps)).Slot);
            Assert.NotEqual(held.Handle.Slot, (await HandleOfAsync(database, 1, stamps)).Slot);

            // The held read returns the state it found or absence, never the entity created again.
            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, null });

            database.DrainRetired();

            Assert.Equal(3, database.Counters.RootEntries);
            Assert.Equal(0, database.Counters.Arena.RetiredBytes);

            // Its slot is the only free one now, so the next insert takes it, with the next generation.
            await WrittenAsync(() => stamps.InsertAsync(StampSchema.StampOf(4, 0)));
            var reused = await HandleOfAsync(database, 4, stamps);

            Assert.Equal(held.Handle.Slot, reused.Slot);
            Assert.Equal(held.Handle.Generation + 1, reused.Generation);
            Assert.Equal<long?>(2, StampSchema.NumberOf(1, stamps.Get(1L)));
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Inserts_replaces_and_deletes_of_the_entity_being_read_and_of_others_return_while_a_read_is_held()
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            var records = database.Set("Stamp");

            for (var key = 1; key <= 3; key++)
            {
                await stamps.InsertAsync(StampSchema.StampOf(key, 0));
            }

            using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(1, stamps.Get(1L)));

            await WrittenAsync(() => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)));
            await WrittenAsync(() => stamps.InsertAsync(StampSchema.StampOf(4, 0)));
            await WrittenAsync(() => records.ReplaceAsync(StampSchema.RecordOf(records.EntityType, 2, 1)));
            await WrittenAsync(() => records.DeleteAsync(2L));
            await WrittenAsync(() => stamps.UpsertAsync(StampSchema.StampOf(3, 1)));
            await WrittenAsync(() => stamps.DeleteAsync(1L));
            await WrittenAsync(() => stamps.InsertAsync(StampSchema.StampOf(1, 2)));

            // Other reads see every write.
            Assert.Equal<long?>(2, StampSchema.NumberOf(1, stamps.Get(1L)));
            Assert.Null(StampSchema.NumberOf(2, records.Get(2L)));
            Assert.Equal<long?>(1, StampSchema.NumberOf(3, records.Get(3L)));
            Assert.Equal<long?>(0, StampSchema.NumberOf(4, stamps.Get(4L)));

            // The held read returns a state of the lifetime it found, or absence.
            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1, null });
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Primary_index_grows_while_a_read_is_held_and_its_old_table_stays_retired_until_the_read_left_and_a_drain()
    {
        // One entity type, so that PrimaryIndexBytes is the bytes of its table.
        var database = await EngineMaster.OpenAsync(StampSchema.StampsOnly);

        try
        {
            var stamps = database.Set<StampSchema.Stamp>();
            await stamps.InsertAsync(StampSchema.StampOf(0, 0));
            database.DrainRetired();

            using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(0, stamps.Get(0L)));

            // The writer inserts until the main table of the index has grown once, while readers on
            // threads of their own look up every key inserted so far. The switch of the growth tells
            // the growth: only the growth of the main table runs it, and the bytes of the index change
            // as well when its overflow takes a page. The table that grew is the one the counters
            // showed before the insert that grew it.
            var inserted = 1;
            var grown = false;
            var table = 0L;

            database.HoldNextGrowth(stamps.EntityType, () => Volatile.Write(ref grown, true));

            await ReadersAndWriter.RunAsync(
                Readers,
                reader =>
                {
                    var step = reader;

                    return () =>
                    {
                        var key = KeyBelow(Volatile.Read(ref inserted), step++);

                        Assert.Equal<long?>(0, StampSchema.NumberOf(key, stamps.Get((long)key)));
                    };
                },
                () => Task.Run(async () =>
                {
                    for (var key = 1; !Volatile.Read(ref grown) && key < MostKeys; key++)
                    {
                        table = database.Counters.PrimaryIndexBytes;
                        await stamps.InsertAsync(StampSchema.StampOf(key, 0));
                        Volatile.Write(ref inserted, key + 1);
                    }
                }))
                .WaitAsync(Bound);

            database.DrainRetired();

            // The replacement is the index now; the old table is retired, whole, while the held read
            // may still use it, and nothing is being built.
            var during = database.Counters;

            Assert.True(Volatile.Read(ref grown), "The main table of the index did not grow.");
            Assert.Equal(table, during.RetiredIndexBytes);
            Assert.Equal(0, during.ReplacementIndexBytes);

            Assert.Equal<long?>(0, await held.ReleaseAsync());

            database.DrainRetired();

            Assert.Equal(0, database.Counters.RetiredIndexBytes);

            for (var key = 0; key < inserted; key++)
            {
                Assert.Equal<long?>(0, StampSchema.NumberOf(key, stamps.Get((long)key)));
            }
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    [Fact]
    public async Task Close_while_a_read_is_held_waits_for_it_and_completes_once_it_has_left()
    {
        var database = await StampSchema.OpenAsync();
        var stamps = database.Set<StampSchema.Stamp>();
        await stamps.InsertAsync(StampSchema.StampOf(1, 0));
        var closed = false;

        try
        {
            using var held = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(1, stamps.Get(1L)));

            // A replace retires the root the held read found.
            await WrittenAsync(() => stamps.ReplaceAsync(StampSchema.StampOf(1, 1)));

            // The close waits for the read in progress, and every operation that begins now is
            // ObjectDisposedException.
            var closing = database.CloseAsync().AsTask();

            Assert.False(closing.IsCompleted, "The close completed while a read was in progress.");
            Assert.Throws<ObjectDisposedException>(() => stamps.Get(1L));
            Assert.Throws<ObjectDisposedException>(database.DrainRetired);

            // The read in progress is served whole, and then the close completes.
            Assert.Contains(await held.ReleaseAsync(), new long?[] { 0, 1 });
            await closing.WaitAsync(Bound);
            closed = true;
        }
        finally
        {
            if (!closed)
            {
                await database.CloseAsync().AsTask().WaitAsync(Bound);
            }
        }
    }

    // The live bytes and the class slack after a drain: what the entities hold.
    private static long HeldBytes(EngineDatabase database)
    {
        database.DrainRetired();

        var arena = database.Counters.Arena;

        return arena.LiveBytes + arena.ClassSlackBytes;
    }

    // A write on another thread that has to return within the bound: a writer that waited for the held
    // read would not.
    private static async Task WrittenAsync(Func<ValueTask<LatticeWriteResult>> write)
    {
        var result = await Task.Run(() => write().AsTask()).WaitAsync(Bound);

        Assert.True(result.Changed);
    }

    // The handle of the root entry of the key: a read of it held and released at once.
    private static async Task<RootHandle> HandleOfAsync(EngineDatabase database, int key, ILatticeSet<StampSchema.Stamp> stamps)
    {
        using var probe = await HeldRead.StartAsync(database, () => StampSchema.NumberOf(key, stamps.Get((long)key)));
        var handle = probe.Handle;

        await probe.ReleaseAsync();

        return handle;
    }

    // A key below the count, a different one at every step.
    private static int KeyBelow(int count, int step) => (int)((long)step * 7_919 % count);
}
