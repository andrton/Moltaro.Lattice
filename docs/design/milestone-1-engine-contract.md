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
| Index owner | Arc primary/secondary adapters, comparison, capacity/node reservations, candidate claims; depends on arena key views |
| Mutation coordinator | Admission, per-root ordering, preparation, ordered commit/publication and results; owns reservations, never application callbacks inside publication |
| PostgreSQL provider | Catalog, independent base units, logical journal, receipts and recovery; consumes versioned encoded effects, not native pointers |
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
kind/flags (4), length/count (4), and relative address or stable text handle (8).
Fixed-layout size must fit one 64-KiB block; large values live outside it. Schema
compilation reports an oversized fixed layout before data admission. Numeric widths
come from the schema; do not widen all small integer fields to Int64.

Store small owned values <=64 bytes in the packed root tail when the root still
fits its block: the descriptor's Inline kind stores root-relative offset/capacity
and logical length. Root moves repair indexed Plain-text entry locations under root
and text protection. A partial update that outgrows inline capacity prepares an
external block; the old inline span becomes counted root slack. Repack that root
under the same bounded compaction protocol; do not keep appending unused tail space.
Dictionary text remains independently owned. No inline byte location escapes a gate.
Never overwrite indexed Plain bytes while an old text entry retains them: prepare
replacement text and switch its entry only after old index/reader retainers drain.

Other variable values <=4 KiB payload initially use one suitable block. Larger values
use blocks with at most 65,504 payload bytes and a bounded directory of chunk
descriptors. A chunk boundary is not a semantic string/collection boundary. Decoders
carry Unicode/element state across chunks. Only affected chunks are prepared for
partial changes; full Save prepares a complete replacement without automatic diff.

Use segmented managed arrays of 4,096 root entries. Initial logical entry fields:
root address (8), last mutation position (8), key-descriptor address (8), stored-root
ID (8), generation (4), type ID (4), state (4), claim count (4): 48 bytes before CLR alignment checks.
Assert actual layout in implementation tests; do not silently rely on an estimated
struct size. Root handle = generation UInt32 plus slot UInt32. Increment generation
on reuse; permanently retire a slot on generation exhaustion. Entry-array growth
adds a page and does not copy every entry. Generation is local lifetime protection,
not a second public object key or a durable revision.

## Collections and strings

List stores its count and an ordered chunk directory. Fixed-width elements are
packed by schema stride; complex elements contain owned descriptors. Indexing uses
chunk arithmetic/directory lookup; insert/remove may prepare and shift the affected
suffix. Never reorder elements to improve storage. Temporary work remains subject
to the root/batch byte budget.

Map/Set use packed entries and linear lookup up to 8 elements initially. Larger
values add a power-of-two open-addressed bucket directory, linear probing, cached
hashes, entry ordinals and explicit empty/deleted markers. Grow before occupancy
including tombstones exceeds 70%; rebuild tombstones during bounded preparation.
Shrink back to compact form at <=4 entries. Buckets point to owned entries, never
managed objects. A table rebuild is staged before commit and installed under the
root gate. Large collections remain chunked; payloads do not become one huge array.
List/Map/Set input and comparison semantics stay as already accepted.

Plain strings preserve their original encoded bytes. Dictionary fields use one
database-wide content table with exact-text equality, preserving case/spelling even
when a field's search policy ignores case. No automatic Plain/Dictionary switching.
All text used by an index has a stable text entry; an indexed Plain value can share
its existing text with the index without content interning. Text entries track saved,
prepared and index references separately. Reclaim only when every reference and
protected reader has drained. Handles carry generations and cannot be rebound to
different text while an old key still refers to them.

Use one dictionary gate initially. Moving shared bytes updates the stable entry
under that gate; published text is immutable. Root-owned movement repairs its parent
descriptor and child owner backlinks under the root gate. Scalar/location movement
does not change logical revisions or write a durable relocation record.

## Reader protection, publication and compaction

Use the proven [arena protocol](arena-protocol.md), extended with the database
publication barrier. Initial synchronization is ReaderWriterLockSlim: one database
barrier, 1,024 root stripes, a catalog/index gate, dictionary gate and allocator gate.
Root stripes bound lock-object memory; collisions can contend. One logical root
protection still covers all its owned blocks. No separate heap lock per object.

Acquisition order for nested work is database barrier -> distinct root stripes in
ascending order -> catalog/index -> dictionary -> allocator. Candidate lookup briefly
claims an entry under catalog/index protection, releases it, then acquires root
protection and rechecks generation/liveness. Never wait for a root while holding
the catalog gate. Drop claims after releasing the root gate. Deleted entries become
reusable only after claims drain; an old claim never follows a recreated key.

Ordinary reads hold shared database/root protection only while evaluating/copying
one root. Borrowed reads are synchronous, read-only scoped callbacks/ref-struct views;
no await, retained native pointer or writable alias is supported. Generated and
dynamic views use the same lifetime. The engine cannot safely preempt arbitrary
user code inside a protected callback: prolonged borrowing can delay publication.
Document this caller obligation and expose durations diagnostically. Fault/close
cannot free an arena until outstanding protected scopes release it.

Enforce the accepted ban on nested public database calls during a borrowed callback
with a thread-local scope guard checked before acquiring engine gates, cleared in
finally. Copy reference keys and leave the scope before resolving another root.
Generated callbacks take a scoped ref-struct view and return a result type that
cannot itself be a ref struct; expose borrowed bytes only as read-only scoped spans,
not escapable Memory owners. Compiler/lifetime misuse tests and callback-exception
cleanup are mandatory. Unsafe caller code is outside the managed borrowing contract.

Ordinary publication takes shared database protection and exclusive affected root
protection; explicit WriteBatch takes exclusive database protection and its root
stripes. All read, index, scan and compaction entry points participate. Publication
only installs reserved values/nodes/descriptors and advances the applied position;
it does no I/O, validation, pool growth or user callbacks. Unexpected failure marks
the instance Faulted before reopening access. No partially published state is served.

The compactor first selects a sparse region from counters, releases allocator
protection, then tries to claim its owner and rechecks it. Skip roots reserved for
logical writes; shared-text movement remains possible through stable handles. Move
at most one bounded block per step, repair descriptors/backlinks, then free the old
slot. Start at <50% live occupancy with destination headroom; stop after a region
is empty or its estimated recoverable capacity is insufficient. Budget approximately
1 ms per turn and yield; a single bounded move is not preempted midway. No second arena.

Begin collection shrink after capacity remains >4 times live need for 5 seconds;
target <=2 times need. Reuse eligible holes first. Counters distinguish live bytes,
class slack, prepared capacity, retired/claimed capacity, free regions and empty
reserve. Allocation failure refuses the write before commit; compaction is not an
unbounded rescue wait. Native free and the OS working-set decrease are different events.

## Index preparation and traversal

Use Arc.Collections 2.2.0 UnorderedMapSlim for primary lookup, with schema-bound
unboxed key adapters; Arc OrderedMultiMap for secondary keys. Standard Dictionary
is not the primary index. Both packages remain behind engine-owned protection.
Physical indexes are rebuilt locally; no disk or replica images are persisted.
[Unique enforcement](../decisions/unique-indexes.md) is explicit in schema and checked
against final state in the ordered commit stage; any-null keys are exempt. Project
earlier operations in an internal commit group without copying the full index.
WriteBatch swaps/delete-and-reclaim are valid when every final unique key has one owner.

Reserve primary insertion capacity before commit. Reuse the experimental equivalent
replacement-table mechanism: serialize index mutations, construct the larger table
while readers use the old one, then briefly swap under the index gate. Charge both
tables and their references to the memory budget. Initial load sizes from captured
counts; later growth doubles capacity when needed. If the peak cannot be reserved,
fail before commit. Do not allow package growth inside publication.

Prepare secondary nodes by staging/removing package nodes; retain replacement text
before commit. Remove/reinsert a reused node with the **new descriptor even when
keys compare equal**. Release the old text only after removing its key. Fixed values
are copied into keys; text is shared through stable references. Compound keys use
schema order and explicit field comparers; keys are not raw byte-memcmp tuples.

Comparison/hash has one schema-owned service across indexes, Map/Set and direct
lookups. Use accepted .NET Equals/CompareTo semantics for floating-point values,
with compatible NaN/zero hashes. String comparison is Ordinal/OrdinalIgnoreCase,
independent of UTF-8/UTF-16 storage. Ordinal can stream UTF-16 code units; initial
UTF-8 IgnoreCase comparisons decode into pre-reserved UTF-16 scratch and use .NET
ordinal span comparison/hashing. Reserve/reuse scratch before gates, including write
publication needs; no per-comparison managed string allocation. This deliberate
CPU/workspace cost is measured against the memory target. Do not substitute UTF-8
byte ordering, culture collation, or an unverified custom case-folding algorithm.

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

Enumeration and index traversal copy bounded candidate batches and claim root
lifetimes before releasing index protection. Recheck predicate/liveness while reading
the current root. Keep a budgeted set of logical type/key identities already visited,
so updates or delete/reinsert cannot duplicate a key during one pass. Charge retained
key text too. Exhausting traversal memory/time gives an explicit error, not a partial
success disguised as a complete result. Offset skips visited matches; Limit bounds
returned matches. The live result set can change; no cross-call snapshot is introduced.

For ordered traversal, retain a budgeted copy of the last key and an index mutation
version. A managed package-node cursor is used only while that version is unchanged
and only under index protection. After a mutation, discard that cursor and seek the
last key inclusively, skipping visited logical identities. Inclusive reseek preserves
unchanged matches in equal-key groups; an exclusive key seek would lose them. This
may repeat scanning work under churn but cannot silently duplicate/omit unchanged
equal-key matches. Cursor/visited key copies own their bytes until disposal; they do
not keep unprotected pointers into removable text. Internal continuation adds no
public sort-key tie breaker or keyset-paging contract.

## Write coordinator and revision semantics

Durable operation admission follows the [retry protocol](operation-retry-protocol.md).
Then reserve logical root identities through a coordinator-owned FIFO conflict queue.
For WriteBatch, claim all distinct `(type,key)` identities atomically under the short
coordinator mutex; do not hold a subset while waiting for others. Independent roots
can prepare concurrently. These reservations are ownership records, not held arena
locks across storage I/O. The compactor skips reserved root payloads.

Preparation validates schema/input/existence/revision conditions, captures needed
current values under short read gates, obtains replacement blocks and index nodes,
and reserves complete publication capacity. No other logical mutation of those roots
can overtake it. Prepared descriptors stay valid until publish/abort because of the
reservation and text/lifetime claims. Release everything on pre-commit failure.

One ordered commit stage forms short groups, preserves explicit WriteBatch boundaries,
assigns positions transactionally and awaits PostgreSQL. Independent operations in
one SQL commit retain their own tokens and positions; they do not become an application
transaction. Publish each operation in order, with a whole WriteBatch at one position.
A committed-publication failure stops the instance, including background application,
until close/reopen recovers every committed operation. The host process need not exit.

Each logical root change receives the operation position as its new revision component.
The opaque revision binds history, type/key and that position; a revision from another
identity cannot match accidentally. For a caller it stays opaque: two revisions of one
entity can be compared for equality and nothing else — no order, no conversion to a
position, no meaning across entities. Recreated roots receive a new position. Confirmed
collection/absence no-ops retain root revisions but receive a durable result and stream
position. Full Save always replaces without equality detection; assigning a ready
scalar value is a mutation even if the value compares equal. No arithmetic commands,
automatic diff or automatic retry are added.

## Independent base, journal and recovery

The provider owns tables for database/catalog metadata, root directory, saved units,
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

Durable and wire logical values use explicit little-endian numeric encoding, .NET
Decimal's four UInt32 components, Guid's documented RFC byte order, UTC ticks for
DateTimeOffset, day number for DateOnly and ticks for TimeOnly/TimeSpan. Validate
declared ranges and Unicode. The format header carries magic, format version, schema
version, payload length and checksum. No CLR object image, package node or native
segment address is a durable format. Fixed layout offsets derive deterministically
from the compiled schema; unit/chunk placement is internal.

Use SHA-256 for persisted-root and complete journal payload integrity; framing uses
the same digest in its body envelope. Length/version checks precede decoding. Specify
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
The handshake checks compatibility, not identity: it authenticates nobody. That the
replication port is reachable only from the nodes of this database is a deployment
precondition, accepted by the owner on 14 November 2026; the engine adds no shared
secret and no TLS of its own. A child's open has a finite, configurable timeout: when
it is not Ready in time, the open fails with Unavailable and the instance closes.
One connection multiplexes replication, write forwarding, status and acknowledgments;
control frames have priority between bounded data frames. Embedded master reads/writes
invoke the same engine directly without transport or a mandatory local child.

Initial frame header: magic UInt32, protocol version UInt16, kind UInt16, flags UInt32,
body length UInt32, correlation ID UInt64, stream sequence UInt64 (32 bytes); body has
an integrity checksum. Hello binds database identity/history, schema fingerprint,
wire/layout compatibility and role. Reject unsupported formats before allocation.
Data frames are <=64 KiB; a larger bounded transaction is fragmented and published
only after its complete end/digest. ACK means **applied to data and indexes**, not merely
received by the socket. A child sends no success ACK for a partial WriteBatch.

Keep one shared RAM delivery tail with per-child applied cursors. Release a transaction
after every participating recipient has applied it. If a disconnected/slow child holds
the tail beyond its byte/time budget, remove its retention claim and require reload
when it returns. It may still serve ordinary local stale reads, but a MinPosition wait
cannot succeed until it catches up. PostgreSQL is never a child catch-up source.

Initial transfer uses the accepted fenced capture: register S and a root-slot catalog
fence; retain deletion/incarnation watermarks and the shared live stream after S.
Copy each root coherently under its protection, including owned blocks and required
shared text. Copy bytes into bounded send buffers, release gates, then perform I/O.
Wire block IDs and relocation metadata let the child fix relative descriptors into
its own arena; no assumption of equal process addresses. Shared text is transferred
with capture IDs and remapped to local text handles. Do not pin old payloads for the
entire snapshot or hold read locks over the network.

Process-local hash codes/buckets are derived state. Transfer Map/Set entries and
rebuild their hash directories on the child, just as dictionary and search indexes
are rebuilt. Never reuse randomized .NET hashes from another process. The block
importer marks derived regions for reconstruction and repairs descriptors before
readiness; its schema/layout metadata distinguishes entries from lookup acceleration.

Each captured root includes its last mutation position/incarnation. At the end of
enumeration, record a fixed published position R. Filter and replay transactions
S..R per root against those watermarks, preserving whole transaction application.
Apply deletion watermarks to avoid resurrecting removed/recreated keys. Roots inserted
outside the initial fence arrive through the retained stream. No reads are enabled
until the complete state and local indexes cover R. Live changes after R continue
through the same ordered application path. The existing bootstrap experiment proves
this state-selection model; binary/shared-block integration remains a release test.

**Decided by the owner on 14 November 2026:** children that start together are loaded
in parallel, not one after another. A deployment of 5–10 children has to come up on
default settings, and a queue of single captures would not let it. Each capture has its
own S, its own catalog fence, its own watermarks and its own R; all of them read the one
shared live stream, which is retained from the earliest S still in use. The number of
captures that run at once is an option, 10 by default. A child beyond the limit, or one
whose capture cannot reserve its buffers and watermarks within the memory budget, waits
before its capture is registered; that wait is part of its open timeout. The earlier
starting value of one capture at a time is withdrawn. The bootstrap experiment covered
one capture; several at once, and their cost to the master's readers and writers, are a
release test.

Before a full reload, close readiness, finish protected reads, discard the old arena
and load one new arena. Requests may wait at the bounded readiness gate, initially
30 seconds, with request/count limits. No unbounded retained closures or second arena.
Child restart always starts empty. Missing same-history RAM tail or a new history
requires full reload; ordinary master restart preserves history but may lose the tail.

Save forwarded by a child returns only after its local application. Related requests
carry an opaque minimum history/position; waits have a fixed target and finite timeout,
not a moving master head. Explicit WaitForPosition is available; subsequent requests
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
allocator details remain internal tuning. By the owner's decision of 15 November 2026
the instance budget is an option whose default is computed when the instance opens: half
of the memory available to the process, which is the limit of its container where there
is one and the physical memory of the machine otherwise. An application may set the
budget explicitly, and has to when one process opens several instances, because each
instance computes its default from the whole. The budget is enforced in either case. A
timeout or a limit below that an
application can observe is an option with this starting value as its default, never a
constant in the code; "configurable" in a row is not the complete list of such options.

| Limit / setting | Starting value |
| --- | --- |
| Root encoded size, including owned data | 1 MiB; configurable |
| Explicit WriteBatch | 128 roots and 8 MiB encoded/prepared input; configurable |
| Active prepare workers | 2; configurable and bounded |
| Internal group | Up to 32 operations / 8 MiB / 1 ms collection delay; never split a WriteBatch |
| Queued write input | 1,024 requests and min(16 MiB, 10% instance budget) |
| Storage recovery wait / SQL failure deadline | 30 s / 5 s per attempt, explicit Unknown around uncertain commit |
| Session synchronization / reload readiness wait | 10 s / 30 s, configurable; cancellation allowed for read waits |
| Scan/query temporary memory and execution | 8 MiB / 5 s per request, configurable; only time inside the engine counts, not the time a caller spends between two items |
| Child open | 30 s from the open call to Ready, configurable; then the open fails with Unavailable |
| Master leadership | Taken at open; a second master on the same store is refused at once. The leadership number is checked in every transaction that changes the store; the leadership session every 5 s, configurable |
| Retained operation outcomes of a MemoryOnly master | min(32 MiB, 10% instance budget), configurable; new writes are refused when it is full, an unexpired outcome is never dropped |
| Empty native reserve / compaction headroom | One 4-MiB segment / one 64-KiB move plus metadata |
| Base application | Up to 256 transactions / 8 MiB effects per batch, 32 MiB working set; <=100 ms idle cadence, immediate continuation when behind |
| Journal trigger / hard limit | 64 MiB or 5 s oldest unapplied; 1 GiB hard cap, configurable |
| Durable receipt budget | 1 GiB estimated table/index usage; conservative admission reservation and periodic actual-size reconciliation |
| Retry window / generation admission duration | 15 min configurable / 60 s internal |
| Shared pending replication / per-peer I/O buffer | min(32 MiB, 20% instance budget) / 256 KiB |
| Slow-peer retention deadline / concurrent captures | 30 s / up to 10 captures at once, configurable; a child beyond the limit waits before its capture is registered |
| Reconnect / applier failure pauses | 100 ms exponential backoff capped at 5 s, bounded by the relevant request/retention limits |

Max single transaction/root must fit the reassembly, preparation and applier workspaces.
Reserve managed/native/index/comparison/buffer overhead as well as encoded bytes;
encoded size alone is not a memory reservation. Reject inconsistent options at open.
Count old/new primary tables and managed arrays awaiting collection in peak evidence.
Do not weaken the 1.5x peak target by measuring a post-growth reference baseline.

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
the same declared indexes/comparers and explicit dictionary-string sharing. Reuse
the selected Arc containers where appropriate so an intentionally wasteful baseline
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
| Q17 / E01,E03 | Class free lists, striped protection, claims/generations, bounded compaction/shrink | Constant-live-set churn, delete/reinsert, dictionary/index moves and no uncontrolled retained growth |

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

Reviewed the implementation repository's `docs/work/epic-1-core.md` and
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
