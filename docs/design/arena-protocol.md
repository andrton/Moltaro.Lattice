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
`Engine/Entities/EntityTable.cs`, the writer's in `Engine/Entities/ChangePublisher.cs`.

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

## The reads of an entity table

The entities of one entity type are read by key, by enumeration and through a secondary
index (`Engine/Entities/EntityTable.cs`). Every read runs inside the protection of reads,
takes no lock and writes nothing but its slot of the protection, while the write executor
changes the primary index and the root entries under the lock of the writes. A write being
prepared looks its key up under that lock, outside the protection, since nothing changes
them while it holds the lock. The search through a secondary index is described with the
index ([the ordered index](ordered-index.md), "The search by a full key").

### A read by key

1. It looks the hash of the key up in the primary index, which gives the slots of the root
   entries whose key has that hash, and compares the key with the key each of their roots
   stores ("The comparison of a key", below). The root of a slot the index names is live
   or retired, never freed, while the read is inside, and every root of one lifetime holds
   the same key: the primary key is immutable, and a replace keeps the stored spelling of a
   String key compared without case.
2. It takes the state, the root and the sequence of the last mutation of the entry as one
   state, by the stamp of the entry ("The stamp of a root entry", above): a write stores
   the mark before it changes the root or the state and its own sequence after, and a read
   that finds the mark, or another sequence after it took the rest, takes them again. It
   writes nothing for this.
3. A live root is copied whole, with every block it owns, into memory of the engine: the
   blocks of a published root never change, and a root that a write replaced or deleted is
   retired, not freed, until the read has left, so the copy is one state
   ([memory and access](../decisions/memory-and-access.md)), never memory that was freed or
   given to another root.
4. It leaves the protection. The caller builds its result from the copy, and only then does
   code of the application run. A read allocates the returned copy and nothing else.

A deleted entry is released, and its slot given to another root, only after the reads that
could have found it have left, so a read never follows an old entry to a key that was
created again.

### The position of an answer

For a root it copied: the sequence of the database read between the two reads of the
entry's sequence, or the entry's sequence when that is later, because an insert publishes
its entry before it advances the sequence of the database; the state copied is the
entity's at that position. For a root deleted before the read reached it: the sequence of
its delete. For an absent key: the sequence of the database read before the lookup, when
the lookup ended with the sequence unchanged; otherwise the read looks up again. An insert
publishes its entry in the index before the sequence of the database and a delete the
sequence before it removes the entry, so an absence seen at an unchanged sequence holds at
that sequence.

### An enumeration

An enumeration goes a step at a time. Between two steps nothing is held but what its pass
holds, managed memory of the enumeration. A step is made of rounds, each inside the
protection of reads, which it enters and leaves, and each walking a bounded number of words
of the index, so that no round holds back what writes retire for longer than a bounded piece
of work, however many entries a step has to pass, as after a growth. A round:

1. It looks at the time limit of a read, then takes the current table of the index with
   its version. A version other than the one its position belongs to means that the index
   grew since, and the walk starts again at the start of the new table: a growth places
   every entry anew, so a position in the old table means nothing in the new one. Mapping
   it, bucket b to buckets 2b and 2b + 1, would miss the entries the growth moved from the
   overflow buckets of the old table into main buckets that the position has passed.
2. It walks the words of the table from the position. For each entry it reads the key the
   root of the entry stores and skips it when the pass has returned that key: that keeps a
   key from being returned twice, after a growth, whose walk meets again what was walked,
   and after a delete and an insert of a returned key, whose new entry the walk may meet at
   a later word.
3. Otherwise, when the key the entry holds has no room in the pass, the round ends before
   the entry, before it copies anything; outside the protection the room is counted against
   the memory limit of a read and made, and the next round reads the entry anew. So a step
   copies the entity it returns once, the first of a pass and one whose key makes the room
   grow included, and nothing large is allocated inside the protection. With room, it copies
   the root as a read by key does, skips a root deleted meanwhile, and otherwise stages the
   key and the position after the entry, which the pass takes once the entity is built
   outside the protection. Room made for an entry that then turns out deleted is counted all
   the same: the limit counts the capacity the pass holds, and the pass holds it whether or
   not that key is remembered.
4. At the end of the table, the sequence of the database read when the round began is the
   position of the end when it is unchanged after the walk, as for an absent key; otherwise,
   and when the round walked its words without finding an entity, the next round goes on
   from the position.

The pass counts the time of every step, its rounds and what lies between them included, and
fails the round that would exceed a limit.

Every entity that nobody changes during the pass is returned once: its entry is in the index
throughout the pass, an entry does not move within a table, so a walk of a table meets it at
its word, and a walk of a grown table from its start meets it too; the key it stores is the
same in every root of its lifetime, so it is skipped only once it has been returned. Every
returned entity is one written state, by the stamp of its entry. An insert, a replace, a
delete or a growth never ends the pass, and a replace changes no entry of the index at all.

### The comparison of a key

The equality, the hash and the order of values follow the rules the schema declares, in one
service across the indexes and direct lookups (`Engine/Comparison/ValueComparer.cs`): the
primary index uses its equality and its hash, the secondary indexes its order, and nothing
else in the engine compares a key with stored text.

What equality is, for each type of a primary key:

- An integer and a Guid are held in the index as their value, and the equality and the hash
  of that .NET value are the key's: two integers of the key type are one key when they are
  the same number, two Guids when their 16 bytes are the same.
- A String follows the comparison its field declares, with the same result in a UTF-8 and in
  a UTF-16 database: Ordinal, the equality of the UTF-16 code units, or OrdinalIgnoreCase,
  by the case table of Lattice's own ([strings](../decisions/strings.md), Q04). The rule of
  equality and hash for text is the schema's, `LatticeValueDeclaration.TextComparison`,
  which the core follows for Map keys, Set elements and the keys of a write batch as well.

A stored text is compared with a given one a piece at a time. The stored text is read as
UTF-16, 256 code units at a time, into a buffer on the stack, and each piece is compared with
the same code units of the given text by the rule; the texts are equal when every piece is
and both end together. That is the answer of the rule for the whole texts. Both comparisons
hold texts of different lengths different and compare them place by place: a code unit with
the code unit at the same place, and without case a surrogate pair with a pair at the same
place, as one scalar value, whose mapping keeps the number of code units. No piece of the
stored text ends inside a pair, so a pair of the stored text is always compared whole; where
the given text has the first half of a pair at the end of its piece, the stored text has
another character at that place, which a high surrogate standing for itself equals under
neither comparison, so both the pieces and the whole texts differ. The order of an index
compares a text of a UTF-8 database under Ordinal by its bytes instead, with the one turn
that makes their order that of the UTF-16 code units ([the ordered index](ordered-index.md),
"Entries and their order"); every other comparison reads the code units. No culture is
consulted, and nothing of the casing of the runtime is called.

The hash of a key in the primary index is the engine's own, seeded with the seed of the
instance, so that the index depends on nothing a process sets and nobody who does not know
the seed can choose keys that share a hash: Marvin32, the hash of text of the .NET runtime,
over the UTF-16 code units of the text, and for a comparison without case over the code units
the case rule maps the text to, the mapping every hash without case follows. It is a hash of
code units, so a text hashes the same in a UTF-8 and in a UTF-16 database. The engine
computes it from the text a caller gives, when it makes the key, before the read enters the
protection of reads, and the index keeps it in its entry, so no stored text is ever hashed.

A comparison allocates nothing, whatever the length of the text and wherever it lies: its
buffers are pieces of 512 bytes of its stack, those of the mapping only for a comparison
without case, and none of them is zeroed, since every code unit a comparison reads it wrote
first. A lookup costs the hash of the given text, one pass over it, and for each key of the
index with the same hash, in practice the one that is looked for, one pass over its stored
text, decoded from UTF-8 or copied, and one pass of the rule over both. Without case a code
unit that equals its counterpart needs no mapping, and one that does not is looked up in the
case table.

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
