# Moltaro.Lattice

A schema-driven in-memory object database for .NET. Compact native storage,
master-coordinated writes and local read replicas, with PostgreSQL durability.

> **First production milestone · accepted design.** The public facade and a managed
> placeholder exist today. The native, durable and distributed engine still needs
> implementation and integrated release verification.

<a id="purpose-and-boundaries"></a>
<a id="physical-foundation-before-implementation"></a>

## Engine and distribution

One instance owns one database: schema, data, indexes, strings and memory budget.
The same engine runs embedded in an application or in a dedicated service.

<!-- DIAGRAM:architecture -->

**Many callers, one authoritative master.** Application threads may submit writes
concurrently. Independent preparation overlaps; short bounded groups commit and
publish in order. Children forward writes and apply the master's confirmed changes.

**Local reads.** A ready child reads its own arena. PostgreSQL belongs to the Durable
master; children never connect to it. An embedded MemoryOnly master also works without
PostgreSQL or a child connection. Persistence mode is fixed when opening the instance:
Durable is the default for a master, and a child is always MemoryOnly.

<a id="document-and-schema-model"></a>

## Objects and schema

Each entity type has a declared schema and one immutable primary key. A root owns its
nested values and collections. Key references connect independent entities into graphs
without foreign-key enforcement, cascades or automatic loading of a whole graph.

A **root** is one stored entity with everything it owns. The public contract calls it
an *entity*, and a *record* when it is accessed by name without a .NET class. A *child*
is a read replica.

- **Types:** fixed-width numbers, Decimal, Boolean, String, Guid, temporal values,
  Binary and numeric enum/Flags. Null is allowed only where declared.
- **Keys:** a primary key is an integer, a String or a Guid, never null.
- **Collections:** List preserves order and duplicates. Map and Set enforce uniqueness
  without ordering guarantees. Owned complex values belong in List and Map values; Map
  keys and Set elements are simple values, not structures or collections.
- **Bindings:** typed .NET access and runtime access by schema share the same engine.
  A generated domain class is optional.
- **Evolution:** stable type/field identities and versioned layouts are required.
  Explicit migration pauses all writes, makes the affected entity types unavailable and
  has every child reload; lazy mixed-format conversion is deferred.

## Memory and strings

Fixed fields stay together in the root. Small owned values can be inline; larger
strings and collections use separate blocks and bounded chunks. This is the selected
layout for the first milestone.

<!-- DIAGRAM:memory -->

### Text is compact, with explicit sharing

- **UTF-8 by default.** UTF-16 is a database-creation option. Both preserve valid
  Unicode; malformed input is rejected before commit.
- **Plain by default.** Fields explicitly marked Dictionary share equal original text
  through one dictionary per database. There is no automatic policy switching.
- **Stable text references.** Indexes share protected text instead of copying it.
  Stored, prepared and index references are counted; reclamation waits for active users.
- **Schema-owned comparison.** Ordinal is the default; individual fields may use
  OrdinalIgnoreCase. Case-insensitive lookup still preserves the original spelling.

### Space is reused throughout the database lifetime

Size-class free lists find reusable slots without scanning all segments. Counters
identify sparse regions; background compaction moves bounded blocks under owner
protection. Empty segments enter a limited reserve or are released. Collection shrink
reclaims sustained excess capacity. No second full arena is needed.

Initial segment sizes, chunk sizes and work budgets are specified in the engine
contract. They are starting settings to measure, not unresolved architecture choices.
The PostgreSQL **256-byte saved unit is separate from native allocation sizes**.

<a id="reads-ownership-and-concurrency"></a>
<a id="queries-and-indexes"></a>

## Reads and indexes

**Ordinary reads return owned copies.** Later updates cannot change a value already
returned to application code. A separate short, synchronous protected read offers a
read-only arena view; it cannot escape, await, perform I/O or make nested database calls.
Keeping it short is the caller's obligation: while it is held, publication can be delayed.

One logical gate protects a root and all its owned blocks. A read is coherent for that
entity. Several reads, an enumeration or a graph walk do not create a shared snapshot.

| Access path | Selected mechanism |
| --- | --- |
| Primary key | Arc.Collections UnorderedMapSlim → stable root control entry |
| Declared secondary index | Arc.Collections OrderedMultiMap; single or composite keys |
| Application-defined traversal | Explicit, budgeted .NET enumeration |

The caller names the index. Equality, leading-key/range access and Limit/Offset are
supported; a named-index lookup never silently becomes a full scan. Every node builds
its indexes locally before readiness and maintains them with entity changes. Indexes
over the members of a collection, automatic index choice and keyset continuation are
not in this milestone.

**Unique is explicit.** The master validates the final state before effects commit.
A conflict rejects the whole operation or WriteBatch. Keys with any null component
are exempt. A batch may swap unique values or delete and reclaim one atomically.

<a id="writes-and-persistence"></a>

## Writes and durability

Applications edit their own copies, then submit changes. Save sends the whole entity
without automatic diff; partial updates are explicit. Expected revisions protect
against conflicting changes. Borrowed views never mutate database memory directly.

The public contract has no operation called Save. It names a complete write by its
condition: `Insert` requires an absent key, `Replace` an existing one, `Upsert` accepts
both. Saving several entities together in a WriteBatch ordinarily uses `Upsert`.

A write can also be refused before it executes, or wait a bounded time: when the master
cannot reserve memory within its budget, when the journal has reached its hard limit, or
while PostgreSQL is unavailable.

<!-- DIAGRAM:write -->

### A WriteBatch is one transaction

A bounded batch may change different entity types in the same database. It contains
at most one operation per type/key. The starting limits are 128 entities and 8 MiB per
batch, and 1 MiB per entity; they are configurable. Validation, durable effects and publication preserve
its all-or-nothing boundary; children preserve that same boundary during replay.

It is a transaction of the database, not one the application works inside: a batch
cannot read, and there is no interactive read/write transaction.

Batch construction holds no locks. Readers continue during storage I/O; publication
briefly closes access while the complete batch becomes visible. Internal grouping of
independent writes for PostgreSQL efficiency does not make those calls a shared transaction.

### The saved base advances in the background

Foreground writes append compact logical changes. A worker **inside the master process**
coalesces a bounded committed prefix into the saved base and commits its base cursor
with those updates. Foreground success does not wait for this maintenance step.

Recovery loads the independent binary base, replays the remaining journal and builds
indexes. Native compaction does not relocate persisted units. Journal size/age triggers,
hard limits and bounded workspaces prevent unbounded retention.

Operation IDs and retained outcomes support safe same-operation retry within the
configurable **15-minute default window**. An unknown commit is not treated as rollback.
If committed publication fails, the instance stops serving data until reopen/recovery.

## Replica lifecycle

Initial loading and live updates come from master memory over the internal connection.
Children that start together load in parallel, up to a configurable limit.
A new child receives the whole database, builds local indexes and catches up through a
fixed published position before opening reads.

<!-- DIAGRAM:replication -->

### A position carries the dependency between requests

Save through a child waits until that child has applied the committed operation.
The next request carries its history and position to any child. That child waits for
at least the supplied position, with a timeout, then executes the request. If the
child does not apply a committed write in time, the caller gets a timeout that carries
the committed position: the write is not rolled back and must not be sent again.

<!-- DIAGRAM:session -->

Waiting does not freeze the database or chase a moving latest position. A later update
may already be visible. Unconstrained reads can use intact local state during lag or
disconnection; status reports the observed progress and whether it is current or unknown.

The master retains a bounded shared delivery tail only while recipients need it.
Acknowledged payloads are released. A child that needs changes the master no longer
retains reloads fully, with one arena and bounded read waiting; an empty restarted child
cannot serve data offline.

<a id="deployment"></a>
<a id="control-and-maintenance"></a>

## Operations and milestone scope

The environment supplies one master and its URL. Lattice does not elect a replacement
or promote a child. One store has one master at a time, and the engine enforces it: a
master takes the leadership of its store when it opens, a second master on the same
store is refused while the first lives, and a master that lost the leadership commits
nothing more. The replication port is
protected by the deployment, not by the engine: it must be reachable only from the
nodes of the database. An application pod can embed a child; its open has a finite,
configurable timeout. The engine reports ready only
when its database state and indexes are complete; how a pod's probes use that is a
hosting decision.

- **Diagnostics:** DI services and host-mounted HTTP endpoints expose local status
  and can forward master-status requests through a child.
- **Backup and restore:** infrastructure uses PostgreSQL mechanisms. Restore into a
  separate target, verify it, then activate a new history; children reload fully.
- **Automatic maintenance:** bounded compaction, string reclamation, collection shrink
  and saved-base application are part of the engine.
- **Deferred:** OQL, joins, external transactions, cross-call graph snapshots, lazy
  schema conversion, manual maintenance commands and a management UI.
- **Not part of the design:** a partition layer, and election or promotion of a master.
  These were removed, not postponed.

Declared indexes, Unique indexes, collections, PostgreSQL durability and atomic
multi-type WriteBatch remain first-milestone requirements. Consumer integrations adapt
to these contracts.

## Release targets

The first reference dataset has **121,000 roots** and a deployment profile of **5–10
children**. These are acceptance targets, not measured production results.

| Scenario | Required target |
| --- | --- |
| Ordinary offered load | 10,000 local reads/s per child; 200 changes/s total through the master |
| Small-operation p99 | Local read ≤1 ms after synchronization; child-facing write ≤50 ms |
| New child ready | ≤10 seconds on the reference dataset under ordinary load |
| Retained storage memory | At least 25% below the equivalent optimized managed reference |
| Ordinary/startup node peak | ≤1.5× the defined steady-state node memory |
| Separate burst | 10,000 changes/s for 30 seconds; recovery within 60 seconds at ordinary load |

Implementation, integrated fault/concurrency checks and full-system measurements remain.
The physical baseline is specified; prototype measurements establish individual mechanisms,
not a completed production engine.

## Supporting documents

Follow the document that answers the question, rather than searching through this overview.

- [Engine implementation contract](milestone-1-engine-contract.md) — exact layouts,
  ownership, protocols, starting limits and required verification.
- [Accepted decisions](../decisions/README.md) — guarantees, rationale and alternatives.
- [Performance and evidence](performance-requirements.md) — target definitions;
  [prototype evidence](prototype-evidence.md) retains the historical measurements.
- [Planning handoff](planning-handoff.md) — scope and inputs for milestone/sprint planning.

Historical comparisons remain in the Markdown evidence and decision records. This page
shows the selected first-milestone architecture.
