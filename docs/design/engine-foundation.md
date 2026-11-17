# Physical engine foundation

**Ownership overview, 14 November 2026.** The first production milestone requires
PostgreSQL persistence. One engine instance opens one database, with no partition
layer. The [implementation contract](milestone-1-engine-contract.md) now fixes the
mechanisms and starting settings described as open in the earlier overview below.
Milestone and sprint planning is tracked in [Epic 1](../work/epic-1-core.md). The former [MemoryOnly experiment](first-prototype.md)
is not the production release scope. No production engine exists yet.

[Architecture](index.html) · [Decision register](questions.md) · [Database scope](../decisions/database-scope.md)

The [accepted first-milestone profile](performance-requirements.md#accepted-primary-workload-and-first-milestone-access-scope)
uses users, groups, roles and settings with 5–10 children, with 10,000 changes/s tested
separately as peak demand. OQL is deferred beyond this milestone. Direct access to
declared single-field/composite indexes uses [explicit selection](../decisions/index-access.md),
without automatic index selection. This removes the language/parser requirement, not physical
indexes, comparison rules, coherent publication or durability.

## Fixed requirements

- One authoritative master accepts writes from many concurrent application threads
  and clients. Master authority does not prescribe one calling or execution thread.
  A child applies confirmed changes; it never independently changes replica state.
- Durable success requires PostgreSQL commit and coherent publication in the
  master's arena/indexes. Accepted internal group commit can combine independent
  prepared changes in one PostgreSQL transaction without shared read atomicity.
  Changes are prepared one write at a time under the lock of the writes; short bounded foreground groups commit
  and publish sequentially through one ordered stage per instance. Readers retain
  access during storage I/O; root/index gates cover only protected memory work.
- One instance owns one database: its schema, data, indexes, string dictionary,
  native arena, resource budget and history. Lookup uses entity type and primary
  key within that database. References and WriteBatch have the same database scope.
  There is no partition catalog, routing ID or subset subscription.
- Native segments contain bulk data; managed .NET owns suitable control structures.
  Relative addresses, owned variable blocks, size-class reuse and bounded incremental
  compaction remain accepted. The database owns one bounded safely empty-segment
  reserve. Each root has a stable control entry and relative block locations.
  Control entries use managed arrays of structs, growing by additional arrays;
  exact fields and array sizes remain engineering/measurement work.
- A root object and its owned tree form one atomic read/write unit. Ordinary reads
  return independent copies; short protected reads may borrow memory. No root or
  index lock spans database/network I/O. No global calculation snapshot is promised.
- A [configurable root-size cap](../decisions/write-admission.md#configurable-root-object-size-limit)
  covers owned data, excluding the contents of referenced roots. Oversized mutations
  fail before commit. The numerical threshold is to be measured; the cap is separate
  from the instance memory budget and from the WriteBatch.
  **Changed by the owner on 15 November 2026:** an entity has no size limit of its own; it
  is refused only when the memory budget cannot hold it.
- An explicit WriteBatch is atomic within one database; since 15 November 2026 it has no
  limit of its own on its operations or its size. Construction holds
  no engine locks; readers continue through preparation/storage I/O, and a database
  barrier closes access for whole-batch publication. Replay and replicas preserve
  that boundary. Separate reads/scans do not gain a shared snapshot.
- If committed publication fails, the affected Lattice instance stops serving new
  local reads/writes and recovers through controlled close/reopen using one arena.
  The embedding application process need not terminate.
- Operation tokens and compact Durable result receipts provide authoritative status
  lookup and safe same-token resubmission within bounded retention, defaulting to
  15 minutes from first master admission, configurable without extending on retry. Receipts commit
  with the mutation; expired tokens cannot be admitted as new work. Automatic retries
  and cross-restart MemoryOnly durability are not implied.
- Explicit partial updates prepare affected values/blocks before commit and publish
  fixed values in place or install replacement blocks after commit. Full Save prepares
  the complete replacement without diff; required publication resources are prepared
  within the instance budget before journal submission.
- [Repeated scalar targets in one explicit partial update](../decisions/mutation-commands.md#repeated-scalar-targets-in-an-explicit-partial-update)
  reject that mutation, or its containing WriteBatch, before commit. Ordered collection
  commands and repeated local assignments before ordinary Save retain their contracts.
- The PostgreSQL compact journal is the foreground durable path. An in-process
  background applier coalesces changes into saved units and advances the applied
  position atomically. The 256-byte saved unit does not dictate native block size.
- MemoryOnly remains an available mode and an isolation tool for measurements;
  it does not replace persistence in the first production milestone.
- Primary lookup uses a hash table reaching stable root entries. The standard .NET
  Dictionary is excluded from its production implementation. Since 16 November 2026 it
  is a hash table of the engine's own, read without locks while a write is published
  (the owner's decision; see index structures). Before that, Arc.Collections 2.2.0
  UnorderedMapSlim was selected for the first implementation, with engine-owned key
  views, capacity reservation and publication protection. Full lifetime integration
  and target-environment performance remain engineering work. See [index structures](../decisions/index-structures.md).
- Ordered secondary indexes use Arc.Collections 2.2.0 OrderedMultiMap in v1. Its
  public node reuse supports preparation before commit; higher retained index memory
  than the measured Kaos alternative is accepted. Index keys copy fixed-size values
  and share stored immutable text through protected stable references, preserving
  Plain/Dictionary policy. Exact descriptors, synchronization and traversal still
  require engine integration and verification.
- [Float32/Float64 special values](../decisions/queries.md#floating-point-special-values)
  use one typed Equals/CompareTo contract across OQL, indexes, Map and Set. Hashes
  must agree for all equivalent NaNs and for both zero signs. Native keys and scans
  must preserve the same semantics; ordinary C# comparison operators differ for NaN.
- [String ordering and ranges](../decisions/queries.md#string-ordering-and-query-overrides)
  use the field's Ordinal/OrdinalIgnoreCase policy, without a v1 query override.
  UTF-8 and UTF-16 must produce the same logical order; raw byte ordering is not
  a substitute for the selected .NET comparison semantics.
- Master and child use the same logical arena-change mechanism. The master validates
  application writes; Durable adds a PostgreSQL commit before publication. A MemoryOnly
  child receives initial state and confirmed changes only from the master, publishes
  them locally and never accesses PostgreSQL. Full loading/index preparation and
  catch-up through a recorded published position precede readiness.
- [Replication is served from master memory](../decisions/replication-and-hosting.md#pending-delivery-in-memory).
  One shared delivery buffer retains changes only until all participating recipients
  acknowledge them; no extra retention window follows delivery. New children load
  the full arena state. Reconnect may use a remaining RAM tail, otherwise full reload;
  PostgreSQL is not a catch-up source. Existing pending-work limits still apply.
- [First-release diagnostics](storage-and-maintenance.md#control-and-maintenance-tools-q18)
  provide a DI service and library-owned HTTP module mounted under a host-selected
  path. A child reports local state or requests master state through the internal
  protocol. The master needs no public diagnostic endpoint; no CLI/GUI or separate
  mandatory management host is selected. Administrative scope is diagnostic-only;
  manual maintenance commands are deferred, with automatic engine policies unchanged.
  Exact API/framing remains engineering work.
- [Backup/restore execution](storage-and-maintenance.md#accepted-backup-and-restore-control)
  belongs to infrastructure, with no Lattice v1 launch commands. The engine must still
  validate restored state, activate the restore's new history and expose readiness
  only after coherent recovery; the opening/activation protocol requires verification.

These requirements are owned by the [decision records](../decisions/README.md).
The sections below retain the ownership rationale and earlier experiment boundaries.
The implementation contract owns subsequent concrete selections; a historical
proposal below is not a second pending approval or evidence of measured performance.

## Ownership and proposed physical placement

The accepted [database scope](../decisions/database-scope.md) gives each open instance
its own schema catalog, records, indexes and string dictionary. Within that context,
logical identity is entity type + primary key. Related types and their references
share the database; independent instances do not share a runtime schema catalog.

The database owns its memory budget, mutation coordination and native segments.
The [segment policy](../decisions/physical-storage.md#database-owned-native-segments)
allows compatible blocks from multiple types to share capacity and keeps one bounded
reserve of safely empty segments. There is no default/implicit partition or global
registry that groups several databases into a newly named replacement abstraction.

A root contains fixed fields and descriptors for variable payloads. Owned blocks
identify their owner and allocation capacity so movement/reclamation can find the
descriptor that must change. Large values use bounded chunks. Dictionary strings
use dictionary entries with stored-reference accounting, not arbitrary shared
owned blocks. Physical indexes are reconstructed locally from schema and records.

[String input validity](../decisions/strings.md#input-validity-and-preservation) is
settled for both database encodings: accept well-formed Unicode, reject malformed
text before commit without replacement and preserve valid text exactly. Validation
belongs to preparation; chunked codecs must carry state across split sequences.

Persisted metadata must distinguish logical identity, schema/layout version and
physical location. Native process addresses cannot be durable identifiers or wire
references. Persisted base units and logical mutations need a defined mapping to
objects, deletions and schema versions before implementation begins. The accepted
[independent binary base](../decisions/physical-storage.md#independent-persisted-base-and-arena-reconstruction)
uses provider-local addresses and reconstructs the arena on load. Pure native
relocation does not require a persisted relocation. Exact directory/encoding and
saved-to-live mapping remain engineering work; the journal and 256-byte baseline
are unchanged.

## Accepted root entry and remaining access engineering

The accepted [root control-entry decision](../decisions/physical-storage.md#stable-control-entry-per-root)
uses one stable entry per root lifetime, coordinating current location, revision and
access/lifetime state. Logical identity remains type + primary key within the database.
Owned payloads continue to use relative locations; there is no universal handle for every
block. [Control entries use managed arrays of structs](../decisions/physical-storage.md#managed-arrays-of-structs-for-control-entries),
with growth by added arrays rather than a full-entry-table copy. Locators identify
logical slots/lifetimes, not raw CLR pointers. Exact field layout, array sizes and
concurrent directory/lifetime integration still require design and verification.

Lookup must retain a valid control entry before releasing index protection. The
reader then acquires root protection, rechecks lifetime state and resolves the current
location. It must not retain an unprotected physical address between these steps.
Deletion and reinsertion under the same key must not let an old reader reach a new
allocation accidentally. Generation checks or equivalent lifetime protection need
one concrete implementation, including index enumeration and deferred entry reuse.

[Safe lookup, deletion and reuse](questions/root-location.md#implementation-task-safe-lookup-deletion-and-entry-reuse)
are implementation work, not an approval gate. A deletion published before protected
read access yields absence for the old target; a read holding protection delays
actual removal. Choose and verify retention/reuse bookkeeping within those contracts;
no particular counter or epoch algorithm is accepted or awaiting product selection.

The compactor takes the same root protection as a mutation, relocates selected
blocks, updates all affected descriptors/locators and retires old allocations.
Dictionary relocation has its own shared-entry protection; a root gate alone cannot
protect text referenced by other roots. The lock-order design must cover both paths.
Protected readers prevent unsafe moves; owned copies no longer retain arena memory.

The allocator tracks occupied, reusable, retired and reserved capacity separately.
Compaction must have bounded work and temporary headroom. Its success is reclaimed
capacity under a fixed live dataset, not merely a lower hole count. Native virtual
addresses need not become adjacent; physical RAM placement belongs to the OS.

The [arena protocol](arena-protocol.md) specifies a concrete bounded-experiment
implementation of these paths. [ArenaLifecycle evidence](prototype-evidence.md#bounded-arena-lifecycle)
checks protected reads, private preparation, targeted writes, deletion/recreation,
shared text and root/chunk relocation together. Exclusive owner gates drain these
readers before reclamation; claimed root metadata cannot be reused prematurely.
The [Arc extension](../../src/Prototypes/ArenaLifecycle/INDEXES.md) replaces fixture primary
lookup with UnorderedMapSlim and adds an OrderedMultiMap index on protected text
plus a fixed numeric component. It checks explicit index ownership of text,
equal-value descriptor replacement and candidate/root lifetime handoff. The model
still uses striped root gates and a coarse catalog/index gate. General schemas,
scalable synchronization and multi-core verification remain work. That isolated
experiment did not cover WriteBatch or durable replay; the integration below adds
bounded evidence for those paths. The [tail investigation](../../src/Prototypes/ArenaLifecycle/INDEXES.md#bounded-tail-investigation)
reproduced long execution stalls without GC or measured gate contention; it does
not establish production latency bounds.

The subsequent [WriteBatch prototype](../../src/Prototypes/WriteBatch/README.md) integrates
one arena and its selected indexes with atomic multi-type Upsert/Delete publication,
real PostgreSQL commit, retained results and close/reopen after controlled failures.
Four abrupt application-crash boundaries also pass recovery/retry checks. This
supports the accepted mechanism, while its serialized preparation, fixed capacities
and binary fixture base do not close general layout, production admission, base
applier, network-failure or replication integration work.

The [256-byte saved-unit extension](../../src/Prototypes/WriteBatch/UNITS.md) subsequently
integrates bounded background coalescing, whole-batch base/cursor commits, separate
cleanup and receipts retained beyond journal removal. It verifies concurrent SQL
recovery snapshots and nine application-crash boundaries. This closes a bounded
integration check, not the general format, retention/resource protocols or production
provider. It does not implement or measure replication visibility.

## Concurrent submissions and durable publication

The accepted [preparation policy](../decisions/physical-storage.md#targeted-mutation-preparation-and-publication)
uses bounded targeted work: fixed-field assignments in place after commit, prepared
blocks for variable changes and full replacement preparation for Save. It preserves
the durable acknowledgment and memory-admission contracts.
[Per-object coordination and internal group commit](../decisions/write-acknowledgment.md#per-object-coordination-and-internal-group-commit),
[explicit atomic WriteBatch within one database](../decisions/document-model.md#atomic-writebatch-within-one-database),
and [instance recovery after committed publication failure](../decisions/checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure)
are accepted, together with [operation identity and retained results](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1).
The partition lifecycle proposal is withdrawn. Continue with the concrete physical
mechanisms in the completion checklist; no replacement partition-management layer
is required.

A pending mutation owns its place among conflicting writes to the same object while
readers retain access to the published state. Prepared independent mutations may
share a storage commit. Final state-dependent preparation must respect predecessor
state. The accepted [ordered stage](../decisions/write-acknowledgment.md#ordered-commit-and-publication),
as amended on 17 November 2026, prepares one write at a time and serializes
saving/publication of short bounded foreground groups. The current group completes publication before the
next proceeds through that stage. MemoryOnly omits the storage wait. See
[coordination rationale](questions/master-write-order.md).

1. Admit a bounded request; validate its database/schema context and input lifetime.
   Persist first admission/fingerprint/expiry through the [operation protocol](operation-retry-protocol.md)
   before dispatching effects; coordinate duplicates without extending the deadline.
2. Establish its conflict order, read the required state under short protection and
   reserve preparation/publication capacity. Revalidate revisions before committing
   if preparation was based on a state that another mutation could change.
3. Atomically claim the operation token and append logical effects plus the compact
   result receipt in PostgreSQL; await durable commit without arena/index locks.
   Existing tokens follow the result-resolution path, not a second mutation. Readers
   can still read the prior state.
4. Publish the root and affected indexes coherently under short gates. Root removal,
   secondary-index candidates and compaction must follow the same lifetime protocol.
5. Complete the call and make the confirmed change available for replication. The
   background applier advances the saved base independently of this acknowledgment.

[Cancellation](../decisions/mutation-commands.md#cancellation-at-the-commit-boundary)
withdraws work before the synchronized storage-dispatch boundary. After dispatch,
the engine completes the ordered path within its own finite failure deadlines and
reports the actual outcome; a caller cannot abort shared storage or publication.
MemoryOnly uses publication handoff. Complete cancellation cleanup before releasing
the caller's input; uncertain commits retain the existing result-resolution rules.

Ordering ownership is not an application-held lock over the I/O wait. The scheduler
must prevent conflicting pending writes from invalidating a prepared commit while
allowing readers and unrelated preparation to proceed. The detailed gate order,
reservation lifetime and publication-failure path remain required design work.

A client-side Save can forward a command to the master without modifying its local
replica independently. Success also waits for coherent application on the calling
child. Related requests propagate a minimum history/position and wait for it with
a timeout on any serving pod, without waiting for all replicas. The
[session-consistency contract](../decisions/session-consistency.md) owns facade semantics;
transport, exact signatures and bounded waiter implementation remain engineering work.
An explicit local position wait is available before indexed reads. It waits for
coherent data/index publication; following operations retain the dependency and
revalidate readiness/history. Waiting creates neither a snapshot nor an arena pin.

The [shared arena application contract](../decisions/replication-and-hosting.md#shared-arena-application-on-masters-and-replicas)
reuses preparation/publication logic for master changes and received committed
effects. Authority, durable acknowledgment and replay ordering belong to their
respective orchestration paths. PostgreSQL saved-base maintenance is separate from
applying a change to the arena on either node.

## Accepted explicit database WriteBatch

Q01 accepts all-or-nothing publication of explicitly grouped objects of different
entity types. Ordinary complete-object batch saving uses
[Upsert semantics](../decisions/mutation-commands.md#atomic-batch-save-with-upsert-semantics),
creating absent objects and fully replacing existing ones from the supplied values.
There is one commit for the whole group, not an independent commit per object. The
[accepted duplicate-identity rule](../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch)
requires one operation per type/key inside the explicit batch and rejects the whole
batch before commit on repetition. This does not restrict separate ordinary writes
or repeated edits of one caller-owned value before a single Save. The
[accepted package](questions/write-batch.md) is one application
batch in one database. Construct it without engine locks, prepare
and validate the whole group, commit it together, then publish behind a database
barrier. Readers continue during storage I/O and wait during publication; conflicting
database writes are scheduled separately. The barrier must cover every access path.

This requires logical transaction boundaries in journal recovery, saved-state/capture
and replica application. Separate reads or a scan can still cross the publication
boundary; a consistent multi-object read scope is an additional contract. General
application-held transactions, cross-database atomic writes and external transaction
participation are outside the accepted package. SDK names, numerical bounds and
concrete synchronization/recovery mechanisms still require implementation and proof.

## Recovery and failure boundaries

Before durable commit, rejected work must leave the published object unchanged.
A lost commit response is an uncertain outcome, not proof that the write failed.
After a durable commit, publication failure cannot discard the persisted operation:
the instance must prevent inconsistent service and recover the committed state.
The accepted [recovery policy](../decisions/checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure)
stops new local reads/writes on the failed instance and recovers through controlled
close/reopen using one arena, without mandatory host-process termination. All failure
transitions still require testing.

The accepted [operation-result contract](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1)
uses bounded durable receipts to resolve uncertain commits and allow same-operation
resubmission. Its retention boundary rejects expired tokens rather than executing
them as new commands. Receipts do not prove publication or replica freshness. Exact
token/expiry/status mechanics still need implementation and failure testing.

Loading needs compatible database/schema metadata, a consistent saved base with
its applied position, and the committed journal tail. Index construction and replay
finish before readiness. During master recovery, the affected instance does not
serve application reads/writes; its saved-base applier and cleanup remain stopped
until recovery completes. No concurrent saved-base capture mechanism is required
for this ordinary reopen path. During normal operation, the background applier
atomically commits saved changes with their position; cleanup retains everything
required by recovery and active retained consumers.

A child's initial loading is a separate master-to-child protocol. It receives the
full database and a gap-free ordered stream through a published readiness position
from the master, then continues local background application. It does not load from
PostgreSQL. The detailed transfer/handoff protocol remains E07 work and must support
both Durable and MemoryOnly masters.
The source is the master's arena plus its shared pending-delivery buffer. Do not
query PostgreSQL for child catch-up or keep acknowledged payloads for future clients.
Initialization holds a bounded claim on only the tail needed for its handoff.

[Initial loading uses binary arena-block transfer](../decisions/replication-and-hosting.md#initial-replica-state-representation)
with installation metadata, prioritizing startup speed. Per-record re-encoding is
not the default transfer path. The child reconstructs local runtime metadata and
indexes in its one arena, then catches up through the published readiness boundary.
This does not change live logical updates or the independent PostgreSQL base format.
Exact capture, block/reference handling and handoff remain implementation work;
the representation direction is not awaiting another approval.

The [initial capture/handoff mechanism](../decisions/replication-and-hosting.md#initial-capture-and-readiness-handoff)
is accepted for v1: register delivery at S and fence the control-entry range; retain
deleted identities/revisions without old payloads, prevent premature slot reuse and
copy coherent roots with individual watermarks. After the scan fixes R, install the
complete base and replay whole transactions through R, filtering effects per root.
The provisional arena remains unreadable and has no applied acknowledgment until
readiness and local index preparation are complete. Acknowledgment releases pending
delivery and temporary capture metadata under the existing bounded-retention rules.

The [bounded correctness prototype](prototype-evidence.md#bounded-replica-bootstrap-correctness)
supports this mechanism; production shared-string, chunk, allocator and multi-child
integration remain engineering work. It assumes a correct published transaction
order. The later [commit/publication scheduling decision](../decisions/write-acknowledgment.md#ordered-commit-and-publication)
accepts preparation of one write at a time and sequential bounded groups to supply
that order. The prototype did not test this production pipeline; position assignment,
capture registration and failure integration still require verification.

Ordinary restart preserves durable history. Restore from backup establishes a new
history and forces child reload. These are already accepted contracts; the exact
PostgreSQL schema, replay protocol and failure transitions still need completion.
Preserving durable history identity does not preserve a lost RAM delivery tail;
children missing that range fully reload from the recovered master's memory.

## Architecture completion checklist

The [milestone implementation contract](milestone-1-engine-contract.md) resolves
placement, lifetime, allocation/index preparation, write scheduling, persistence,
transport, schema evolution and initial bounded settings. Its
[consumer compatibility check](milestone-1-engine-contract.md#consumer-compatibility-check)
is historical context: no consumer requirement blocks or narrows the engine. Earlier
open-mechanism notes in this overview must be read against that subsequent specification.

| Area | Fixed baseline / remaining evidence |
| --- | --- |
| Scope | One database, no partitions; PostgreSQL master, full MemoryOnly children; first workload and deferred features explicitly listed |
| Arena | Relative blocks, concrete header/descriptors, paged root entries, class regions, chunked values and bounded Map/Set layouts; implement and validate every type |
| Lifetime | Database/root-stripe/catalog/text/allocator ordering, claimed generations, bounded compaction; validate concurrent readers and retained references |
| Indexes | Selected Arc containers, reserved growth/nodes, shared text, typed comparison and bounded traversal; verify complete integration and memory targets |
| Writes | Root reservations, preparation of one write at a time, ordered commit/publication, full WriteBatch boundaries and concrete operation-result protocol; verify all failure transitions |
| Persistence | Independent binary roots in 256-byte units, transactional base/cursor/journal, receipt retirement and offline recovery; integrated crash tests remain required |
| Replication | Framed TCP, fenced capture/remapping, ordered applied ACK, bounded tail/full reload and session dependencies; real multi-process tests remain required |
| Schema | Stable IDs/layouts, typed/dynamic binding and explicit disk-shadow migration with all writes paused and affected types unavailable; implement compatibility/recovery checks |
| Operations | Diagnostic-only API, infrastructure backups and explicit restore activation; deployment/resource tests remain required |

The architecture specification enables planning; no consumer blocker remains.
It does not establish production readiness. Implementation must pass the accepted
concurrency, durability, resource, recovery and performance gates before release.
