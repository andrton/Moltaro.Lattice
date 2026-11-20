using Moltaro.Lattice.Schema;
using Moltaro.Lattice.TestSupport.Replication;

namespace Moltaro.Lattice.Engine.Tests.Engine.Replication;

// Opens the masters and children of the tests of replication, through LatticeDatabase.Create or, with
// test inputs, through EngineDatabase.Create (exception X3, docs/work/exceptions.md); after the open a
// test uses the public interfaces. A master is MemoryOnly and accepts children on the loopback
// interface, at a port the system chooses (MasterAndChild.Loopback) unless the test gives the address;
// a child is given the address its master reports in its ListenUri. Either takes the inputs a test gives (EngineTestInputs),
// such as an allocator that counts the native memory or the seed of the hashes; inputs left empty are
// what an instance made without them uses. A master whose socket the system refuses fails its open with
// the state of the system in the message (ListenFailure).
internal static class ReplicationNodes
{
    // The bound of every wait of these tests.
    public static readonly TimeSpan Bound = TimeSpan.FromMinutes(1);

    // An open timeout far beyond the bound: an open given it that ends within the bound did not end
    // because its time was up.
    public static readonly TimeSpan BeyondTheBound = TimeSpan.FromHours(1);

    // An open timeout short enough for a test to let it pass, and long enough that every wait of an open
    // that has to end within it, a connect, a Hello or the next frame of a load, does so on a machine
    // busy with other work as well. A timeout of 1 s ended a load whose frames arrived at most half a
    // second apart when the suite ran beside three other builds and suites.
    public static readonly TimeSpan ShortOpenTimeout = TimeSpan.FromSeconds(5);

    public static Task<EngineDatabase> OpenMasterAsync(LatticeSchema schema) =>
        OpenMasterAsync(schema, MasterAndChild.Loopback, new EngineTestInputs());

    public static Task<EngineDatabase> OpenMasterAsync(LatticeSchema schema, EngineTestInputs inputs) =>
        OpenMasterAsync(schema, MasterAndChild.Loopback, inputs);

    public static Task<EngineDatabase> OpenMasterAsync(LatticeSchema schema, Uri listenUri) =>
        OpenMasterAsync(schema, listenUri, new EngineTestInputs());

    public static async Task<EngineDatabase> OpenMasterAsync(LatticeSchema schema, Uri listenUri, EngineTestInputs inputs)
    {
        var master = EngineDatabase.Create(MasterOptions(schema, listenUri), inputs);

        await ListenFailure.ExplainAsync(master.OpenAsync().AsTask().WaitAsync(Bound));
        return master;
    }

    // A master that gives a node that connects to it an open timeout far beyond the bound to introduce
    // itself, so that within the bound only a check of what the node sent ends its connection.
    public static async Task<EngineDatabase> OpenPatientMasterAsync(LatticeSchema schema)
    {
        var options = new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
            ListenUri = MasterAndChild.Loopback,
            OpenTimeout = BeyondTheBound,
        };
        var master = (EngineDatabase)LatticeDatabase.Create(options);

        await ListenFailure.ExplainAsync(master.OpenAsync().AsTask().WaitAsync(Bound));
        return master;
    }

    public static LatticeDatabaseOptions MasterOptions(LatticeSchema schema, Uri listenUri) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = schema,
        ListenUri = listenUri,
    };

    // A child of the master at the address, with the default open timeout; the open completes when the
    // child has loaded the database, within the bound.
    public static Task<EngineDatabase> OpenChildAsync(Uri masterUri) =>
        OpenChildAsync(ChildOptions(masterUri), new EngineTestInputs());

    public static Task<EngineDatabase> OpenChildAsync(Uri masterUri, EngineTestInputs inputs) =>
        OpenChildAsync(ChildOptions(masterUri), inputs);

    public static async Task<EngineDatabase> OpenChildAsync(LatticeDatabaseOptions options, EngineTestInputs inputs)
    {
        var child = EngineDatabase.Create(options, inputs);

        await child.OpenAsync().AsTask().WaitAsync(Bound);
        return child;
    }

    // The options of a child of the master at the address, with the default open timeout.
    public static LatticeDatabaseOptions ChildOptions(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
    };

    // The options of a child of the master at the address whose waits for a position, among them the wait
    // of a write it forwarded for its application, last beyond the bound of the tests, so that a test that
    // holds a write there for as long as it needs never sees it end by its time.
    public static LatticeDatabaseOptions ChildWaitingBeyondTheBound(Uri masterUri) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
        SynchronizationTimeout = BeyondTheBound,
    };

    // The options of a child of the master at the address, with the open timeout and the memory limit
    // given; null for the default limit.
    public static LatticeDatabaseOptions ChildOptions(Uri masterUri, TimeSpan openTimeout, long? memoryLimit = null) => new()
    {
        Role = LatticeRoleEnum.Child,
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        MasterUri = masterUri,
        OpenTimeout = openTimeout,
        MemoryLimit = memoryLimit,
    };
}
