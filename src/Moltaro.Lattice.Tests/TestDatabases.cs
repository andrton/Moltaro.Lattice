namespace Moltaro.Lattice.Tests;

// Creates and opens a database of the engine, which the public contract runs on from Epic 1, sprint S12.
// Only the opening differs: after it a test uses the public interfaces and nothing else.
internal static class TestDatabases
{
    // Creates a database without opening it, as LatticeDatabase.Create does.
    public static ILatticeDatabase Create(LatticeDatabaseOptions options) => LatticeDatabase.Create(options);

    // Creates a database and opens it, as LatticeDatabase.OpenAsync does.
    public static async ValueTask<ILatticeDatabase> OpenAsync(LatticeDatabaseOptions options)
    {
        var database = Create(options);
        await database.OpenAsync();
        return database;
    }

    // A MemoryOnly master, with the schema of TestData unless another one is given.
    public static ValueTask<ILatticeDatabase> OpenMasterAsync(LatticeSchema? schema = null) =>
        OpenAsync(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema ?? TestData.Schema,
        });

    // A MemoryOnly master that accepts children, with the schema of TestData unless another one is
    // given, on the loopback interface at a port the system chooses (MasterAndChild.Loopback). Its
    // ListenUri is the address a child is given. A master whose socket the system refuses fails its open
    // with the state of the system in the message (ListenFailure).
    public static async ValueTask<ILatticeDatabase> OpenListeningMasterAsync(LatticeSchema? schema = null)
    {
        var master = Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema ?? TestData.Schema,
            ListenUri = MasterAndChild.Loopback,
        });

        await ListenFailure.ExplainAsync(master.OpenAsync().AsTask());
        return master;
    }

    // A child of the master, at the address the master reports. Its open completes when it has loaded
    // the database.
    public static ValueTask<ILatticeDatabase> OpenChildAsync(ILatticeDatabase master) =>
        OpenAsync(new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
        });
}
