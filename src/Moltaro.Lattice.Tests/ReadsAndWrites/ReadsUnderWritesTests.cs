namespace Moltaro.Lattice.Tests.ReadsAndWrites;

// Reads by key while writes change the same entities (brief of Epic 1, sprint S06, Acceptance;
// milestone contract, "Reader protection, publication and compaction"; Q06 and Q06.1 of the decision
// on memory and access):
// - A read is coherent while the entity is being replaced. It copies one state of the entity under
//   one protection that covers every block the entity owns, so it returns exactly one written state,
//   never a mix of two, whether the values lie inline, in a block of their own or in chunks.
// - A read that found an entity which is deleted before the read reaches it returns absence, never
//   the values of another entity (the planner's answer to Q32), also when the slot of the deleted
//   entity is given to another one at once.
// - A read finds every entity whose insert has returned while other entities are inserted and the
//   primary index grows.
// - A read with a session returns the state its key had at the position of its answer (the
//   remarks of LatticePosition and LatticeReadResult): never an older state with a newer position,
//   and never absence at a position where the entity existed.
// In each test one writer makes a bounded number of writes while readers, through the class and by
// name, on threads of their own, read until the writes are done; each reader has read once before
// the first write. Nothing sleeps, and what the tests assert does not depend on how the threads
// interleave: every read is checked on its own.
public sealed class ReadsUnderWritesTests
{
    private const int Readers = 4;

    // 300 replaces of one entity, whose largest value, of 70,000 bytes, lies in two chunks.
    private const int Replaces = 300;

    // 100 rounds that delete 8 entities and insert 8 others, 1,600 writes.
    private const int Rounds = 100;

    // 20,000 inserts, which grow the primary index several times.
    private const int Inserts = 20_000;

    // 3,000 inserts, replaces and deletes of two keys.
    private const int Writes = 3_000;

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Read_returns_exactly_one_written_state_while_the_entity_is_being_replaced(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, GaugeSchema());
        var gauges = database.Set<Gauge>();
        var gaugeRecords = database.Set("Gauge");
        await gauges.InsertAsync(Gauge.Of(0));

        await ReadersAndWriter.RunAsync(
            Readers,
            reader => reader % 2 == 0 ? () => Gauge.ExpectOneState(gauges.Get(1L)) : () => Gauge.ExpectOneState(gaugeRecords.Get(1L)),
            async () =>
            {
                for (var counter = 1; counter <= Replaces; counter++)
                {
                    await gauges.ReplaceAsync(Gauge.Of(counter));
                }
            });

        // After the writes the last state is read, through the class and by name.
        Assert.Equal(Replaces, Gauge.ExpectOneState(gauges.Get(1L)));
        Assert.Equal(Replaces, Gauge.ExpectOneState(gaugeRecords.Get(1L)));
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Read_during_deletes_and_recreations_returns_the_entity_with_its_own_values_or_absence(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        long[] first = [1, 2, 3, 4, 5, 6, 7, 8];
        long[] second = [101, 102, 103, 104, 105, 106, 107, 108];
        long[] keys = [.. first, .. second];

        foreach (var key in first)
        {
            await users.InsertAsync(UserOf(key, 0));
        }

        await ReadersAndWriter.RunAsync(
            Readers,
            reader =>
            {
                var next = reader;
                return reader % 2 == 0
                    ? () => ExpectOwnValuesOrAbsence(keys[next++ % keys.Length], users)
                    : () => ExpectOwnValuesOrAbsence(keys[next++ % keys.Length], userRecords);
            },
            async () =>
            {
                // The entities of one group are deleted and those of the other inserted, so the slots
                // the deletes release are taken by entities with other keys.
                for (var round = 1; round <= Rounds; round++)
                {
                    var (deleted, inserted) = round % 2 == 1 ? (first, second) : (second, first);

                    foreach (var key in deleted)
                    {
                        Assert.True((await users.DeleteAsync(key)).Changed);
                    }

                    foreach (var key in inserted)
                    {
                        await users.InsertAsync(UserOf(key, round));
                    }
                }
            });
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Read_finds_every_inserted_entity_while_the_primary_index_grows(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        await users.InsertAsync(new User { Id = 0, Name = "user 0" });

        // How many entities have been inserted: the keys below it are there.
        var inserted = 1;

        await ReadersAndWriter.RunAsync(
            Readers,
            reader =>
            {
                var step = reader;
                return reader % 2 == 0
                    ? () => ExpectFound(users, KeyBelow(Volatile.Read(ref inserted), step++))
                    : () => ExpectFound(userRecords, KeyBelow(Volatile.Read(ref inserted), step++));
            },
            async () =>
            {
                for (var key = 1; key < Inserts; key++)
                {
                    await users.InsertAsync(new User { Id = key, Name = $"user {key}" });
                    Volatile.Write(ref inserted, key + 1);
                }
            });

        Assert.Equal(Inserts, users.Enumerate().Count());
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Read_with_a_session_returns_the_state_its_key_had_at_the_position_of_its_answer(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation);
        var users = database.Set<User>();
        var userRecords = database.Set("User");
        long[] keys = [1, 2];

        // The history the writer records: the position before the first write, and every write with
        // its position, its key and the round of the state it left, null for absence. There is one
        // writer, so its writes are in the order of their positions. Every reader keeps what it read.
        var start = database.Position;
        var writes = new List<(LatticePosition Position, long Key, int? Round)>();
        var observed = new List<(long Key, LatticePosition Position, int? Round)>[Readers];

        await ReadersAndWriter.RunAsync(
            Readers,
            reader =>
            {
                var next = reader;
                var session = new LatticeSession();
                var seen = observed[reader] = [];

                return reader % 2 == 0
                    ? () => seen.Add(ReadWithSession(users, keys[next++ % keys.Length], session))
                    : () => seen.Add(ReadWithSession(userRecords, keys[next++ % keys.Length], session));
            },
            async () =>
            {
                var current = new Dictionary<long, int?> { [1] = null, [2] = null };

                for (var write = 1; write <= Writes; write++)
                {
                    var key = keys[write % keys.Length];
                    LatticeWriteResult result;
                    int? round;

                    // An absent key is inserted; one that exists is deleted at every third write and
                    // replaced at the others.
                    if (current[key] is null)
                    {
                        round = write;
                        result = await users.InsertAsync(UserOf(key, write));
                    }
                    else if (write % 3 == 0)
                    {
                        round = null;
                        result = await users.DeleteAsync(key);
                    }
                    else
                    {
                        round = write;
                        result = await users.ReplaceAsync(UserOf(key, write));
                    }

                    current[key] = round;
                    writes.Add((result.Position, key, round));
                }
            });

        // The state of every key at every position of the history, by the ordinal of the position.
        var ordinals = new Dictionary<LatticePosition, int> { [start] = 0 };
        var states = keys.ToDictionary(key => key, _ => new int?[writes.Count + 1]);

        for (var ordinal = 1; ordinal <= writes.Count; ordinal++)
        {
            var (position, key, round) = writes[ordinal - 1];
            ordinals.Add(position, ordinal);

            foreach (var state in states.Values)
            {
                state[ordinal] = state[ordinal - 1];
            }

            states[key][ordinal] = round;
        }

        foreach (var (key, position, round) in observed.SelectMany(seen => seen))
        {
            Assert.True(ordinals.TryGetValue(position, out var ordinal), $"A read of {key} answered at {position}, a position of no write.");
            Assert.True(states[key][ordinal] == round, $"A read of {key} at {position} found the state of round {round?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent"}; the key had the state of round {states[key][ordinal]?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent"} there.");
        }
    }

    private static User UserOf(long key, int round) => new()
    {
        Id = key,
        Name = NameOf(key, round),
        Email = $"{key}@example.org",
        Age = round,
        Enabled = key % 2 == 0,
    };

    // More than 64 bytes, so that the name has a block of its own.
    private static string NameOf(long key, int round) => $"user {key} of round {round} ".PadRight(100, '.');

    private static void ExpectOwnValuesOrAbsence(long key, ILatticeSet<User> users)
    {
        var user = users.Get(key);
        if (user is null)
        {
            return;
        }

        Assert.Equal(key, user.Id);
        Assert.NotNull(user.Age);
        Assert.Equal(NameOf(key, user.Age.Value), user.Name);
        Assert.Equal($"{key}@example.org", user.Email);
        Assert.Equal(key % 2 == 0, user.Enabled);
    }

    private static void ExpectOwnValuesOrAbsence(long key, ILatticeSet userRecords)
    {
        var record = userRecords.Get(key);
        if (record is null)
        {
            return;
        }

        Assert.Equal(key, record["Id"]);
        Assert.Equal(NameOf(key, record.Get<int>("Age")), record["Name"]);
        Assert.Equal($"{key}@example.org", record["Email"]);
        Assert.Equal(key % 2 == 0, record["Enabled"]);
    }

    // A read with the session: its key, the position of its answer and the round of the state it
    // found, after checking that the state is that of its own key.
    private static (long Key, LatticePosition Position, int? Round) ReadWithSession(ILatticeSet<User> users, long key, LatticeSession session)
    {
        var result = users.GetAsync(key, session).AsTask().GetAwaiter().GetResult();

        if (!result.Found)
        {
            return (key, result.Position, null);
        }

        Assert.Equal(key, result.Entity.Id);
        Assert.Equal(NameOf(key, result.Entity.Age!.Value), result.Entity.Name);

        return (key, result.Position, result.Entity.Age);
    }

    private static (long Key, LatticePosition Position, int? Round) ReadWithSession(ILatticeSet userRecords, long key, LatticeSession session)
    {
        var result = userRecords.GetAsync(key, session).AsTask().GetAwaiter().GetResult();

        if (!result.Found)
        {
            return (key, result.Position, null);
        }

        var round = result.Entity.Get<int>("Age");
        Assert.Equal(key, result.Entity["Id"]);
        Assert.Equal(NameOf(key, round), result.Entity["Name"]);

        return (key, result.Position, round);
    }

    // A key below the count, a different one at every step.
    private static long KeyBelow(int count, int step) => (long)step * 7_919 % count;

    private static void ExpectFound(ILatticeSet<User> users, long key)
    {
        var user = users.Get(key);

        Assert.NotNull(user);
        Assert.Equal($"user {key}", user.Name);
    }

    private static void ExpectFound(ILatticeSet userRecords, long key)
    {
        var record = userRecords.Get(key);

        Assert.NotNull(record);
        Assert.Equal($"user {key}", record["Name"]);
    }

    // The entity type Gauge, bound to the class Gauge.
    private static LatticeSchema GaugeSchema() => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Gauge", "ClrType": "{{typeof(Gauge).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Counter", "Type": "Int64" },
                { "Id": 3, "Name": "Label", "Type": "String" },
                { "Id": 4, "Name": "Line", "Type": "String" },
                { "Id": 5, "Name": "Page", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // One entity whose every field is derived from one counter: Label inline, Line in a block of its
    // own, Page in two chunks (brief of S05, "Where a value goes"). A state that mixes two writes has
    // fields of two counters.
    private sealed class Gauge
    {
        private const int LineLength = 200;

        private const int PageLength = 70_000;

        public long Id { get; set; }

        public long Counter { get; set; }

        public string Label { get; set; } = "";

        public string Line { get; set; } = "";

        public string Page { get; set; } = "";

        public static Gauge Of(long counter) => new()
        {
            Id = 1,
            Counter = counter,
            Label = LabelOf(counter),
            Line = Repeated(TokenOf(counter), LineLength),
            Page = Repeated(TokenOf(counter), PageLength),
        };

        // The counter of the state read, after checking that every field belongs to it.
        public static long ExpectOneState(Gauge? gauge)
        {
            Assert.NotNull(gauge);
            Assert.Equal(1, gauge.Id);

            return ExpectOneState(gauge.Counter, gauge.Label, gauge.Line, gauge.Page);
        }

        public static long ExpectOneState(LatticeRecord? record)
        {
            Assert.NotNull(record);
            Assert.Equal(1L, record["Id"]);

            return ExpectOneState(record.Get<long>("Counter"), record.Get<string>("Label")!, record.Get<string>("Line")!, record.Get<string>("Page")!);
        }

        private static long ExpectOneState(long counter, string label, string line, string page)
        {
            Assert.InRange(counter, 0, Replaces);
            Assert.Equal(LabelOf(counter), label);
            Assert.True(Repeats(line, TokenOf(counter), LineLength), $"Line is not of the state {counter}.");
            Assert.True(Repeats(page, TokenOf(counter), PageLength), $"Page is not of the state {counter}.");

            return counter;
        }

        private static string LabelOf(long counter) => $"label {counter}";

        private static string TokenOf(long counter) => $"{counter:D8}|";

        private static string Repeated(string token, int length) =>
            string.Create(length, token, static (span, token) =>
            {
                for (var start = 0; start < span.Length; start += token.Length)
                {
                    token.AsSpan(0, Math.Min(token.Length, span.Length - start)).CopyTo(span[start..]);
                }
            });

        // Whether the value is the token repeated to the length, compared without a copy.
        private static bool Repeats(string value, string token, int length)
        {
            if (value.Length != length)
            {
                return false;
            }

            for (var start = 0; start < length; start += token.Length)
            {
                var part = value.AsSpan(start, Math.Min(token.Length, length - start));
                if (!part.SequenceEqual(token.AsSpan(0, part.Length)))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
