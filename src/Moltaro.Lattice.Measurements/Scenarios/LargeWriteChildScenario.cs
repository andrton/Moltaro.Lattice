using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Engine.Delivery;
using Moltaro.Lattice.Engine.Entities;
using Moltaro.Lattice.Engine.Positions;
using Moltaro.Lattice.Engine.Protection;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Replication.Load;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The scenario large-write-child: what an entity of 32 MiB costs a master with a child following (brief
// of Epic 1, sprint S11, the planner's block of 17 November 2026, point 6, and his decision after the
// study, point 12). The database and the series are those of large-writes (LargeWritesScenario), on a
// master that accepts children on the loopback interface:
// - with no child, then with one child following: one caller replaces the entity of 32 MiB, write after
//   write, which holds the lock of the writes for the whole of a write, its copy for the child included;
//   and one caller replaces small entities beside it;
// - in isolation, on an arena of its own: the copy the capture of a load makes of a root of 32 MiB inside
//   the protection of reads (BlockExport.Write into the buffer of a connection), and the copy a write
//   makes of it for the tail of the children under the lock of the writes (TransactionEncoder.Write),
//   each 20 times, min, median and max, beside the same copy into an array allocated once, into one taken
//   from ArrayPool, and a plain copy of 32 MiB between two managed arrays for scale.
// Both sides run in one process.
internal static class LargeWriteChildScenario
{
    public const string Name = "large-write-child";

    private const int Copies = 20;

    public static IReadOnlyList<string> Run()
    {
        var lines = new List<string>
        {
            $"Large entities as in large-writes: 1 entity of {Number(LargeWritesScenario.LargeBytes)} bytes and {Number(LargeWritesScenario.SmallEntities)} of {LargeWritesScenario.SmallBytes} bytes, MemoryOnly, accepting children on the loopback interface",
        };

        using var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = LargeWritesScenario.Schema,
            ListenUri = new Uri("lattice://127.0.0.1:0"),
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var blobs = database.Set("Blob");
        var large = LargeWritesScenario.RecordOf(blobs.EntityType, 0, LargeWritesScenario.LargeBytes);
        var small = Enumerable.Range(1, LargeWritesScenario.SmallEntities).Select(id => LargeWritesScenario.RecordOf(blobs.EntityType, id, LargeWritesScenario.SmallBytes)).ToArray();

        blobs.InsertAsync(large).AsTask().GetAwaiter().GetResult();

        foreach (var record in small)
        {
            blobs.InsertAsync(record).AsTask().GetAwaiter().GetResult();
        }

        lines.AddRange(Series(database, blobs, large, small, "no child"));

        using (var child = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = database.ListenUri,
        }))
        {
            child.OpenAsync().AsTask().GetAwaiter().GetResult();
            lines.AddRange(Series(database, blobs, large, small, "1 child following"));

            var waited = Stopwatch.StartNew();
            var target = database.Counters.PublishedSequence;

            while (child.Counters.PublishedSequence < target)
            {
                Thread.Sleep(1);
            }

            lines.Add($"1 child following: it reached the master {Milliseconds(waited.Elapsed)} ms after the last series");
        }

        lines.AddRange(Copied());
        lines.Add(SmallCopied());

        return lines;
    }

    // For scale beside the copies of 32 MiB: the copy of a small root for the tail, a user of the
    // reference fixture, in isolation, as a write makes it under the lock of the writes.
    private static string SmallCopied()
    {
        const int Count = 2_000_000;
        var schema = LatticeSchema.Compile(ReferenceFixture.ScalarSchemaJson(unique: false));
        var user = ReferenceFixture.Generate(schema).First(record => record.EntityType.Name == "User");
        var entityType = user.EntityType;
        var layout = RootLayout.Compile(entityType, schema);
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 64L * 1024 * 1024 });
        var roots = new RootTable();
        var epochs = new ReadEpochs(roots);
        var table = EntityTable.Open(entityType, schema, arena, roots, epochs, new RetiredMemory(epochs, arena, roots), new PositionClock(1, roots), seed: 0);
        var writer = new RootWriter(arena, layout, roots.Add((uint)entityType.Id));

        RootValues.Write(ref writer, entityType, [.. entityType.Fields.Select(field => user[field.Name])]);

        var root = writer.Complete();
        var tail = new TailCopy();
        var times = new List<double>();

        for (var round = 0; round < 3; round++)
        {
            var started = Stopwatch.GetTimestamp();

            for (var copy = 0; copy < Count; copy++)
            {
                tail.Copy(table, arena, root);
            }

            times.Add(Stopwatch.GetElapsedTime(started).TotalNanoseconds / Count);
        }

        times.Sort();

        return $"A write's copy of a small root for the tail (a user of the fixture, {BlockExport.Length(arena, layout, root)} bytes exported; TransactionEncoder.Write into the blocks of the tail, released after each), {Count} times in each of 3 rounds: {string.Join(", ", times.Select(time => $"{time.ToString("F0", CultureInfo.InvariantCulture)} ns"))} each";
    }

    private static List<string> Series(EngineDatabase database, ILatticeSet blobs, LatticeRecord large, LatticeRecord[] small, string children)
    {
        var largeTimes = new long[LargeWritesScenario.MostTimed];
        var smallTimes = new long[LargeWritesScenario.MostTimed];
        var next = 0;
        var peak = 0L;

        void WriteLarge()
        {
            blobs.ReplaceAsync(large).AsTask().GetAwaiter().GetResult();
            peak = Math.Max(peak, database.Counters.PendingDeliveryBytes);
        }

        void WriteSmall() => blobs.ReplaceAsync(small[next++ % small.Length]).AsTask().GetAwaiter().GetResult();

        var largeAlone = LargeWritesScenario.Series([(WriteLarge, largeTimes)]);
        var largeLine = $"{children}, 1 caller that replaces an entity of 32 MiB, alone: {LargeWritesScenario.Rate(largeAlone[0])}, time of a write, which holds the lock of the writes, {LargeWritesScenario.Times(largeTimes, largeAlone[0])}; PendingDeliveryBytes after a write, peak {Number(peak)}";

        var beside = LargeWritesScenario.Series([(WriteSmall, smallTimes), (WriteLarge, largeTimes)]);
        var besideLine = $"{children}, 1 caller that replaces small entities beside 1 caller that replaces an entity of 32 MiB: small {LargeWritesScenario.Rate(beside[0])}, time of a small write {LargeWritesScenario.Times(smallTimes, beside[0])}; large {LargeWritesScenario.Rate(beside[1])}, time of a large write {LargeWritesScenario.Times(largeTimes, beside[1])}";

        return [largeLine, besideLine];
    }

    // The two copies of a root of 32 MiB, in isolation (see the class).
    private static List<string> Copied()
    {
        var schema = LargeWritesScenario.Schema;
        var entityType = schema.EntityTypes.Single();
        var layout = RootLayout.Compile(entityType, schema);
        using var arena = new Arena(new ArenaOptions { MemoryLimit = 256L * 1024 * 1024 });
        var roots = new RootTable();
        var epochs = new ReadEpochs(roots);
        var retired = new RetiredMemory(epochs, arena, roots);
        var table = EntityTable.Open(entityType, schema, arena, roots, epochs, retired, new PositionClock(1, roots), seed: 0);
        var handle = roots.Add((uint)entityType.Id);
        var writer = new RootWriter(arena, layout, handle);
        var data = new byte[LargeWritesScenario.LargeBytes];

        new Random(42).NextBytes(data);
        entityType.TryGetField("Id", out var id);
        entityType.TryGetField("Data", out var value);
        writer.SetInteger(id!, 0);
        writer.SetBinary(value!, data);

        var root = writer.Complete();
        var buffer = new TransferBuffer();
        var reused = new byte[BlockExport.Length(arena, layout, root)];
        var tail = new TailCopy();
        var capture = new List<TimeSpan>();
        var delivery = new List<TimeSpan>();
        var reusedCopy = new List<TimeSpan>();
        var pooledCopy = new List<TimeSpan>();
        var plain = new List<TimeSpan>();

        for (var copy = 0; copy < Copies; copy++)
        {
            buffer.Clear();

            var started = Stopwatch.GetTimestamp();

            BlockExport.Write(buffer, arena, layout, root);
            capture.Add(Stopwatch.GetElapsedTime(started));

            started = Stopwatch.GetTimestamp();
            tail.Copy(table, arena, root);
            delivery.Add(Stopwatch.GetElapsedTime(started));

            started = Stopwatch.GetTimestamp();
            BlockExport.Write(new ArrayWriter(reused), arena, layout, root);
            reusedCopy.Add(Stopwatch.GetElapsedTime(started));

            started = Stopwatch.GetTimestamp();

            var rented = ArrayPool<byte>.Shared.Rent(reused.Length);

            BlockExport.Write(new ArrayWriter(rented), arena, layout, root);
            pooledCopy.Add(Stopwatch.GetElapsedTime(started));
            ArrayPool<byte>.Shared.Return(rented);

            started = Stopwatch.GetTimestamp();
            data.AsSpan().CopyTo(reused);
            plain.Add(Stopwatch.GetElapsedTime(started));
        }

        return
        [
            $"The capture's copy of a root of 32 MiB inside the protection of reads (BlockExport.Write into a TransferBuffer), {Copies} times: {Spread(capture)}",
            $"A write's copy of a root of 32 MiB for the tail of the children under the lock of the writes (TransactionEncoder.Write into the blocks of the tail, released after each), {Copies} times: {Spread(delivery)}",
            $"The same copy, for comparison, into one array allocated once: {Spread(reusedCopy)}; into an array taken from ArrayPool<byte>.Shared and returned: {Spread(pooledCopy)}",
            $"For scale, a copy of 32 MiB from one managed array into another: {Spread(plain)}",
        ];
    }

    // Writes into an array given from its start, for the copies into an array allocated once and into a
    // pooled one; the array holds what is written.
    private sealed class ArrayWriter(byte[] bytes) : IBufferWriter<byte>
    {
        private int _position;

        public Span<byte> GetSpan(int sizeHint = 0) => bytes.AsSpan(_position);

        public Memory<byte> GetMemory(int sizeHint = 0) => bytes.AsMemory(_position);

        public void Advance(int count) => _position += count;
    }

    // A tail of one child that applies each transaction at once: the copy a write makes for the tail, and
    // its release, as the lock of the writes and the sender of the child do them.
    private sealed class TailCopy
    {
        private readonly DeliveryTail _tail = new();

        private readonly DeliveryCursor _cursor;

        private readonly DeliveryReader _reader;

        private ulong _sequence;

        public TailCopy()
        {
            _cursor = _tail.Subscribe(startPosition: 0);
            _reader = new DeliveryReader(_cursor);
        }

        public void Copy(EntityTable table, Arena arena, ArenaAddress root)
        {
            _sequence++;
            TransactionEncoder.Write(_tail, _sequence, table, arena, root);
            _tail.Commit();

            while (_reader.HasBytes())
            {
                _reader.Take(long.MaxValue);
            }

            _cursor.Sent = _sequence;
            _tail.Acknowledge(_cursor, _sequence);
        }
    }

    private static string Spread(List<TimeSpan> times)
    {
        times.Sort();

        return $"min {Milliseconds(times[0])} ms, median {Milliseconds(times[times.Count / 2])} ms, max {Milliseconds(times[^1])} ms";
    }

    private static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static string Milliseconds(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);
}
