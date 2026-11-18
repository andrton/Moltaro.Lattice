# Arena access, publication and reclamation protocol

Engineering specification for the bounded arena experiment, 13 November 2026.
It implements the accepted ownership and protection contracts without selecting
production field widths, allocation sizes or lock representations. Evidence and
limitations belong to the experiment's README and the prototype evidence record.

[Engine foundation](engine-foundation.md) · [Allocation policy](../decisions/allocation-and-compaction.md) · [Read access](../decisions/memory-and-access.md)

The subsequent [bounded experiment](../../src/Prototypes/ArenaLifecycle/README.md) passed
its directed, churn and paced concurrent checks. Its [Arc index extension](../../src/Prototypes/ArenaLifecycle/INDEXES.md)
also checks index-owned text, equal-key descriptor replacement and candidate handoff.
This is evidence for the specified fixtures, not all schedules or production owners.

## Ownership and addresses

The native allocator owns segments and size-class slots. A block has a relative
segment/offset address, logical length, capacity, state and owner. Its states are
Free, Prepared and Live; detached data may become reusable only after its protecting
readers and other retainers have drained. Allocation must include prepared blocks
and relocation destinations in the same memory budget as published data.

A managed struct control entry identifies one root lifetime: key, generation,
published revision, root address, live/deleted state and temporary lookup claims.
The primary index reaches this entry, never an unprotected native pointer. Owned
chunks identify the root entry and the descriptor position that must be repaired
when they move. Shared dictionary text instead belongs to a text entry
with its own address and one count of the blocks that refer to it: a root, a prepared root
or, later, a block of an index holds one reference for each Text descriptor it contains,
from the moment it is written until it is freed.

All addresses are internal. Relocation changes addresses, not keys, revisions or
logical contents. Published dictionary text is immutable; an update acquires a
reference to the replacement text. It never overwrites a string shared by roots.

## Lock and lifetime order

1. Under the short catalog gate, look up a root and claim its entry lifetime.
   Release the catalog gate before waiting for root protection.
2. Acquire root protection and revalidate that the claimed lifetime is still live.
   A deletion already published means absence. Never redirect an old claim to a
   replacement root with the same public key.
3. Access owned blocks only under root protection. Access dictionary bytes under
   additional dictionary protection. Neither a span nor an address escapes.
4. Release dictionary/root protection before dropping the lifetime claim under
   the catalog gate. Reuse a deleted control slot only after its last claim drains.

In the indexed fixture, nested memory work follows root -> catalog/index -> dictionary
-> allocator. The catalog/index gate may protect a text comparison but never waits
for a root gate: pin/lookup/release scopes end before root acquisition. The allocator
never acquires an owner gate. A compactor discovers a candidate under allocator
protection, releases it, then acquires the owner and revalidates the candidate.

The experiment uses striped reader/writer gates for roots, one dictionary gate and
one allocator gate. This deliberately bounds lock-object overhead; unrelated roots
sharing a stripe can contend. Production packed root gates and dictionary sharding
are not established by these measurements. A future batch takes the existing
database publication barrier before root gates in canonical order; the experiment
does not validate that multi-root integration.
If a future batch uses striped gates, acquire each distinct physical stripe once
in stripe order; sorting logical root identities alone does not order shared gates.

The Arc extension implements index publication protection between root and dictionary
protection. Candidate lookup retains root lifetimes under the catalog/index gate,
releases it, then reads roots and rechecks the predicate. Removed keys release text
ownership only after protected index traversal has drained. Comparers protect each
native dereference, so compaction can update a stable text address without reindexing.
The checked lock order uses one coarse catalog/index gate; scalable gates and the
remaining production E04 integration are not established by this experiment.

No catalog, root, dictionary or allocator gate spans database/network I/O. Protected
reads are short synchronous internal work. Test hooks used to force an interleaving
are not a public callback API or permission for application code to wait under a gate.

## Deferred reclamation, 16 November 2026

Where the text above says otherwise, this section replaces it for the engine.

By the owner's decision of 16 November 2026 the engine protects a read by an epoch and
frees later, as `LightEpoch` of Garnet/Tsavorite does. A read enters the protection when
its step inside the engine begins and leaves when the step ends. Memory that a read
could still reach when a write replaces or removes it — an old root with its blocks, a
replaced table of an index, the entry of a deleted root — is retired, not freed: it is
detached from its owner, counted as retired, and freed once every read that had entered
before it was retired has left. A write being published never waits for readers, and a reader
never waits for the writer on its ordinary path.

What does not move: a read is coherent for one entity, it sees one written state and
never a mixture; a retired entry is reused only after the reads that could see it have
left, and a read never follows an old entry to a key that was created again; a close
waits for the reads in progress and frees everything retired; no code of the
application runs, and nothing waits — no I/O, no `await`, no wait for a position —
while a read is inside the protection, so a read holds retired memory back only for the
length of one step. A read of an absent key repeats its lookup when a write was published
during it; the step ends with the first lookup no write crossed. The publishing thread
looks at the readers when the items retired since its last look reach the number of slots
or their bytes reach one segment, and whenever memory is short, a drain is asked for or
the database closes. What a look leaves behind, because reads still hold it, stays retired
and counted in the retired bytes, and a later look frees it; the count toward the next
look starts at the look, so that a read held long does not make every write look. A look
that leaves retired items behind arms a second trigger: until a look leaves nothing
behind, a published write that retires nothing counts as one item, so the next look comes
within one window of writes of any kind. Without such a look a write that retires nothing
counts nothing, so a load without a held read looks as often as it did without that
trigger. What waits between two looks is bounded by those two thresholds beside what
reads hold.
The bytes of a retired root are the slots of its
own blocks and those of the entry of every Dictionary text it names: an entry whose last
reference lies in retired roots stays live until the drain frees them and is freed with
them, so it waits for the same look. A text that other roots share is counted for each
root that names it. That over-counts, which is safe: it only makes a look come earlier,
and a look frees nothing a read can still reach. An enumeration enters and leaves at each step,
and within a long step after each bounded piece of walking, and holds nothing between
two steps. A reader that finds no free slot of the epoch
table waits for one and never fails. A published root is never changed: a partial update
publishes a new root block that shares the unchanged value blocks (the owner's decision
of 16 November 2026).

Retired memory is the engine's until it is freed: it is counted, it counts against the
memory budget of the instance, and a write that meets the budget first frees what can be
freed and waits for the reads that hold the rest before it is refused. The engine frees
retired memory on the path of its own operations, as the mechanism it follows does; it
starts no thread and no timer for it. A database nobody uses may therefore hold the last
retired memory until its next operation or its close.

## The stamp of a root entry

A read by key and the capture of a load take the state, the root and the sequence of the
last mutation of a root entry as one state, without a lock, while the publishing thread
changes the entry. The stamp of the entry is the sequence of its last mutation, and one
value, 0, which no published write has, is no stamp but the mark of a write in progress. A
write stores the mark, then the new state or root, then its own sequence as the new stamp,
each store with release semantics. A read loads the stamp with acquire semantics and backs
off while it finds the mark, loads the state and the root with acquire semantics, and loads
the stamp again: the same stamp both times is one state, another one means that a write came
between, and the read takes the entry again. The reader's half is in
`Engine/Entities/EntityTable.cs`, the writer's in `Engine/Writes/WriteExecutor.cs`.

The protocol is that of `SeqLock` of crossbeam (`crossbeam-utils/src/atomic/seq_lock.rs`) and
of the optimistic read of its `AtomicCell` (`atomic_cell.rs`), at commit
`a28f40fdddb70bea8e5faf0efad9af0be4b9dbea`. What differs, and why:

- The word is the sequence of the last mutation of the entry, so the stamp is the sequence
  that names the state copied, and the read loads no word besides the one it takes for its
  revision; the entry has no room for a word of its own, since it stays at 48 bytes. The mark
  is 0 where the source's is 1: no published write has the sequence 0. The new stamp is the
  sequence of the write where the source's is the old stamp plus 2: sequences only grow, so
  neither repeats a stamp a read holds.
- The source loads the data with volatile loads, which order nothing, and puts an acquire
  fence before a relaxed second load of the stamp. Here each load of the data is itself an
  acquire, which keeps the second load of the stamp after it, as the fence does; and each
  store of the writer is a release, so the store of the data keeps the store of the mark
  before it, as the release fence after the source's swap does.
- The source's writers take the lock by a swap with acquire semantics, because several
  contend for it; here one thread publishes, under the lock of the writes, and stores the
  mark.
- The source's read, after one failed attempt, takes the lock of the writers so that writers
  do not starve it. A read here takes no lock and writes nothing but its slot of the
  protection, so it tries again: at once after a failed validation, and while it finds the
  mark after a back-off by `SpinWait`, which spins, then gives up the processor, as the
  source's `Backoff.snooze` does for a writer that finds the lock held.
- The source has a write that releases the lock without a new stamp (abort). A write here
  stops between its stores only when its publication fails, which allocates nothing and does
  no I/O, so only a fault injected by a test or a defect fails it; that faults the database,
  and a read that retries on the mark gives up with Unavailable.

## Read and mutation paths

An ordinary copy read claims the root, acquires protection, copies scalar fields,
owned chunks and dictionary text, then releases all protection. The returned arrays
and strings belong to the caller. A protected calculation reads the same bytes and
returns only a detached scalar result; it allocates no object graph.

Full Save reserves a root block, owned chunks and a prepared dictionary reference.
Fill and validate these private blocks before storage dispatch. Failure or cancellation
before dispatch returns every reservation. A pending mutation retains its lifetime
claim and conflict-order ownership, not a read/write gate, during a durable wait.
This experiment supplies the resulting ordered publication path without a storage
provider; it is not a test of PostgreSQL or the parallel preparation scheduler.

After a known commit, acquire root and necessary dictionary protection, validate the
prepared predecessor, publish the new root/revision, convert prepared string claims
to stored references, and detach old owned blocks and old string references. The
protected reader cannot observe a mixture of old and new fields. Root lifetime and
allocator resources must already exist; the production path must also reserve index
resources before commit. If committed publication fails, use the accepted instance
fault/reopen policy, not an attempted rollback of durable data.

A fixed-field partial update modifies its fixed fields under exclusive root
protection, without replacing the root or its payload blocks. Delete removes the
catalog mapping and marks the old lifetime dead under root protection, detaches its
blocks and releases stored text references. A queued old lookup sees absence; a new
lookup may reach a newly created lifetime. Prepared operations retain old entries
until they publish or abort, so slot reuse cannot change their target.

## Reclamation and relocation

For this protocol's synchronous root reads, acquiring exclusive root protection
already drains readers of owned data. Detached owned blocks can therefore be freed
within that scope after descriptor publication; an epoch queue is unnecessary for
these blocks. A queued lookup retains control metadata, not the removed payload.

Shared text is freed only when root, prepared and index counts are all zero and
exclusive dictionary protection excludes text readers. A root gate alone is
insufficient. The Arc extension supplies index retainers, including prepared keys;
replication and other production owners still need integration. Zero asynchronous
retired bytes here must not be generalized to unimplemented retaining subsystems.

The compactor selects a live slot in a sparse segment with compatible destination
space in an earlier segment. It tries the owner's exclusive gate without waiting;
a busy owner is skipped. Recheck generation, current descriptor and allocation owner
after acquiring protection. Reserve one existing destination, copy the bounded block,
repair the root/dictionary/child descriptor, and release the old slot. There is no
whole-object copy for moving one chunk, no revision change and no journal entry.

Compaction does not allocate a new segment merely to relocate. It has a bounded
candidate scan and one-block work step. The harness schedules steps; production
trigger/fairness scheduling remains separate. An empty segment may enter the bounded
reserve; excess completely unused segments are returned to the native allocator.
Releasing native allocations does not guarantee an immediate fall in process RSS.

## Required bounded checks and measurements

- Coherent copy/calculation reads across updates; detached copies remain unchanged.
- A held reader delays mutation, while compaction skips that busy owner.
- Lookup before deletion, deletion before root acquisition, same-key recreation and
  delayed control-entry reuse; stale relocation candidates must be harmless.
- Full Save preparation and abort, readable old state during a simulated storage
  wait, targeted fixed-field writes, and shared text retained by another root or
  prepared write.
- Churn at bounded live cardinality with holes, root/chunk/text relocation, complete
  native ownership/reference audits and a final zero-allocation disposal check.
- Separate latency distributions for protected reads, owned copies, in-place writes,
  full replacements, deletion and compaction steps. Include waits and scheduler
  effects; do not subtract them or call a tiny run a throughput guarantee.
- Report native allocated capacity, useful live bytes, live class slack, prepared
  capacity, reusable space, empty reserve and relocation peak separately. Report
  managed heap, allocation traffic, process working set and CPU time separately.

All checks are synthetic and resource bounded. This is a mechanism experiment,
not a release plan, production allocator or proof of arbitrary reader schedules.

## Bounded WriteBatch extension

The [WriteBatch experiment](../../src/Prototypes/WriteBatch/README.md) owns one ArenaLifecycle
instance behind a database publication barrier. Its Read, Query and compaction paths
participate in the barrier; roots/indexes/text still use the protection above.
The existing lock order is extended at the outside by database protection.

Whole-batch validation/preparation precedes a real PostgreSQL commit, with no arena
gate spanning storage I/O. The prototype's writer permit serializes preparation as
well as commit/publication; parallel preparation is not established by this check.
Publication drains protected access, installs all roots and indexes, and advances
the applied position only after the last member. Failure marks the instance faulted
before opening access, and reopen rebuilds one arena from the independent fixture
base and whole committed journal batches. There is no second serving arena.

Controlled interleavings, pre-commit rollback, post-commit publication failures,
same-token repeats and four abrupt application-exit/reopen cases passed. Publication
pause and memory observations belong to the experiment report. The fixture's fixed
limits, receipt retention and binary encoding are not a production persistence format
or final unified memory budget; the accepted 256-byte base/applier remains separate.

The subsequent [saved-unit extension](../../src/Prototypes/WriteBatch/UNITS.md) exercises
256-byte base units and background application on that same path. Maintenance owns
only its SQL connection/cursor transaction, never arena protection. Saved units,
applied position and revision floor commit together; cleanup removes only covered
journal rows and preserves receipts. A single recovery SQL snapshot ties the base
to its tail. The independent fixture format still needs general-schema integration.
