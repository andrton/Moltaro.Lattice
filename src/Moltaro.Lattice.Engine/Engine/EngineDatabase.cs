using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine;

// The database of the engine: an ILatticeDatabase over the native arena, the table of root
// entries and the root layouts of this assembly, with the primary index. It is what the public
// contract runs on once the placeholder of the core library is gone.
//
// Not implemented yet: Create and every member throw NotSupportedException. What follows is what
// Epic 1, sprint S06, implements.
//
// How a test reaches it. Until S12 LatticeDatabase still opens the placeholder, and a test opens
// the engine through Create, the internal entry of exception X4 (docs/work/exceptions.md): only
// the opening differs, and every operation after it goes through the public interfaces. In S12
// LatticeDatabase returns this class, and Create is removed with its uses.
//
// What Create opens. In S06 an instance is a MemoryOnly master that serves its own process;
// children, the address at which a master accepts them, and Durable storage come with S10 to S12
// and S27. Create makes the instance without opening it, as LatticeDatabase.Create does.
// OpenAsync compiles the root layout of every entity type before any data is admitted: a fixed
// part larger than one block fails the open with InvalidSchema, and an entity type with a field
// the engine cannot store yet gets no layout (brief of S06, the planner's answer to point 1 of
// Q35).
//
// A read of one entity. A read by key copies one state of one entity under the protection of its
// root, which covers every block the root owns, and returns an owned copy; it holds nothing once
// it has returned. It finds the root through a short claim on its entry, taken under the catalog
// gate, and it never waits for a root while it holds that gate. An entity deleted before the read
// reaches it is absent, never the values of another entity (milestone contract, "Reader
// protection, publication and compaction"; Q06 and Q06.1 of the decision on memory and access;
// brief of S06, the planner's answer to Q32).
//
// A write of one entity. A write is atomic for its entity: it is checked and prepared before
// anything is published, publication installs it whole at the next position of the history, and
// a write that fails leaves nothing behind. A new database is at sequence zero, and its first
// write gets sequence one (D3 of Epic 1). Every published change gives the entity a new revision,
// and a revision binds its entity: one read from an entity never satisfies a condition on
// another, and a recreated entity has a new one (milestone contract, "Write coordinator and
// revision semantics"; Q13 of the decision on mutation commands; D4 of Epic 1). In S06 writes are
// executed one after another; the write coordinator comes with S09. No lock is held across an
// await.
internal sealed class EngineDatabase : ILatticeDatabase
{
    // Makes an instance for the options without opening it. In S06 a MemoryOnly master.
    public static EngineDatabase Create(LatticeDatabaseOptions options) => throw NotImplemented();

    // What the engine holds at this moment, for the tests and later for diagnostics.
    public EngineCounters Counters => throw NotImplemented();

    public LatticeDatabaseStateEnum State => throw NotImplemented();

    public LatticeSchema Schema => throw NotImplemented();

    public LatticePosition Position => throw NotImplemented();

    public ValueTask WaitForPositionAsync(LatticePosition position, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        throw NotImplemented();

    public ValueTask WaitForPositionAsync(LatticeSession session, CancellationToken cancellationToken = default) =>
        throw NotImplemented();

    public ValueTask OpenAsync(CancellationToken cancellationToken = default) => throw NotImplemented();

    public ILatticeSet<TEntity> Set<TEntity>()
        where TEntity : class =>
        throw NotImplemented();

    public ILatticeSet Set(string entityName) => throw NotImplemented();

    public ILatticeWriteBatch CreateWriteBatch() => throw NotImplemented();

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) => throw NotImplemented();

    public ValueTask DisposeAsync() => throw NotImplemented();

    public void Dispose() => throw NotImplemented();

    // The one refusal of every member until S06 implements the engine database.
    private static NotSupportedException NotImplemented() =>
        new("The engine database of Epic 1, sprint S06, is not implemented yet.");
}
