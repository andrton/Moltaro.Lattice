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

    // Generates the fixture, opens the database and inserts the users by name.
    public static UserDatabase Load()
    {
        var schema = LatticeSchema.Compile(WithUserClass(ReferenceFixture.ScalarSchemaJson(unique: false)));
        var records = ReferenceFixture.Generate(schema).ToList();
        var users = records.Where(record => record.EntityType.Name == "User").ToList();

        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
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
            $"Fixture: the scalar form without its Unique marks, seed {ReferenceFixture.Seed}, {ReadSeries.Number(records.Count)} records, digest {ReferenceFixture.Digest(records)}",
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

    public void Dispose() => _database.Dispose();

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
