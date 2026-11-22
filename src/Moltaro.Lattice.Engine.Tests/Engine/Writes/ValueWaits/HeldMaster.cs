namespace Moltaro.Lattice.Engine.Tests.Engine.Writes.ValueWaits;

// The master of the tests of a write that waits for the holder of a value: UniqueSchema, with the
// persistence step of the test (HeldPersistence), which holds the write of the holder while the waiter is
// looked at. Mebibyte is the unit of the Data of an account in the tests that fill the memory of the arena
// beside a wait.
internal static class HeldMaster
{
    public const int Mebibyte = 1024 * 1024;

    public static Task<EngineDatabase> OpenAsync(HeldPersistence storage) =>
        EngineMaster.OpenAsync(UniqueSchema.Schema, new EngineTestInputs { Persistence = storage });
}
