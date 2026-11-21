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
    // EngineCounters.MemoryLimit reports the limit in force. Tests of a small arena use these; tests
    // of the option use the public open (brief of Epic 1, sprint S09, the answers of the planner to
    // the contract author). Null: the arena takes its limit from the options of the database.
    public ArenaOptions? Arena { get; init; }

    // The allocator through which the arena obtains and returns its native memory, so that a test can
    // count every native byte the database obtained and returned. Null: NativeAllocator itself.
    public NativeAllocator? Allocator { get; init; }

    // The seed with which the primary indexes and the text table compute every hash, so that a test can
    // build keys that share one hash under one seed and show them spread under another (the planner's
    // answer to Q50; brief of S08, part 2), and give a child another seed than its master (brief of S13).
    // Null: the instance draws its own when it opens.
    public ulong? IndexSeed { get; init; }

    // The persistence step of the commit stage, so that a test can hold a commit until it releases it
    // (IPersistence; brief of S09, "Dispatch"). Null: the persistence of MemoryOnly, which completes at
    // once.
    public IPersistence? Persistence { get; init; }
}
