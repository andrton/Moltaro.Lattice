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
}
