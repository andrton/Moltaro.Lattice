using Moltaro.Lattice.Engine.Writes;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Allocation;

namespace Moltaro.Lattice.Engine;

// What a test of the engine gives an instance besides its options (exception X3,
// docs/work/exceptions.md), through EngineDatabase.Create(options, inputs). A test sets only what it
// needs; each member left null is what an instance made by Create(options) uses, so an instance made
// without inputs pays nothing for them.
internal sealed class EngineTestInputs
{
    // The options of the arena, so that a test can bound the arena to a few segments. When they are
    // given they are the options of the arena, and LatticeDatabaseOptions.MemoryLimit is not read;
    // EngineCounters.MemoryLimit reports the limit in force. Tests of a small arena use these; tests of
    // the option use the public open. Null: the arena takes its limit from the options of the database.
    public ArenaOptions? Arena { get; init; }

    // The allocator through which the arena obtains and returns its native memory, so that a test can
    // count every native byte the database obtained and returned. Null: NativeAllocator itself.
    public NativeAllocator? Allocator { get; init; }

    // The seed with which the primary indexes and the text table compute every hash, so that a test can
    // build keys that share one hash under one seed and show them spread under another, and give a child
    // another seed than its master. Null: the instance draws its own when it opens.
    public ulong? IndexSeed { get; init; }

    // The persistence step of the commit stage, so that a test can hold a commit until it releases it
    // (IPersistence). Null: the persistence of MemoryOnly, which completes at once.
    public IPersistence? Persistence { get; init; }

    // The source of the timestamps a read takes for its time limit, in ticks of Stopwatch, so that a test
    // decides how much time a step spends inside the engine, and when, without the clock of the machine and
    // without waiting (Entities.ReadSettings). It is called on the thread of the read, inside the step, and
    // must neither wait nor call the database. Null: Stopwatch.GetTimestamp.
    public Func<long>? ReadTimestamps { get; init; }
}
