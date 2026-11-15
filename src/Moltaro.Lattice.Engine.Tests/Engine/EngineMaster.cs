using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Opens a MemoryOnly master of the engine through its internal entry, EngineDatabase.Create
// (exceptions X3 and X4, docs/work/exceptions.md). After the open a test uses the public interfaces,
// and reads what only the engine shows from its counters.
internal static class EngineMaster
{
    public static async Task<EngineDatabase> OpenAsync(LatticeSchema schema)
    {
        var database = EngineDatabase.Create(new LatticeDatabaseOptions
        {
            PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
            Schema = schema,
        });

        await database.OpenAsync();
        return database;
    }
}
