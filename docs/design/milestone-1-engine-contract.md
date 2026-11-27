# Milestone 1 engine implementation contract

Technical baseline and planning handoff, 14 November 2026. The mechanism
baseline is fixed here under the instruction to finish first-milestone design,
including the subsequently accepted consumer-required unique secondary indexes.
This is a specification for implementation, **not a milestone/sprint plan or a claim
that the production engine is implemented or verified**. Planning belongs to the
receiving agent. Product semantics remain owned by the linked decision records.

For milestone 1, this document and [operation retry protocol](operation-retry-protocol.md)
resolve earlier notes saying that physical mechanisms, framing or numerical starting
settings still need selection. Historical alternatives and experiment limitations
remain evidence; they are not another approval queue. Internal tuning values below
may change on evidence without changing observable guarantees or persisted formats.

## Delivery boundary

One database per engine instance, an embedded or hosted authoritative master, a
PostgreSQL Durable provider and MemoryOnly child replicas. Implement the complete
write/recovery/replication path. The first workload is users, groups, roles and
settings with 5–10 children. Include the accepted schema/type model, owned trees,
key references, typed and schema-driven access, protected read-only views, explicit
partial updates, atomic multi-type WriteBatch and declared-index access.

OQL, joins, cross-call snapshots, external transactions, partitioning, automatic
failover, lazy mixed-layout migration, manual maintenance APIs and administration
UI are outside this milestone. The broad v1 query-language decisions remain future
scope. Do not remove indexes, persistence, compaction or replica/session consistency
to make the first milestone look smaller.

The [performance requirements](performance-requirements.md) own release targets:
121,000 reference roots; 10,000 local reads/s per child; 200 logical changes/s total;
ordinary p99 local read <=1 ms and child write <=50 ms; one-child startup <=10 s;
retained storage <=75% of the optimized managed reference; ordinary/startup node
peak <=1.5 times steady memory. The separate burst is 10,000 changes/s for 30 seconds
with recovery within 60 seconds at ordinary load. These remain unproven product gates.

## Component boundaries

| Owner | Responsibility and dependencies |
| --- | --- |
| Schema/catalog | Compile JSON/builder input into immutable field/layout/index metadata; no transport or persistence dependency |
| Arena | Native segments, owned blocks, string storage, root entries and lifetime protection; depends on compiled schema |
| Index owner | The primary hash table and the copy-on-write B+trees of the secondary indexes, both the engine's own, comparison, capacity and node reservations; depends on the arena and its text entries |
| Mutation coordinator | Admission, per-root ordering, preparation, ordered commit/publication and results; owns reservations, never application callbacks inside publication |
| Durable storage | The engine's part of durability: the encoded format, the journal and its receipts, the coalescing of the journal into the base, the comparison of saved units, the order of recovery, the policy of cleanup and retirement. It reaches a store only through the public provider contract |
| Storage provider | The storage operations of the public provider contract, each with the guarantee the contract states, among them the leadership of a store, journal transactions with their receipts and positions, saved units with the base cursor, the streamed base, cleanup. It stores versioned encoded effects and units, never native pointers, and passes the conformance tests of the contract. PostgreSQL is the first provider and the production one, SQL Server the second ([Q09.6](../decisions/persistence-backends.md#a-public-provider-contract-and-a-second-provider), the owner, 27 November 2026) |
| Replication session | Binary framing, initial capture and ordered application; consumes the same prepared-effect application path; no SQL dependency on children |
| Facade/bindings | Typed copies, generated/protected views, dynamic schema access, explicit indexes and session context; delegates all mutations to coordinator |
| Diagnostics | Read engine counters/state; host mounts DI/HTTP services; child may forward master status over the internal connection |

The public facade in this repository already contains API and managed in-memory behavior.
Treat that as an integration surface, not proof of the native/Durable/distributed
engine. Preserve its established naming and session concepts; reconcile missing
capabilities explicitly rather than implementing two competing public APIs.

## Native layout and allocation

Use ordinary native allocations, initially 4 MiB segments, aligned to 16 bytes.
Do not mmap a mutable SQL image. Segment IDs are UInt32 table slots; offsets are
UInt32 byte offsets, packed into a UInt64 relative address. Address zero is null.
Native addresses never appear in durable records or public API results.

Active segments belong to this database only. Initial size classes are 16-byte steps
through 1 KiB, 256-byte steps through 4 KiB, then powers of two through 64 KiB,
including header/alignment. This bounds small-value slack. Each region serves
one class, with a free-slot bitmap/list and live/prepared/free counters. A per-class
list of regions with free capacity finds space without scanning the arena. Return
an empty region to the segment allocator; fully empty segments enter the bounded
reserve or are freed. Do not promise physical RAM-page contiguity or compact the
operating system's memory.

Owned blocks have a 32-byte header: root handle (8), parent relative address (8),
descriptor offset in that parent (4), logical payload bytes (4), block kind (2),
flags (2), layout version (4). A root uses parent address zero. Shared-text blocks
identify a text entry instead of a root via the kind flag. Slot class/capacity and
state live in allocator metadata. Invalid addresses, counts and lengths are rejected
on import/recovery before any dereference.

A compiled root layout consists of its header, nullable bitmap, fixed-width fields
and 16-byte variable descriptors in stable field-ID order. A descriptor contains
kind/flags (4), length/count (4), and relative address (8); for a Dictionary field the
address of the shared entry.
Fixed-layout size must fit one 64-KiB block; large values live outside it. Schema
compilation reports an oversized fixed layout before data admission. A secondary index
has a limit of the same kind, a rule of the schema language and the same on every node:
a node of 64 KiB holds four of its widest entries, each String part counted as a copy of
64 bytes, and an index whose four widest entries cannot fit one fails the open with
`InvalidSchema` and the index named (`LatticeIndex` states the widths;
[ordered index](ordered-index.md#nodes-in-the-arena)). Numeric widths come from the
schema; do not widen all small integer fields to Int64.

An owned value takes no block and no descriptor of its own: its fields are flattened into
the fixed part of the entity or of the record that holds it, behind a presence bit when it is
nullable. Small strings, binaries and packed Lists <=64 bytes lie in the packed root tail when
the root still fits its block: the descriptor's Inline kind stores the root-relative offset and
the logical length. An index entry never points into a root: it holds a copy of a key
of 64 bytes or less and references the text entry of a longer one ("Index preparation
and traversal"), so a root that moves repairs no index. A partial update packs its inline
bytes in a fresh holder and computes new offsets. The end of the new holder bounds the
available capacity. Use the ordinary out-of-line placement when the value cannot fit.
The previous holder keeps its payload until readers leave. Dictionary text remains
independently owned. See [ownership across partial updates](arena-protocol.md#ownership-across-partial-updates).

Other variable values <=4 KiB payload initially use one suitable block. Larger values
use blocks with at most 65,504 payload bytes and a bounded directory of chunk
descriptors. A chunk boundary is not a semantic string/collection boundary: a character, or
the record of an element of a List whose elements hold no descriptor, may continue in the next
chunk, and decoders carry it across; the record of an element that holds descriptors lies
whole in one block, since a descriptor is never split. A partial update reuses unchanged
out-of-line branches. Repacking an edited collection may prepare new record blocks or
rebuild its local lookup. Full Save prepares a complete replacement without automatic diff.

Use segmented managed arrays of 4,096 root entries. Initial logical entry fields:
root address (8), last mutation position (8), key-descriptor address (8), stored-root
ID (8), generation (4), type ID (4), state (4), reserved (4): 48 bytes before CLR alignment checks.
Assert actual layout in implementation tests; do not silently rely on an estimated
struct size. Root handle = generation UInt32 plus slot UInt32. Increment generation
on reuse; permanently retire a slot on generation exhaustion. Entry-array growth
adds a page and does not copy every entry. Generation is local lifetime protection,
not a second public object key or a durable revision.

## Collections and strings

A List has one of two forms, fixed by the schema. Elements that hold no descriptor are
packed by their stride and stored as the bytes of a Binary value: the descriptor's length is
those bytes, and the List lies inline, in one block or in chunks under a directory. Elements
that hold descriptors are records, each a structure with room inline for its values: the
descriptor's length is the count of the elements, and the records lie in one block when they
fit one chunk, else in chunks under a directory, every chunk but the last holding the same
number of whole records. Indexing uses chunk arithmetic/directory lookup; insert/remove may
prepare and shift the affected suffix. Never reorder elements to improve storage. Temporary
work remains subject to the memory budget of the instance.

Map and Set entries use the same packed or record storage as List elements. A Set entry
holds one member. A Map entry holds a key and a value in one record. Whole writes and
wire receipt select the form by count. Up to eight entries, lookup scans the compact
entries using the schema comparison. A whole replacement with four entries therefore
returns to compact storage. From nine entries, an open-addressed lookup uses linear
probing, cached hashes and entry ordinals at up to 70 percent occupancy. Slot capacity
fills a size class. Large lookups use bounded slot chunks under a directory.

The lookup is derived from the entries before the root is published, using the
instance's hash seed. Uniqueness is checked before publication. A prepared lookup and
its entries have the same ownership and retirement rules as the rest of the root.
[Arena layout and transfer](arena-protocol.md#stored-structures-and-root-transfer)

Plain strings preserve their original encoded bytes. Dictionary fields use one
database-wide content table with exact-text equality, preserving case/spelling even
when a field's search policy ignores case. No automatic Plain/Dictionary switching.
An index entry holds a copy of a text key of 64 bytes or less, whatever the storage
mode of the field; a longer key is shared, the index entry referencing the text entry
the root already references, and an indexed value is never moved out of the root
because it is indexed. A field that is part of an index therefore stores a value longer
than 64 bytes as a text entry, whatever its storage mode — for a Plain field a rule of
the layout compiled from the schema — and such an entry is found by its bytes, so equal
long values of an indexed Plain field are stored once
([index structures](../decisions/index-structures.md), the note of 19 November 2026). Each Text
descriptor contributes one reference from preparation through retirement. The
local writes lock protects the count. A retired owner releases a reference after
protected readers can no longer follow it. The table returns the stable ID after
freeing the current text blocks. Earlier relocation copies are retired separately
under their own epochs. The text table keeps paged writer slots, buckets and a
sparse address locator. Pages can be returned as their retained entries disappear.

Published text bytes stay immutable. Relocation publishes the current address of a
shared text through its locator. Root-owned movement repairs the owning descriptor
and child backlinks under the local lock of the writes. Readers acquire one whole
address under their existing epoch. Relocation preserves logical revisions and
canonical transfer bytes and writes no durable relocation record.

## Reader protection, publication and compaction

Use the proven [arena protocol](arena-protocol.md), with no publication barrier of the
database: a publication of several entities publishes its position only after every one of
them is stored, and a read does not take a stamp above the position
([the write batch extension](arena-protocol.md#bounded-writebatch-extension)). Reads are
protected by epochs and retired memory is freed later, as
the [arena protocol](arena-protocol.md#deferred-reclamation-16-november-2026) says (the
owner's decision of 16 November 2026): a read by key writes to no memory another reader
writes to on its ordinary path, a write being published does not wait for readers, and a lookup
runs while the writer changes the index. The local writes lock protects publication
and changes to the allocator and text table, including maintenance. The reader's epoch
protects the storage it can reach through a root.

An owned read enters the epoch, looks up, copies and leaves. A deleted entry becomes
reusable only after the reads that could see it have left, so a read that took the
entry cannot follow it to a recreated key.

Ordinary reads protect a root while the engine evaluates or copies it. Both the
set by name and the class set lend a read-only view of one stamped root to a
synchronous callback. The class set uses a generated projection whose field handles
are bound to the actual schema. The callback takes a scoped ref struct and returns
an ordinary result. The compiler keeps borrowed values and read-only spans inside
the callback. A view holds the epoch until the callback returns or throws, including
when another write publishes a new state.

The caller keeps the callback short and avoids awaiting, yielding, I/O, external
waits and nested database calls. Copy reference keys and leave the view before
resolving another root. The engine does not guard against nested calls or preempt
user code. An open viewer can delay reclamation and the four waits that depend on it.
A fault refuses new views and keeps retired memory until close. See the
[protected view protocol](arena-protocol.md#protected-views) for position sampling,
memory lifetime and exception propagation. Unsafe caller code is outside the managed
borrowing contract.

Publication is one at a time under the lock of the writes and excludes no read. An
explicit WriteBatch is published so that no read takes one of its entities in its new state
before its position, and a read that begins at that position sees all of it: every entity of
the batch is stored with the stamp of the batch before its position is published, and a read
does not take a stamp above the position, so a read that meets an entity of the batch first
waits for the position ([write path](write-path.md#how-a-publication-is-made)). A read by key
reads one entity and is exact. A search or an enumeration that runs across the publication
is no snapshot: a step goes on after a take that waited, and it may return some entities of
the batch as they were before and others as after, as separate reads may. A child never
exposes a prefix of a batch nor advances its applied position past a part of one.
All read, index, scan and compaction entry points participate. Publication
only installs reserved values/nodes/descriptors and advances the applied position;
it does no I/O, validation, pool growth or user callbacks. Unexpected failure marks
the instance Faulted before reopening access. No partially published state is served.

While a master or child is Ready, a periodic worker offers a turn every 16 ms.
It enters the operation barrier, tries the local writes lock and rechecks admission
before changing storage. Reclamation and text maintenance share 64 work units.
A search of at most 128 units selects storage from a nonempty region strictly below
half live occupancy with an assigned compatible vacancy in an earlier segment.
The same search can select a live 32 KiB or 64 KiB block from a segment whose
published slots charge no more than one sixteenth of its capacity. A segment with
an allocated secondary index node is ineligible for this second rule. The destination
is an assigned compatible slot or an unassigned region in another active segment
with higher published slot charge, or a lower segment number at equal charge.
An individual block can progress when space is available for it. The empty reserve
remains available for normal allocations.

A prepared master write prevents relocation. The worker can publish one move per
turn and retires the old slot under its epoch. Secondary index nodes stay in place.
Lookups continue through their root handles and shared text IDs.

The total charge of retained relocation copies and the proposed destination fits
one 64 KiB slot. The target for local work is approximately 1 ms per turn, and the
measured full elapsed time includes runtime and scheduling pauses. Work proceeds
within one arena. [Reclamation and relocation](arena-protocol.md#reclamation-and-relocation)
describes the publication and lifetime rules.

Begin collection shrink after capacity remains >4 times live need for 5 seconds;
target <=2 times need. Reuse eligible holes first. Counters distinguish live bytes,
class slack, prepared capacity, retired/claimed capacity, free regions and empty
reserve. Allocation failure refuses the write before commit; compaction is not an
unbounded rescue wait. Native free and the OS working-set decrease are different events.

## Index preparation and traversal

The primary lookup is a hash table of the engine's own after the hash index of
Garnet/Tsavorite, read without locks while a write being published changes it (the owner's
decision of 16 November 2026, [index structures](../decisions/index-structures.md)).
Standard Dictionary is not the primary index. A declared secondary index is a B+tree of
the engine's own whose published nodes never change (the owner's decision of 19 November
2026, the same record), after LMDB's copy-on-write tree, the one-store publication of
ROWEX and LeanStore's walk by a new descent at the fence; the
[ordered index](ordered-index.md) tells how it works. No package takes part in either.
Physical indexes are rebuilt locally; no disk or replica images are persisted.
[Unique enforcement](../decisions/unique-indexes.md) is explicit in schema and checked
against final state in the ordered commit stage; any-null keys are exempt. Project
earlier operations in an internal commit group without copying the full index.
WriteBatch swaps/delete-and-reclaim are valid when every final unique key has one owner.

Reserve primary insertion capacity before commit. When the table is full the writer
builds the larger table from the current one while readers go on with it, publishes the
new table by one store and retires the old one by the epoch of the reads. Charge both
tables and their references to the memory budget. Initial load sizes from captured
counts; later growth doubles capacity when needed. If the peak cannot be reserved,
fail before commit. Nothing grows inside publication.

A secondary index is kept by copy on write. Its entry is (key, identity of the root
entry), unique, so the entries of one key form a run ordered by identity. A write copies
each node it changes into a node reserved for it and publishes the copy by one store of
its address, with release semantics, into the live parent or into the root of the tree;
a reader loads every address with acquire semantics, takes no lock, checks no version and
never retries, and the replaced node is retired and freed after the reads that could
reach it have left. Nothing of a published node is written again but that one address.
An entry is a hint: a search reads the root of each entry of its run and returns the
entity only if its current state has the searched key, so a reader never finds an entity
under a key it no longer has, and an entity that had the key for the whole search is
found. The entries of a write are installed in the hold of the lock that publishes its
root, each by its own stores, in this order: insert the new entries, publish the root,
remove the old entries; the check against the root covers the moments between. A key
that compares equal under other bytes (another case, another scale of a Decimal)
rewrites its entry at its place in the order, by a copy of its leaf as every change of a
node is. Fixed values are copied into keys as their root stores
them; the stored form of a fixed value is written, and read, in one place, which the root
writer, the search key and the order of an index use. A text key of 64 bytes or less is
copied into the entry, whatever the storage mode of its field; a longer one references
the text entry its root references, and a node holds one reference for each such key
among its entries and separators from the moment it is written until it is freed (see
"Collections and strings"). Compound keys use schema order and explicit field
comparers; keys are not raw byte-memcmp tuples; a null orders after every value of its
part.

Index nodes and their text are prepared before the commit: in the hold that prepares a
write, the index reserves prepared nodes and places in the retired list for the need of
its publication — with p inserts and q removes prepared and unpublished, the most nodes
their publications can take from the tree as it is, each insert counted at the height
the inserts before it can reach and each remove at the height after all of them
([ordered index](ordered-index.md#the-reservation)) — and releases them if
the write is withdrawn. With the persistence step busy a prepared, unpublished write holds
them and no reader can reach them. A reservation the arena cannot hold refuses the write
for memory before its commit. Once a write has touched an index, its pool keeps a
standing reserve, the need of one change of key, counted in the prepared bytes and
against the memory limit and freed at the close; it is never the reason of a refusal:
before a write is refused for memory every index gives back what it holds above the need
of the writes prepared, and the write is tried once more without it. A retired node is a retired item of its own, with its bytes
toward the next look; freeing it releases the references to text entries it holds.

Comparison/hash has one schema-owned service across indexes, Map/Set and direct
lookups; the order of a secondary index is the order of the same service. Use accepted
.NET Equals/CompareTo semantics for floating-point values, with compatible NaN/zero
hashes. String comparison is Ordinal/OrdinalIgnoreCase, independent of UTF-8/UTF-16
storage. Comparison without case uses the versioned case table of the core
([strings](../decisions/strings.md), Q46), the same on every node; no .NET culture or
runtime table takes part. No comparison allocates a managed string.

Numeric lookup/bound parameters bind once to the declared field type using the
accepted allowlist: same-type values; in-range integer-to-integer; exact integer-to-
Decimal; and exactly representable integer-to-Float32/Float64. Extend it with exact
Float32-to-Float64 widening and Float64-to-Float32 conversion only when the finite
value round-trips exactly; NaN/infinity classes and zero sign are preserved. Continue
to reject implicit Decimal/floating conversion and floating/Decimal-to-integer
conversion, even for 42.0. Reject overflow and invalid enum/Flags bits. Unsupported
combinations require an explicitly typed caller value. This closes the shared
Q07.3.6 binding boundary without OQL or silent rounding. Null ordering and explicit
primary-key tie-breaking stay unchanged.

Enumeration and index traversal are a step at a time and hold nothing between two steps: no
node, no cursor, no slot of the protection. A step enters the protection of reads, walks a
bounded number of entries, takes each root by its stamp and rechecks it against the key of
the entry that led to it, copies what it returns and leaves; the entity is built outside.
Keep a budgeted set of the primary keys already returned or passed over, so updates, key
movement or delete/reinsert cannot duplicate a key during one pass; charge retained key
text too. Exhausting traversal memory/time gives an explicit error, not a partial success
disguised as a complete result. Offset skips matches, which are remembered and counted
once; Limit bounds returned matches. The live result set can change; no cross-call
snapshot is introduced.

An ordered traversal is the walk of the [ordered index](ordered-index.md#how-a-range-is-walked):
the entries between two search keys of the leading values and the bounds of the range, each
standing before or after every entry its parts equal; one descent to where a step starts, in
either direction, and the next leaf by a new descent at the fence. A step seeks strictly after
the last (key, identity) it returned, which it holds in detached form, a copy that owns its
text, so it returns no pair twice and every pair that is there throughout, also inside a run
of equal keys; the set of primary keys covers what the pairs cannot, a key moved ahead of the
walk and an entity created again. Internal continuation adds no public sort-key tie breaker or
keyset-paging contract.

## Write coordinator and revision semantics

Durable operation admission follows the [retry protocol](operation-retry-protocol.md).
Writes are executed one at a time under one lock of the writes, with a group commit at
the persistence boundary (the owner's decision of 17 November 2026 to put the write path on a
proven source; the study of S09, part 2 names the sources). A call takes the lock once
and prepares its write in place: it looks the key up, checks its conditions against the
published state, encodes its values into prepared blocks and reserves everything its
publication needs. No other write runs meanwhile, so nothing has to be reserved against
another writer. A WriteBatch is prepared the same way, all of its entities in one hold,
member by member in their order. A complete member is placed from the root it was
encoded to when it was added. An update checks its revision condition against the current
root, then executes its commands in order and prepares a new root if any command changes
it. Changes are reserved and checked together on the state they leave. Which member
fails is judged in two phases. A batch through a child reaches the master as one request
and is prepared the same way. [Write path](write-path.md#a-write-batch)

The prepared write joins the open group. When no group is in the persistence step, the group
is handed to it at once; when that step completes at once, as in MemoryOnly, the caller
publishes in the same hold and returns its result. While a group is in the persistence step,
later calls prepare under the lock and form the next group — at most 32 writes or 8 MiB,
a larger write alone, never waiting to fill — and whoever completes the persistence step
publishes its group in order and hands over the next. A write to an entity that has an
earlier write not yet completed — prepared and unpublished, or itself waiting — waits
behind the latest such write, first in first out, and holds nothing while it waits; so
does a batch one of whose entities has one, and a single write whose latest such write is
a batch waits for that batch. It is prepared when that write completes in
any way, published, refused or cancelled, and is checked against the state published
then. In MemoryOnly this happens only while a write refused for memory is being
resolved. A write whose token is cancelled before its dispatch is never dispatched.
Prepared blocks stay valid until
they are published or abandoned, because nothing else can reach them. Everything is
released on a failure before the commit.

The persistence step is called under the lock and only takes the group; its work is behind
the task it returns. It assigns positions transactionally and, in Durable, awaits
PostgreSQL. Independent operations in one commit retain their own tokens and positions;
they do not become an application transaction. Publish each operation in order, with a
whole WriteBatch at one position. A committed-publication failure stops the instance,
including background application, until close/reopen recovers every committed operation.
The host process need not exit.

Each logical root change receives the operation position as its new revision component.
The opaque revision binds history, type/key and that position; a revision from another
identity cannot match accidentally. The entities of one WriteBatch share its position, so
the revision of each also holds the number of its member, from 1 in the order of the
members for a batch of two or more, and 0 for a single write and for a batch of one, which
travels to a child with the root. For a caller it stays opaque: two revisions of one
entity can be compared for equality and nothing else — no order, no conversion to a
position, no meaning across entities. Recreated roots receive a new position. Confirmed
collection/absence no-ops retain root revisions but receive a durable result and stream
position. Full Save always replaces without equality detection; assigning a ready
scalar value is a mutation even if the value compares equal. No arithmetic commands,
automatic diff or automatic retry are added.

## Independent base, journal and recovery

The engine does the work this section describes: encoding, background application,
coalescing, the comparison of units, recovery and cleanup. It reaches the store through
the public provider contract
([Q09.6](../decisions/persistence-backends.md#a-public-provider-contract-and-a-second-provider),
the owner, 27 November 2026). The tables and transactions named here are the content
the PostgreSQL provider keeps for those operations. Another provider keeps the same
content in a form of its own.

The PostgreSQL provider has tables for database/catalog metadata, root directory, saved units,
journal transactions/fragments, base-applied position and operation receipts. A root
directory row maps `(type ID, canonical primary key)` to a provider-local Int64 stored
root ID, layout/schema version, root revision, encoded byte length and unit count.
Stored root IDs are not public keys or native handles. The master reserves IDs for
insertions from a transactional counter and records them in journal effects;
reinsertions allocate a new ID. Background application addresses the stored ID, not
SQL text collation. Primary-key uniqueness/equality is enforced by the engine's
schema comparer; preserve the stored spelling on updates through equivalent keys.

Saved units use `(stored root ID, unit ordinal)` and exactly 256 payload bytes; the
root length removes final padding. Each encoded root contains a versioned header,
null bitmap, schema-ordered fixed values and relative offsets/lengths for variable
values. Owned trees have no external native pointers. Persist actual string bytes;
rebuild the database dictionary and all indexes on load. On-disk text duplication is
allowed; it does not force duplicate in-memory text. References encode target type/key.

Durable logical values use explicit little-endian numeric encoding, .NET
Decimal's four UInt32 components, Guid's documented RFC byte order, UTC ticks for
DateTimeOffset, day number for DateOnly and ticks for TimeOnly/TimeSpan. The replication
stream carries stored bytes in the layout and byte order the Hello has checked (decided
after the studies of S10 and S11). The layout is one version for the node, carried in
every block header and root envelope and in the low 16 bits of the Hello's layout format,
and raised when the same schema would compile to bytes an earlier build reads otherwise:
2 since a field that is part of an index stores a value longer than 64 bytes as a text
entry, so builds of the two forms refuse each other at the handshake. Validate declared ranges and Unicode. The format header carries magic, format version, schema
version, payload length and checksum. No CLR object image, package node or native
segment address is a durable format. Fixed layout offsets derive deterministically
from the compiled schema; unit/chunk placement is internal.

Use SHA-256 for persisted-root and complete journal payload integrity. A frame of the
replication transport carries a CRC32C over its header and body (decided after the
study of S10, 17 November 2026). Length/version checks precede decoding. Specify
the digest over header identity/version/length plus payload, excluding the digest
field itself. This avoids machine-dependent checksums or checksum-algorithm negotiation
in format 1; its CPU cost belongs in readiness/write measurements.

A foreground journal transaction contains complete binary logical effects, token,
position and transaction boundaries. Large payloads are fragmented with length and
digest; header/fragments/receipt/cursor commit atomically. Schema-invalid or incomplete
transactions are never replayed partially. Positions come from provider control inside
the commit transaction, not a nontransactional SQL sequence.

The in-process base applier reads bounded complete journal transactions after its
cursor, up to the master's published boundary. Coalesce each touched root to its final
state, fetching prior saved roots in bounded SQL batches when needed. Apply commands
in order; do not reduce non-idempotent collection edits to an unsafe last assignment.
Encode changed roots, compare/store changed 256-byte units, remove obsolete units,
update directories and advance the base cursor in **one transaction**. This unit
comparison is background storage work, not an automatic Save diff at the API.

Keep a bounded working set; materialize/encode large touched roots one at a time
within the same SQL transaction, flushing bounded unit batches without advancing
the cursor early. Reduce the coalescing window when needed. The workspace must hold
the largest accepted root, transaction effects and driver buffers, not simultaneous
decoded copies of every root in a WriteBatch. Cleanup removes only transactions covered
by the committed base cursor. Receipts have independent retention. Failures retry with
bounded pauses; healthy foreground appends continue until the journal hard limit.

The base-applied cursor has its own provider row. The applier does not hold the
foreground admission/commit control row while writing units. Advance its own cursor
in the unit transaction; cleanup consults that committed cursor. This keeps a long
background unit batch from serializing ordinary receipt/journal commits behind it.

Ordinary master reopen is offline: stop applier/cleanup, read catalog + base cursor +
base + committed tail consistently, validate checksums/lengths/schema and rebuild the
arena, dictionary and indexes, then replay complete transactions and become Ready.
No rereading SQL on a resident read and no concurrent applier during this recovery.
Use one arena; on failure dispose it after readers drain and report recovery failure.

## Replication transport and initial loading

Use a long-lived full-duplex TCP connection to the configured master URL, with bounded
binary frames and asynchronous I/O. The host owns network exposure and optional TLS
termination/configuration; no message broker, service discovery or election dependency.
A node checks the structure of everything it receives and does not validate the values
of fields again: [trust between the nodes of one database](../decisions/replication-and-hosting.md#trust-between-the-nodes-of-one-database).
The handshake checks compatibility, not identity: it authenticates nobody. That the
replication port is reachable only from the nodes of this database is a deployment
precondition, accepted by the owner on 14 November 2026; the engine adds no shared
secret and no TLS of its own. A child's open has a finite, configurable timeout for
each wait: when the master cannot be reached, or nothing arrives from it, for that
time, the open fails with Unavailable and the instance closes; a load that keeps
arriving is not ended by it, and the cancellation of the open call bounds the whole open
(decided after the study of S10, 17 November 2026).
One connection multiplexes replication, write forwarding, status and acknowledgments;
control frames have priority between bounded data frames. Embedded master reads/writes
invoke the same engine directly without transport or a mandatory local child.

Initial frame header: magic UInt32, protocol version UInt16, kind UInt16, flags UInt32,
body length UInt32, correlation ID UInt64, stream sequence UInt64 (32 bytes); body has
an integrity checksum. Hello binds database identity/history, schema fingerprint,
wire/layout compatibility and role. Reject unsupported formats before allocation.
Data frames are <=64 KiB; a larger transaction is fragmented, its first frame announces
its length, and a child applies it only once all of it has arrived. A transaction begins
with its sequence (UInt64), the length of its effects (UInt64) and the number of its
effects (UInt32), from which a child reserves the estimate of the transaction before it
reads them. A transaction is checked by the check of its frames, its announced length, its
sequence, which follows the one before (decided after the study of S11), and the number
of its effects, which its length has to be able to carry, before anything is reserved for
it, and which has to be the number the child reads; a transaction whose number fails
either is malformed. ACK means **applied to data and
indexes**, not merely received by the socket: a child sends `Applied` with a sequence for
each burst it received once it has applied all of it, and never a sequence before R. A
child sends no success ACK for a partial WriteBatch.

A transaction counts as sent to a child when its last frame has been added to the
connection, before the socket has taken it. A child is therefore believed about a
transaction as soon as the master has queued its last frame; an Applied of a transaction
whose last frame is not yet queued ends the connection.

Keep one shared RAM delivery tail. A committed transaction is encoded and appended to it
in the hold of the lock of the writes that publishes it, in the order of sequences, and
only while a child or a capture is registered; it is encoded once for all children and
framed per connection, on that child's sender task, outside every lock. Every
child has its own cursor, and the master releases the tail up to the lowest position
among its children and registered captures. The tail is managed memory outside the
instance budget, unbounded until S33; a write of the master is never refused or delayed
because a child is slow (decided after the study of S11). The tail retains whole blocks
of 1 MiB: at most the bytes not yet applied, rounded up to blocks, plus the block being
filled, a block a sender still reads, and one spare; with no child and no capture it
retains nothing. If a disconnected/slow child holds
the tail beyond its byte/time budget, remove its retention claim and require reload
when it returns. It may still serve ordinary local stale reads, but a MinPosition wait
cannot succeed until it catches up. PostgreSQL is never a child catch-up source.

Initial transfer uses a fenced capture, registered in one hold of the lock of the
writes: it takes S, the published sequence, the fence of root slots and its cursor into
the tail. Copy each root coherently under its protection, one root at a time, including
owned blocks and required shared text. Copy bytes into bounded send buffers under read
protection and release that protection before I/O. Capture retains no slot, deleted
root or deletion mark (decided after the study of S11).
No address and no block number travels: the child stores received bytes through its
own allocation, which writes every location (decided after the study of S10). Each
Dictionary text reference carries the encoded text, which is resolved in the receiving
node's text table. Do not pin old payloads for the
entire snapshot or hold read locks over the network. R is read in a hold of the lock of
the writes after the last root was copied, because a root can carry a sequence the
published clock does not show yet; `LoadEnd` carries S, R and the number of roots sent.

Wire format 7 keeps the existing frame envelope and whole-root layout version 2.
Whole roots transfer Map and Set entries in the List encoding. The receiving node
chooses the resident form by count and builds its own lookup with its local seed.
Uniqueness is part of the transfer structure and is checked even if a newer local
root already covers the effect. [Root transfer and checks](arena-protocol.md#transfer-order-and-checks)

Each captured root carries the sequence of its last mutation. A loading child is sent
its stream after `LoadEnd`, on the same connection, from S. A Write effect carries the
root in the load format, a Delete the type id and the key, a write that changed nothing
only its position. The child applies a copy and an effect by one rule, under its lock of
the writes: a state of a key at sequence s is skipped when the child holds that key with
a last mutation not earlier than s; otherwise a Write inserts or replaces and a Delete
removes the key if it is held. The child addresses an entity by the key in the root,
never by a slot of the master, so a key that arrives twice in a load is resolved by the
rule. A deleted entity cannot come back, because its delete is in the tail and the tail
is applied after every copy; a recreated entity has a later sequence than anything of its
earlier lifetime; roots inserted outside the fence arrive through the tail. A transaction
is applied whole: one of its effects can be skipped while another is applied, and a
transaction is never skipped whole by the sequence of one effect. Before placing
a full root, the child makes room to record it for rollback. A failed list growth
therefore leaves no unrecorded root or handle. Publication and rollback discard
oversized preparation containers without allocating. A later preparation
recreates them under the writes lock before taking ownership of roots or
reservations. The allocation belongs to that later attempt.

The clock of a loading child starts at S. Reads are enabled once the complete
state and local indexes cover R, and the child is Ready when it has applied R.
Live changes after R continue through
the same ordered application path. A Write of the stream whose root does not carry the
sequence of its transaction is refused with its whole transaction. The bootstrap
experiment tested the earlier state-selection model with deletion watermarks, which the
owner withdrew on 17 November 2026; binary/shared-block integration remains a release
test.

A sparse committed effect names the type, key, original batch member, sequence of the
root it extends and the ordered actions that changed the entity. Forwarded requests and
sparse effects use the common checked lookup-key encoding. The lookup spelling can
have a different byte length from an equal stored key. The stored-value limits
apply to root values and command operands. Operands carry resolved List positions
or Map and Set presence requirements where an edit needs them.
A new hashed lookup is built for the resulting count and an unchanged lookup can be
shared whole. Each child applies these effects in its own physical form, with the
same compact-to-hashed and hashed-to-compact thresholds.

During catch-up only, a root whose last mutation covers the effect's sequence can
skip it, after the entire body has been validated. An older root must be the exact base.
An absent root leaves a temporary type-and-key marker until a committed Delete, even
if a whole Write appears before that Delete. The marker changes are applied with the
whole transaction. Retained keys, table capacity and staging are bounded by the child's
memory limit as a separate loading buffer. A child with unresolved markers at R or
without space to finish the load fails before Ready. Both successful and failed loading
release the temporary buffer.

During live application a wrong base or an impossible resolved action is divergence.
The child checks the rest of the transaction first. Invalid wire structure follows the
protocol failure path. Verified divergence faults the child at its prior position
without publishing any member of the transaction.

Controlled resource refusal during load ends open with `Unavailable`. After readiness
it pauses the follower on the same connection. A frame that needs new assembly backing
is validated and reserved before its bytes are consumed. A complete transaction that
cannot be applied gives up its partial preparation and keeps its assembled body valid.
This can hold the receive buffer for a single frame or charged pieces for several
frames. The follower keeps serving local reads at its prior applied position. It
acknowledges a transaction only after publication and resumes in order when useful
capacity returns. A previously applied position can still be acknowledged. Close or
an observed connection end releases that connection's cursor. The current engine can
remain paused without reading past the held frame. The delivery retention limit and
reconnect or reload are separate stages.

**Decided by the owner on 14 November 2026:** children that start together are loaded
in parallel, not one after another. A deployment of 5–10 children has to come up on
default settings. Each capture has its own S, its fence of slots and its R. All of
them read the shared live stream, which is retained from the earliest S still in
use. The owner withdrew deletion watermarks on 17 November 2026. The number of
captures that run at once is an option, 10 by default. A child beyond the limit, or one
whose capture cannot reserve its buffers within the memory budget, waits
before its capture is registered; that wait is part of its open timeout. The earlier
starting value of one capture at a time is withdrawn. The bootstrap experiment covered
one capture; several at once, and their cost to the master's readers and writers, are a
release test.

Before a full reload, close readiness, finish protected reads, discard the old arena
and load one new arena. Requests may wait at the bounded readiness gate, initially
30 seconds, with request/count limits. No unbounded retained closures or second arena.
Child restart always starts empty. Missing same-history RAM tail or a new history
requires full reload; ordinary master restart preserves history but may lose the tail.

A child forwards a write as a request on its one connection. For a complete write it
sends the encoded root. For an update it sends the key, condition and original ordered
commands with schema-encoded operands. The master checks the request and executes it.
A write batch is one request, checked member by member and executed as one write.
The batch input and workspace are charged to their owner on each node. A slow receive
or apply step can hold delivery blocks on the master even when it keeps committing.
The encoder writes directly into the delivery tail. Physical blocks held for a cursor,
reader, capture, an in-progress transaction or reusable spare are counted once, outside
instance admission. Committing a transaction updates logical `PendingDeliveryBytes`.
A spare or current block can remain held after logical pending bytes reach zero.

An earlier forwarding measurement recorded these capacities before instance admission.
For a forwarded batch of complete writes of B bytes, the child holds about B bytes
of managed memory from commit to reply and 2B while writing the encoded members.
An update holds object snapshots of its operands, whose size depends on their types
and values. For complete writes the same batch is held on
the master in that run as B for the request from its first frame to the
outcome of the batch, B in the tail of the children and about 450 bytes a member of
records — the member, its entry in the set of entities, the text of a String key, its
change, its entity type and some five places of the retired list — beside B in the arena;
measured with 100,000 users of the fixture, 96.9 MB were held at B = 17.2 MB, about 5.6B,
and afterwards each child keeps B in the buffer of its stream (V1 of
[the refactoring backlog](../work/refactoring.md)). A large batch holds
back the later requests of its child until its last frame is sent, the child's
acknowledgments passing between its frames, and the master reads no frame of that child
while it checks the batch and prepares and publishes it. The reply of a forwarded write, a
refusal as a success, is recorded before the write leaves
the master's write path and reaches the child before the transaction of that write. A
master that closes ends each connection in order: its recorded replies, what it holds
for that child, one frame Closed, the end of its sending side; then it waits for the
child to end its side, within a bound. Closed says that every request of the connection
not answered before it was not executed and will not be: a child that receives it ends
such a request in Unavailable. A connection that ends without Closed leaves a request
that was sent whole and not answered with an indeterminate outcome; an end in order of
the connection alone says nothing about execution (decided at the review of S12 of
Epic 1, 18 November 2026).

A successful reply for a request with updates includes a low-bit-first bitmap in
original command order, including no-ops. The receiver checks its count, size and
padding against the correlated request. Replies without updates keep their short form.
An open child completes a forwarded write and exposes its command outcomes after the
child applies the position. If the child closes after receiving success, the committed
result and its command outcomes remain available. A timeout, fault or indeterminate
outcome does not expose the bits.

Related requests carry an opaque minimum history/position. Waits have a fixed target
and finite timeout. Explicit WaitForPosition is available; subsequent requests
retain the dependency and recheck lifecycle/history. No all-child barrier or snapshot.

## Schema identity, migration and facade integration

The JSON/compiler catalog owns stable positive UInt32 type, field and index identities;
renames preserve IDs, deletion does not recycle IDs. In the descriptor the top array is
`EntityTypes`, an index carries its own `Id`, and an index has no kind: one secondary
structure serves equality and ranges. A part of an index key is a field of the entity type
or, by the owner's decision of 14 November 2026, a path from such a field through owned
values to a field inside them; a path never goes through a collection, and an absent
owned value on it makes that component null. Persist a monotonically increasing
catalog version plus deterministic layout fingerprints. The builder and JSON reader
use the same compiler, including persisted Unique index flags in schema fingerprints.
Bind .NET classes/generated views once per schema version;
dynamic access uses field handles from that same metadata. Exact namespaces and method
spelling follow the facade project; they do not change these engine guarantees.

Use the accepted type/collection/reference restrictions from
[schema and types](../decisions/schema-and-types.md), not the scalar-only subset of an
early facade. Preserve the facade's opaque position and session abstraction. A
position is a nonzero 64-bit history identity and a 64-bit sequence. Sequence zero is
the initial prefix of a new history, with nothing applied yet; it is a valid position
in a session and on the wire. Every committed transaction is given a positive sequence,
starting at one. SQL positions stay in the positive Int64 range and refuse overflow. Store stable
database identity separately and bind it in connection/open context. History is
generated on create/explicit restore and preserved on ordinary reopen. Revision,
position, schema version and operation token remain different types.

Migration is explicit and eager. Pause all writes, also those to unaffected types,
drain the writer/applier to a fixed base position and make affected types unavailable;
on the master, reads of unaffected types may continue. The owner confirmed this scope on
14 November 2026: it fills what Q03 left open, and a migration is planned as a stop of
writes for the whole database. Migration exists for a Durable master only, by the same
decision: a MemoryOnly master has no saved roots to convert and loses its data whenever
it closes, so its schema is changed by opening it with the new one, and a migration call
on it is refused.
Persist a migration manifest with source/target catalog, affected types and progress.
Convert affected saved roots in bounded batches into a shadow **disk** generation;
do not maintain a second native arena. Source data is untouched until activation.
Atomically activate the target catalog and saved-root generation after validation.
Before activation a crash can resume or discard the shadow; after activation reopen
loads the new generation. No row-by-row mixed-layout reads or lazy conversion.

Reload/rebuild affected local data/indexes before restoring readiness. Children see
a schema-change control boundary and perform a full one-arena reload; during it every
read on that child waits within the reload readiness bound. Old schema-bound
inputs fail validation; already admitted unexecuted operations are rejected with a
stable schema-change result before activation. Same-history committed receipts remain
resolvable. Migration converter execution is application C# code outside memory gates,
never a user callback inside publication or an automatically generated SQL workflow.

Facade integration must expose/retain operation tokens before dispatch, authoritative
status and expiry, expected revisions, complete WriteBatch boundaries, explicit partial
commands, direct index handles, dynamic/protected reads, resource errors and diagnostic
context. Complete the existing facade toward these contracts; its current signatures
and managed data stores are not a substitute for implementation. Do not publish an
unversioned durable/wire format while those integration tests are absent.

## Initial bounded settings

These are implementation starting settings, **not measured optimal values or new SLOs**.
Expose public workload limits through options and validate relationships at open;
allocator details remain internal tuning. By the owner's decisions of 15 November 2026
the instance budget is an option whose default is computed when the instance opens: half
of the memory the .NET runtime reports as available to the process. That is the physical
memory on a machine without limits; in a container it is by default three quarters of the
container's limit; a heap hard limit configured for the garbage collector lowers it. An
application may set the budget explicitly. It has to when one process opens several
instances, because each instance computes its default from the whole, and it should
whenever it limits the heap of the collector, because that setting says nothing about the
native memory the engine needs. The budget is enforced in either case. A
timeout or a limit below that an
application can observe is an option with this starting value as its default, never a
constant in the code; "configurable" in a row is not the complete list of such options.
By the owner's decision of 15 November 2026 a limit exists only where the engine cannot
work without it or a resource cannot hold the work: no default turns ordinary load into a
refusal, and where it can the database waits or slows down instead of refusing.

| Limit / setting | Starting value |
| --- | --- |
| Root encoded size, including owned data | The instance budget bounds capacity. One String or Binary value is bounded by the storage format. A new lifetime also needs an addressable root slot. |
| Explicit WriteBatch | Local snapshots and execution use the instance budget. A new lifetime needs an addressable root slot and a durable append needs capacity in the journal. The application chooses the number of operations and their size. |
| Internal group | Up to 32 operations / 8 MiB; 1 ms collection delay for a durable commit only, not built before S27; never split a WriteBatch |
| Queued write input | No limit of its own, by the owner's decision of 15 November 2026: neither on the number of requests nor on their bytes. A write waits for its turn, what waits is counted in the instance budget, and a write is refused only when the budget cannot hold it |
| Storage recovery wait / SQL failure deadline | 30 s / 5 s per attempt, explicit Unknown around uncertain commit |
| Session synchronization / reload readiness wait | 10 s / 30 s, configurable; cancellation allowed for read waits |
| Scan/query temporary memory and execution | No limit by default, by the owner's decision of 15 November 2026. An application may set a limit on the memory an enumeration remembers and on the engine time of a read, for the database (`ReadMemoryLimit`, `ReadTimeLimit`); only time inside the engine counts, not the time a caller spends between two items. That memory is not counted in the instance budget |
| Child open | 30 s to reach the master and between two parts of the load, configurable; then the open fails with Unavailable |
| Master leadership | Taken at open; a second master on the same store is refused at once. The leadership number is checked in every transaction that changes the store; the leadership session every 5 s, configurable |
| Retained operation outcomes of a MemoryOnly master | No limit of its own, by the owner's decision of 15 November 2026: counted in the instance budget; an unexpired outcome is never dropped, and a write is refused only when the budget cannot hold it |
| Empty native reserve / compaction headroom | One 4-MiB segment / at most 64 KiB for retained relocation copies and the next destination, plus metadata |
| Base application | Up to 256 transactions / 8 MiB effects per batch, 32 MiB working set; <=100 ms idle cadence, immediate continuation when behind |
| Journal trigger / hard limit | 64 MiB or 5 s oldest unapplied; no hard limit by default, by the owner's decision of 15 November 2026. An application may set one, and appends beyond it are then refused. Without one the time of a recovery grows with the unapplied tail |
| Durable receipt budget | No limit by default, by the owner's decision of 15 November 2026; an application may set one, with conservative admission reservation and periodic actual-size reconciliation as before |
| Retry window / generation admission duration | 15 min configurable / 60 s internal |
| Shared pending replication / per-peer I/O buffer | min(32 MiB, 20% instance budget) / 256 KiB. The bound of pending replication is built with S33; until then it is counted and reported, and a slow child makes it grow |
| Slow-peer retention deadline / concurrent captures | 30 s / up to 10 captures at once, configurable; a child beyond the limit waits before its capture is registered. Built with S33; until then captures are not limited |
| Reconnect / applier failure pauses | 100 ms exponential backoff capped at 5 s, bounded by the relevant request/retention limits |

One root and one transaction may
be larger than any bounded buffer of this table — an internal
group, the pending replication, a batch of base application: each of them bounds what
accumulates and takes a transaction that exceeds it alone, and none refuses a transaction
for its own size. The MemoryOnly engine charges memory by structure, and reserves before
it acquires and before a transaction can publish. A native segment is charged at its
grant with its managed metadata, the empty reserve, unused slots and alignment tails
included, and released when it is returned; native slots within a charged segment add no
second charge; the nodes of the secondary indexes are native blocks. A managed structure
that grows with the data — root pages, the tables and overflow buckets of the primary
indexes, the arrays of the text table, collection tails, the retirement queue,
publication scratch, the buffers of connections and of a load in transfer, a child's key
set — is charged by the bytes of its backing array when it grows: the new array is reserved before it is allocated, and the old one is released
when it is dropped or, for a generation a reader may still hold — the replaced table of a
primary index, a replaced directory —, once the reads that could hold it have left, so a
replacement is charged while its predecessor is held. A replaced leaf or a dropped branch
of the text table is released at once; a read holds at most one of each, and the headroom
holds one of each for every read slot. Small fixed bookkeeping — the owner objects of the
structures, weak references, fixed directories, the headers of each entity type and each
index — is not charged object by object: a headroom computed from the schema, the options
and the read slots of the host when the instance opens covers it and is reported as its
own figure ([storage and maintenance](storage-and-maintenance.md#the-memory-budget) explains
each part). Every write unit — an ordinary write, a batch, a child transaction, a write a
child forwards and the request it arrives as on the master — reserves one estimate by one
rule: a base for the unit, a constant for each member, and the lengths of what the unit
holds of its input. The input of a batch is charged when it is added: its encoded length
and the member constant, and with the first member also the base and a constant for the
builder; the reservation comes after every check of the addition and after the snapshot
it builds. An ordinary write, a child transaction and a forwarded request reserve their
estimate when they start — the base, the member constant for each member and the encoded
commands of a partial update — and release it when they end; a write a child forwards, and a
batch committed on a child, also reserve the encoded length of the request once it is encoded and
before it is sent. Their
values are charged where they land, in the root image of the arena, which is one workspace
charged at its size, in the native blocks and in the connection buffers. Reservation and release are one atomic
counter. The budget includes native read scratch when the engine owns it. Managed
enumeration workspace uses the separate read budget. Application memory and garbage
awaiting collection are outside instance admission, and the budget is not an exact cap
of the memory of the process. The retained delivery tail is accounted for separately until
its own retention policy is implemented. [Storage and maintenance](storage-and-maintenance.md#the-memory-budget)
describes the budget with its constants and the wait of a child.

A transaction that exhausts the byte budget is refused with `WriteRejected` and `Memory`.
When all addressable root slots are in use or permanently exhausted, a request for a
new lifetime is refused with `RootSlots`. A free root slot can be reused without reserving
a new page. If a root slot can be addressed and its new backing cannot fit, the reason is
`Memory`. A resource refusal of a complete batch has no `OperationIndex`. Local batch
addition can report `Memory` synchronously before queueing or transmission. It keeps the
previous members and leaves the failed addition available to retry.

A refusal leaves installed data, revisions and position unchanged. It returns the
reservations it did not use and every slot, handle and block it prepared. Capacity that a
structure grew during the attempt stays with the structure, charged and reusable by the
next write; a native segment the unwind leaves empty goes to the reserve or is returned,
as any empty segment. Storage still protected by another owner remains charged. A dropped
managed reference ends its ownership charge; collection happens on the runtime's schedule.
Report both effects separately in peak evidence. The 1.5x peak target uses the steady
reference baseline before growth.

## Diagnostics, backup and deployment boundary

Diagnostics report role/lifecycle, history/schema, published/applied/base cursors,
known master progress with observation age, pending delivery, retained journal/receipts,
queue reservations, native/managed storage breakdown, compaction and failure reasons.
Unknown master progress is explicitly unknown, not zero lag. Master-status forwarding
has a bounded timeout and cannot stall ordinary local reads. Host registration/path,
authentication and network policy belong to the embedding application.

Infrastructure performs PostgreSQL backup and restore. Restore procedure: stop the
old serving master, restore to a separate target, open that target for offline
validation, explicitly activate a new history, load/validate the single arena,
then expose readiness and let children fully reload. Preserve the original target
until verification. A failed validation never changes the serving connection to the
unverified target. No Lattice backup-launch or force-maintenance endpoint is required.
Deployment-specific backup retention, RPO/RTO and pod resource requests are operational
inputs, not unresolved engine algorithms or promises of automatic failover.

**One store has one master at a time, and the engine enforces it.** Decided by the
owner on 14 November 2026; it replaces the earlier precondition that left this to the
host.

A Durable master takes the leadership of its store when it opens: a session-level
PostgreSQL advisory lock, held on a connection of its own for as long as the instance is
open. While that session lives, the open of any other master on the same store fails at
once with an error that names the reason. It does not wait, and nothing is taken over
from the master that holds the store. PostgreSQL releases the lock when the session
ends, so a master that closed, or whose process died, frees the store without any action
of the engine.

A master that lost its leadership while its process lives must not change the store
either. Taking the leadership advances a leadership number kept in a row of its own.
Every transaction that changes the store — commit, base application, cleanup, receipt
retirement, migration — reads that row under a shared lock and commits only when the
number is still its own; the row is separate from the foreground control row, so the
background applier still does not wait for commits. The master also checks its
leadership session at a configurable interval. When the session has ended, writes pause
as during any unavailability of the store, and the master tries to take the leadership
again within the storage recovery wait. It goes on only when it holds the lock again and
the leadership number is still its own; otherwise it becomes Faulted and a new instance
has to be opened. No transaction of a master is committed after another master took the
store. The activation of a restored store takes the leadership as an open does.

This is exclusion, not election. The engine appoints no master, promotes no child and
moves no leadership; starting the master, and starting it again elsewhere, stays with
the host. Two things remain deployment matters: the leadership connection has to be a
direct session, because a proxy that pools per transaction cannot keep a session lock,
and how quickly PostgreSQL notices a dead session is a setting of the server.

## Reproducible first fixture

Use deterministic generator seed 42 and stable numeric IDs starting at 1. References
wrap into the declared target range. Text below is synthetic; no private data or
external project is needed. Index definitions are identical in both measured engines.

| Type / count | Payload and declared indexes |
| --- | --- |
| User / 100,000 | Int64 Id; Login `user-` + eight digits; Email `u` + eight digits + `@example.test`; DisplayName `User ` + eight digits; Dictionary Region from 16 values; Boolean IsActive; ordered List of three Group references. Indexes: Unique Login, non-unique Email, compound (Region, Login) |
| Group / 10,000 | Int64 Id; Name `group-` + five digits; ordered List of two Role references. Index: Name |
| Role / 1,000 | Int64 Id; Name `role-` + four digits; Set of eight Dictionary permission strings selected from 64 values. Index: Unique Name |
| Setting / 10,000 | String key `setting-` + five digits; Dictionary Section from 16 values; Name `key-` + five digits; 128-byte valid UTF-8 Value; Map of three String keys/values from 16 synthetic pairs. Index: (Section, Name) |

Default strings are Plain/UTF-8/Ordinal except explicitly Dictionary fields. Add
separate small correctness fixtures for UTF-16, IgnoreCase, malformed Unicode,
Binary, enum/Flags and every numeric/temporal width; they need not inflate the load
fixture. Include non-ASCII valid text in a deterministic 10% of settings while keeping
their encoded value length fixed. Payload generation belongs outside timed operations.

Read mix: 80% ID reads (including a fixed 5% missing keys), 15% exact declared-index
lookups and 5% leading-key/range lookups with Limit 20. Type mix is 80% User, 10%
Setting, 5% Group and 5% Role. Small reads return owned bounded values; separately
measure protected views and report their scope duration. Do not mix full enumeration
into the 1-ms small-read percentile. Reads following a dependency wait start latency
measurement after that dependency has already been applied, as accepted. The 1-ms
target is judged on the ID and exact-index reads; the range lookups are reported as
their own class against the same figure.

One write trace cycle has 40 single-root User partial assignments, eight single-root
Setting Saves, and one two-root User/Group WriteBatch: 50 logical root changes in
49 calls. Four cycles/s implement the ordinary 200 changes/s profile; 200 cycles/s
offer the 10,000 changes/s burst. Values alternate so writes are not predominantly
no-ops. Include create/delete/recreate, failure and fragmentation in separate bounded
correctness/churn traces. Report offered, completed and refused work separately. This
trace is the ordinary profile: 192 single-root calls and 4 two-root batches a second,
with no separate run of 200 single-root calls. The 50-ms target is judged on the
single-root calls; the batches are reported as their own class and inherit no
threshold. The owner confirmed both judging rules on 14 November 2026; the conditions
of a gate run are in the [performance requirements](performance-requirements.md#accepted-measurement-conditions).

The managed reference uses compact preallocated managed rows, bounded collections,
the same declared indexes/comparers and explicit dictionary-string sharing. Use
compact managed containers of the same kind as the engine's structures — a hash table
and an ordered tree — so an intentionally wasteful baseline
cannot manufacture the 25% result. Include reserved capacities and all retained
index/control/text structures on both sides. Collect runtime/process/peak metrics
separately. Record hardware, .NET/PostgreSQL versions, CPU quotas, memory limits and
network topology with results; those are execution inputs, not hidden design choices.

## Verification handoff and closure

| Former open area | Fixed implementation direction | Required release evidence |
| --- | --- | --- |
| Q07.3.6 / E04,E08 | Shared typed binding/comparison, explicit index facade; OQL deferred | Every scalar/key family, overflow/null/NaN/Unicode and composite ranges agree across access paths |
| Q08.1.3 / E07 | Framed TCP, fenced S/R capture, shared bounded RAM delivery, applied ACK, one-arena reload, fixed-position waits | Actual 5–10-process topology, bootstrap mutations/WriteBatch, gaps/duplicates/disconnect/history changes and bounded buffers |
| Q09.2 / E06 | Independent 256-byte base, transactional cursor and journal, two-transition receipt protocol | Integrated foreground/applier/cleanup/reopen failures, operation expiry, bounded storage and recovery equality |
| Q09.4 | Infrastructure restore, explicit new-history activation, offline validation | Restore to isolated target, rejected damaged/incompatible target, child full reload |
| Q10.1 | Accepted synthetic profile and targets; deployment fixture/hardware recorded by benchmark owner | Reproducible same-data managed comparison, ordinary and burst workloads; no substitution of prototype timings |
| Q13 / E06,E08 | Complete/partial commands, duplicate rules, bound revisions, terminal receipts/no-op accounting | Typed/dynamic facade and wire agree; cancellation/unknown/retry never duplicates effects |
| Q14 | Explicit reservations and settings above; failure before commit or stopped instance after publication failure | Exhaust each budget, drain protected readers, no post-cancel queued execution or unbounded wait |
| Q16 / E01,E02,E05 | Concrete headers/descriptors, chunk/hash layouts, schema/version metadata, independent base | Binary round trips for every type; malformed input rejection; ownership/reference-count audits |
| Q17 / E01,E03 | Class free lists, protected root lifetimes and bounded compaction and shrink | Constant-live-set churn, delete and reinsert, relocation of root-owned data and shared text, unchanged index results and no uncontrolled retained growth |

**These nine areas have an implementation baseline, and Unique indexes are accepted
for the core milestone. No unresolved architecture selection currently blocks
planning of the first production engine milestone.** Remaining work is
implementation and verification with explicit owners and gates. Source/file organization, method names and measured tuning
do not require another architecture vote. A discovered contradiction or failed gate
must be reported and fixed; specification closure is not permission to conceal it.

The receiving agent can now plan the milestone and technical sprints against this
contract. It must preserve the tested mechanism boundaries and schedule the missing
integrated evidence before production. This document intentionally does not assign
sprints, estimate dates, or declare a production release ready.


## Consumer compatibility check

**Historical context, not a constraint on the engine.** This section preserves the
consumer review that helped identify missing guarantees during design. The owner
confirmed during the documentation transfer that Identity must adapt to the accepted
engine decisions. Its working draft does not narrow this milestone or reopen index,
Unique-index or atomic multi-type WriteBatch support. The Identity work document was
updated to this boundary on 14 November 2026; the engine planner starts from the
contract above.

The requirement descriptions below refer to the draft reviewed at that time, not a
claim about the latest Identity implementation or plan. Keep the reasoning available
without treating the older model, feature selection or workaround as a current
integration mandate.

Reviewed the implementation repository's `docs/work/epic-1-core.md` (now `docs/work/epic-1/`) and
`docs/work/epic-2-identity-storage.md` on 14 November 2026, read-only. Epic 2 describes
an ASP.NET Core Identity store. Its requirements are consumer evidence, not automatic
authorization to change accepted engine contracts.

| Consumer requirement | Engine baseline / decision boundary |
| --- | --- |
| R1: unique normalized user/role names, including concurrent creation | **Accepted and specified:** [explicit Unique indexes](../decisions/unique-indexes.md) on scalar/composite keys; master validates final state before effects commit, skips keys containing null and rejects the whole operation/WriteBatch on conflict. Nine bounded Arc scenarios passed; integrated engine verification remains required |
| R2: non-unique field lookup | Covered by declared-index access; implementation and facade tests required |
| R3: scalar/structured collections including Binary | Covered by schema/owned collection contract; current scalar-only facade must be extended |
| R4: collection-member search | Consumer marks this Should and permits explicit enumeration initially. Native multi-value indexes are outside this milestone; measure fallback before enabling external-login/passkey scale claims |
| R5: inherited .NET properties | Binding must include eligible public inherited properties without duplicating metadata; a small facade test is required |
| R6: package/app schema ownership | Core accepts one complete compiled schema. The minimal integration supplies an application-owned schema/template and validates required package fields at startup. Runtime fragment merging is not required by the core; the package planner owns its registration/template API |
| R7: durability | Mandatory first-milestone PostgreSQL path |
| R8: optimistic concurrency | Accepted expected-revision check; adapter maps conflicts to its domain error |
| R9: String/Guid keys and nullable time/Binary fields | Accepted type catalog; no auto-generated integer IDs required |
| R10: named databases in DI | Independent engine instances; the facade/host owns names, with no shared partition abstraction |
| R11: enumeration for administrative queries | Budgeted .NET enumeration is included; a live pass is not a database snapshot; adapter must expose limits/failures honestly |
| R12: child writes and inter-request consistency | Engine session/position contract is included. The host propagates response/request positions through request context; a request-scoped store alone does not preserve a dependency across HTTP requests |

External-login integration has an additional modeling obligation: the reference
Identity EF model makes `(LoginProvider, ProviderKey)` a primary key, and passkeys
have a global credential key. This can be seen in the [official .NET 10 model](https://github.com/dotnet/aspnetcore/blob/v10.0.0/src/Identity/EntityFrameworkCore/src/IdentityUserContext.cs).
A collection scan followed by an independent user Save cannot enforce those global
constraints under concurrency. If those features are enabled, use adapter-owned
scalar lookup/claim entities with an explicit unique composite index, and update
the owning user plus its claims in one existing WriteBatch. For binary credentials,
an exact canonical Base64 encoding can provide a String claim key while retaining
Binary data; do not assume a collision-free truncated hash.

This is a concrete integration path using already accepted core features, not a
requirement for native multi-valued indexes. The consumer's proposed "only one user
entity per mutation" model must be reconciled accordingly by its planner. Recheck
the ownership/revision of a claim before acting on a live user read; no referential
integrity or multi-call snapshot is implied. Configure supported user/claim counts
to fit a complete batch; do not split an atomic delete into acknowledged partial
success. Consumer tests must cover duplicate credential claims and concurrent removal.

Passkey feature selection, package template composition details, request-context
middleware and administrative query behavior are adapter planning work. They do not
require OQL, general joins, external transactions or lazy schema migration in core.
If that consumer scope changes to require an unsupported guarantee, reopen that
specific decision rather than silently weakening it or declaring every future
capability a first-milestone blocker.
