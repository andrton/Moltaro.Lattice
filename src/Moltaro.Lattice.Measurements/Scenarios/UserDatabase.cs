using System.Diagnostics;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine;
using Moltaro.Lattice.Measurements.Fixture;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Measurements.Scenarios;

// The database the scenarios of reads by an integer key read: the 100,000 users of the scalar form of
// the reference fixture without its Unique marks, inserted by name into the engine, MemoryOnly. The
// class FixtureUser is named for the entity type User, which leaves the schema the scalar form, so
// that the users are read through the class as well as by name.
//
// A writer beside the reads replaces the users with the values they have, or inserts and deletes
// users of its own: users with keys above the fixture's, which the reads never read, made as copies
// of the fixture's users with another key.
//
// For the scenarios of Dictionary strings (brief of Epic 1, sprint S13) the field Region, one of 16
// texts of 9 bytes, can be declared with Dictionary storage: the same users, generated from the scalar
// form and copied by name into a schema that differs from it in that one declaration.
internal sealed class UserDatabase : IDisposable
{
    private readonly EngineDatabase _database;

    private readonly List<LatticeRecord> _users;

    // The users the writer has replaced, so that the next replace takes the next user.
    private int _replaced;

    private UserDatabase(EngineDatabase database, List<LatticeRecord> users, string fixture, string loaded)
    {
        _database = database;
        _users = users;
        Fixture = fixture;
        Loaded = loaded;
        ByName = database.Set("User");
        ByClass = database.Set<FixtureUser>();
    }

    public ILatticeSet ByName { get; }

    public ILatticeSet<FixtureUser> ByClass { get; }

    public int Count => _users.Count;

    // The line of a report that names the fixture.
    public string Fixture { get; }

    // The line of a report that says what was loaded into which database, and how long it took.
    public string Loaded { get; }

    // Generates the fixture, opens the database, under the read limits given or none and accepting
    // children at the address given or at none, and inserts the users by name; with Region in Dictionary
    // storage when dictionaryRegion is true; without the three indexes of User when withoutIndexes is
    // true, the same users of a type that declares none.
    public static UserDatabase Load(long? readMemoryLimit = null, TimeSpan? readTimeLimit = null, Uri? listenUri = null, bool dictionaryRegion = false, bool withoutIndexes = false)
    {
        var scalar = LatticeSchema.Compile(WithUserClass(ReferenceFixture.ScalarSchemaJson(unique: false)));
        var records = ReferenceFixture.Generate(scalar).ToList();
        var json = WithUserClass(ReferenceFixture.ScalarSchemaJson(unique: false));
        var schema = dictionaryRegion || withoutIndexes
            ? LatticeSchema.Compile(withoutIndexes ? WithoutUserIndexes(json) : WithDictionaryRegion(json))
            : scalar;
        var copies = dictionaryRegion || withoutIndexes;
        var users = records.Where(record => record.EntityType.Name == "User").Select(user => copies ? CopyInto(schema, user) : user).ToList();

        var database = (EngineDatabase)LatticeDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ReadMemoryLimit = readMemoryLimit,
            ReadTimeLimit = readTimeLimit,
            ListenUri = listenUri,
        });

        database.OpenAsync().AsTask().GetAwaiter().GetResult();

        var byName = database.Set("User");
        var loading = Stopwatch.StartNew();

        foreach (var user in users)
        {
            byName.InsertAsync(user).AsTask().GetAwaiter().GetResult();
        }

        loading.Stop();

        return new UserDatabase(
            database,
            users,
            $"Fixture: the scalar form without its Unique marks{(dictionaryRegion ? ", Region in Dictionary storage" : "")}{(withoutIndexes ? ", User without its indexes" : "")}, seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
            $"Database: the engine, MemoryOnly; {ReadSeries.Number(users.Count)} users inserted by name in {ReadSeries.Seconds(loading.Elapsed)} s");
    }

    // Keys of users drawn from 1 to Count by the generator of the fixture with the seed, the same on
    // every run.
    public long[] Keys(ulong seed, int count)
    {
        var random = new FixtureRandom(seed);
        var keys = new long[count];

        for (var index = 0; index < keys.Length; index++)
        {
            keys[index] = random.Below(_users.Count) + 1;
        }

        return keys;
    }

    // One replace of the next user with the values it has, for a writer on one thread.
    public void ReplaceNext() => ByName.ReplaceAsync(_users[_replaced++ % _users.Count]).AsTask().GetAwaiter().GetResult();

    // One replace of the user at the place, taken modulo the users, with the values it has, for writers
    // on several threads that each replace users of their own.
    public void Replace(int place) => ByName.ReplaceAsync(_users[place % _users.Count]).AsTask().GetAwaiter().GetResult();

    // The counters of the engine, for a scenario that reports what the write coordinator formed.
    public EngineCounters Counters => _database.Counters;

    // The position the database has published.
    public LatticePosition Position => _database.Position;

    // The address the database accepts children at, with its port; null when it was opened without one.
    public Uri? ListenUri => _database.ListenUri;

    // A user of the writer with the key, above the fixture's: a copy of a user of the fixture with
    // that key, not inserted yet.
    public LatticeRecord NewUser(long key)
    {
        var template = _users[(int)((key - 1) % _users.Count)];
        var user = new LatticeRecord(template.EntityType);

        foreach (var field in template.EntityType.Fields)
        {
            user[field.Name] = template[field.Name];
        }

        user[template.EntityType.Key.Name] = key;

        return user;
    }

    public void Insert(LatticeRecord user) => ByName.InsertAsync(user).AsTask().GetAwaiter().GetResult();

    public void Delete(long key) => ByName.DeleteAsync(key).AsTask().GetAwaiter().GetResult();

    // Inserts users of the writer, from the first key above the fixture's on, until the primary index
    // has grown once and then until it holds margin users fewer than its capacity, so that the insert
    // after margin more grows it again; returns the key of the next user to insert. The index doubles
    // its capacity when it grows, before the key that does not fit (PrimaryIndex), so the users it
    // held before that key are its old capacity.
    public long FillBelowGrowth(int margin)
    {
        var next = (long)_users.Count + 1;
        var bytes = _database.Counters.PrimaryIndexBytes;

        while (_database.Counters.PrimaryIndexBytes == bytes)
        {
            Insert(NewUser(next++));
        }

        var capacity = 2 * (next - 2);

        while (next - 1 < capacity - margin)
        {
            Insert(NewUser(next++));
        }

        return next;
    }

    // Runs the action inside the next growth of the main table of the primary index of the users, on
    // the thread of the insert that grows it (EngineDatabase.ArmGrowthSwitch).
    public void OnNextGrowth(Action action) => _database.ArmGrowthSwitch(ByName.EntityType, action);

    // What the primary indexes hold, in bytes: it doubles when the index of the users grows.
    public long IndexBytes => _database.Counters.PrimaryIndexBytes;

    public void Dispose() => _database.Dispose();

    // The descriptor with Region of the users in Dictionary storage.
    private static string WithDictionaryRegion(string json)
    {
        const string Region = "\"Name\": \"Region\", \"Type\": \"String\"";

        if (!json.Contains(Region, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The descriptor of the fixture does not declare Region as this scenario expects.");
        }

        return json.Replace(Region, $"{Region}, \"StringStorage\": \"Dictionary\"", StringComparison.Ordinal);
    }

    // The descriptor with User declaring no index: its three indexes, ByLogin, ByEmail and
    // ByRegionAndLogin, taken out of the list, which stays, empty.
    private static string WithoutUserIndexes(string json)
    {
        var first = json.IndexOf("{ \"Id\": 1, \"Name\": \"ByLogin\"", StringComparison.Ordinal);
        var last = json.IndexOf("\"Name\": \"ByRegionAndLogin\"", StringComparison.Ordinal);
        var open = first < 0 ? -1 : json.LastIndexOf('[', first);
        var close = last < 0 ? -1 : json.IndexOf(']', json.IndexOf(']', last) + 1);

        if (open < 0 || close < 0)
        {
            throw new InvalidOperationException("The descriptor of the fixture does not declare the indexes of User as this scenario expects.");
        }

        return string.Concat(json.AsSpan(0, open), "[]", json.AsSpan(close + 1));
    }

    // A user of the fixture as a record of the entity type User of the schema, field by field.
    private static LatticeRecord CopyInto(LatticeSchema schema, LatticeRecord user)
    {
        schema.TryGetEntityType("User", out var entityType);

        var copy = new LatticeRecord(entityType!);

        foreach (var field in user.EntityType.Fields)
        {
            copy[field.Name] = user[field.Name];
        }

        return copy;
    }

    // The descriptor with the class FixtureUser named for the entity type User.
    private static string WithUserClass(string json)
    {
        const string User = "\"Name\": \"User\",";

        if (!json.Contains(User, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The descriptor of the fixture does not name the entity type User as this scenario expects.");
        }

        return json.Replace(User, $"{User} \"ClrType\": \"{typeof(FixtureUser).FullName}\",", StringComparison.Ordinal);
    }
}
