using Moltaro.Lattice.Access;

namespace Moltaro.Lattice.Engine.Tests.Engine.Protection;

// Reads by key while few entities are replaced, deleted and created again, so that readers and
// writers meet on the same roots and the entries of deleted roots are given to other keys (milestone
// contract, "Reader protection, publication and compaction"; arena protocol, "Lock and lifetime
// order"; brief of Epic 1, sprint S08, the guarantees that do not move):
// - A read is coherent: it returns one written state, never a mix of two writes and never memory that
//   was freed or reused, whether a value lies in the fixed part, inline, in a block of its own or in
//   a chunk.
// - A deleted entry is reused only after the readers that could see it are gone, and a read never
//   follows an old entry to a key that was created again: a read returns its own key's entity or
//   absence, never the entity of another key.
// - A read is never stale and never falsely absent: what it returns is a state its key had at some
//   moment of the read. Every write of a key has an index, and the state after it is the entity
//   with that number or, after a delete, absence. A read that started after write b had returned and
//   ended before write a + 1 started returns the state after one of the writes b to a.
// - Once every write has returned, the entities are deleted and a drain has found no read in
//   progress, nothing is left: no live byte, no prepared or retired block, no retired table of the
//   index and no root entry (EngineCounters; brief of S08, "Counters and the drain, part 1").
// It holds for an Int64 key and for a String key, which the index compares with the text in the
// root. Readers read through the class and by name, on threads of their own, until the writes are
// done; two writers write two keys each, a fixed number of times. Nothing sleeps, and every read is
// checked on its own, so what the test asserts does not depend on how the threads interleave.
public sealed class ChurnTests
{
    private const int Keys = 4;

    private const int Writers = 2;

    // The writes of each key after its first insert, in cycles of four: replace, replace, delete,
    // insert. 300 deletes and 300 inserts again of every key, 4,800 writes in all.
    private const int WritesPerKey = 1_200;

    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // At least four readers, so that readers meet on a root; at most eight, beside the writers.
    private static int Readers => Math.Clamp(Environment.ProcessorCount, 4, 8);

    [Theory]
    [InlineData("Int64")]
    [InlineData("String")]
    public async Task Reads_while_few_entities_are_replaced_deleted_and_created_again_return_a_state_their_key_had_and_leave_nothing_behind(string keyType)
    {
        var database = await StampSchema.OpenAsync();

        try
        {
            var records = database.Set(StampSchema.EntityTypeOf(keyType));
            var entityType = records.EntityType;
            var stamps = database.Set<StampSchema.Stamp>();
            var namedStamps = database.Set<StampSchema.NamedStamp>();

            Func<int, long?> readByClass = keyType == "Int64"
                ? key => StampSchema.NumberOf(key, stamps.Get((long)key))
                : key => StampSchema.NumberOf(key, namedStamps.Get(StampSchema.NameOf(key)));
            Func<int, long?> readByName = key => StampSchema.NumberOf(key, records.Get(StampSchema.KeyOf(entityType, key)));

            // For every key, the index of the last write that has started and of the last that has
            // returned. Write 0 is the first insert.
            var started = new int[Keys];
            var returned = new int[Keys];

            for (var key = 0; key < Keys; key++)
            {
                await records.InsertAsync(StampSchema.RecordOf(entityType, key, 0));
            }

            await ReadersAndWriter.RunAsync(
                Readers,
                reader =>
                {
                    var next = reader;
                    var read = reader % 2 == 0 ? readByClass : readByName;

                    return () => ExpectStateDuringRead(next++ % Keys, read, started, returned);
                },
                () => Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(() => WriteAsync(records, writer, started, returned)))))
                .WaitAsync(Bound);

            // Afterwards each key has the state of its last write, through the class and by name; then
            // everything is deleted, and nothing is left.
            for (var key = 0; key < Keys; key++)
            {
                long? last = IsDelete(WritesPerKey) ? null : WritesPerKey;

                Assert.Equal(last, readByClass(key));
                Assert.Equal(last, readByName(key));

                if (last is not null)
                {
                    Assert.True((await records.DeleteAsync(StampSchema.KeyOf(entityType, key))).Changed);
                }
            }

            database.DrainRetired();
            var counters = database.Counters;

            Assert.Equal(0, counters.Arena.LiveBytes);
            Assert.Equal(0, counters.Arena.ClassSlackBytes);
            Assert.Equal(0, counters.Arena.PreparedBytes);
            Assert.Equal(0, counters.Arena.RetiredBytes);
            Assert.Equal(0, counters.RetiredIndexBytes);
            Assert.Equal(0, counters.RootEntries);
        }
        finally
        {
            await database.CloseAsync().AsTask().WaitAsync(Bound);
        }
    }

    // Whether the write with this index is a delete, after which the key is absent: the third of every
    // cycle of four.
    private static bool IsDelete(int write) => write % 4 == 3;

    // Write i of a key replaces it, deletes it or inserts it again by i alone, with the state number i.
    private static async Task WriteAsync(ILatticeSet records, int writer, int[] started, int[] returned)
    {
        var entityType = records.EntityType;

        for (var write = 1; write <= WritesPerKey; write++)
        {
            for (var key = writer; key < Keys; key += Writers)
            {
                // A full fence, so that a reader that sees anything of this write sees that it started.
                Interlocked.Exchange(ref started[key], write);

                var result = (write % 4) switch
                {
                    0 => await records.InsertAsync(StampSchema.RecordOf(entityType, key, write)),
                    3 => await records.DeleteAsync(StampSchema.KeyOf(entityType, key)),
                    _ => await records.ReplaceAsync(StampSchema.RecordOf(entityType, key, write)),
                };

                Assert.True(result.Changed);
                Volatile.Write(ref returned[key], write);
            }
        }
    }

    // One read of the key, checked against the writes of the key around it: the first it may see is
    // the last that had returned before it began, the last it may see the last that had started when
    // it ended.
    private static void ExpectStateDuringRead(int key, Func<int, long?> read, int[] started, int[] returned)
    {
        var first = Volatile.Read(ref returned[key]);
        var number = read(key);

        // A full fence, so that the index below is read after everything the read has read.
        Interlocked.MemoryBarrier();

        var last = Volatile.Read(ref started[key]);

        if (number is { } found)
        {
            Assert.True(
                found >= first && found <= last,
                $"A read of key {key} found its state {found} while the writes {first} to {last} were the ones it could see.");
        }
        else
        {
            Assert.True(
                HasDelete(first, last),
                $"A read of key {key} found no entity while the writes {first} to {last}, none of them a delete, were the ones it could see.");
        }
    }

    // Whether a write from first to last, both included, is a delete.
    private static bool HasDelete(int first, int last)
    {
        var firstDelete = first + ((3 - (first % 4) + 4) % 4);

        return firstDelete <= last;
    }
}
