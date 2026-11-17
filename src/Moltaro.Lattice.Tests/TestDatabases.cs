using Moltaro.Lattice.Engine;

namespace Moltaro.Lattice.Tests;

// Creates and opens a database of the placeholder or of the engine, so that one scenario test runs
// on both. The placeholder is reached through the public entry point, LatticeDatabase. The engine is
// reached through its internal entry, EngineDatabase.Create, under exception X4
// (docs/work/exceptions.md), until Epic 1, sprint S12 makes LatticeDatabase return it. Only the
// opening differs: after it a test uses the public interfaces and nothing else.
internal static class TestDatabases
{
    // The data of a theory that runs on both implementations.
    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum>> Implementations =>
    [
        new(DatabaseImplementationEnum.Placeholder),
        new(DatabaseImplementationEnum.Engine),
    ];

    // The data of a theory that runs a master and its child on both implementations. A master of the
    // engine that accepts children and its child, which loads the database over TCP, come with Epic 1,
    // sprint S10, and until then the row of the engine is skipped.
    public static IEnumerable<TheoryDataRow<DatabaseImplementationEnum>> ImplementationsWithChildren =>
    [
        new(DatabaseImplementationEnum.Placeholder),
        new(DatabaseImplementationEnum.Engine) { Skip = ChildLoading.NotImplemented },
    ];

    // Creates a database without opening it, as LatticeDatabase.Create does.
    public static ILatticeDatabase Create(DatabaseImplementationEnum implementation, LatticeDatabaseOptions options) => implementation switch
    {
        DatabaseImplementationEnum.Placeholder => LatticeDatabase.Create(options),
        DatabaseImplementationEnum.Engine => EngineDatabase.Create(options),
        _ => throw new ArgumentOutOfRangeException(nameof(implementation), implementation, "No such implementation."),
    };

    // Creates a database and opens it, as LatticeDatabase.OpenAsync does.
    public static async ValueTask<ILatticeDatabase> OpenAsync(DatabaseImplementationEnum implementation, LatticeDatabaseOptions options)
    {
        var database = Create(implementation, options);
        await database.OpenAsync();
        return database;
    }

    // A MemoryOnly master, with the schema of TestData unless another one is given.
    public static ValueTask<ILatticeDatabase> OpenMasterAsync(DatabaseImplementationEnum implementation, LatticeSchema? schema = null) =>
        OpenAsync(implementation, new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema ?? TestData.Schema,
        });

    // A MemoryOnly master that accepts children, with the schema of TestData unless another one is
    // given: the placeholder at an address of its own (MasterAndChild.NewAddress), the engine on the
    // loopback interface at a port the system chooses (MasterAndChild.Loopback). Its ListenUri is the
    // address a child is given.
    public static ValueTask<ILatticeDatabase> OpenListeningMasterAsync(DatabaseImplementationEnum implementation, LatticeSchema? schema = null) =>
        OpenAsync(implementation, new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema ?? TestData.Schema,
            ListenUri = implementation == DatabaseImplementationEnum.Engine ? MasterAndChild.Loopback : MasterAndChild.NewAddress(),
        });

    // A child of the master, at the address the master reports. Its open completes when it has loaded
    // the database.
    public static ValueTask<ILatticeDatabase> OpenChildAsync(DatabaseImplementationEnum implementation, ILatticeDatabase master) =>
        OpenAsync(implementation, new LatticeDatabaseOptions
        {
            Role = LatticeRoleEnum.Child,
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            MasterUri = master.ListenUri,
        });
}
