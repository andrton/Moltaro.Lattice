using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;

namespace Moltaro.Lattice.Engine;

// The database of the engine: an ILatticeDatabase over the native arena, the table of root
// entries and the root layouts of this assembly, with the primary index. It is what the public
// contract runs on once the placeholder of the core library is gone.
//
// Not implemented yet: Create and every member throw NotSupportedException. What follows is what
// Epic 1, sprint S06, implements; the details are in its brief, in "What the engine does where
// the block above does not say".
//
// How a test reaches it. Until S12 LatticeDatabase still opens the placeholder, and a test opens
// the engine through Create, the internal entry of exception X4 (docs/work/exceptions.md): only
// the opening differs, and every operation after it goes through the public interfaces. In S12
// LatticeDatabase returns this class, and Create is removed with its uses.
//
// Create. In S06 an instance is a MemoryOnly master. Create makes it without opening it and
// refuses in the order of LatticeDatabase.Create: ArgumentNullException for null options;
// Validate, which gives InvalidConfiguration; then NotSupportedException for Durable persistence
// (until S27), for a child role (until S10) and for a ListenUri (until S10).
//
// States. Created, Loading during OpenAsync, Ready, Closed; Connecting and CatchingUp do not
// occur, and Faulted only through a failed publication, which S09 tests. OpenAsync compiles the
// root layout of every entity type before any data is admitted: a fixed part larger than one
// block fails the open with InvalidSchema, and an entity type with a field the engine cannot
// store yet gets no layout (the planner's answer to point 1 of Q35). A failed open closes the
// instance. Before the open an operation is Unavailable; after a close it is
// ObjectDisposedException. A close waits for the reads in progress before it frees the arena.
//
// Position and revision. Each open has a new random non-zero history; the sequence is 0 and grows
// by 1 with each published write. A revision is the history and the root entry's
// LastMutationSequence: in S06 every position changes one entity, so the revision of one entity
// never satisfies a condition on another, and a recreated entity has a new one. LatticeRevision is
// two opaque 64-bit values, so this composition is internal and can change; how a revision binds
// its entity when several entities change at one position belongs to the stage with the batch
// (milestone contract, "Write coordinator and revision semantics"; D4 of Epic 1).
//
// A read of one entity. A read by key copies one state of one entity under the protection of its
// root, which covers every block the root owns, and returns an owned copy; it holds nothing once
// it has returned. It finds the root through a claim on its entry, taken under the catalog gate,
// and never waits for a root while it holds that gate. An entity deleted before the read reaches
// it is absent, never the values of another entity (milestone contract, "Reader protection,
// publication and compaction"; Q06 and Q06.1 of the decision on memory and access; the planner's
// answer to Q32). It allocates the returned copy, the object or the record with everything
// reachable from it, and nothing else; a key boxed by a public signature that takes object is the
// caller's allocation. The sets of the core box the key and pass an array, so the engine has sets
// of its own.
//
// A write of one entity. A write is atomic for its entity: it is checked and prepared before
// anything is published, and a write that fails leaves nothing behind. In S06 writes are executed
// one after another; the write coordinator comes with S09. No lock is held across an await.
//
// The limits before S09. The arena gets half of GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
// read at open, and an entity at most 1 MiB, as internal values that S09 turns into options; a
// refusal is WriteRejected without a Limit until S09.
//
// What is refused until a later sprint. Until S07 the engine accepts the key types the three test
// classes of S06 use: Int64, and String with Ordinal comparison, Plain storage and UTF-8. It
// refuses with NotSupportedException everything the public documentation says is not stored yet,
// and every other key type: another key type, a field compared without case, an Enum field and a
// schema in UTF-16 until S07; Dictionary storage until S13; a unique index until S16; owned
// values, collections and references until S18 and S19. Every write of such an entity type, an
// insert, a replace, an upsert or a delete, with or without a revision, is refused at the write,
// after the checks of its arguments and the validation of the entity (Q25); a read of it by key
// finds nothing, and an enumeration of it is empty. Binding a class and Index(name) work.
//
// Enumeration until S08. An enumeration is lazy, holds nothing between items and claims each
// entry as a read by key does. It fails with NotSupportedException from the MoveNext that sees
// the primary index of its entity type changed, by an insert or a delete; a replace does not
// change it. What the enumeration returned before stays valid. Until S08 the primary index grows
// under exclusive protection.
//
// Sessions and the write batch. On a master the members that take a session, and
// WaitForPositionAsync, are served: a master has applied every position it gave out.
// CreateWriteBatch returns the batch of the core: building it works, and its commit is
// NotSupportedException until S17.
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
