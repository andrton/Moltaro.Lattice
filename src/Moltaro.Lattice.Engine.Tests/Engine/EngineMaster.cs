using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Opens a MemoryOnly master of the engine through its internal entry, EngineDatabase.Create
// (exceptions X3 and X4, docs/work/exceptions.md). After the open a test uses the public interfaces,
// and reads what only the engine shows from its counters. The second form gives the arena options and
// the allocator of its native memory, for a test that bounds the arena or counts what it obtained.
internal static class EngineMaster
{
    public static async Task<EngineDatabase> OpenAsync(LatticeSchema schema)
    {
        var database = EngineDatabase.Create(OptionsOf(schema));

        await database.OpenAsync();
        return database;
    }

    public static async Task<EngineDatabase> OpenAsync(LatticeSchema schema, ArenaOptions arena, NativeAllocator allocator)
    {
        var database = EngineDatabase.Create(OptionsOf(schema), arena, allocator);

        await database.OpenAsync();
        return database;
    }

    private static LatticeDatabaseOptions OptionsOf(LatticeSchema schema) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = schema,
    };
}
