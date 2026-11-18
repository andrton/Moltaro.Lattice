using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine.Tests.Engine;

// Opens a MemoryOnly master of the engine through its internal entry, EngineDatabase.Create
// (exceptions X3 and X4, docs/work/exceptions.md). After the open a test uses the public interfaces,
// and reads what only the engine shows from its counters. The second form fixes the seed of the
// hashes of the primary indexes, for a test of what the seed changes; the third gives the arena
// options and the allocator of its native memory, for a test that bounds the arena or counts what it
// obtained; the last takes every input a test of the engine can give (EngineTestInputs), such as the
// storage step of the commit stage.
internal static class EngineMaster
{
    public static async Task<EngineDatabase> OpenAsync(LatticeSchema schema)
    {
        var database = (EngineDatabase)LatticeDatabase.Create(OptionsOf(schema));

        await database.OpenAsync();
        return database;
    }

    public static Task<EngineDatabase> OpenAsync(LatticeSchema schema, ulong indexSeed) =>
        OpenAsync(schema, new EngineTestInputs { IndexSeed = indexSeed });

    public static Task<EngineDatabase> OpenAsync(LatticeSchema schema, ArenaOptions arena, NativeAllocator allocator) =>
        OpenAsync(schema, new EngineTestInputs { Arena = arena, Allocator = allocator });

    public static async Task<EngineDatabase> OpenAsync(LatticeSchema schema, EngineTestInputs inputs)
    {
        var database = EngineDatabase.Create(OptionsOf(schema), inputs);

        await database.OpenAsync();
        return database;
    }

    private static LatticeDatabaseOptions OptionsOf(LatticeSchema schema) => new()
    {
        PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
        Schema = schema,
    };
}
