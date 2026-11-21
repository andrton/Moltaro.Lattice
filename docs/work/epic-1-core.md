# Epic 1: Core

**Status:** planned and reviewed · 14 November 2026. The owner's answers to the questions
of the plan and of its reviews are recorded below. Seven independent reviews followed one
another until the last found nothing that blocks. The owner approved the plan by merging
it into `main`. The sprints are being carried out; [Status](#status) says where each one
stands.

## Goal

Replace the placeholder behind the public contract with the real database engine, as the
first production milestone: durable in PostgreSQL, replicated to 5–10 children, within
its memory and latency targets. It is for applications that keep a read-dominated working
set in memory on every node and need writes that are durable, ordered and atomic.

Today the contract (`LatticeDatabase`, `ILatticeDatabase`, sets, records, write batches,
the compiled schema) is served by a simple managed in-memory implementation. It supports
MemoryOnly mode, scalar fields and a master with its children inside one process. It is
not the engine, and nothing in this plan treats its behavior as proof of the engine.

## Scope

The scope is not decided in this document. The engine design was accepted on 14 November
2026; this plan orders it and gives every part an owner.

| Document | What the plan takes from it |
| --- | --- |
| [Planning handoff](../design/planning-handoff.md) | Required behavior, what is outside the milestone, the production gates |
| [Milestone 1 engine contract](../design/milestone-1-engine-contract.md) | Component owners E01–E08, physical layouts, limits, the reference fixture, the verification table |
| [Decision register](../design/questions.md) and [decision records](../decisions/README.md) | What was agreed and why |
| [Performance requirements](../design/performance-requirements.md), [prototype evidence](../design/prototype-evidence.md) | The targets, and what the experiments did and did not prove |

**Included.** One database per instance; a master that is embedded or hosted; Durable
persistence in PostgreSQL and explicit MemoryOnly; MemoryOnly children over TCP. The
accepted schema and type model with owned trees, List, Map, Set, Binary and references by
key. Typed access and access by name, owned copies and protected read-only views. Native
storage with bounded allocation, compaction and explicit memory-pressure outcomes. Primary
and declared secondary indexes, unique indexes, index search. Complete writes, explicit
partial updates, expected revisions, the atomic write batch. Operation identities and safe
retry. Journal, saved base, recovery, restore into a new history. Session consistency.
Explicit eager schema migration. Diagnostics. Every release check of the
[verification table](../design/milestone-1-engine-contract.md#verification-handoff-and-closure).

**Outside this milestone.** A query language, its parser and planner; joins; automatic
index choice; indexes on collection members; snapshots across calls; external
transactions; lazy conversion of mixed schemas; generic continuation by the last key;
manual maintenance commands, CLI or GUI; a message broker. A partition layer and the
election or promotion of a master are not part of the design at all. Nothing else is left
out: an accepted feature that is hard is scheduled, not deferred.

**Not part of this epic.** The stores of [Epic 2](epic-2-identity-storage.md). That package
adapts to the engine and does not set its scope. Its skipped tests, including Microsoft's
specification tests, become a check of the finished engine when Epic 2 runs.

### Where the contract stands today

The state after the last closed sprint; the coordinator keeps it current.

**In the contract and working.** Opening and closing; master and child;
MemoryOnly. The compiled schema: the closed JSON descriptor with identities of entity
types, fields, indexes, enum types and owned types, scalar fields, enum and Flags types,
text policies and the string encoding, owned values, Lists, Maps, Sets and references,
index parts that are paths into owned values; binary save and load with a digest; both
fingerprints; the optional .NET class, bound by one table for every position, with a .NET
enum that has to agree with its enum type. Records and owned values that hold nested
values and copy what is set into them. Sets by class and by name, for
entity types of scalar and Enum fields, under every key type, both text comparisons, UTF-8
and UTF-16, a String stored Plain or as Dictionary: read, enumerate, insert,
replace, upsert, delete, expected revisions. Search through a declared secondary index
by its full key, by its leading fields and with a range, in either direction and in pages
with Offset and Limit, within the read limits, by class and by name, on a master and on a
child (S15, part 1). Positions and sessions; a write through a
child completes when that child has applied it; waiting for a position with a timeout.

**How the engine does it.** Since S12 `LatticeDatabase` lives in the engine assembly and
opens the engine; the placeholder is deleted. Mechanisms of `Moltaro.Lattice.Engine`: the
arena of native memory — segments, size classes, regions, the block header, relative
addresses, the reserve, the memory limit and its counters — and the table of root entries
with generations (S04); the root layout compiled from an entity type of scalar fields,
with the writer and the reader of a root — fixed fields at their declared width, String
and Binary values inline, in a block of their own or in chunks (S05). The measurement
program `Moltaro.Lattice.Measurements` holds the reference fixture, its first scenario
and the development manifest (S05). The engine database: a MemoryOnly master that
inserts, reads, replaces, upserts and deletes entities of scalar fields by primary key,
by class and by name, with revisions, positions and sessions; layouts compiled at open;
the lookup claim and the protection of a root, with no code of the application inside
a gate; reads that allocate the returned copy and nothing else. The scenario tests of
reads and writes run on it (S06; through `LatticeDatabase` since S12). By the owner's decisions of 15 November 2026 an entity, a write
batch and the queue of writes have no limit of their own, and the validation of the core
refuses text that is not well-formed on both implementations. From S07 the engine
stores every key type of the catalog — the eight integers, String with either
comparison, Guid —, a String field compared without case, Enum fields and a schema in
UTF-16, through one comparison and hash service of the schema; the validation of the
core checks enum values, and a write batch takes one operation per entity as the schema
compares keys. Text compared without case is compared by a case table of Lattice's own
in the core, the simple uppercase mapping of Unicode 16.0.0 in version 1 of the table,
and not by the runtime: keys, Map keys, Set members and the identity of a batch read it
through one rule. From S08, part 1, a read of the engine is protected by an epoch and
takes no lock; what a write replaces or removes is retired and freed once the reads that
could reach it have left; the primary index is a hash table of the engine's own that is
read while a write is published; the package `Arc.Collections`, referenced by the engine
and used by none of its code, was removed in S13 by the owner's decision of 18 November
2026, and the secondary indexes of S14 are a structure of its own. Since S13 a String field
with Dictionary storage is stored once for the database: a text entry of the arena that
holds the exact bytes, the root holding its relative address, one count of the blocks
that refer to it changed only under the lock of the writes, and a table by content that
only writes use; a read of such a field costs what a read of a Plain value out of line
costs, and a child builds its own entries from the bytes it receives. From S08, part 2,
an enumeration of the engine goes on while entities change and the index grows: it
remembers the keys it has returned, returns no identity twice and every entity nobody
changed, and holds nothing between two steps; the options `ReadMemoryLimit` and
`ReadTimeLimit`, none set by default, bound the memory and the time of a pass, and no
call carries limits of its own; every hash of the primary index uses a seed the instance
draws when it opens. Since S09, part 2 a write of the engine is prepared and published
under one lock of the writes, one write at a time, and handed to a storage step in
groups of at most 32 writes or 8 MiB; in MemoryOnly that step completes at once and the
call publishes in the same hold. Writes to one entity are executed in the order of their
calls; a write is withdrawn by its token until it is dispatched; `MemoryLimit` is the
limit of the arena, and a write is refused for memory only when it does not fit beside
the stored data; a fault of a publication leaves the instance `Faulted`. The close of a
database is one operation that every later call joins; the token of an asynchronous read
is honored in every wait of its step; a key is given by name without a box. From S10 a master of the engine listens on its `ListenUri` and a child of the engine
connects over TCP, loads the schema and every entity with its revision, and becomes Ready;
a frame carries a CRC32C and no address travels; `OpenTimeout` bounds reaching the master
and the time without a frame. From S11 the master keeps writing while children
load and follow: a child ends its load at a fixed position of the master and applies the
master's changes in order, each whole, at the master's positions and revisions; the
master releases a change when every child has applied it, and until S33 nothing bounds
what it keeps for a child that falls behind. From S12 a child forwards its writes to its
master on its one connection: the child validates and encodes the root, the master checks
it as a child checks a root of a load and executes it, and the reply reaches the child
before the transaction of that write; the call returns when the child has applied it. A
master that closes ends its connections in order and says so with the frame `Closed`, so
a write it did not execute ends in `Unavailable` and only a connection lost without that
frame leaves a sent write `IndeterminateOutcome`. A wait for a position wakes only when a
publication reaches it. A child that lost its master serves reads at its position and
refuses writes; it does not connect again until S34. From S14 every declared secondary
index is a copy-on-write B+tree of the engine's own in the arena: its published nodes
never change and a reader takes no lock; the entries of a write are installed in the hold
that publishes its root, the upkeep of every index of a type in one place
(`ChangePublisher`); a search by the full key returns an entity only if its current state
has the key. A text key of 64 bytes or less is copied into the entry whatever the storage
mode of its field, a longer one references the text entry of its root, and a field that
is part of an index stores a value longer than 64 bytes as a text entry. A child builds
its indexes while it loads and keeps them while it follows. A search remembers the
entities it returned or passed over, so an entity is returned at most once, also across a
delete and a re-creation and when its key moves ahead of the walk.

**In the contract, not implemented.** Entry points refuse with `NotSupportedException`;
tests are written and skipped; samples are written.

| Feature | Contract | Tests waiting | Sprint |
| --- | --- | --- | --- |
| Unique indexes | `"Unique": true`, `LatticeIndex.IsUnique`, `UniqueConstraintViolation` | `UniqueIndexTests` | S16 |
| Atomic write batch across entity types | `ILatticeWriteBatch` | `WriteBatchTests` | S17 |
| Durable mode | `LatticePersistenceModeEnum.Durable`, the default | None; one test asserts the refusal | S27–S32 |
| Storing owned values, Lists, Maps, Sets and references, and reading them into a class | The declarations of S03; the schema compiles, records and classes hold the values, a write of such an entity type is validated and then refused | `ValueWriteTests` and `ValidationBeforeRefusalTests` assert the refusal | S18, S19 |

**Accepted for the milestone, not in the contract yet.** Each gets its contract, sample
and tests in the sprint named.

| Feature | Sprint |
| --- | --- |
| The memory budget as an option, the budgets of a read as options that are not set by default; resource errors. An entity, a batch and the queue of writes have no limit of their own (decisions O18, O19 and O20) | S08, S09, S17, S24 |
| Explicit partial updates | S20 |
| Protected read-only views | S21–S22 |
| Operation identities, retained outcomes and safe retry | S26 |
| The storage provider as an object of its own package | S27 |
| Activation of a restored database | S36 |
| Schema migration | S37–S38 |
| Diagnostics | S39 |

## Requirements

Levels are those of the [work README](README.md). In this epic **Release** does not mean
"later": a Release requirement is part of the acceptance of this epic. The level only says
that the sprints before it can be developed and tested without it.

"Today" is the state on 14 November 2026: **works** in the placeholder, **contract** when
the entry points exist and refuse, **absent** when not even the contract exists.

| # | Requirement | Decided in | Level | Today | Sprints |
| --- | --- | --- | --- | --- | --- |
| R1 | One database per instance, no partitions. Embedded and hosted masters use the same engine. Many callers submit writes; only the configured master executes them | [Q11, Q15](../decisions/replication-and-hosting.md), [Q12](../decisions/database-scope.md) | Must | Works | S06–S12 |
| R2 | The public library keeps no package dependency. The PostgreSQL driver has one owning assembly, and dependencies point one way. The engine references no package since S13: `Arc.Collections` was removed by the owner's decision of 18 November 2026 | [Handoff](../design/planning-handoff.md#planning-obligations) | Must | Holds | S01, S13, S27 |
| R3 | The schema catalog owns stable positive identities of entity types, fields and indexes, a catalog version and deterministic layout fingerprints; one compiler serves every input | [Engine contract](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration), [Q03.9](../decisions/schema-and-types.md) | Must | Partly: indexes have no identity, no fingerprint | S02 |
| R4 | The accepted scalar catalog and its rules: eight integers, Float32, Float64, Decimal, Boolean, String, Guid, the temporal types, Binary; enum and Flags; explicit nullability; String comparison Ordinal or OrdinalIgnoreCase per field | [Q03.1–Q03.8](../decisions/schema-and-types.md), [Q04](../decisions/strings.md) | Must | Field types work; enums and text policies absent | S02, S05, S06–S08 |
| R5 | Owned entity trees, ordered Lists, Maps, Sets and references by key without integrity enforcement, with the accepted input, null and comparer rules | [Q02, Q02.1, Q02.2](../decisions/document-model.md), [Q03.3, Q03.4.2](../decisions/schema-and-types.md) | Must | Absent | S03, S18, S19 |
| R6 | Every data operation exists for entity classes and for records by name, with the same rules | [Q06.3](../decisions/memory-and-access.md) | Must | Works for scalar fields | Every sprint that adds an operation |
| R7 | Ordinary reads return owned copies, coherent for one entity | [Q06, Q06.1](../decisions/memory-and-access.md) | Must | Works | S06–S08 |
| R8 | Short synchronous protected read-only views: no copy, no escape, no nested database call | [Q06.2, Q06.3](../decisions/memory-and-access.md) | Must | Absent | S21–S22 |
| R9 | Native segments, size classes, relative addressing, stable managed root entries, large values in bounded chunks | [Q05](../decisions/memory-and-access.md), [Q16](../decisions/physical-storage.md) | Must | Absent | S04, S05 |
| R10 | Text: Plain and Dictionary storage, the database encoding, stable text entries, safe sharing and reclamation, well-formed Unicode only | [Q04.1–Q04.5](../decisions/strings.md) | Must | Works for String fields; text in collections and index keys with their sprints | S05, S13 |
| R11 | Space is reused for the life of the database: size-class reuse, bounded incremental compaction, collection shrink, release of empty segments | [Q17](../decisions/allocation-and-compaction.md) | Must | Absent | S04, S23–S24 |
| R12 | Memory is bounded and its exhaustion is explicit: reservations before commit, refusal instead of unbounded waiting, a child pauses application and keeps its reads; an entity has no cap of its own (decision O19) | [Q14](../decisions/write-admission.md) | Must | Absent | S09, S23–S24, S33–S34 |
| R13 | Index structures of the engine's own — a hash table for the primary index (S08), an ordered structure for secondary indexes chosen in S14 after its study (the selection of the Arc collections is superseded, 18 November 2026) — rebuilt locally on every node | [Index structures](../decisions/index-structures.md), [Q19](../decisions/index-lifecycle.md) | Must | Absent | S06–S08, S14–S15 |
| R14 | One comparison and binding service for indexes, collections and lookups: the numeric allowlist, NaN and zero, null order, enum order, both text encodings | [Q07.3.6](../decisions/queries.md), [engine contract](../design/milestone-1-engine-contract.md#index-preparation-and-traversal) | Must | Absent | S06–S08, S14–S15, S19 |
| R15 | Search through a named index: full-key equality, leading fields with a range, both directions, Limit and Offset, budgets; never a silent scan | [Index access](../decisions/index-access.md), [Q07.1.1, Q07.3.x](../decisions/queries.md) | Must | Done: the full key in S14, leading fields, ranges, both directions, Offset, Limit and the read limits in S15, part 1; the binding rules wait for S15, part 2 | S14–S15 |
| R16 | Enumeration and index traversal over live data: coherent entities, no identity twice in one pass, unchanged matches included, a budget on time and memory | [Q07.1, Q07.3.5](../decisions/queries.md) | Must | Done: an enumeration and a traversal of an index go on while entities change, with the budget of the options (S15, part 1) | S06–S08, S14–S15 |
| R17 | Unique indexes on one field or several, validated against the final state on the master; keys with a null are exempt; swaps and delete-and-reclaim inside a batch | [Q07.4](../decisions/unique-indexes.md) | Must | Contract; `UniqueIndexTests` skipped | S16, S17 |
| R18 | Complete writes: Insert, Replace, Upsert, Delete; expected revisions; immutable keys; a complete write is never turned into a diff; inputs stay stable until completion | [Q13](../decisions/mutation-commands.md) | Must | Works | S06–S08 |
| R19 | Explicit partial updates: scalar assignments, replacement of a whole collection field and the accepted List, Map and Set element commands, in order, atomic for one entity | [Q13](../decisions/mutation-commands.md) | Must | Absent | S20 |
| R20 | The atomic write batch: several entity types, one operation per type and key, one position, bounded, refused as a whole | [Q01](../decisions/document-model.md), [Q13](../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch) | Must | Contract; `WriteBatchTests` skipped | S17 |
| R21 | Write coordination: preparation of one write at a time (as amended on 17 November 2026), order per entity, ordered bounded commit and publication, resources prepared before effects commit, cancellation only before dispatch, a failed publication stops the instance | [Q09.1](../decisions/write-acknowledgment.md), [Q13](../decisions/mutation-commands.md#cancellation-at-the-commit-boundary) | Must | Absent | S09 |
| R22 | Workload limits are options, validated together when the instance opens | [Engine contract](../design/milestone-1-engine-contract.md#initial-bounded-settings) | Must | Only the synchronization timeout | S06–S08, S09, S10–S11, S17, S23–S24, S26–S34, S39 |
| R23 | Operation identities: a token kept before submission, a retained outcome, safe repetition for 15 minutes by default, never a second execution | [Q13](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1), [retry protocol](../design/operation-retry-protocol.md) | Must | Absent | S26, S28–S29, S31 |
| R24 | Durable is the default mode and MemoryOnly is explicit; the mode is chosen when the instance opens; children are MemoryOnly and never connect to PostgreSQL | [Q09.5](../decisions/persistence-modes.md), [Q09.6](../decisions/persistence-backends.md) | Must | Options exist; Durable refuses | S27 |
| R25 | A Durable write succeeds after its durable admission, its PostgreSQL commit and its coherent publication; an uncertain commit is reported as uncertain | [Q09.1](../decisions/write-acknowledgment.md), [Q09.2](../decisions/checkpoint-and-journal.md) | Release | Absent | S28–S29 |
| R26 | An independent saved base in 256-byte units advanced in the background with coalescing; base and cursor commit together; journal and receipts are bounded when the application sets a limit, and shrink back after load in any case | [Q09.2](../decisions/checkpoint-and-journal.md) | Release | Absent | S30–S31 |
| R27 | Recovery: an offline reopen rebuilds one arena and its indexes from the base and the committed tail; nothing partial is replayed; damaged or incompatible data is refused | [Q09.2](../decisions/checkpoint-and-journal.md), [Q16](../decisions/physical-storage.md) | Release | Absent | S30–S31, S32 |
| R28 | A child loads the whole database from the master's memory, rebuilds its indexes and becomes ready at a fixed position; changes then arrive in order as complete transactions. Children that start together load in parallel, up to a configurable limit | [Q08, Q08.1.1](../decisions/replication-and-hosting.md) | Must | Works over TCP; no limit of captures yet | S10–S11, S33 |
| R29 | A write through a child succeeds after the master completed it and that child applied it. Related requests carry a minimum position and wait for it with a timeout on any node | [Session consistency](../decisions/session-consistency.md) | Must | Works over TCP, in one process or several; no reconnection yet | S12, S34 |
| R30 | Delivery history in RAM is bounded and released after acknowledgment; a child resumes from it or reloads into one arena; reads wait within a bound; a foreign history is refused; local reads continue while disconnected | [Q08.1.2, Q08.1.3](../decisions/replication-and-hosting.md) | Release | Absent | S33–S34, S35 |
| R31 | Backup and restore are run by infrastructure. A restored database is validated offline and activated with a new history; children reload | [Q09.4](../decisions/backup-and-restore.md) | Release | Absent | S36 |
| R32 | Explicit eager schema migration with the affected data paused; stable identities; resumable before activation | [Q03, Q03.9](../decisions/schema-and-types.md) | Must | Absent | S37–S38 |
| R33 | Diagnostics through dependency injection and host-mounted HTTP endpoints: local status and the master's status asked through a child. Maintenance is automatic; there are no manual commands | [Q18](../design/storage-and-maintenance.md#control-and-maintenance-tools-q18) | Must | Absent | S39 |
| R34 | The accepted performance and memory targets are met on the reference fixture, with the measurements recorded | [Q10.1](../design/performance-requirements.md) | Release | Not measured | S25, S40, S41 |
| R35 | One store has one master at a time, enforced by the engine: a master takes the leadership of its store at open, a second master on the same store is refused while the first lives, and a master that lost the leadership commits nothing more. Exclusion only; nothing is elected | Owner, 14 November 2026: [decision O9](#decisions-of-the-owner-14-november-2026), [Q15 record](../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection) | Release | Absent | S27, S28–S29, S30–S31, S36, S37 |

## Acceptance

The epic is done when all of the following hold.

**Behavior.**

1. Every requirement above is delivered. No entry point of an accepted feature throws
   `NotSupportedException`, and no test of `Moltaro.Lattice.Tests` is skipped.
2. The tests that were written ahead — the skipped tests of `IndexTests`,
   `UniqueIndexTests` and `WriteBatchTests` — pass with their assertions unchanged. So do
   the tests written in each sprint of this plan. One assertion of an older test changes,
   by the owner's decision of 15 November 2026 on the position of a write that changes
   nothing: the brief of S12 names it. The four tests that assert a refusal,
   such as `Durable_persistence_is_not_implemented_yet`, are gone.
3. The placeholder is gone. The samples run to the end against the engine, and the code in
   `README.md` compiles.
4. Nothing that Epic 2 needs from the core is missing: its R1, R2, R7 and R13.

**Release checks.** Each row of the
[verification table](../design/milestone-1-engine-contract.md#verification-handoff-and-closure)
is closed by one sprint; the sprints listed as contributing deliver their part earlier.

| Area | Evidence required | Closed in | Contributing |
| --- | --- | --- | --- |
| Q07.3.6 Binding and comparison | Every scalar and key family, overflow, null, NaN, Unicode and composite ranges agree between key lookup, index search, typed access and access by name | S20 | S07, S14, S15, S19 |
| Q08.1.3 Replication | A real topology of 5–10 processes; a child loads while single writes and batches continue; gaps, duplicates, disconnection and a change of history; bounded buffers | S35 | S10–S11, S12, S33–S34 |
| Q09.2 Base and journal | Foreground, applier, cleanup and reopen interrupted at every boundary; operation expiry; bounded storage; the recovered state equals the acknowledged one | S32 | S28–S29, S30–S31 |
| Q09.4 Restore | Restore into an isolated target; a damaged or incompatible target is refused; children reload on the new history | S36 | S27 |
| Q10.1 Targets | The same data in an optimized managed reference; the ordinary and the burst profile; no prototype timing reused | S41 | S25, S40 |
| Q13 Writes and retry | Typed, by-name and remote submission agree; cancellation, an unknown outcome and a repeat never apply an effect twice | S35 | S20, S26, S28–S29 |
| Q14 Bounded resources | Each budget exhausted in a test; protected readers drain; nothing queued runs after its cancellation; no unbounded wait | S39 | S08, S09, S10–S11, S14–S15, S17, S21–S22, S24, S26, S29, S30–S31, S32, S33–S34 |
| Q16 Layout and formats | Binary round trips for every type; malformed input refused before use; ownership and reference counts audited | S37 | S05, S10–S11, S13, S18, S19, S30–S31, S32 |
| Q17 Lifetime and compaction | Churn with a constant live set; delete and reinsert; text and index entries moved; no retained growth that nothing controls | S23 | S04, S13 |

**Numeric gates.** The targets are those of the
[performance requirements](../design/performance-requirements.md), on the
[reference fixture](../design/milestone-1-engine-contract.md#reproducible-first-fixture):
121,000 roots — 100,000 users, 10,000 groups, 1,000 roles, 10,000 settings — generated
with seed 42.

| # | Gate | Target | Measured in | Early figure in |
| --- | --- | --- | --- | --- |
| G1 | Retained storage memory | At most 75% of the optimized managed reference with the same data, indexes and metadata, after load and after churn | S25, and again in S41 on the final build | — |
| G2 | Small local read | p99 at most 1 ms for reads by identifier and by exact index value, at 10,000 reads/s per ready child, required position already applied, while 200 changes/s arrive. Range reads are reported as their own class | S40 | S25 on one node |
| G3 | Small write through a child | p99 at most 50 ms for single-entity writes, from the call to application on the calling child, durable commit and admission included, at 200 logical changes/s in total. Batches are reported as their own class | S40 | S12 through a child in MemoryOnly; S28 on a Durable master alone |
| G4 | Readiness of a new child | At most 10 s from its open call to Ready, under the ordinary load, the other children reading; 5 runs, each within the target | S40 | S35 |
| G5 | Peak memory of a node | At most 1.5 times its steady ready-state memory, for the master and each child, during ordinary mutations with compaction and during the start of one child | S40 | S23 on one node |
| G6 | Burst | 10,000 changes/s offered for 30 s, then the ordinary profile restored within 60 s at 200 changes/s; every submitted operation resolved as completed, refused or uncertain; no accepted write lost | S41 | — |
| G7 | Deployment profile | Every gate above holds with 5 children and with 10 | S40, S41 | S35 for correctness |

Rules for every measurement:

- Gates are judged on the reference machine, the owner's workstation. The master, every
  child and PostgreSQL run as separate containers with declared limits of CPU and memory.
- A run has a manifest written before it starts and approved by the owner: hardware,
  operating system and virtualization, .NET and PostgreSQL versions, the limits of every
  container, network placement, concurrency, warm-up and duration. A result without its
  manifest is not evidence.
- Before a gate run the program checks the machine: enough free memory for the sum of the
  limits, a quiet processor for a stated time, no foreign containers. When the check
  fails the run does not start; there is no way to measure anyway.
- Offered, admitted, completed, refused and uncertain operations are reported separately,
  with p50, p95, p99 and the maximum. Slow or failed operations stay in the denominator.
- Component measurements are reported next to the totals. A prototype timing is never
  copied into a result.
- An early figure that already misses its gate is reported to the owner in that sprint.
  The sprint does not close on it silently.
- No measurement runs in the default test run, and none is launched to write or review
  this plan.
- A figure a brief calls *asserted* — a size, an allocation count — is a test of the
  default lane and fails the build when it does not hold.
- A figure a brief calls *reported* is produced by a named scenario of the measurement
  program and entered in [measured results](#measured-results) with the manifest it ran
  under. Manifests are committed files of the measurement project; raw output is not
  committed.
- There are four manifests. The development manifest, written in S05, names
  the machine, the operating system and the versions; the figures of the sprints up to
  the gates run under it without containers, are for comparison between sprints only,
  and need no approval. The single-node manifest of S25 and the topology manifest of S40
  govern the gates. The burst has a manifest of its own, written in S41, with its resource
  budget and its baseline window. The owner approves the single-node, the topology and the burst manifest, each before the
  first run it governs.
- A manifest is not changed after a result was seen. A new one needs a new approval, and
  the results under the old one stay in the table.

**Added by the planner on 19 November 2026,** after the first start of a rented machine,
which grew from four planned hours to more than seven by additions:

- Everything for one start of a rented machine is one queue that runs on the machine
  itself; nothing waits for a session on the workstation.
- A start has a plan written before it: the series, their order, the expected length and
  what each decides. A series added during a start needs a reason the planner agrees to.
  The plan names what each line really runs: the scenario, the builds and the rounds.
- On a machine of N cores the judged case of many readers uses at most N/2 readers.
- A suspected fall is confirmed only by a series with at least three builds rotating,
  among them a second build of one of its commits as the gauge, and is called only where
  three rounds agree against the gauge; the lines of one run are not independent
  evidence. A fall of 5 to 7 % reported from two builds and three rounds did not exist.
- A start comes after the review and the breaker of its sprint are closed.
- A single run on the workstation selects nothing: it gives a direction and a listing,
  and a choice between builds is made by a series.
- Figures from a rented machine are a new series and are never compared in absolute
  terms with those of the workstation.
- Nothing about the machine goes into the repository beyond "a rented ARM64 machine, 16
  cores". How it is reached, and the raw outputs of every start, are kept in a note
  outside the repository, which the coordinator keeps and the planner knows of. The owner
  is asked only to start the machine and to stop it; the coordinator reaches it by that
  note and tells him at once when a queue has ended.
- The output of every repeat of the test suite is kept, so that a failure is never
  without a name.

### Measured results

Filled by the coordinator when a sprint closes. S42 moves the table into the product
documentation before this document is removed.

| Sprint | Figure | Value | Manifest | Date |
| --- | --- | --- | --- | --- |
| S05 | Bytes retained per user of the scalar form (100,000 users; live bytes and class slack of the arena), against 66 bytes of logical field sizes | 176 bytes, 2.67 times the logical size: 162 live and 14 slack; the fixed part of the root is 105 bytes | development | 15 November 2026 |
| S05 | Bytes retained per setting of the scalar form (10,000 settings), against 160 bytes of logical field sizes | 288 bytes, 1.80 times the logical size: 288 live and no slack; the fixed part of the root is 96 bytes | development | 15 November 2026 |
| S05 | Native capacity obtained per entity, with what no counter shows (Q31) | 209.72 bytes per user (5 segments), 419.43 per setting (1 segment) | development | 15 November 2026 |
| S06 | Read by key of one of 100,000 users of the scalar form, by class, one thread; 1,000,000 reads after 100,000 of warm-up, three runs | p50 400 to 600 ns, p99 2,000 to 3,300 ns alone; p50 600 to 1,100 ns, p99 1,300 to 5,400 ns with one writer replacing entities | development-2 | 15 November 2026 |
| S06 | The same read by name | p50 1,000 to 1,200 ns, p99 2,600 to 4,100 ns alone; p50 1,400 to 1,500 ns, p99 3,500 to 4,900 ns with one writer | development-2 | 15 November 2026 |
| S07 | Read by a String key of one of 10,000 settings of the scalar form, by name, one thread; 1,000,000 reads, three runs | Ordinal: p50 400 to 500 ns, p99 1,600 to 2,000 ns. Without case, by the case table, in the stored spelling and in upper case: p50 400 to 500 ns, p99 1,600 to 2,100 ns; with the comparison of .NET before the table, p50 400 to 500 ns, p99 1,600 to 1,900 ns | development-2 | 15 November 2026 |
| S08, part 1 | Reads by key in total, by class, of 100,000 users, without a writer; windows of 2 s, three runs | 1 thread 2.0 to 2.3 M/s, 2: 4.4 to 5.0, 4: 8.4 to 9.3, 8: 14.2 to 14.7, 16: 16.2 to 17.6 (one disturbed run left out); on the protection of S06 1.9, 2.6, 2.4, 2.1, 2.1 | development-3 | 16 November 2026 |
| S08, part 1 | Writes a second of one writing thread beside 20 readers of its own database, medians of five runs | replaces 408 k/s (S06: 757 k/s); inserts and deletes 640 k/s (S06: 19 k/s); inserts with growth 421 k/s (S06: 8 k/s); reads beside them 14.2 to 14.7 M/s in total (S06: 1.7 to 1.8) | development-3 | 16 November 2026 |
| S08, part 1 | The same writes beside N readers of its own database, divided by those beside N readers of another database of the process; N of 1, 2, 4, 8, 16, 19; three runs | 0.72 to 1.42 in two runs; in the third, a disturbed one, one point at 0.68; the median of every point at least 0.74 | development-3 | 16 November 2026 |
| S08, part 1 | Bytes of the primary index per entry at 100,000 and at 1,000,000 entities | 22.3 and 17.8, for an Int64 key, a composite Int64 key and a String key alike; the map of the package took 36.7 and 29.4 for Int64, 47.2 and 37.7 for String | development-3 | 16 November 2026 |
| S08, part 1 | The insert that grows the primary index from 131,072 entries; an ordinary insert takes 0.2 to 0.5 µs | 1.3 to 1.9 ms, median 1.56 ms, in the implementer's runs; 2.3 to 8.0 ms on a loaded machine in the review | development-3 | 16 November 2026 |
| S08, part 2 | One pass of an enumeration over 100,000 users, by class, without a writer and without limits; median of a series, three runs | 41 to 53 ms on a machine under other load; 43 to 49 ms in part 1, where a pass remembered nothing and failed when the index changed | development-3 | 16 November 2026 |
| S08, part 2 | The same pass beside a writing thread, and with the index growing once during it (about 132,000 entities) | beside replaces 59 to 60 ms; beside inserts and deletes 59 to 61 ms; with a growth 88 to 90 ms in the implementer's first runs, 195 to 240 ms on a loaded machine later; in part 1 the two last failed | development-3 | 16 November 2026 |
| S08, part 2 | Memory a pass holds per key it has returned, at 100,000 keys | 21.0 bytes for an Int64 key, 41.9 for a Guid, 68.2 for a String of 13 characters; between half and all of that over one doubling of the table | development-3 | 16 November 2026 |
| S08, part 2 | Reads by key in total, by class, without a writer, the branch against main in alternating runs | 16 threads 14.7 to 15.6 M/s against 14.9; 20 threads 14.9 to 16.2 against 15.6 to 17.1: no difference | development-3 | 16 November 2026 |
| S09 | Completed writes a second of 1, 4 and 16 calling threads, replaces; three alternating runs against S08 | 1.19 to 1.27 M/s, 0.43 to 0.63 M/s, 0.58 to 0.67 M/s; on S08 1.79 to 1.91 M/s, 1.17 to 1.25 M/s, 1.08 to 1.19 M/s | development-3 | 17 November 2026 |
| S09 | The same for inserts and deletes | 1.45 to 1.74 M/s, 0.36 to 0.74 M/s, 0.62 to 0.71 M/s; on S08 3.10 to 3.30 M/s, 2.10 to 2.26 M/s, 2.06 to 2.18 M/s | development-3 | 17 November 2026 |
| S09 | p99 of one write with 16 calling threads; bytes of managed memory allocated per write | 60 to 74 µs against 320 to 700 µs on S08 (1.6 to 2.7 ms beside 16 readers in the review); 160 bytes a replace and 136 an insert or delete for one caller, as on S08, and 350 to 362 with 4 callers, 538 to 543 with 16 | development-3 | 17 November 2026 |
| S09 | Reads by key in total on 16 threads, alone and beside 1, 4 and 16 writing callers | 19.2 M/s alone against 18.1 to 18.4 on S08; 15.9 to 17.3, 14.7 to 15.5 and 13.8 to 14.7 beside the callers, against 16.9 to 17.9, 16.1 to 17.4 and 15.8 to 16.5 on S08; readers of another database of the process fall alike | development-3 | 17 November 2026 |
| S09, part 2 | Completed writes a second of 1, 4 and 16 calling threads, replaces; alternating rounds against S08 and S09, the rounds in which the machine ran at its ordinary speed | 1.59 to 1.63 M/s, 1.04 to 1.10 M/s, 0.91 to 1.03 M/s; on S08 1.86 to 1.92, 1.25 to 1.32, 1.16 to 1.23 M/s; on S09 1.21 to 1.29, 0.38 to 0.67, 0.57 to 0.64 M/s | development-3 | 17 November 2026 |
| S09, part 2 | The same for inserts and deletes | 2.35 to 2.58 M/s, 1.75 to 1.81 M/s, 1.54 to 1.80 M/s; on S08 3.01 to 3.04, 2.05 to 2.22, 1.95 to 2.21 M/s; on S09 1.72 to 1.79, 0.30 to 0.69, 0.60 to 0.68 M/s | development-3 | 17 November 2026 |
| S09, part 2 | p99 of one write for 1, 4 and 16 calling threads, replaces, alone; bytes of managed memory allocated per write | 1.3 µs, 94 to 99 µs, 0.95 to 1.0 ms (S08: 1.3 µs, 75 to 83 µs, 0.59 to 0.71 ms); 160 bytes a replace and 124 an insert or delete for any number of callers (S08: 160 and 136; S09: up to 546) | development-3 | 17 November 2026 |
| S09, part 2 | Reads by key in total on 16 threads, alone and beside 16 callers that replace | 17.5 to 19.6 M/s alone; 14.9 to 15.7 M/s beside the callers, against 15.1 to 15.3 on S08 and 13.9 to 14.4 on S09 | development-3 | 17 November 2026 |
| S09, part 2 | One write of an entity of 32 MiB: how long it holds the lock of the writes | p50 2.6 to 2.7 ms, p99 5 to 7 ms | development-3 | 17 November 2026 |
| S09, part 2 | Small writes of one caller beside a caller that writes entities of 32 MiB one after another | 1,500 to 4,300 writes/s, p99 from microseconds to 16 ms, the longest wait 100 to 112 ms; without the give-way of a large write 900 to 1,600 writes/s, p99 up to 64 ms; on S08 0.57 to 0.91 M/s with a longest wait of 3 to 7 ms, on S09 1.24 to 1.44 M/s with 0.2 to 0.4 ms | development-3 | 17 November 2026 |
| S10 | Time from the open of a child to Ready, 100,000 users of the scalar form, master and child in one process over loopback; five loads | 58 to 301 ms, median 184 ms; the first loads include the compilation of the code | development-4 | 17 November 2026 |
| S10 | Bytes of one such load on the wire, next to the stored bytes | 15,410,260 bytes from the master and 108 from the child: 154.1 bytes per entity, against 162 live and 176 retained per entity in the arena of the master; the child stores what the master stores | development-4 | 17 November 2026 |
| S10 | Memory of one such load | an arena of 20 MiB on each side; 7.8 MiB of managed memory allocated during a load, 7.5 MiB held after it (the index and the root entries); the working set of the process 128 MiB before the first child and 166 MiB after the fifth | development-4 | 17 November 2026 |
| S10 | Reads by key and writes of a master without a child, the branch against its base in alternating rounds | reads on 20 threads 17.2 to 17.6 M/s against 14.9 to 16.3; one caller replacing 1.38 to 1.48 M/s against 1.49 to 1.53; 16 callers inserting and deleting 1.58 to 1.70 M/s against 1.62 to 1.65: within the spread of the rounds | development-3 | 17 November 2026 |
| S11 | Time from the open of a child to Ready, 100,000 users, while the master takes 200 writes a second and 16 threads read it; children in the master's process; four runs (measured before the tail of blocks) | one child 440 to 580 ms, two at once 530 to 703 ms; 78 to 128 transactions in (S, R], `LoadEnd` to Ready 0.1 to 4.5 ms (26.1 ms once), 0 to 5.1 % of the states skipped, peak of `PendingDeliveryBytes` 14.4 to 22.6 KB; following for 2 s the children apply 198 to 200 transactions a second, as the master writes | development-4 | 18 November 2026 |
| S11 | The same load at the full rate of one caller | Ready in 0.96 to 1.12 s, one child or two; 40.5 to 51.3 thousand transactions in (S, R], `LoadEnd` to Ready 300 to 533 ms, up to 94 % of the states skipped (the copy already held a later state), peak of `PendingDeliveryBytes` 11.7 to 15.4 MB; following, the master writes 276 to 340 k/s and the children apply 290 to 358 k/s | development-4 | 18 November 2026 |
| S11 | Read p99 of the master during a load at the full rate, 16 readers | 4.1 to 5.4 µs with no load, 5.1 to 6.6 µs during one load, 4.6 to 6.2 µs during two | development-4 | 18 November 2026 |
| S11 | Reads by key beside a following child, one caller writing, per reader thread, below what the machine gives (it keeps at most 9 to 10 of its 20 logical processors busy at sustained speed) | 2 readers: 708 to 766 k/s on the base, 742 to 813 k/s with no child, 704 to 864 k/s with a child, p99 2.7 to 4.3 µs throughout; 4 readers with the child in its own process: 566 to 763 k/s against 616 to 769 k/s. A following child does not slow a read. The rows of 16 readers are a capped machine and decide nothing; the first round after a load with the child inside the master's process shows the child's own heap promoted in that process, a property of the measurement, not of a master's read | development-4 | 18 November 2026 |
| S11 | Writes a second of a master with following children, with the tail of blocks and after the review; alternating rounds of one day | one caller: 364 to 510 k/s with no child, 377 to 415 k/s with one child in the process, 451 to 474 k/s with one in its own process, 375 to 398 k/s with two; 16 callers: 245 to 270 k/s with one or two children, as with none; a write allocates 160 bytes with no child and 164 to 182 with children; with no child nothing is encoded or retained and the branch equals its base | development-4 | 18 November 2026 |
| S11 | The tail of blocks of 1 MiB against one object and one array for each transaction (the trial that was kept) | allocation per write with a child 446 to 462 bytes against 165 to 182; one caller with one child 331 to 383 k/s against 382 to 437 k/s, write p99 14.6 to 15.9 µs against 9.0 to 12.3 µs, pause of the collector per 2 s 25 to 143 ms against 6.9 to 9.2 ms; 16 readers beside 16 callers with a child: writes 60 to 77 k/s against 127 to 150 k/s, reads 2.02 to 2.52 M/s against 2.51 to 2.98 M/s. Tried without gain and not kept: acknowledgments that do not release, a sender that spins before it waits, a larger gen0 budget. Not built: a pool of tail memory and encoding outside the lock (S33) | development-4 | 18 November 2026 |
| S11 | One write of 32 MiB with a child following | p50 2.5 to 2.6 ms with no child, 27.6 to 34.8 ms with one; small writes beside large ones 3.7 to 17.9 k/s with no child and 293 to 592 a second with one, p99 42 to 101 ms: they wait while the large write is copied into the tail under the lock. The capture's copy of one root of 32 MiB inside the protection: median 10.4 to 14.9 ms. Carried to S33 | development-4 | 18 November 2026 |
| S11 | One acknowledgment that releases a million transactions of 64 bytes, under the lock of the writes | 4.0 to 19.6 ms when every header was read, 0.03 to 0.46 ms with the release by whole blocks | development-4 | 18 November 2026 |
| S12 | The mechanism that wakes waits for a position, before and after its replacement (waiters ordered by target, the lowest target published): the clock alone with 100 and 10,000 waiters; and a child with 10,000 waiters while 16 callers write to its master | alone: 0.95 to 2.1 million publications a second and 0.8 to 1.1 KB allocated per publication before, 175 to 177 million and nothing allocated after, at every count of waiters; on a child: the master wrote 373 to 394 k/s before and 668 to 706 k/s after, the collector paused 515 to 551 ms in 2 s before and 8 to 11 ms after; a read with a satisfied session on a child p50 0.5 to 0.6 µs both ways | development-4 | 18 November 2026 |
| S12 | One small replace forwarded through a child, one caller, loopback; and its parts | p50 50 to 116 µs, p99 87 to 364 µs (the first round of a process is the slow one). Parts: the write on the master p50 0.7 to 1.3 µs; a round trip of 64 bytes on loopback p50 23 to 28 µs; from the end of the master's write to the end of the child's wait p50 25 to 59 µs. The gate of a write through a child is 50 ms | development-4 | 18 November 2026 |
| S12 | 16 callers forwarding through one child; and the same beside 4 callers and 4 readers of the master | 50 to 78 k writes a second, p50 196 to 295 µs, p99 0.45 to 1.3 ms; beside the master's own load 15 to 28 k/s forwarded (p99 1.8 to 5.1 ms) while the master wrote 315 to 473 k/s and its reads kept p99 1.6 to 2.8 µs (1.4 to 1.7 µs with no forwarding) | development-4 | 18 November 2026 |
| S12 | Reads by key on a child, 4 threads, beside 16 callers forwarding through it, the process at its cap of 10 processors | 3.6 to 5.6 million a second, p99 1.4 to 2.1 µs, against 4.9 to 8.4 million and 1.0 to 2.0 µs alone; no code of a read changed, the fall is the processors the callers take | development-4 | 18 November 2026 |
| S12 | Small forwarded writes beside forwarded writes of 32 MiB through the same child (one connection) | p50 56 to 63 µs, p99 3.5 to 5.2 ms, longest 214 to 349 ms against 4.8 to 16 ms alone; an Applied of a master's write arrives up to 77 to 98 ms late. Carried to S33 with Q52 | development-4 | 18 November 2026 |
| S12 | The close of a master and of a child with 16 callers forwarding | master: median 2.4 to 3.3 ms after the frame Closed (1.9 to 3.3 ms before it), longest 7.5 ms; child: median 1.7 to 3.1 ms, longest 18 ms | development-4 | 18 November 2026 |
| S12 | Allocation per forwarded write | about 3.2 KB on the child and 1.1 KB on the master; not reduced in S12, a figure to return to when G3 is measured | development-4 | 18 November 2026 |
| S12 | A master with no child against the commit before the implementation of S12, alternating rounds | no fall with 4 and 16 callers, readers, and readers beside writers; allocation per write unchanged (160 and 124 bytes); one caller: replace 1.39 to 1.52 M/s against 1.46 to 1.57 M/s, inside the spread between runs (the order of the runs changed the sign) | development-4 | 18 November 2026 |
| S13 | Read by key of an entity whose field is stored as Dictionary against the same field as Plain out of line, by class | 1 thread 1.57 to 1.93 M/s against 1.22 to 1.62 M/s; 16 threads 7.9 to 9.2 M/s against 6.3 to 8.1 M/s: not slower. Against a Plain value small enough to lie in the root the two cross at 65 bytes | development-5 | 18 November 2026 |
| S13 | Reads beside a writer that allocates in the size class of the entries, 7 readers and the writer on 8 processors, 16 rounds of each, Plain and Dictionary alternating, the machine 27 to 50 % busy with other work | reads beside the writer over reads alone: Plain median 0.875 (0.75 to 1.01), Dictionary 0.86 (0.64 to 0.97), the ratio of the medians 0.98; reads alone 8.17 M/s Plain and 9.96 M/s Dictionary, beside the writer 7.30 and 8.41 M/s; the writer 795 k/s beside Plain readers and 704 k/s beside Dictionary readers. With 16 readers: 0.93 and 0.95 | development-5 | 18 November 2026 |
| S13 | Writes of an entity with a text of 9 bytes, the field as Dictionary against Plain | 12 to 20 % fewer writes a second as Dictionary; a write whose text is already stored costs 60 to 110 ns more, one whose text is new about 330 ns more (reads win over writes; the advice is in the decision on strings) | development-5 | 18 November 2026 |
| S13 | Memory of 100,000 users of the fixture with one field of 16 distinct texts of 9 bytes | 16.0 MB as Dictionary against 17.6 MB as Plain, 9.1 % less | development-5 | 18 November 2026 |
| S13 | Memory for each value when all 1,000,000 texts are distinct, 9 bytes each | 163 bytes as Dictionary against 80 as Plain; the table takes 51.4 bytes for each distinct entry | development-5 | 18 November 2026 |
| S13 | A child that loads 100,000 such users | the bytes on the wire are those of Plain (15,410,268); the child holds 16 entries; Ready about 0.12 µs later for each Dictionary value | development-5 | 18 November 2026 |
| S13 | Bytes the arena holds while one text of 1 MiB is replaced 100 times with no read in progress | 101 MiB as Dictionary against 4.0 MiB as Plain before the fix of the review; after it the test holds Dictionary to at most twice Plain | development-5 | 18 November 2026 |
| After S13 | Moves of the epoch for each 10,000 writes, counted by a probe, before the fixes and after | replaces 78 and 78, with a read held or not; deletes of an absent key beside a held read 0 and 78, one look for a window; 99 inserts to 1 replace, pure inserts, nothing retired: 0 and 0. No time was measured | — | 18 November 2026 |
| After S13R, part 1 | The spread of the method, measured for the first time: two builds of one commit (8c66279) against each other, read-by-key-threads, four rounds with the order of the builds rotated, 13 to 28 % of the machine busy with other work before a run. Later judgements of a paired ratio refer to it | the second build over the first, by pairs: 1 thread alone 1.00, 0.97, 1.32, 1.05; 1 thread beside a writer that replaces 1.05, 0.97, 1.06, 0.98; 16 threads alone 0.96, 1.05, 1.00, 1.02; 16 threads beside the writer 0.96, 1.06, 1.00, 1.01. Medians within 2.5 %, pairs within about 6 %, one outlier | development-5 | 19 November 2026 |
| After S13R, part 1 | Read by key of the reference fixture, the commit before S13 (8c66279) against main after part 1 of S13R (40f6a8c), in the same four rounds: closes the read of S13, of the immutable schema and of the second trigger of the drain (the fixes after S13) and of part 1 | head over each of the two builds of the base, medians: 16 threads alone 1.035 and 1.040 (9.13 and 9.16 against 9.51 M/s), every pair at or above 1.00; 16 threads beside the writer 1.026 and 1.025 (8.48 and 8.54 against 8.83 M/s); 1 thread alone 1.05 and 0.95 (2.47 and 2.63 against 2.50 M/s: the head lies between the two builds of the base); 1 thread beside the writer 0.96 and 0.98 (2.37 and 2.39 against 2.25 M/s), pairs 0.83 to 1.08. No fall; the case of 1 thread beside a writer is inside the spread of four rounds and is measured again with eight | development-5 | 19 November 2026 |
| After S13R, part 1 | Writes, write-callers, 8c66279 against 40f6a8c, three rounds | medians of the paired ratios: 1, 4 and 16 callers that replace 1.01, 0.99, 0.99 (1.44 to 1.46 M/s for one caller); that insert and delete 1.05, 1.03, 1.03; allocation for a write unchanged, 160 and 124 bytes. Writers beside 16 readers of the written database 0.96, 1.15, 1.11 (replace) and 1.09, 1.01, 1.13 (insert and delete); those readers 1.03, 1.00, 0.97 and 1.02, 0.955, 1.03. No fall | development-5 | 19 November 2026 |
| After S13R, part 1 | A child, the commit before part 1 (645fa43) against 40f6a8c, three rounds | following-children, writes a second: one child following 0.98, 1.05, 1.07 for 1 caller, 16 callers, 16 callers beside 16 readers; two children 1.03, 1.05, 0.98; with the children in processes of their own 1.01 to 1.07 in two rounds, the third disturbed (0.63 to 0.76 in four lines of one run). child-under-writes at the full rate of one caller: a child applied 1.24 to 1.33 M transactions a second against 1.21 to 1.27 M. child-load: single loads take 36 to 334 ms in both builds, three runs decide nothing; measured again with the case above | development-5 | 19 November 2026 |
| S13 | Read by key of the reference fixture, no Dictionary field, the commit before the implementation (8c66279) against the head, 8 rounds in the order base head, head base, each on 8 processors, the machine 19 to 77 % busy with other work. Left open by this series; answered by the series of 19 November 2026 below | head over base, by pairs: 1 thread 1.38, 0.65, 1.17, 0.69, 0.87, 1.33, 0.79, 1.03, median 0.95; 16 threads median 1.09 (0.78 to 1.34, and one pair whose base run was disturbed). Medians of the eight runs: 1 thread 1.72 M/s base and 1.49 M/s head; 16 threads 6.69 and 7.56 M/s. The pairs swing by more than the difference looked for, so the series decides nothing; on a Plain read the code differs from the base in one place, the test of the length where the base tested the kind, one comparison as before | development-5 | 18 November 2026 |
| S13R, part 2 | The spread of the method on a rented ARM64 machine, 16 cores: two builds of one commit against each other. The figures of this machine are a new series and are never compared in absolute terms with the rows of the workstation | one thread within ±5 % (one case +8 to 10 %); 8 threads ±10 %; 16 threads ±16 %, the machine saturated; writes ±6 % | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Read by key of the reference fixture, the cases left open above: case 2 of S13, and one reader beside a writer | closed: 12 rounds, the median of the ratio head over base 0.98 to 1.00 | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | The load of a child, child-load | no difference between the builds | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Read by key of text after A6 | a first series of two builds and three rounds showed 5 to 7 % slower; a series of four builds did not reproduce it: ratios about 1.00 for A6 as committed, A6 as fixed and main. Not measurable on this scenario, ±10 % a pair | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Writes after D1, write-callers, eight rounds with the builds rotating; the median of the ratio to main (40f6a8c) for one caller and for 16 that replace, one caller and 16 that insert and delete | the first cut, 670617c: 0.967, 0.981, 0.967, 0.960; the head before the attempts, 0151c9f: 0.979, 0.985, 0.987, 0.968; 70fcc30: 0.999, 0.992, 0.974, 0.967; babd343: 0.979, 0.976, 0.974, 0.965. A second series of the same form with 0d63331: 670617c 0.996, 0.970, 0.984, 0.974; babd343 0.983, 0.974, 0.991, 0.981; 0d63331 1.014, 1.000, 1.011, 1.001; beside 16 readers the writes of 0d63331 0.99 to 1.03 and the reads 1.00 to 1.04. The first cut cost 2 to 3 % on a write; written where it lies, the change costs nothing measurable. The rows of 4 callers scatter 0.85 to 1.13 a round and are not judged | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Writes with children following, following-children, four rounds, five builds rotating (main, 670617c, 42b6317, 0151c9f, babd343), the children in processes of their own started through the executable | 16 callers that replace, over main for 670617c, 42b6317, 0151c9f and babd343: no child 0.969, 0.934, 0.953, 0.945; one child 0.988, 0.950, 0.978, 0.966; two children 0.946, 0.969, 0.947, 0.951; one child in its own process 0.958, 0.944, 0.971, 0.957; two in their own processes 0.958, 0.948, 0.972, 0.966; reads beside the writers 0.97 to 1.01. An earlier series of two builds and three rounds had shown 0.94 and 0.91 with a child following. No fall belongs to a child: the rows with a child lose what the rows without lose, the cost of the first cut of D1, which 0d63331 removes | rented-arm64-1 | 19 November 2026 |
| S14 | Memory and the build of secondary indexes, index-build, on a rented ARM64 machine, 16 cores, the engine at 11adfeb (before the last fixes of the review), two or three runs of each case within a few per cent: bytes of the arena for an entry of an index and for an entity; the time of one caller to insert 100,000 entities of a type with 0, 1, 3 and 6 Int64 indexes | 17.6 bytes an entry with ascending keys and 25.1 with random keys, the same with 1, 3 and 6 indexes; 112 bytes an entity without indexes. The build 104 ms with no index; 254 to 327 ms with one; 546 to 770 ms with three; 980 to 1,440 ms with six, the first figure with ascending keys and the second with random ones | rented-arm64-1 | 19 November 2026 |
| S14 | Writes a second of one caller, index-writes, a type with 0, 1, 3 and 6 indexes; and of eight callers | a replace that changes every indexed key 952 k, 210 k, 85 k, 47 k; a replace that keeps every key 814 k, 648 k, 436 k, 307 k; insert and delete 1,385 k, 436 k, 166 k, 85 k. Eight callers, the replace that changes every key: 408 k, 79 k, 41 k, 26 k | rented-arm64-1 | 19 November 2026 |
| S14 | The same writes of one caller as the time of a write; what a write allocates and takes | a replace that changes the key 1.05 µs with no index and 4.8, 11.8, 21.3 µs with 1, 3 and 6, about 3.5 µs an index; an insert or a delete 0.72 µs and 2.3, 6.0, 11.8 µs, about 1.85 µs an index. Managed bytes a write unchanged by indexes. Blocks of the arena taken for an insert, a replace and a delete with one index 7.6, 13 and 4, about one of them used | rented-arm64-1 | 19 November 2026 |
| S14 | Exact search by the full key, index-search, 1 and 8 readers: an Int64 key, distinct keys, through the class, alone, beside a writer that changes indexed keys, and by name; the first item under one of 16 keys; a text key, Plain, Ordinal, distinct | Int64: 450 k a second on 1 thread and 2.9 M on 8; beside the writer 400 to 415 k and 2.4 to 2.6 M; by name 445 k and 2.7 M. The first item under one of 16 keys 776 k and 4.4 M. Text of 8 bytes 163 k and 1.2 M; of 100 bytes 119 k and 0.86 M. For scale, the read by primary key of the same items 1.5 M and 12 M. Managed bytes a search 720 through the class and 936 by name, against 88 for a read by key | rented-arm64-1 | 19 November 2026 |
| S14 | Read by primary key beside writes to indexes, index-read-by-key, through the class: a type with 6 indexes alone and beside a writer that changes every indexed key; the same type with no index | 1 reader: 1.53 M a second alone and 1.38 M beside the writer with 6 indexes, 1.49 M and 1.44 M with none; 8 readers: 11.9 M and 10.3 M with 6 indexes, 11.6 M and 10.7 M with none | rented-arm64-1 | 19 November 2026 |
| S14 | The prototype of the study on the same machine, bare trees and no engine, 8 readers, three runs: the copy-on-write tree against the tree changed in place. Not a figure of the engine | exact search alone 15.6 M a second and 15.2 M; beside a writer of the same index at full speed 10.4 M against 14.5 M; ranges forwards 6.2 M and 5.8 M alone, 3.7 M against 5.4 M beside the writer. One index write beside the 8 readers, ns: an insert 840 against 525, a delete 842 against 619, a replace 1,762 against 1,197. The bare tree alone: a replace that changes the key 1.14 µs, an insert 0.52 µs, a search 2.1 M a second on a thread | rented-arm64-1 | 19 November 2026 |
| S14 | The forms of a text key in the prototype on the same machine, 8 readers, random keys, searches a second at keys of 8, 32 and 100 bytes. Not a figure of the engine | a copy 10.1 M, 7.1 M, 4.6 M; a shared text entry 4.6 M, 4.3 M, 4.0 M; a shared entry with a prefix 9.2 M, 9.0 M, 9.2 M | rented-arm64-1 | 19 November 2026 |
| S14, part 2 | Exact search by the full key, index-search, 1 thread, through the class, alone; three builds rotating, three rounds | Int64 796 k a second (main 464 k), text of 8 bytes 503 k (161 k), of 100 bytes 288 k (117 k); 416, 456 and 640 bytes a search (720, 760, 944). The gauge, a second build of main, 0.98 to 1.02 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | A replace that keeps every indexed key, index-writes, 1 caller, against the same type without indexes | 0.96, 0.94 and 0.86 of its rate with 1, 3 and 6 indexes (main 0.80, 0.56, 0.39): about 30 ns an index, against about 320 on main; the type without indexes 805 k writes a second on every build | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The upkeep of one index for a replace that changes the key, index-writes, 1 caller, against the replace of the bare tree of the prototype in the same start, 1,153 ns | 1,465 ns with 1 index, 1.27 times the bare tree (main 3,579 ns, 3.10); 1,313 and 1,258 ns an index with 3 and 6 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | Writes and reads by key of the reference fixture's users, a type with three indexes, write-callers and read-by-key-threads, eight rounds | a replace keeping every key 1.72 times main with 1 caller, about 1.5 with 4 and 16; an insert and a delete 2.50 and about 1.96; reads by key up to 8 threads 0.96 to 1.07 of main, the gauge 0.98 to 1.04 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The size of a node, 2 KiB against 1 KiB, index-search, index-writes, index-build, a second build of the head as the gauge | 8 readers beside a writer: reads 1.04 (gauge 0.99), the writer 0.95 (0.94); a replace that changes the key 0.97 to 0.98 (0.99 to 1.00); 16.8 and 23.7 bytes an index entry against 17.6 and 25.1 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | Text keys of 100 and 200 bytes, index-long-text, five rounds: a prefix of 8 bytes beside a shared key, and the references of a text held for each entry, against the head | the prefix: a search 1.26 and 1.30 with distinct heads, 0.89 and 0.93 with a common head, a replace that changes the key 1.17 and 0.98; the references for each entry: a replace that changes the key 1.45 to 1.51, an insert and a delete 1.9 to 2.1 with distinct heads, a search unchanged. The gauge 0.99 to 1.02 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The harness of B2, the new (main) against the old (f3d767f) on the same engine, three rounds and one pair of the gauge | read-by-key-threads up to 8 threads 0.96 to 1.05, write-callers 0.98 to 1.03, index-search 0.99 to 1.07; index-read-by-key 0.96 to 1.03 but for 0 indexes, 1 thread, alone, 0.89 [0.94 0.87 0.89], whose gauge pair is 1.07 | rented-arm64-1 | 20 November 2026 |
| S15, part 1 | Exact search, index-search, 1 thread alone, against 5cc64c8 (main after S14R), the first of three starts, four builds, three rounds | Int64 through the class 0.88, by name 0.92; text of 8 bytes 0.87, of 100 bytes 0.92; the first item of 6,250 equal keys 0.52, its bytes 416 to 840; S14R itself loses nothing there | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | The walk, index-search, the first start: a range of 100, a page at Offset 10,000, the rounds of a step | a range of 100 about 9,800 a second alone, 0.89 forwards and 0.84 in reverse beside a writer, about 62,000 with 8 readers; the page at Offset 10,000 404 a second against 10,405 for the first page, the price of remembering the identities passed over; counting under `ReadMemoryLimit` not visible for an entity of 112 stored bytes; 3.22 descents at a fence for a range of 100; `EntriesPerRound` 64, 256 and 1,024 not apart outside the gauge, 256 kept | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | Writes and reads by key, the upkeep, forwarded writes, the first start | writes and reads by key lose nothing to the sprint; a type without indexes loses nothing (F7); the upkeep of one index 1.20 to 1.26 times the bare tree of the same rounds; the forwarded write loses nothing after F12; F9 not explained by how a step is called | rented-arm64-1 | 21 November 2026 |
| S14R | Inserts and deletes of the reference fixture's users, write-callers, against 22b8ee1, as merged and with two fixes, the first and second starts of S15, part 1 | as merged 0.93 to 0.96; with V1 0.96 to 0.99; with V2 1.01 to 1.08, taken and merged (PR 37); V2 on index-long-text 8 to 14 % faster on a replace that changes a long key and on an insert and a delete with distinct heads | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | Exact search at the close, the third start, against 5cc64c8, 1 thread alone, class and by name | Int64 0.966 and 0.957; text of 8 bytes 0.899 and 0.911; text of 100 bytes 0.951 and 0.964; the first of many 0.826 and 0.869; beside a writer one entity 0.99 to 1.06. Targets of S14, part 2 at the head: 692 k against 700 k, 426 k against 450 k, 258 k against 200 k, with 5cc64c8 reading 730 k and 474 k | rented-arm64-1 | 21 November 2026 |

The figures of S14 come from the scenarios `index-build`, `index-writes`, `index-search`
and `index-read-by-key`, run on the engine at 11adfeb, before the last fixes of the
review; the rows of the prototype are the bare trees of its study on the same machine.
The figures of S14, part 2 are of the head 9e34443 against `main` at 3229ba1, each series
with a second build of the same commit rotating in its rounds as the gauge; the raw outputs
are kept outside the repository.
No acceptance of S14 set a target for the cost of a write or of a search; the record of
S14 says what is weak in them, and S14, part 2 is about them.

The figures of S09 come from the scenario `write-callers`. Writes fell against S08: by
a third to a half for one caller, and several callers write about half of what one writes
where S08 lost about a third. The cause is the work of the coordinator on every write —
two locks taken several times where S08 took one, the queue of an identity, and for
callers that wait a hand-over between threads — and not the number of places of
preparation: with 16 or 64 places instead of 2 nothing was gained. What the path gives
for it: the order of calls, cancellation before dispatch, a p99 of a write with many
callers that is lower, and groups for the storage step of S27. Reads by key did not fall:
beside writing callers they are about 10% lower, and so are readers of another database,
so it is the share of the machine. The requirements are 200 writes a second, 10,000 at
the peak and a p99 of 50 ms; the lowest rate measured is 233,000 a second. The owner
decided on 17 November 2026 that S09 closes with these figures and that S09, part 2 puts
the write path on a proven source at once.

The figures of S09, part 2 come from the scenarios `write-callers` and `large-writes`,
measured after a restart of the machine in alternating rounds; in some rounds the machine
ran at about a third of its speed, and the rows give the rounds in which it did not.
Against S09 the write path is faster for every number of callers, by a third for one and
by 1.5 to 3 times for 4 and 16, allocates what S08 allocated, and keeps what S09 added:
the order of calls, cancellation before dispatch and groups for the storage step. Against
S08 it is 14 to 17% lower for one caller, and for several by the same share, since all
of a write runs under the one lock. The implementer measured where the 70 to 90 ns a
write go: about 10 ns outside the engine call, and the rest in what a write carries since
S09 and S08 did not have — the record of the write, the group, the storage step and
publication as a step of its own; none of the simple removals gave a measurable gain, and
the one that did, a second path for a write that completes in its hold, was not taken.
The Acceptance line "one caller not lower than S08" is therefore not met; the owner
decided on 17 November 2026 that the path is accepted as it is and that optimization
stops here. Reads did not fall. What a large entity costs is new: a caller that writes
entities of 32 MiB one after another leaves another caller about a thousandth of its
small writes, because the lock of .NET is not fair and lets the caller that just released
it take it again for up to about 100 ms; a write larger than the limit of a group gives
its processor up once after it releases the lock, which raises the small writes about
2.5 times and bounds nothing. A first-in-first-out gate in front of the lock was
measured and dropped: with 16 callers beside 16 readers on 20 processors no write
completed in less than 10 ms. This is the input of Q52. Two reviewers measured that,
while a storage step is busy, admitting a write walks the list of the writes that wait:
with 40,000 waiting, 0.6 to 0.8 ms an admission; it never happens in MemoryOnly and is
carried to S27.

The figures of S08, part 2 come from the scenario `enumerate`. Remembering the keys costs
about a quarter of a pass, about 110 ns an entity; with a limit set a pass also takes
timestamps, about 13 ms per 100,000 entities. A step after a growth walks the new table
from its start in rounds of 256 words of the index, so that no round stays inside the
protection of reads for more than a bounded piece of work: 0.14 to 2.6 ms at the longest
with 1,000,000 keys returned, against 117 to 269 ms before the rounds. A seeded hash of
text after Marvin32 of .NET is not slower than the hash it replaced: 24 to 26 ns against
27 to 28 ns for a key of 13 characters compared without case, and 9 to 10 ns for Ordinal
before and after, in the implementer's benchmark and not under a manifest.

The figures of S08, part 1 come from the scenarios `read-by-key-threads` and
`primary-index`. The machine has cores of two kinds and carried 15 to 25% of other load
on that day, and the same series varied up to twofold between runs, so the figures say
how the protections compare and no more. The replaces of a writing thread beside 20
readers are lower than on the protection of S06 because the readers now run: beside
as many readers of another database the same thread is as slow. The growth of the index
takes longer than with the hash of S07, which spread consecutive integers evenly; the
mixed hash spreads them as it spreads any keys. Per instance the protection costs 16 KiB
for the table of epochs and about 0.8 KiB of padding, and 16 KiB per segment of 4 MiB
for the marks of retired blocks; the 1,025 locks of S06, about 100 KiB, are gone.

The figure of S07 comes from the scenario `read-by-text-key`; its keys are 13 ASCII
characters, and no key outside ASCII was measured by it. One hash and one equality of
such a key cost about 40 ns by the table against 29 ns by .NET, and 94 ns against 73 ns
for a key of 15 characters that starts in Cyrillic, in the implementer's own benchmark,
not under a manifest. The table and `OrdinalIgnoreCase` of .NET 10 disagree on 16
scalar values under ICU, 1,031 under NLS and 6 under invariant globalization; in every
case the table holds equal what the runtime holds different. The table adds 17 KiB to
the core assembly. A long key outside ASCII compared without case reads slower by the
table than it did by .NET, in the probe of the review and not under a manifest: a key
of 1,000 UTF-16 units of mixed text 6.4 µs against 3.9 to 4.1 µs, and one of 100,000
units 0.63 ms against 0.36 to 0.42 ms in UTF-16 and 1.02 ms against 0.77 ms in UTF-8;
keys of 13 characters and long ASCII keys read as before.

The figures of S06 come from the scenario `read-by-key`
(`dotnet run -c Release --project Moltaro.Lattice.Measurements -- read-by-key development-2`,
from `src`). They are given as the range of three runs, and a single run can lie well
outside it — the review measured a p50 of 1,700 ns by class alone in one of two runs —
because the runs differ more
than the series do: the processor of the development machine has cores of two kinds and
the threads are not pinned. The manifest `development-2` is the development manifest
with a `Run` sentence that is true of a series with a writer; the figures of S05 stay
under `development`. Reads by key on several threads were measured by the review of S06
and not under a manifest: they do not scale with threads, which is entry Q42 of the
[open questions](open-questions.md), reported in S08 and judged with the figures of S25.

The figures of S05 come from the scenario `root-bytes` of the measurement program
(`dotnet run -c Release --project Moltaro.Lattice.Measurements -- root-bytes development`,
from `src`) and were the same on Windows and on Linux. They are for comparison between
sprints, not a gate: G1 is judged in S25 against the managed reference. What they show
already is where the bytes of a small entity go — the 32-byte header of the root block
and a 16-byte descriptor for every String, 64 bytes of descriptors for the 57 bytes of
text a user has — and the coordinator reported it to the owner with the close of S05.

## Open questions

Everything raised while planning was answered by the owner on 14 November 2026; the
answers are recorded below and the sprints are written to them. The first
independent review raised seven questions, which the owner answered on the same day. The
second review raised two more, P8 and P9, and the fourth one, P10; the owner answered all
of them, and none is open. A question that
appears later goes to the owner, as the [development process](process.md) says; it is not
settled inside a sprint.

### Answered after the fourth review, 14 November 2026

**P10. Answered on 14 November 2026: an edit that states nothing new is made in the commit
that needs it.** A sprint that removes a member, changes the form of a descriptor or
deletes a class forces edits on existing tests, samples and code that state nothing new:
the same use under another name, the same descriptor in another form, a helper that names
another class. Three reviews found such edits assigned to nobody, or to a step in which
they could not pass. Whoever makes the commit that would otherwise break makes the edit
in that commit, in tests and samples as well; an assertion is never changed this way,
and the reviewers check exactly that. The [development process](process.md#a-sprint-on-the-full-track)
says so, and the briefs of S02, S03 and S12 name each such edit. *Not chosen:* skipping
the affected tests until the implementation, which leaves a sprint without its tests and
still does not let the contract commit build.

### Answered after the second review, 14 November 2026

**P8. Answered on 14 November 2026: the node program and the measurement program may reach
inside the engine.** Exception X3 covers them with the three test projects, under the
same condition; neither is ever shipped. The node program arms fault points inside its
own process, so that a test can kill a master at a chosen point. The measurement program
opens the engine before S12, when only the internal entry exists, and reads the counters
of the engine before S39 makes them public. *Not chosen:* producing the figures of the
first sprints from tests, which would put measurements into a test lane.

**P9. Answered on 14 November 2026: an index may name a field inside an owned value.** A
part of an index key is a field of the entity type or a path from such a field through
owned values to a field inside them; a path never goes through a List, a Map or a Set,
because indexes over the members of a collection are outside the milestone. When an
owned value on the path is absent, that component of the key is null, with the accepted
rules for a null component: the entity is found by a search for null, and a unique index
does not compare it. The records of unique indexes and of queries already describe this
behavior; the descriptor and `LatticeIndex` get the form in S03, the storage in S18.
*Not chosen:* indexes over fields of the entity type only.

### Answered after the first review, 14 November 2026

**P1. Answered on 14 November 2026: contracts stage by stage, put into code by a contract
author.** The planner writes the types and members into the briefs of a stage before that
stage begins, and the owner approves the contract of each stage before its first sprint.
A contract author, a role of its own, puts the contract of a sprint into code as the
first step. The [development process](process.md#the-plan) now says so. *Not chosen:*
writing the whole public API of the milestone into the briefs before the plan is
approved. The types and members of the first stages, sprints S02 to S12, are written
into their briefs below and are approved with the plan.
*Note of 21 November 2026:* the contract author is no longer a role of its own; by the
owner's word of that day the implementer of a sprint puts its contract into code in step 1
of the [development process](process.md).

**P2. Answered on 14 November 2026: sprints that join several behaviors are split.** The
first plan had 32 sprints. Seven of them were split in two after the first review and
three more after the second, which gives 42; the order did not change. With the same answer the owner decided that every sprint, on
either track, has an independent review, and that review and fixes repeat until a review
finds no P1, P2 or P3; the [development process](process.md#review) says so, and it holds
for this plan as well.

**P3. Answered on 14 November 2026: children load in parallel.** The starting value of
the milestone contract, one capture at a time, would not let ten children that start
together become ready within the 30-second open timeout, and the owner withdrew it.
Captures run in parallel; their number at once is an option, 10 by default, and a child
beyond it waits. Each capture has its own start position, fence, watermarks and
readiness position. The open timeout stays one timeout that counts everything from the
open call, the wait for a turn included. The decision is entered in the
[milestone contract](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading)
and in the [Q08 record](../decisions/replication-and-hosting.md). The bootstrap
experiment covered one capture only, so several at once are verified in S11, S33 and
S35.

**P4. Answered on 14 November 2026: the three test projects of the engine may reach
inside.** Exception X3 covers `Moltaro.Lattice.Engine.Tests`,
`Moltaro.Lattice.PostgreSql.Tests` and `Moltaro.Lattice.Cluster.Tests` under one
condition: each such test states an invariant written in a decision record or in the
engine contract. It covers fault points inside the engine that only a test can arm,
reading the provider's tables, and damaging a connection on purpose. Fault points are
internal, off unless a test arms them, and not reachable through the public API. With
the same answer the text of principle 11 reads "No other shipped assembly references
those two packages": the measurement program uses the index package for the managed
reference, and the provider tests use the PostgreSQL driver to create and drop their
databases. Both texts in the brief of S01 are the approved ones.

**P5. Answered on 14 November 2026: one store, one master, enforced.** The question was
whether the epoch check also covers the background transactions of a superseded master.
The owner replaced the epoch decision as a whole: see O9 below. Every transaction that
changes the store, foreground or background, checks the leadership number, so the gap
the review found does not exist in the new rule.

**P6. Answered on 14 November 2026: a restored copy is the procedure's to activate.** The
content of a restored PostgreSQL database is identical to the original, so the engine
cannot tell it from an ordinary restart; opened without activation it continues the old
history. The accepted design leaves this to the restore procedure, and the owner keeps
it so: the procedure says plainly that a restored copy is activated before it is opened,
and S36 exercises it. *Not chosen:* a marker that infrastructure sets on a restored
target, which moves the same human error one step earlier and is new scope.

**P7. Answered on 14 November 2026: migration is for a Durable master only.** A MemoryOnly
master has no saved data to convert and loses its data whenever it closes; to change its
schema an application closes it and opens it with the new one. The migration call on a
MemoryOnly master is refused with an error that says so. *Not chosen:* converting in
memory, which would be a mechanism of its own without a saved copy to fall back on.

### Decisions of the owner, 14 November 2026

Each entry says what was decided, what was not chosen, and which sprints it shapes.

**O1. Three assemblies.** The database is three shipped assemblies, each depending only
on the one before it. `Moltaro.Lattice` is the public contract and what needs no engine:
options, errors, the schema and its compiler, the access interfaces and values; it has no
package dependency. `Moltaro.Lattice.Engine` holds arena, indexes, coordinator,
replication, MemoryOnly and the entry point `LatticeDatabase`, which keeps its namespace;
it owned `Arc.Collections`, pinned to exactly 2.2.0, the version the evidence was measured
on, until the owner removed the unused reference on 18 November 2026 (S13). `Moltaro.Lattice.PostgreSql` is the Durable provider and owns `Npgsql`; a child never
loads it. Dependency injection references the engine, because it opens the database; the
Identity package references only the contract. The options take the storage as an object
of the provider package, and `StorageConnectionString` is removed. *Not chosen:* the
engine inside `Moltaro.Lattice` with `Arc.Collections` as its single dependency. Principle
11 of `AGENTS.md` is reworded in S01 with the approved text. Shapes S01, S12, S27.

**O2. The placeholder is frozen and leaves in S12.** Nothing new is implemented in it; a
declaration it cannot honor is refused on write, as a unique index is today. In S12 the
engine does everything the placeholder does, the entry point switches and the placeholder
is deleted in the same change; no switch between two implementations remains. From S06
to S12 the engine's tests open it through an internal entry of the engine assembly and
then use only the public interfaces; this is exception X4 of the
[exceptions register](exceptions.md). *Not chosen:* implementing index
search, unique indexes and the batch in the placeholder so that Epic 2 could start
earlier.

**O3. The full type model before PostgreSQL.** The order is that of
[dependency order](#dependency-order). *Not chosen:* PostgreSQL directly after the batch,
which gives durable data sooner but builds the journal, the applier and the operation
fingerprint for complete writes first and reworks them for ordered collection commands,
and measures the memory gate near the end.

**O4. Test lanes enabled by environment variables.** As in [test lanes](#test-lanes).
The PostgreSQL job and the cluster job become required checks of `main` when their lanes
exist. *Not chosen:* tests that fail when the service is absent; test projects outside
the solution.

**O5. Engine mechanisms are tested directly.** `Moltaro.Lattice.Engine.Tests` may test
internal types of the engine; each such test states an invariant written in a decision
record or in the engine contract. For such a sprint the brief names the internal types under test, and the contract step
puts them into code, documented and refusing, so that the tests are
still written first and by another agent. This is exception X3 of the
[exceptions register](exceptions.md); `AGENTS.md` gets the approved text in S01. The
answer to P4 above extends it to the provider tests and the cluster tests.
*Note of 21 November 2026:* by the owner's word of that day the tests are still written
first, in step 1 of the [development process](process.md), now by the implementer of the
sprint; the independent view moved to the reviewer and the breaker.

**O6. Protected views in both forms.** A view by name through field handles, and a typed
view with properties for an entity class. The typed view needs code written for each
class at build time, so a source generator is delivered as an analyzer project of its
own, which owns the Roslyn dependency at build time only. Typed copies keep accessors
compiled once when a class is bound. Nothing else depends on the generator; leaving it
out later would be a decision of the owner about the scope. *Not chosen:* views by name
only. Shapes S21–S22.

**O7. A child's open has a finite timeout.** It is an option, 30 seconds by default; a
child that is not ready in time fails with `Unavailable` and is closed. The text of
`ILatticeDatabase.OpenAsync` that lets a child wait without end is replaced. Shapes S10.

**O8. The engine does not protect the replication port.** The handshake binds database,
history and schema and authenticates nobody. That the port is reachable only from the
nodes of the database is a precondition of deployment, stated in the product
documentation in S10. *Not chosen:* a shared secret in the handshake; TLS inside the
engine. What a node therefore trusts of a peer is decision O21.

**O9. One store has one master at a time, and the engine enforces it.** The owner first
chose to detect a superseded master by an epoch, and on the same day replaced that with
a stronger and simpler rule. A Durable master takes the leadership of its store when it
opens — a session lock in PostgreSQL, held while the instance is open — and a second
master that opens the same store is refused at once and takes nothing over. When the
master closes or its process dies, PostgreSQL frees the store. So that the rule also
holds for a master that lost its leadership session while its process lives, taking the
leadership advances a leadership number, and every transaction that changes the store —
commit, base application, cleanup, receipt retirement, migration — commits only when
that number is still its own. The master checks its leadership session at an interval
that is an option of the storage, 5 seconds by default; when the session has ended it
pauses writes and tries to take the leadership again within the storage recovery wait; it
goes on only when it holds the lock again and the leadership number is still its own, and
faults otherwise, so that it never continues over writes it has not seen. It is exclusion, not election: the engine appoints nobody.
This changes the accepted design, which left the case to the host; it is requirement R35
and was entered in the
[milestone contract](../design/milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary)
and in the [Q15 record](../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection)
on 14 November 2026. Shapes S27, S28–S29, S30–S31, S36, S37.

**The design reference follows these decisions.** On 14 November 2026 the decisions that
change or complete the design — O7, O8, O9, O10, O11, O12, D1, D3, D4, D6, P3, P7 and P9 — were
entered in the milestone contract, the operation retry protocol, the performance
requirements, the architecture page and the decision records they touch. The briefs
below point to those documents, not to this list.

**O10. The schema descriptor.** An index has a required stable `Id`. The top array is
`EntityTypes`, not `Tables`. An index has no `Kind`. Shapes S02, with the Identity
package's descriptor and `README.md`.

**O11. Where and how the gates are judged.** On one reference machine, the owner's
workstation; that is enough to close the epic, and a network-separated run is not
required. The master, every child and PostgreSQL run as separate containers with declared
limits of CPU and memory; the manifest records that the containers run under a virtual
machine of the workstation. Before a gate run the measurement program checks the state of
the machine — enough free memory for the sum of the limits, a quiet processor for a
stated time, no foreign containers — and does not start when the check fails. Sixteen
concurrent write callers; 2 minutes of warm-up and 10 minutes of measurement for the
ordinary profile; readiness measured 5 times, every run within the target; PostgreSQL 18
as the only tested and stated version. S25 writes the manifest of the single-node runs
and S40 the manifest of the topology; the owner approves each before its first gate run.

**O12. Two limits the design did not give.** A MemoryOnly master keeps operation outcomes
without a limit of their own, by the owner's decision O20 of 15 November 2026: they are
counted in the instance budget, and an unexpired outcome is never dropped (the limit this
decision first gave them, the smaller of 32 MiB and 10% of the instance budget, is gone).
The time limit of a traversal, when an application sets one, counts only time inside the engine, not the
time the caller spends between two items. Shapes S26, S08.

**O13. Code shared by the test projects has a project of its own.** Decided while S01 was
reviewed. `Moltaro.Lattice.TestSupport` is a library that is never shipped and holds no
tests; a test project references it for code that two or more test projects need, and
such code is not copied between them. The review of S01 found a helper of the dependency
tests in two test projects; two more helpers were already copied between the tests of the
core and those of dependency injection; and the tests of S22, S27 and S35 will need the
same code again. *Not chosen:* creating the project when a third copy appears; a
duplicate that is not removed when it is seen stays. `AGENTS.md` gets the approved rule
in S01. Shapes S01.

**O14. The coordinator pushes and merges closed sprints.** Decided after S01. The owner
gave the coordinator of this epic a standing permission: when a sprint is closed and no
decision of the owner is open, the coordinator pushes its branch, opens the pull request,
squashes it into `main` once every job that CI runs for the pull request has passed, and
goes on to the next sprint; it reports after the merge. Every pull request is squashed,
also that of a sprint on the full track. The series contract, tests, implementation,
fixes is then no longer in the history of `main`: after the merge it is visible in the
pull request on GitHub only. What the coordinator stops for is otherwise unchanged and is
listed in the [development process](process.md#when-to-stop-and-ask-the-owner). A release
tag, the rulesets and the settings of the repository stay the owner's. *Not chosen:* the owner merging every sprint by hand, which made the owner a
relay between two steps that need no decision; a merge commit for a sprint on the full
track. [Branches and releases](branches.md#who-does-what) has the rule. Shapes every
sprint from S02 on.
*Note of 21 November 2026:* by the owner's word of that day, a coordinator no longer goes on
to the next sprint after a merge: it stops at the boundary, and a new coordinator session
takes the next sprint, as the [development process](process.md#new-sessions-at-a-sprint-boundary)
says.

**O15. A .NET enum that does not agree with the schema is refused, as a class is.**
Decided while S02 was carried out; the question is Q1 of the
[open questions](open-questions.md). A property of a bound class may be a .NET enum. That
enum has to agree with the enum type the schema declares for the field: the same
underlying type, the same members — every name with its value, in both directions — and
the Flags mark. An enum that does not agree is handled exactly as a class that does not
agree with its entity type: the binding fails with `BindingFailed` and names the
property. Nothing is mapped, guessed or tolerated, and a schema that changed without the
class is found when the class is bound, not when a value is read. *Not chosen:* leaving
the rule to the implementation; accepting an enum that names a part of the declared
values. Shapes S02.

**O16. A small change goes into the branch of the sprint that is open.** Decided while
S02 was carried out. A change that comes up during a sprint and is small — an answer of
the owner that touches a rule, a document, a file of the repository's settings or the
brief of a later sprint; a sentence of documentation — is made in the branch of that
sprint and merged with it, and gets no branch and no pull request of its own. It is
named in the brief of the sprint or in the [open questions](open-questions.md) with the
answer that caused it, and it is reviewed with the sprint like everything else on the
branch. The light track stays for a change that is made while no sprint is open, or that
cannot wait for the open one. *Not chosen:* a branch, a pull request and a review round
of their own for every such change, which cost the owner a merge each and bought nothing.
Shapes every sprint from S02 on; S02 carries, besides its own work: the register of open
questions with its rule in the development process; the rule that review rounds are not
limited by a number, and this rule on small changes, both decided by the owner while the
sprint was open and named here; the squash in the ruleset of `main` (Q2); the sentences
"Not implemented yet" in the documentation of unique indexes (Q12) and the completed
documentation of the write batch and of `Set<TEntity>()` (Q6); and the texts that the
owner's answers added to the briefs of S03, S04 and S37 and to the document of Epic 2
(Q3, Q4, Q5, Q9, Q10, Q11).

**O17. The default of the instance memory budget is computed.** Decided on 15 November
2026 and given through the planner's session, when the owner learned that the limit of the
arena is required. The budget is an option whose default is half of the memory the .NET
runtime reports as available to the process when the instance opens: the physical memory
on a machine without limits; in a container by default three quarters of the container's
limit; a heap hard limit configured for the garbage collector lowers it. It is enforced
as Q14 accepts. An application that prefers to run into swap sets the budget above
physical memory. *Not chosen:* the fixed 256 MiB, far too small for real data; all of the
memory; no limit unless one is set. Shapes S09; it rode in the branch of S05 under O16,
with one sentence of the engine contract and a note in the decision on write admission.
The owner's answer to Q37 of the [open questions](open-questions.md) corrected the source
after the review of S05: the decision first read "the limit of its container, or the
physical memory of the machine", which the runtime does not report, and the owner chose
the runtime's figure, said plainly, over platform code of the project's own.

**O18. A write batch has no limit of its own.** Decided on 15 November 2026 and given
through the planner's session. The owner first asked what the limit of 128 entities was
for and called it excess configuration, then refused a size limit as well, fixed or taken
as a share of the memory budget: the size of a batch is the application's choice, and the
task of the database is to store it atomically. A batch has no limit on the number of its
operations and none on its size, and no option for one; it is refused only when a
resource cannot hold it — the memory budget of the instance, the hard limit of the
journal, when one is set. The rule that follows: a bounded buffer bounds what accumulates and never
refuses one transaction for its own size; what a transaction needs is reserved in the
instance budget before it is committed, and one the budget cannot hold is refused before
it has any effect. *Not chosen:* 128 operations and 8 MiB by default; a size limit derived
from the budget. Shapes S09 and S17 and the contracts of the later stages; it rode in the
branch of S06 under O16, with the engine contract, the decision on the document model,
the architecture, `README.md` and the documentation of `ILatticeWriteBatch`. Principle 5
of `AGENTS.md` called a batch "bounded"; the owner approved the sentence without those
words with decision O19.

**O19. An entity has no size limit of its own.** Decided on 15 November 2026 and given
through the planner's session, after decision O18 and for the same reason: how large an
entity is, is the application's choice. The 1 MiB starting limit of the engine contract
and the option that was to carry it are removed; an entity is refused only when the
memory budget of the instance cannot hold it. One String or Binary value stays bounded by
the storage format at 268,173,376 bytes, the capacity of one directory block of chunks: a
fixed rule, not an option, and a value above it is `ValidationFailed` with the field
named. *Not chosen:* keeping the option without a default. Shapes S06 and S09, reaches
back into S05, whose `RootWriter` loses `maxEntitySize`, and forward into every brief
that sized a buffer or a test by "1 MiB per entity", which the planner words with its
stage. With the decision the owner approved principle 5 of `AGENTS.md` without the words
"which is bounded"; the coordinator made that edit on the owner's own word in its
session, given on the same day, because `AGENTS.md` is the instruction file of the agents.

**O20. No default turns ordinary load into a refusal.** Decided on 15 November 2026 and
given through the planner's session, as the general form of O18 and O19, and confirmed to
the coordinator for `AGENTS.md`. A limit exists only where the engine cannot work without
it or a resource cannot hold the work; the database does not cap what an application
chooses to do, and where it can it waits or slows down instead of refusing. An
application that meets an error in production has a problem, and one that meets slowness
changes how it writes. Three limits lose their limit altogether: the queue of writes
(1,024 requests and 16 MiB), the read budget as a default (8 MiB and 5 seconds) and the
memory of retained outcomes on a MemoryOnly master (32 MiB); what they held is counted in
the memory budget of the instance. Two keep an option that is not set by default: the
hard limit of the journal and the budget of the receipts, both 1 GiB before; without a
hard limit the time of a recovery grows with the unapplied tail. The read budget stays as
an option an application may set. *What stays as it is:* the memory budget of the
instance and its refusal (O17, Q14); every timeout of a wait, because a wait has to be
finite and its error means a real failure; the rules of the schema language; the bound of
one value in the storage format; internal tuning that never refuses; the limits of
delivery to children, which cost a slow child a reload and an application nothing. Shapes
S08, S09, S17, S26, S31 and S32; principle 9 of `AGENTS.md` carries the rule. Gate G6 was
written for a queue that refuses: the planner words it again with the manifest of S41 and
brings it to the owner then.

**Questions that do not block are written down.** A gap that a sprint finds and that no
brief, decision or rule answers is entered in the [open questions](open-questions.md),
with what the code does in the meantime. The coordinator names the new entries when a
sprint closes, and all of them are gone through with the owner when the epic closes.

**Options, not constants.** A timeout or a limit that an application can observe is an
option with a default, validated at open. The owner added this to principle 9 of
`AGENTS.md` on 14 November 2026; every brief below follows it. By the owner's answer to
Q28 of the [open questions](open-questions.md) it speaks of a running instance: a rule of
the schema language, such as the depth of a declaration, is fixed and the same in every
process, and principle 9 says so since S03.

**The exceptions register.** A departure from a rule of `AGENTS.md` is recorded in
[exceptions to the rules](exceptions.md) with its reason: entered when the owner approves
it, and moved to **In force** by the change that introduces it.

**O21. Values received from another node stay trusted.** Decided on 18 November 2026,
when an outside reviewer called it a P1. The planner asked whether values received from
another node stay trusted or a validator on receipt is added; the owner, as the planner
relays, answered that they stay trusted and that it is written into the decision, so that
other reviewers see it and it does not keep coming up. A node checks the structure of everything it receives and
does not validate the values of fields again; the decision, its consequence and what
remains a defect are in
[trust between the nodes of one database](../decisions/replication-and-hosting.md#trust-between-the-nodes-of-one-database).
*Not chosen:* a second validator over received bytes, on the master under the lock of
the writes and for every root of a load.

### Where two design documents differ

A comparison of the design documents on 14 November 2026 found the seven places below.
The owner confirmed D1, D3 and D6, which change what a caller observes or what a gate
measures; for the others the plan names the document it follows.

**D1. Which writes stop during a schema migration.** The
[decision](../decisions/schema-and-types.md) stops reads and writes of the affected data
and left the exact scope open; the
[milestone contract](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration)
pauses all writes, drains the writer and has every child reload fully. *Confirmed: the
milestone contract.* On the master all writes pause and reads of unaffected entity types
continue; on a child every read waits within the reload bound. A migration is planned as
a stop of writes for the whole database. Shapes S37–S38.

**D2. Journal triggers.** The milestone contract starts with 64 MiB or 5 seconds of
unapplied journal and, by the owner's decision of 15 November 2026, no hard limit unless
an application sets one; the
[journal decision](../decisions/checkpoint-and-journal.md) and the historical part of the
performance requirements name 256 MiB and 30 minutes as a trial value that was not
approved. *The milestone contract prevails*; S31 uses its values and reports how they
behave.

**D3. The position of an empty database.** [Session consistency](../decisions/session-consistency.md)
treats position zero with a valid history as the initial prefix; the milestone contract
defines a wire position as a positive sequence; the placeholder returns sequence zero for
a new database. *Confirmed:* a new database has a valid position — its history and
sequence zero, nothing applied yet — and the first committed transaction gets sequence
one. A positive sequence is what a commit is given; the initial prefix stays
representable in a session and on the wire. Shapes S06, S10.

**D4. How a revision relates to a position.** Every document keeps the two apart, while
the milestone contract builds a new revision from the position of the operation. *Both
hold:* inside the engine a revision binds history, entity and position; in the public
contract it stays opaque. A caller may compare two revisions of one entity for equality
and nothing else: no order, no conversion to a position, no meaning across entities.
S06 states this in the documentation of `LatticeRevision`; S17, where two entities can
first change at one position, tests that they do not share a usable revision.

**D5. "v1" and the first milestone.** Several accepted rows of the
[register](../design/questions.md) say "v1" for features the milestone defers, such as a
query language with its scan fallback. *The planning handoff prevails:* every requirement
above is taken from it and from the milestone contract, never from a "v1" label.

**D6. The mix of the reference fixture.** The performance requirements ask for one entity
change per write call at 200 changes a second; the fixture reaches 200 with 192
single-entity calls and 4 two-entity batches a second. Its read mix contains 5% range
reads with Limit 20, while the 1 ms target names identifier and exact-index reads.
*Confirmed:* the fixture is the ordinary profile, and each target is judged on the
operation it was accepted for. G3 is judged on the single-entity calls; batches are
reported as their own class and have no threshold. G2 is judged on identifier and
exact-index reads; range reads are reported as their own class against the same figure,
and a miss there does not block the epic but is reported to the owner as its own line.
Shapes S25, S40.

**D7. Starting values the records call illustrative.** The 30-second reload wait, the
pages of 4,096 root entries and the 1 MiB cap of an entity are starting values in the
milestone contract and "not accepted defaults" in their decision records. *The milestone
contract prevails as a baseline to measure.* They are verified where their mechanism is
delivered: the root-entry pages in S04, the entity cap in S05 and S09, the reload wait in S34. A value that fails its check is changed on the evidence and reported; no guarantee
changes with it. The cap of an entity was removed by the owner's decision O19 of 15
November 2026: an entity has no limit of its own.

### Choices made in the contracts of S02 to S12

The blocks "Types and members" settle the following points, which no decision record
settles. They are approved with the plan, each by name.

- **The descriptor is closed.** A property the reader does not know is `InvalidSchema`.
  Before S02 such a property was ignored, and a misspelt `Comparison` would silently
  have given the wrong comparison. By the owner's answers of 14 November 2026 to Q7 and Q8 of the
  [open questions](open-questions.md), the same holds for a property in a place where it
  does not apply — a text policy on a field that is not a String, an enum type named by
  a field that is not an Enum — and for a property given twice.
- **The string encoding of a database is declared in its schema**, as `StringEncoding`.
  The decision makes it a setting fixed when the database is created and leaves its
  place open; in the schema it is saved, loaded and bound into the fingerprint with
  everything else that decides the layout.
- **Identities stay `int` in the public model** and must be at least 1. The milestone
  contract calls them positive UInt32; the engine keeps them as UInt32, and every value
  an `int` can hold from 1 up fits.
- **The value of an enum member is `Int128`**, the one type that holds every value of the
  eight integer types without reinterpreting bits.
- **A fingerprint covers what decides layout, comparison and constraints**, and not
  names, `ClrType`, the catalog version or the order of the descriptor; the list is in
  the block of S02.
- **In a bound class a collection is `List<T>`, `T[]`, `Dictionary<K, V>` or
  `HashSet<T>`.** Interface types are refused for now and can be added without breaking
  anything.
- **A read budget is two options of the database**, `ReadMemoryLimit` and `ReadTimeLimit`,
  none set by default; they hold for every enumeration and, from S15, every search, with
  one error code and the exceeded limit named in `Limit`. No call carries limits of its
  own (the owner, 16 November 2026).
- **An address is `lattice://host:port`**, and `ILatticeDatabase.ListenUri` reports the
  address a master actually listens at, so that port 0 can be used.
- **A field is its identity, its name and its declaration.** `LatticeField` loses `Type`,
  `IsNullable` and `ValueType`, which `Declaration` now holds; `LatticeIndex` loses
  `Fields` in S03, which `Parts` replaces. Nothing is released, so nothing is kept twice.
- **Two fingerprints.** `LatticeSchema.Fingerprint` binds a child to its master and
  covers layout, comparison and constraints; `LatticeEntityType.LayoutFingerprint`
  covers how one entity type is stored and tells a migration which types to convert.
- **The identity of a database is a `Guid`**, and the wire protocol starts with the magic
  `LATT` and version 1. The design gives neither a value.
- **The reference fixture has a scalar form** for the sprints that cannot store its
  collections, Dictionary strings and unique indexes yet. The gates use the full form
  only.
- **What the placeholder does with what is added before S12.** A new option is validated
  and otherwise not used by the placeholder, and its documentation says so until S12. A
  new member the placeholder cannot honor throws `NotSupportedException` there, as
  everything not implemented does: the enumeration with limits of S08. `ListenUri`
  returns the configured address, which is the truth for the placeholder.

## Sprints

### Rules for every sprint

- The [development process](process.md) applies. On the full track one implementer puts
  the contract into code and writes the tests in commits that come before any of its
  implementation; the reviewer and the breaker give the independent view.
- A sprint on the full track has its own branch, `epic-1/sprint-NN-name`, cut from
  `main` and merged back when the sprint closes, as [branches and releases](branches.md)
  says; the light sprint S01 uses a light-track branch. One sprint runs at a time. `main` stays green after every merge: what is not implemented refuses and
  its tests are skipped.
- Every document under **To read** is read at its baseline of 14 November 2026. If one
  changes, the coordinator stops and has this plan corrected.
- The briefs of S02 to S12 name their types and members exactly, under "Types and
  members"; the owner approves them with the plan, and the implementer of the sprint puts
  them into code as written. A later brief says what its contract must be able to do, and the
  planner writes its types and members before its stage begins: S13–S17, S18–S25,
  S26–S32, S33–S36, S37–S39. The owner approves the contract of each stage before its
  first sprint, as the [development process](process.md#the-plan) says.
- A feature is delivered for entity classes and for records by name in the same sprint.
- From S12 on, a sprint that changes what is stored also delivers what a child needs:
  the transfer of the new structure in the initial load, its live application, and a
  test in which a child loads it and follows changes to it. From S30 on, the same holds
  for the saved base, the journal and recovery.
- A sprint that adds a public behavior adds or extends its sample.
- A timeout or a limit that an application can observe is an option with a default,
  never a constant in the code. It becomes an option in the sprint that owns the
  mechanism, with the starting value of the
  [engine contract](../design/milestone-1-engine-contract.md#initial-bounded-settings),
  and all options are validated together at open.
- A departure from a rule of `AGENTS.md` is approved by the owner, entered in
  [exceptions to the rules](exceptions.md) when it is approved and moved to **In force**
  there by the change that introduces it.
- The physical specification is not repeated here. A brief names the section that owns it.
- A verification sprint is on the full track: its implementer writes the scenarios and
  the harness in step 1 and then fixes what they expose.
- A test that only asserts that a feature is not implemented, such as
  `Search_is_not_implemented_yet`, is removed by the implementer in step 1 of the sprint
  that implements the feature. "Assertions unchanged" speaks of the tests of the
  behavior.
- An acceptance item that says what the code must not do — hold a lock across an `await`,
  perform I/O inside publication, run user code inside a gate — is checked by the two
  reviews of the sprint, and by a test where the brief names one.

### Dependency order

```text
S01 ground
 └─ S02 schema ── S03 schema of owned values and collections
     └─ S04 arena ── S05 root layout ── S06 one entity ── S07 key types
         └─ S08 enumeration, index growth ── S09 coordinator
             └─ S10 child loads ── S11 child follows
                 └─ S12 child writes, sessions; placeholder removed
                     └─ S13 text entries ── S14 exact search ── S15 ranges, binding
                         └─ S16 unique ── S17 write batch ── S18 owned values, lists
                             └─ S19 maps, sets ── S20 partial updates
                                 └─ S21 views by name ── S22 typed views
                                     └─ S23 compaction ── S24 memory budget
                                         └─ S25 memory gate ── S26 operation identity
                                             └─ S27 provider ── S28 durable commit
                                                 └─ S29 storage failures ── S30 saved base
                                                     └─ S31 journal limits ── S32 recovery
                                                         └─ S33 bounded delivery
                                                             └─ S34 reconnect ── S35 cluster
                                                                 └─ S36 restore ── S37 ── S38
                                                                     └─ S39 ── S40 ── S41 ── S42
```

Why this order:

- **One vertical slice first.** S04–S12 take scalar entities from native memory to a
  child over TCP. It ends the life of the placeholder early; from then on every feature
  is tested only through the public API, on both roles.
- **The coordinator before anything that commits.** Its commit stage has a storage step
  from the start, which completes at once in MemoryOnly. Replication reads the published
  stream and does not care whether the commit was durable, so it can come before
  PostgreSQL without rework.
- **Text entries before secondary indexes.** An index shares text through stable text
  entries.
- **The full type model before persistence.** The journal, the applier
  and the operation fingerprint all encode ordered commands; built after S20 they see the
  whole vocabulary once. The wire format starts in S11 with complete writes and gains
  each command in the sprint that adds it. It also lets S25 measure the memory gate on one node before the
  second half of the epic.
- **Operation identities before PostgreSQL.** The durable commit transaction is defined
  by the receipt protocol: admission first, then effects and outcome together.
- **Migration and diagnostics last among the features.** Migration needs the saved base
  and the reload of children; diagnostics report counters that exist only when their
  mechanisms do.
- After S17 nothing in the core blocks the MemoryOnly stages of Epic 2. Whether that
  epic starts before this one ends is the owner's decision and not part of this plan.

### Test lanes

| Lane | Contains | Runs locally | Runs in CI |
| --- | --- | --- | --- |
| Default | Contract tests, engine component tests, master and children in one process over loopback TCP, with a port chosen by the system or reserved by the test when the address has to be known before the master opens, allocation tests | `dotnet test` from `src`; no service needed; stays within about a minute | The required `build` job, as today; a second job runs the same lane on Windows |
| PostgreSQL | `Moltaro.Lattice.PostgreSql.Tests`: provider, durable commit, base, recovery | With `LATTICE_TEST_POSTGRES` set to a connection string whose role may create databases. Every test class creates a database with a unique name and drops it. A test that restarts the server starts its own `postgres:18` container and needs Docker; without Docker it reports itself skipped with that reason | A job of its own on every pull request; it starts a `postgres:18` service container and sets the variable |
| Cluster | `Moltaro.Lattice.Cluster.Tests` starts `Moltaro.Lattice.TestNode` processes: one master and its children on loopback, with killing and reconnecting | With `LATTICE_TEST_CLUSTER=1` and the PostgreSQL lane enabled, because the master is Durable; three children by default | A job of its own on every pull request with three children; 5 and 10 children in a job started by hand |
| Measurements | `Moltaro.Lattice.Measurements`, a console program: the fixture, the managed reference, the load profiles, the check of the machine. Not a test project | Started by hand with a manifest, the nodes in containers with limits; raw output under the ignored `src/.local` | A job started by hand, to see that it runs; a gate is judged only on the reference machine |

- A lane whose variable is absent reports its tests as skipped with the reason "the lane
  is not enabled". That is a missing prerequisite, not a hidden failure: CI always
  enables both lanes, and the coordinator verifies a sprint with the PostgreSQL lane enabled from S27 on, and
  with the cluster lane as well from S35 on.
- The default lane runs in three jobs: `build` on Linux x64, `windows` on Windows x64 and
  `arm64` on Linux ARM64, a runner of four processors. The third was added on the
  owner's decision of 19 November 2026: reads take no lock and a write is published by
  one store, and the memory ordering of x64 hides a class of faults that ARM64 does
  not. A failure on ARM64 that the other jobs do not show is a finding about the engine
  first: it is reported with the name of the test before anyone changes the test.
  Longer race runs on a machine with many ARM cores are a later step.
- **Tests that depended on the load of the machine (19 November 2026).** Ten repeats of
  the suite in Release beside three other builds and suites on the workstation had three
  failures in replication tests, and twelve repeats on a quiet workstation none. Three
  tests raced one second of wall time and were given a hold the test controls or a wait
  of 5 s: `OpenTimeoutTests.Load_that_takes_longer_than_the_OpenTimeout_while_its_parts_keep_arriving_becomes_ready`,
  `IdleMasterTests` and `SilentPeerTests`; each then passed 50 of 50 under a loaded
  machine. The evidence is the margin of each wait, about 0.5 s before and at least 4.5 s
  now: the load of the author did not break the old tests either, so no failure was
  reproduced and then cured. Two failures stay unexplained, with what was ruled out: a
  master that could not listen, "the system lacked sufficient buffer space" (the suite
  holds about 28 listeners at its peak and leaves none behind a passing test; nothing is
  retried, in the engine or in a test), and one failure of `AnnouncedLengthTests` in the
  handshake, which has no short wait. The output of every repeat is kept, so that the
  next one has a name and a message.
- **What became of it (closed on 20 November 2026).** By the rule of the owner nothing
  open is handed on, so each item states its result:
  - *A handshake that failed once.* `AnnouncedLengthTests.Request_whose_frames_hold_more_than_it_announced…`
    failed in the handshake in one run of the engine tests in Debug on 19 November 2026,
    while two other agents built and ran suites on the workstation, and passed three of
    three alone; its message was not kept. Ruled out: a short wait in the test (it has
    none: bounds of one minute, timeouts of 30 s). Done: a soak of 500 runs of the
    replication tests of the engine, 244 tests a run, 250 runs in Debug and 250 in
    Release, four loops in parallel beside burners on every logical processor, the output
    of every run kept. It did not appear. Closed as not reproduced in 500 runs under load;
    nothing is retried anywhere.
  - *A master that could not listen.* `ClosedFrameTests.Closed_that_is_not_valid_ends_the_connection_and_a_request_without_a_reply_in_IndeterminateOutcome`
    failed once in ten repeats in Release on the same day under the same load: the bind
    of the listening socket reported that the system lacked sufficient buffer space or a
    queue was full. Ruled out: a leak of the tests (about 28 listeners at the peak of the
    suite, none left behind a passing test) and the range of ports (510 connections
    against about 14,000 ports under the heaviest load). Suspected and not shown: a
    shortage of the system as a whole (the commit charge stood at 59 of 72 GB under
    load). It did not appear again in the soak. The helpers through which tests open a
    listening master now report, when a socket error ends the open, the state of the
    system: the socket error, the commit charge and its limit, the available memory, the
    handles and threads of the process, and the listeners and connections of the machine
    (`ListenFailure` and `SystemState` in `Moltaro.Lattice.TestSupport`). It is diagnosis
    only and nothing is retried, so that a recurrence explains itself. Closed as a
    shortage of the system under load, instrumented.
  - *A forwarded write the child did not apply in ten seconds.* `CancellationTests.Write_the_sender_has_not_taken_is_withdrawn_by_its_cancellation_and_never_executed`
    failed once in five repeats in Release on 19 November 2026, on the engine of `main`
    (the test and the engine unchanged by the change above), while another agent was
    building and measuring on the workstation: "The write is committed by the master at
    position …-1, but this child has not applied it: This node did not reach position
    …-1 within 00:00:10", from `Forwarder.WaitForApplicationAsync`. Ten seconds is long
    for a loaded machine, so this was examined as a defect of the engine first: a child
    that does not apply, or is not woken for, the first transaction after its load. No
    defect of the engine was found. The write that reported it was the setup write of
    32 MiB, which the sender had already taken, not the cancelled one: the withdrawal
    worked. Read with their lines and ruled out: a write between the end of a child's
    load and the start of its sender; the wake of the sender; a large transaction waiting
    in its middle; a follower that stopped, which would end the waiter at once with "lost
    its master"; a lost wake-up of the waiter in `PositionClock`, because the waiter
    publishes its target before it reads the sequence and the publication is followed by
    a full fence before the targets are read; a waiter for a position equal to the one
    just reached, which is woken; the withdrawn call left in the sender's queue, which is
    skipped. On the engine of `main`, 3,000 forwarded writes that each waited for its own
    application and 3,000 waits for the exact committed position all passed, and the test
    with the child's application held for 11 s fails with the identical message. Not
    reproduced in 484 runs: 200 of 200 alone, 259 of 259 beside 20 burners and a loop of
    the engine suite, 25 of 25 inside runs of the suite. On an idle machine the
    transaction arrives whole about 170 ms after the wait begins, so the failure needed a
    stall some sixty times longer; what stalled that one run is not shown. The defect was
    the test's: it put a write of 32 MiB under the default synchronization timeout of
    10 s, which is not what the test is about. Corrected: the tests whose setup is large
    (`CancellationTests`, `AcknowledgmentDuringARequestTests`, the test of 32 MiB of
    `MasterAndChildTests`, `ChildIndexTests`) open their child waiting beyond the bound,
    and the forwarding tests among them that put a write of 32 MiB through the child,
    `AcknowledgmentDuringARequestTests` and both tests of `CancellationTests`, report the
    child's applied position and the state of the system when that wait ends by a timeout
    (`ApplicationWait`).
  - *A read time limit of 100 ms against the machine.* `ReadLimitsTests.Cancellation_of_an_enumeration_under_read_limits_is_OperationCanceledException_and_never_ReadLimitExceeded`
    failed once with the workstation at full load, and
    `ReadTimeInsideTheEngineTests.Step_that_waits_inside_the_engine_longer_than_the_time_limit…`
    twice in 25 runs of the suite. The engine was right: it checks the token before every
    limit, and the step that failed was one whose token was not cancelled. The tests of
    the read time limit (`ReadLimitsTests`, `ReadTimeLimitTests`, `CancelledStepTests`,
    `ReadTimeInsideTheEngineTests`) now use times the test controls, kept once in
    `Moltaro.Lattice.TestSupport` (`ReadTimes`: a limit of 2 s, controlled waits of 3 s).
    The review showed with throwaway changes of the engine that they still catch what they
    exist for: counting the caller's time fails both tests of the limit; not counting the
    wait for a slot fails both tests of the time inside the engine; a cancelled wait that
    reports the limit first fails exactly the two cases meant for it. One order had no
    test, the token before the limits inside a round; it has one now, which fails ten runs
    of ten with the two checks swapped in the engine.
  - *An Applied ahead of the send that the master accepted.* `AppliedTests.Master_ends_the_connection_of_a_child_that_acknowledges_a_transaction_whose_last_frame_it_has_not_been_sent`
    failed once in the 500 runs of the soak: the master did not end the connection within
    the bound of one minute. The engine was right. Forced both ways on the engine as it
    is: a peer whose receive buffer is 64 MiB takes all 16 MiB with the last frame, so the
    master has queued the last frame, accepts the Applied and keeps the connection, which
    is the failure, deterministic; a peer whose receive buffer is fixed at 64 KiB before
    it connects holds 64 KiB, and the master ends the connection. The test rested on how
    much the loopback buffers of the kernel happen to take. That the window grew in the
    failing run is inferred, not reproduced: the default stayed 64 KiB in 20 of 20 tries
    under load. Corrected at the two helpers every such test goes through: `Peer` and
    both sockets of `LoopbackLink` fix their receive buffer at 64 KiB before they connect.
    Fifty runs of the replication tests under load followed, and none failed. On Linux a
    send buffer may grow to 4 MiB, so the cases of 8 MiB have a margin of about two there.
    Left, with its place: the first test of `ChildCloseTests` depends on the receive
    buffer of the engine's own child and has a margin of two over the largest window of
    Windows (16 MiB); S33 gives it a form that does not rest on it.
  - Known and left: `SilentPeerTests` asserts only that the master does not give up
    before its timeout; its upper limit is the bound of one minute.
  - The evidence for the rewritten tests is the margin of each wait and the forcing
    experiments, not a failure reproduced and then cured, except where it says
    "deterministic".
- The `build` job keeps its name, because the ruleset of `main` requires that check. The
  PostgreSQL job and the cluster job become required checks too: the sprint that adds a
  lane adds its job to `.github/rulesets/main.json`, and the owner applies the ruleset.
- A crash test never kills a process the test run did not start, and never stops the
  PostgreSQL server of the machine; a server restart is tested against a container the
  test owns.
- Each project is created by the first sprint that needs it: the measurement program in
  S05, the PostgreSQL tests and the node program in S27, the cluster tests in S35.
- Measurements use a plain bounded harness, as the prototypes do; no benchmark package is
  added without the owner's decision.

### Status

| Sprint | Result | Track | Status |
| --- | --- | --- | --- |
| S01 | Assemblies, package ownership, the default lane | Light | Done |
| S02 | Schema: identities, text policies, enums | Full | Done |
| S03 | Schema and values: owned types, collections, references | Full | Done |
| S04 | Arena | Full | Done |
| S05 | Root layout and values | Full | Done |
| S06 | Reads and writes of one entity | Full | Done |
| S07 | Every key type and declared value | Full | Done |
| S08, part 1 | The protection of reads: reads by key scale with threads | Full | Done |
| S08, part 2 | Enumeration under change and the read limits | Full | Done |
| S09 | Write coordinator and write limits | Full | Done |
| S09, part 2 | The write path after a proven source | Full | Done |
| S10 | A child loads the database | Full | Done |
| S11 | A child loads under writes and follows the master | Full | Done |
| S12 | Writes through a child, sessions; the placeholder is removed | Full | Done |
| S13 | Text entries and Dictionary strings | Full | Done |
| After S13 | The fixes after the outside review | Full | Done |
| S13R, part 1 | Refactoring that owes no measured figure | Full | Done |
| S13R, part 2 | Refactoring that owes a measured figure | Full | Done |
| S14 | Secondary indexes and exact search | Full | Done |
| S14, part 2 | The cost of index upkeep and of a search | Full | Done |
| S14R | Refactoring after S14: structure only | Full | Done, in two parts, on 21 November 2026 ([refactoring backlog](refactoring.md), its order) |
| S15, part 1 | The walk: leading parts, ranges, both directions, Offset and Limit, at most once | Full, critical | Done on 21 November 2026 |
| S15, part 2 | The binding rules | Full | Done on 21 November 2026 |
| S15R | Refactoring after S15 | Full | Done ([refactoring backlog](refactoring.md), its order) |
| S16 | Unique indexes | Full | Not started |
| S17 | Atomic write batch | Full | Not started |
| S18 | Owned values, Lists and references | Full | Not started |
| S19 | Maps and Sets | Full | Not started |
| S20 | Partial updates | Full | Not started |
| S21 | Protected views by name | Full | Not started |
| S22 | Typed protected views | Full | Not started |
| S23 | Compaction and shrink | Full | Not started |
| S24 | The memory budget | Full | Not started |
| S25 | Reference fixture, managed reference, the memory gate | Full | Not started |
| S26 | Operation identity and safe retry | Full | Not started |
| S27 | PostgreSQL provider and Durable open | Full | Not started |
| S28 | Durable commit | Full | Not started |
| S29 | Storage failures and the loss of leadership | Full | Not started |
| S30 | Saved base and background application | Full | Not started |
| S31 | Journal limits, cleanup and receipt retirement | Full | Not started |
| S32 | Recovery under failure | Full | Not started |
| S33 | Bounded delivery and slow children | Full | Not started |
| S34 | Reconnect, reload and a restarted master | Full | Not started |
| S35 | Cluster verification | Full | Not started |
| S36 | Restore and a new history | Full | Not started |
| S37 | Schema migration on the master | Full | Not started |
| S38 | Migration across the database | Full | Not started |
| S39 | Diagnostics | Full | Not started |
| S40 | Ordinary-load gates | Full | Not started |
| S41 | Burst and recovery profile | Full | Not started |
| S42 | Close | Full | Not started |

### Remarks left for the coordinator

The seventh review left four remarks that do not block. They are not corrected here, so
that the text the owner approves is the text that was reviewed; the coordinator settles
each when its sprint is prepared.

1. Exception X3 in the [exceptions register](exceptions.md) says the measurement program
   "opens it through its internal entry", while S12 removes that entry and corrects only
   X4 in the register. S12 corrects the wording of X3 as well.
2. The brief of S12 does not say in one sentence that the move of `LatticeDatabase` and
   the deletion of the placeholder are made by the implementer in the implementation
   commit. It follows from the list of edits, and it is how the sprint is to be run: the
   contract commit changes only the documentation reference.
3. The block of S06 does not say who gives `Moltaro.Lattice.Tests` its reference to the
   engine assembly and its friend access. It is the test author, in the tests commit,
   with the helper that needs them.
4. In S12 the edit of the documentation of `ILatticeDatabase` is presented under the rule
   on edits that state nothing new, yet it is assigned one commit earlier than the commit
   that would break. It is harmless and keeps the roles apart.

The coordinator added the remark below while S01 was reviewed. It is settled in the same
way, when its sprint is prepared.

5. S01 created `Moltaro.Lattice.Engine` as a project that is not packed: it had no code,
   and the Publish workflow packs every packable project on a release tag. From S12 on
   the dependency injection package references the engine, and a package that depends on
   one that is never published cannot be restored; nothing in the build shows it. In the
   implementation commit of S12 the implementer makes the engine project packable and
   corrects the sentence of `README.md` that names the published packages. The brief of
   S12 does not list this edit; the coordinator enters it there when S12 is prepared.
6. The brief of S12 has the test author change "the address helpers of the two test
   projects". Since decision O13 there is one, in `Moltaro.Lattice.TestSupport`, and that
   one is changed.
7. `Moltaro.Lattice.TestSupport` holds two unrelated types at its root, which is fine for
   two files. The sprint that next adds shared code to it — S22 or S27 at the latest —
   gives the project folders by responsibility, as `AGENTS.md` asks of every assembly;
   its brief says so.
8. By the owner's answer to Q12 of the [open questions](open-questions.md), S02 added a
   sentence "Not implemented yet" to the documentation of `LatticeIndex.IsUnique`, of
   `LatticeErrorEnum.UniqueConstraintViolation` and to the remarks of
   `ILatticeSet<TEntity>`. S16 implements unique indexes and removes the three sentences;
   the planner enters it in the brief of S16.
9. S04 gave `Moltaro.Lattice.Engine.Tests` a test collection for the tests that measure
   managed allocation: it runs alone and inside a region in which the runtime starts no
   garbage collection, because a collection caused by another test moves the counter of
   the measuring thread. Every later test that asserts an exact amount of managed
   allocation joins it. S06 and S07 assert the allocation of reads; if such a test lands
   in `Moltaro.Lattice.Tests`, the collection and its attribute move to
   `Moltaro.Lattice.TestSupport` in that sprint, as decision O13 says of code that two
   test projects need, and the brief given to the test author says so.

### Stage 0. Ground

#### S01 · Assemblies, package ownership and the default lane

**Track:** light · **Decisions:** O1, O5

- **Goal.** The repository has a place for the engine, with its dependency declared once
  and its direction fixed, before any engine code is written.
- **Contract.** None. New projects `Moltaro.Lattice.Engine` and
  `Moltaro.Lattice.Engine.Tests`, in the solution; `Arc.Collections` pinned to exactly
  2.2.0 in `Directory.Packages.props` and referenced by no other shipped project; friend-assembly
  access from the contract to the engine and from the engine to its tests; the scenario
  tests of `Moltaro.Lattice.Tests` get the same access in S06, for their helper.
- **To read.** [Handoff, planning obligations](../design/planning-handoff.md#planning-obligations);
  [index structures](../decisions/index-structures.md#selected-implementation-arccollections-unorderedmapslim);
  [public distribution](../design/public-distribution.md); [branches and releases](branches.md);
  `AGENTS.md`.
- **Acceptance.** The solution builds and every existing test passes. `Moltaro.Lattice`
  still has no package reference, checked by a test of the built assembly's references.
  The `build` job also runs `dotnet format --verify-no-changes`; a second job runs the
  default lane on Windows. The license of `Arc.Collections` is recorded in
  `THIRD-PARTY-NOTICES.md` if its terms require it. The layout tables of `AGENTS.md` and
  `README.md` name the new projects. `AGENTS.md` gets the two texts below, word for word,
  and exception X3 moves to **In force** in the [exceptions register](exceptions.md).
- **Out of scope.** Any engine code; the PostgreSQL and cluster projects; moving the
  entry point, which happens in S12.

Text approved by the owner on 14 November 2026 for principle 11 of `AGENTS.md`:

> **Dependencies point one way.** Three assemblies make the database. `Moltaro.Lattice`,
> the public contract, depends on nothing outside .NET. `Moltaro.Lattice.Engine` depends
> on the contract and owns the index package. `Moltaro.Lattice.PostgreSql` depends on the
> engine and owns the PostgreSQL driver. No other shipped assembly references those two
> packages.
> Inside an assembly a folder is a namespace and one responsibility, and folders depend
> on each other in one direction. An integration is its own thin assembly, named for what
> it is for.

Text approved for the first rule of "Tests And Samples":

> Tests use the public API, with two exceptions. The internal switch that holds back a
> child's replication, so that waiting can be tested deterministically. And the three
> test projects of the engine — `Moltaro.Lattice.Engine.Tests`,
> `Moltaro.Lattice.PostgreSql.Tests` and `Moltaro.Lattice.Cluster.Tests` — which test
> mechanisms that have no public surface: layout, allocation, ownership, what the
> provider stores, failures injected into the engine or into a connection. Each such
> test states an invariant written in a decision record or in the engine contract, never
> how the code happens to be built. Two programs that are never shipped, the node program
> of the cluster tests and the measurement program, have the same access under the same
> condition.

**Added by decision O13, while the sprint was reviewed.** New project
`Moltaro.Lattice.TestSupport`, in the solution: a library that is not packed, holds no
tests and is referenced by the test projects that need it. It takes the helpers of the
dependency tests of this sprint, and the two helpers that `TestData` of
`Moltaro.Lattice.Tests` and `TestApp` of `Moltaro.Lattice.DependencyInjection.Tests`
both had: the new address and the wait for a condition. Their uses move to the shared
code by the rule on edits that state nothing new, P10, and no assertion changes: in
`Moltaro.Lattice.Tests` the classes `ConsistencyTests`, `DynamicSetTests`, `IndexTests`,
`MasterAndChildTests`, `OpeningAndClosingTests`, `UniqueIndexTests`, `WriteBatchTests`
and `TestData`; in `Moltaro.Lattice.DependencyInjection.Tests` the classes
`DependencyInjectionTests` and `TestApp`. A use the build finds outside this list is
reported to the coordinator, who enters it here, and is then edited in the same way. The
layout tables of `AGENTS.md` and `README.md` name the project, and "Tests And Samples"
of `AGENTS.md` gets the text below, word for word, after the rule on the public API.

Text approved by the owner on 14 November 2026:

> Code that two or more test projects need lives once, in `Moltaro.Lattice.TestSupport`,
> and is not copied between them. That project is never shipped and contains no tests.
> What only one test project uses stays in that project.

### Stage 1. Schema

Both sprints work in the contract assembly and need no engine.

#### S02 · Schema: identities, text policies and enums

**Track:** full · **Needs:** S01 · **Decisions:** O10, P10 · **Owner:** E05

- **Goal.** A schema declares everything the accepted scalar model has, and compiles to a
  catalog with stable identities, a version and a fingerprint.
- **Contract.** In the descriptor and the schema model: the top array is `EntityTypes`
  instead of `Tables`; a required `Id` of an index, and no `Kind`; on a
  String field `Comparison` (`Ordinal`, `OrdinalIgnoreCase`) and `StringStorage`
  (`Plain`, `Dictionary`); for the database `StringEncoding` (`Utf8`, `Utf16`); enum and
  Flags declarations with their underlying type, members, aliases and canonical names,
  and a field of an enum type; `LatticeSchema` exposes its catalog version and its
  fingerprint. A .NET enum property binds to an enum field. The binary form of the
  compiled schema carries all of it under a new format version.
- **To read.** [Schema identity](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration);
  [Q03.1–Q03.8](../decisions/schema-and-types.md); [Q04–Q04.2](../decisions/strings.md);
  the [example descriptor](../design/examples/settings.schema.example.json).
- **Acceptance.** Identities are positive and unique within their scope; a duplicate, a
  zero or a missing one is `InvalidSchema` with the place named. Defaults are Ordinal,
  Plain, UTF-8, non-nullable. Enum rules: only declared values, an explicit underlying
  type among the eight integers, Int32 by default, Flags accept every combination of
  declared bits and zero, one canonical name per code. In a Flags enum each
  independent member has exactly one bit, and a member that names a combination contains
  only declared bits. Save and load keep every
  declaration. The fingerprint is equal for equal declarations, changes when a
  layout-relevant declaration or a `Unique` flag changes, and does not change on a
  rename. The layout fingerprint of an entity type changes when one of its
  stored fields changes, and not when an index, a comparison or a name does. Compiling the same descriptor twice gives the same fingerprint on Linux and
  Windows. The Identity package's descriptor and its validation follow the new form. The forced edits of this
  sprint follow the rule on edits that state nothing new, P10, and change no assertion.
  In the contract commit the contract author moves every use of `LatticeField.Type`,
  `IsNullable` and `ValueType` to `Declaration`: in the contract assembly `FieldValues`,
  `EntityBinding`, `IndexCriteria`, `SchemaBinaryFormat`, `SchemaBuilder` and the
  reference in the documentation of `LatticeRecord`; in the Identity package
  `LatticeIdentitySchema`; in the tests `SchemaTests`; in the samples `SchemaSample`.
  `Declaration` works in that commit for the fields that exist today, with `Ordinal` and
  `Plain` for a String, so the build and the tests stay green. The reader keeps reading
  the present form until the implementation commit. In that commit the implementer
  switches `SchemaJsonReader` and moves to the new form every descriptor of the
  repository and the two places in code that read one by its property names:
  `SchemaTests`, `TestData` and `UniqueIndexTests`; `TestApp` of the dependency injection
  tests; `AppSchema` and `app.schema.json` of the Identity tests; `directory.schema.json`
  and `UniqueIndexSample` of the samples; `identity.schema.json` and
  `LatticeIdentitySchema.CreateDescriptor` of the Identity package; the descriptors
  shown in `README.md`. A descriptor of a test that must fail to compile keeps exactly
  its one defect: it gets its index identities like the others. The tests and the sample
  of the new form are written by the test author with descriptors of their own, the
  tests skipped and the sample not yet listed in the program, until that commit. A use
  the build finds outside these lists is reported to the coordinator, who enters it in
  the brief, and is then edited in the same way; it is not edited silently.
- **Out of scope.** Storing or comparing values by these policies: in the placeholder an
  entity type that declares what it cannot honor is refused on write. Collections and
  owned types (S03). Migration between catalog versions (S37–S38).

**Entered by the coordinator in the contract step.** `LatticeFieldTypeEnum.Enum` exists
from the contract commit, while the reader of that commit cannot declare an enum type. So
that a descriptor or a saved schema that names the type `Enum` is still refused with
`InvalidSchema`, as it was before the member existed, the contract author made
`SchemaBuilder` treat `Enum` as an unknown type in that commit; the implementer replaces
the check when enum types can be declared. The scope of the identity of an index is its
entity type, as for a field: two entity types may each have an index with the identity 1.

**Added by decision O15, while the sprint was carried out.** A .NET enum of a bound
property has to agree with the enum type of its field: the same underlying type, the same
members with the same values in both directions, and `[Flags]` exactly when the enum type
is a Flags type. A class whose enum does not agree is refused with `BindingFailed`, with
the property named, when the class is bound. The sentence of the block about two names
for one value follows from it: a .NET enum that has two names for one value agrees only
with an enum type that declares both names, of which one is canonical. The test author
adds the tests after the implementation commit, skipped; the implementer then makes them
pass.

**Read from decision O2 for the tests.** On the placeholder a write is refused with
`NotSupportedException` for an entity type that declares a comparison that ignores case,
the Dictionary storage or an enum field, and for every entity type of a schema that
declares UTF-16: decision O2 has the placeholder refuse a declaration it cannot honor.

**Confirmed by the owner on 14 November 2026.** Two rules of the descriptor were not
given by the approved choice "the descriptor is closed", which spoke of a property the
reader does not know; they were raised as Q7 and Q8 of the
[open questions](open-questions.md), and the choice now names them. A text policy on a
field that is not a String, and an enum type named by a field that is not an Enum, are
`InvalidSchema`. A property that a descriptor gives twice is `InvalidSchema`; the
implementer added that rule with its test.

**Added by the owner's answer to Q12, in this branch.** The documentation of
`LatticeIndex.IsUnique`, of `LatticeErrorEnum.UniqueConstraintViolation` and the remarks
of `ILatticeSet<TEntity>` say, as `ILatticeIndex` does for search, that unique indexes
are not implemented yet: until then every write of an entity type that declares one is
refused with `NotSupportedException`. Documentation only; the contract author adds the
sentences, and S16 removes them.

**Raised by the fixes and confirmed by the owner on 14 November 2026.** The reader of a
saved schema was made strict after the first review: whatever is wrong with the bytes
ends in `InvalidSchema`, and text that is not well-formed UTF-8 is refused instead of
being replaced. With it came two rules that nobody had asked for; they were raised as Q13
of the [open questions](open-questions.md), and the owner confirmed them. `Load(path)`
refuses a file with data after the schema. `Load(stream)` reads one schema and leaves the
stream right after it: what follows in a stream belongs to the caller.

**Types and members of S02.** Namespace `Moltaro.Lattice.Schema`, in the contract assembly.

The descriptor:

```json
{
  "SchemaVersion": 2,
  "StringEncoding": "Utf8",
  "Enums": [
    {
      "Id": 1, "Name": "State", "UnderlyingType": "Int32", "Flags": false,
      "Members": [
        { "Name": "Disabled", "Value": 0 },
        { "Name": "Active", "Value": 1, "Canonical": true },
        { "Name": "Enabled", "Value": 1 }
      ]
    }
  ],
  "EntityTypes": [
    {
      "Id": 1, "Name": "User", "ClrType": "MyApp.User",
      "Fields": [
        { "Id": 1, "Name": "Id", "Type": "Int64" },
        { "Id": 2, "Name": "Login", "Type": "String", "Comparison": "OrdinalIgnoreCase" },
        { "Id": 3, "Name": "Region", "Type": "String", "StringStorage": "Dictionary" },
        { "Id": 4, "Name": "State", "Type": "Enum", "Enum": "State" }
      ],
      "PrimaryKey": [1],
      "Indexes": [ { "Id": 1, "Name": "ByLogin", "Fields": [2], "Unique": true } ]
    }
  ]
}
```

`StringEncoding`, `Enums`, `Comparison`, `StringStorage`, `Flags` and `Canonical` are
optional, with the defaults `Utf8`, none, `Ordinal`, `Plain`, `false` and "the only name
of its value". `UnderlyingType` defaults to `Int32`. A value with several names needs
exactly one `Canonical`. `Tables` is no longer read.

```csharp
public enum LatticeStringComparisonEnum : byte { Ordinal = 1, OrdinalIgnoreCase = 2 }
public enum LatticeStringStorageEnum : byte { Plain = 1, Dictionary = 2 }
public enum LatticeStringEncodingEnum : byte { Utf8 = 1, Utf16 = 2 }

public enum LatticeFieldTypeEnum : byte
{
    // existing members 1..19 unchanged
    Enum = 20,
}

public sealed class LatticeEnumType
{
    public int Id { get; }
    public string Name { get; }
    public LatticeFieldTypeEnum UnderlyingType { get; }      // one of the eight integer types
    public bool IsFlags { get; }
    public IReadOnlyList<LatticeEnumMember> Members { get; }
    public bool IsValid(Int128 value);                       // a declared value; for Flags any combination of declared bits, and zero
}

public sealed class LatticeEnumMember
{
    public string Name { get; }
    public Int128 Value { get; }
    public bool IsCanonical { get; }
}

// What one position holds. In S02 the position is a field; S03 adds the positions inside a collection.
public sealed class LatticeValueDeclaration
{
    public LatticeFieldTypeEnum Type { get; }
    public bool IsNullable { get; }
    public LatticeStringComparisonEnum? Comparison { get; }  // null when the type is not String
    public LatticeStringStorageEnum? StringStorage { get; }  // null when the type is not String
    public LatticeEnumType? EnumType { get; }                // null when the type is not Enum
    public Type ValueType { get; }                           // the .NET type of a non-null value at this position
}

public readonly struct LatticeFingerprint : IEquatable<LatticeFingerprint>
{
    public bool Equals(LatticeFingerprint other);
    public override bool Equals(object? obj);
    public override int GetHashCode();
    public override string ToString();                       // 64 hexadecimal digits
    public static bool operator ==(LatticeFingerprint left, LatticeFingerprint right);
    public static bool operator !=(LatticeFingerprint left, LatticeFingerprint right);
}

// LatticeSchema gains
public LatticeStringEncodingEnum StringEncoding { get; }
public IReadOnlyList<LatticeEnumType> EnumTypes { get; }
public bool TryGetEnumType(string name, [NotNullWhen(true)] out LatticeEnumType? enumType);
public LatticeFingerprint Fingerprint { get; }

// LatticeEntityType gains
public LatticeFingerprint LayoutFingerprint { get; }

// LatticeField keeps Id and Name, gets Declaration and loses Type, IsNullable and ValueType
public LatticeValueDeclaration Declaration { get; }

// LatticeIndex gains
public int Id { get; }
```

`LatticeSchema.Version` stays and is the catalog version. `ValueType` of an Enum position
is the .NET type of its underlying integer; a record holds that integer, and a class may
declare the property as a .NET enum with the same underlying type. A .NET enum with two
names for one value binds only when the schema declares that value with a canonical
name.

The fingerprint of a schema is SHA-256 over a canonical encoding of what decides how data is laid
out, compared and constrained: the string encoding; for every enum type its identity,
underlying type, Flags mark and the set of its values; for every entity type its
identity, its primary key and, for every field, the identity, type, nullability,
comparison, string storage and enum type; for every index its identity, its fields in
order and its `Unique` mark. Names, `ClrType`, the catalog version and the order in which
the descriptor lists things are not part of it. S03 adds owned types and the declarations
of collections and references in the same way.

The layout fingerprint of an entity type covers only what decides how one entity of that
type is stored: the string encoding, and for every field its identity, type,
nullability, string storage and the underlying type of its enum. It does not change
when a comparison or an index changes, or when another entity type changes in a way that
does not alter what this one stores. Two schemas whose entity
type has the same layout fingerprint store its entities alike; a migration converts the
entity types whose layout fingerprint differs.

#### S03 · Schema and values: owned types, collections and references

**Track:** full · **Needs:** S02 · **Decisions:** P9, P10 · **Owner:** E05

- **Goal.** A schema declares owned trees, List, Map, Set and references, and an
  application can build such a value as a record or as an object of its class.
- **Contract.** Declarations of owned value types with their fields; field types `List`,
  `Map`, `Set`, `Reference` and an owned type, with element, key and value declarations
  and explicit nullability in each position. `LatticeRecord` holds nested values and
  collections. The class binding accepts properties of nested classes and of the .NET
  collection types named below, and a reference as the key of its target.
- **To read.** [Q02, Q02.1, Q02.2](../decisions/document-model.md);
  [Q03.3](../decisions/schema-and-types.md), [Q03.4.2 with the collection input contract](../decisions/schema-and-types.md);
  [Binary fields](../decisions/schema-and-types.md).
- **Acceptance.** The compiler accepts complex values in List and Map and refuses complex
  or Binary keys and Set elements, a nullable Map key, and a reference to an undeclared
  entity type, each with the place named. By the owner's answer to Q9 an enum type that
  declares no member is refused with `InvalidSchema` and the enum type named, whether it
  is a Flags type or not; a Flags type whose only member is zero is accepted. By the
  owner's answer to Q10 a catalog version is a whole number from 1: `"SchemaVersion"` of
  zero or below is refused with `InvalidSchema` and the property named. A descriptor
  without the property has version 1, as before. A saved schema carries a digest of
  itself. A change of any single byte of a saved schema, the digest included, is refused
  with `InvalidSchema`: no change loads as another schema. A negative length of the body
  is refused. A length far beyond the data is refused and costs memory for the data only,
  as a count and a text length inside the body do since S02. A form whose digest is right
  is still read strictly: a saved schema with one byte added at the end of its body, and
  one with the last byte of its body taken away, each with its length and its digest
  written again to fit, is refused. A stream is left right after the digest, and a file
  with data after the digest is refused. A schema saved and loaded is
  equal to the one that was saved, with the same fingerprints. A record and an object are validated against the declaration when they are
  built or bound: wrong element type, null where it is not allowed, a duplicate Map key
  in bulk input. A Set built from input keeps the first representation of equal members.
  A class with inherited properties binds as before. By the owner's answer to Q14 the
  property of a field is found as C# finds it by that name on the class: the public
  property of the most derived class that declares one. It has to be an instance property
  with get and set and of the type the field needs. So a derived class that hides a
  property of its base with one of the type the field needs binds, and the field is read
  from and written to the hiding property; a class whose property of that name is static,
  and a class whose hiding property has a type the field does not accept, are refused
  with `BindingFailed` and the property named. Neither ends in another exception, from
  `Set<TEntity>()` or from the typed members of a write batch, and the same holds for the
  classes of owned types that this sprint binds. In a bound class an owned value is an
  object of the class of its owned type at every position: a List of owned values binds
  as `List<T>` or `T[]` of that class and a Map of them as a `Dictionary` with that class
  as its value, a .NET enum that agrees binds as an element as it does as a property, and
  the class of an owned type binds its own owned fields and collections in the same way.
  A field at any position whose owned type names no `ClrType`, and a property, an element
  or a value of type `LatticeOwnedValue` in a class, are `BindingFailed` with the
  property and the owned type named. A record holds a `LatticeOwnedValue` at every
  position, whether or not the owned type names a class. The identity and the name of an
  owned type are unique among owned types: a duplicate of either is `InvalidSchema` with
  the place named, and an owned type may have the identity or the name of an entity type
  or of an enum type. The identity of a field is unique within its owned type. A .NET
  class named by two owned types, or by an owned type and an entity type, is
  `InvalidSchema` with the class named. An owned type that contains itself is refused
  with `InvalidSchema` and the owned types on the way named: through a field of its own
  type, nullable or not, through a List element or a Map value of its own type, and
  through another owned type that contains it. An owned type with a Reference to the
  entity type that holds it compiles. A declaration is at most 32 deep, counted through
  collections and owned types together. A field whose declaration is 32 deep compiles,
  and one that is 33 deep is refused with `InvalidSchema` and the field named: through
  Lists alone, through a chain of owned types each holding the next, and through both
  mixed. A saved schema nested deeper than 32 is refused with `InvalidSchema` however
  deep it is, without exhausting the stack. A search key may give null for a part of an index
  when the indexed field is nullable or a step of its path is a nullable owned value;
  null for a part that can never be null is `ArgumentException`. The key is checked
  before the search is refused as not implemented: a key with a permitted null ends in
  `NotSupportedException`, one with a null that is not permitted in `ArgumentException`.
  An index part, plain or the end of a path, may be a scalar that is not Binary, an Enum
  or a Reference. A part that ends at an Owned field, a List, a Map or a Set is refused
  with `InvalidSchema`, with the index and the field named. A field of a
  `LatticeOwnedValue` accepts an integer of another width when it fits, as a field of a
  record does. A collection is accepted only with its exact element, key and value types:
  a `List<int>` for a List of Int64 is `ArgumentException` with the field named. A Map or
  a Set of references to an entity type whose key is a String compared with
  `OrdinalIgnoreCase` treats keys that differ only in case as one: such a duplicate Map
  key is refused, and such a Set keeps the first. When the comparison of the target's key
  changes, the layout fingerprint of the entity type that holds the collection does not
  change and the fingerprint of the schema does. A record copies what is set into it, at
  every depth. A change of the caller's collection, or of a collection inside it, after
  it was set is not seen through the record. A change of a `LatticeOwnedValue` after it
  was set into a record, into another owned value or into a collection of a record is not
  seen through the record either, and neither is a change of a byte array. One owned
  value set into two records gives two independent values. What a record returns is its
  own and the same object every time: a change made through a returned owned value or
  collection is seen through the record. What a `LatticeOwnedValue` returns is its own
  and the same object every time: a change made through a returned collection or owned
  value is seen through it. An owned value made invalid through a collection it returned,
  such as a null added where none is allowed, is refused with `ArgumentException` when it
  is set into a record, into another owned value or into a collection. A record made
  invalid through a collection it
  returned, such as a null added where none is allowed, is refused with
  `ValidationFailed` when it is written through a set or added to a write batch. A write
  through a set validates the object or the record before it is refused as not
  implemented: a null where none is allowed, or a duplicate Map key, at any depth is
  `ValidationFailed`, and a valid object or record of such an entity type ends in
  `NotSupportedException` until S18 and S19. A write of an entity type that the
  placeholder refuses checks first what needs no engine: an entity without a required
  value is `ValidationFailed`, and a key of another type is `ArgumentException`, for an
  entity type with a unique index as for any other; an entity that passes ends in
  `NotSupportedException`. An index part written as a list of one
  identity is the part of that field, and its index has the same fingerprint as with the
  plain spelling. An empty path, and the same part twice in one index in either spelling,
  are `InvalidSchema` with the index named. An owned type without fields, and one with an
  empty `ClrType`, are `InvalidSchema` with the owned type named.
  An index part that is a
  path is accepted when every step but the last is an owned value and the last is a
  field an index may have; a path through a collection, or to a field that does not
  exist, is refused with the place named. The layout fingerprint of an entity type changes when a field is added to an
  owned type it uses, when the declaration of a collection element changes and when the
  key type of an entity type it refers to changes; it does not change when an index or a
  comparison does. It changes when another field of the entity type becomes the primary
  key, and two entity types that differ only in their identity and their name have the
  same layout fingerprint. In the contract commit the contract author moves the uses of
  `LatticeIndex.Fields` to `Parts` by the rule on edits that state nothing new, P10: in
  the contract assembly `IndexCriteria` and `SchemaBinaryFormat`, in the Identity package
  `LatticeIdentitySchema`, in the tests `SchemaTests`. S02 added five more uses, which
  the coordinator entered here when S02 closed and which the contract author moves in the
  same commit: in the contract assembly `SchemaFingerprints`; in the samples
  `SchemaDeclarationsSample`; in the tests `SavedSchemaTests`, and `DescriptorFormTests`
  and `EnumTypeTests`, where the use stands inside an assertion — the assertion reads
  the same fields through `Parts` and asserts what it asserted. `Parts` works in that
  commit for the indexes that exist today. A use the build finds outside this list is reported to
  the coordinator, who enters it in the brief, and is then edited in the same way.
  The sprint extends what the layout fingerprint encodes, so the value S02 recorded for
  the fixed descriptor changes. That is a change of what a test asserts, so it is not
  made under the rule on edits that state nothing new: the test author makes it. In the
  tests commit the test author marks
  `Layout_fingerprint_of_a_fixed_descriptor_is_the_recorded_one_on_every_platform` of
  `LayoutFingerprintTests` as skipped with the shared reason of the sprint and changes
  nothing else in it. The implementer leaves that one test skipped. After the
  implementation commit the test author records `RecordedAccountLayoutFingerprint` again
  in a commit of its own — the value the test computes against the implementation,
  checked against an independent encoding of exactly the declarations the briefs list —
  and removes the skip. The fingerprint of a schema is extended in this sprint as well,
  to owned types, collections and the paths of index parts, and the brief does not fix
  how the extension is encoded, so the value recorded for the fixed descriptor of
  `SchemaFingerprintTests` may or may not stay.
  `Fingerprint_of_a_fixed_descriptor_is_the_recorded_one_on_every_platform` is therefore
  handled exactly as the layout test: the test author marks it as skipped in the tests
  commit and changes nothing else in it, the implementer leaves it skipped, and in the
  same later commit the test author removes the skip and, when the implementation
  computes another value, records `RecordedFingerprint` again under the same check. The
  answer to Q5 is no reason for that value to change: the fingerprint of a schema has
  covered the primary key since S02.

  The new form also forces edits on `DamagedCompiledSchemaTests`, which writes forms of
  S02 by hand. Its tests state behavior this sprint keeps — the reader is strict, and
  nothing is allocated by a declared size — in a form this sprint changes, so they are
  edited by the rule on edits that state nothing new, P10, in two steps. In the tests
  commit the test author adds to that class one helper that makes a whole form of this
  sprint from the bytes of a body — the magic, format version 5, the length of the body,
  the body, the digest — and one that writes the length and the digest of a form again
  after a test changed its body. The new tests of the sprint use them and are skipped; no
  existing test is touched in that commit. In the implementation commit the implementer
  moves every hand-written form of the class onto those helpers, so that each is a form
  of this sprint whose length and digest are right: the reader gets past the digest and
  refuses the form for the fault its test names, which is the first fault it meets in the
  body. What a body holds after that fault is not changed; some bodies end right after
  it, as they do today:

  - `WriteHeader`, `Bytes`, `Compiled` and `Field`, with the body as `Save` now writes
    it, and through them the forms of `Mark_that_is_neither_0_nor_1_is_refused`,
    `Declaration_whose_policies_do_not_fit_its_type_is_refused`,
    `Negative_count_is_refused`, `Text_length_of_more_than_five_bytes_is_refused`,
    `Text_length_far_beyond_the_data_costs_memory_for_the_data_only` and
    `Count_far_beyond_the_data_costs_memory_for_the_data_only`.
    `Hand_written_form_of_the_tests_below_is_the_form_that_Save_writes` is not changed:
    it is what shows that the hand-written form is the form of this sprint.
  - The literal bytes of `Text_with_a_negative_length_is_refused`, which become a body
    given to the helper.
  - `Name_that_is_not_well_formed_UTF8_is_refused_rather_than_replaced`, which writes
    the digest again after it changed the byte of the name.

  No assertion, no name of a test and no inline data changes; a comment that describes
  the form follows the form. Without these edits four tests would fail, because the forms
  that have to load would not, and the others would pass for a wrong reason, refused as
  an unknown format or for their digest. A test the new form breaks outside this list is
  reported to the coordinator, who enters it in the brief, and is then edited in the same
  way. `Every_truncation_of_a_saved_schema_is_refused`,
  `File_with_data_after_the_compiled_schema_is_refused` and
  `Stream_is_left_right_after_the_compiled_schema` hold for the new form as they are
  written and are not edited.

  `Every_single_byte_change_of_a_saved_schema_loads_or_is_refused` is superseded, not
  edited: the test of this sprint states more, that every such change is refused. The old
  test stays and passes until the new one runs. The test author removes it in the commit
  of its own that follows the implementation, the one that records the fingerprints, and
  names the reason in the commit message.
- **Out of scope.** Storing these values: writing an entity type that uses them is
  refused until S18 and S19. Partial commands (S20). Indexes on collection members,
  which are outside the milestone.

**Entered by the coordinator in the contract step.** The five new field types exist from
the contract commit, while the reader of that commit cannot declare an owned type, an
element or a target. So that a descriptor or a saved schema that names one of them is
still refused with `InvalidSchema`, as before, the contract author made `SchemaBuilder`
treat the five as unknown types in that commit; the implementer replaces the check. The
contract step also found four gaps in this brief, entered as Q15 to Q18 of the
[open questions](open-questions.md): the element type of a collection of owned values in
a bound class, an owned type that contains itself, the scope of an owned type's identity,
name and class, and a null key for an index part behind an absent owned value. Q15, Q17
and Q18 are answered by the planner, Q16 by the owner, and their texts are in this brief.
The tests step found six more, Q19 to Q24, which the planner answered in the same way.
Two rules of the saved form cannot be stated in a test and are checked by the reviews in
the code: nothing of the body is decoded before the digest agreed, and the length is used
for nothing but to find the digest. One test of Q16 needs a saved form that `Save` cannot
write, with a body that encodes owned types: the test author adds it in the commit of its
own that follows the implementation, with the recorded fingerprints.

**Added by the owner's word while the sprint was carried out (decision O16).** Two matters
of order, both made in this branch:

- The test project `Moltaro.Lattice.Tests` had 61 files in its root. The test author put
  it into folders by area of behavior and gave every type its own file, before the
  implementation; `AGENTS.md` and the review checklist of the development process say
  what a test project looks like.
- The folder `Schema` of the contract assembly holds three layers after this sprint: the
  public model, the compilation of drafts into it, and the encodings of a schema. After
  the implementation commit the implementer moves the internal types into
  `Schema/Compilation` (drafts, builders and their checks, and the canonical encoding of
  the fingerprints, which the builder computes) and `Schema/Serialization` (the reader of
  the descriptor and the binary form, which both produce drafts and call the builder), in
  a commit of its own that changes places and `namespace` lines only. So the two
  sub-folders depend in one direction, `Serialization` on `Compilation`; the first
  wording of this item had the fingerprints with the readers, which would have made the
  two depend on each other. The folder `Access` is looked at in the same way in that
  commit. The public types
  stay in `Moltaro.Lattice.Schema`, which is their namespace by the contract, with the
  internal types the model itself holds. Principle 11 of `AGENTS.md` says how a folder
  and its sub-folders depend on each other.

**Types and members of S03.** Namespaces `Moltaro.Lattice.Schema` and `Moltaro.Lattice.Access`.

The descriptor gains a top array `OwnedTypes` and five field types:

```json
"OwnedTypes": [
  { "Id": 1, "Name": "Address", "ClrType": "MyApp.Address",
    "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ] }
],

{ "Id": 5, "Name": "Home",    "Type": "Owned", "OwnedType": "Address", "Nullable": true }
{ "Id": 6, "Name": "Manager", "Type": "Reference", "EntityType": "User", "Nullable": true }
{ "Id": 7, "Name": "Groups",  "Type": "List", "Element": { "Type": "Reference", "EntityType": "Group" } }
{ "Id": 8, "Name": "Options", "Type": "Map",  "Key": { "Type": "String" }, "Value": { "Type": "String", "Nullable": true } }
{ "Id": 9, "Name": "Rights",  "Type": "Set",  "Element": { "Type": "String", "StringStorage": "Dictionary" } }
```

The identity of an owned type is positive and unique among the owned types of the schema.
Owned types are a scope of their own, as enum types and entity types are: an owned type
may have the identity that an entity type or an enum type has. The identity of a field is
unique within its owned type. The name of an owned type is unique among owned types; it
may be the name of an entity type or of an enum type, because `OwnedType`, `EntityType`
and `Enum` in a declaration each name a type of one kind. A .NET class is named by at
most one type of the schema: two owned types that name the same `ClrType`, and an owned
type and an entity type that do, are `InvalidSchema` with the class named. An owned type
declares at least one field: without `Fields`, or with an empty list, it is
`InvalidSchema` with the owned type named. Its `ClrType` is optional and, when given, not
empty, as for an entity type.

By the owner's answer to Q16 an owned type does not contain itself. What an owned type
contains is what its values own: the owned types of its Owned fields, of the elements of
its Lists and of the values of its Maps, through collections inside collections, and
whatever those owned types contain in turn. A declaration in which this leads from an
owned type back to itself is `InvalidSchema`, with the owned types on the way named. The
shortest case is an owned type with a field of its own type, and it is refused whether
the field is nullable or not. A Reference is not containment: an owned type may refer to
any entity type, the one whose entities hold it included. It is a rule of the model, so a
saved schema that declares such a type is refused on load as a descriptor is.

By the owner's answer to Q28 a declaration is at most 32 deep. The depth of a declaration
is 1 for a scalar, a Binary, an Enum and a Reference; for a List and a Set one more than
the depth of its element; for a Map one more than the deeper of its key and its value;
for an Owned one more than the deepest field of its owned type. The declaration of every
field, of an entity type and of an owned type, has a depth of 32 or less; a deeper one is
`InvalidSchema` with the field named. The depth is finite because an owned type does not
contain itself. It is a rule of the schema language, the same in every process, and not
an option. It is a rule of the model, so it holds for a descriptor and for a saved schema
alike; the reader of a saved schema also stops at it while it reads, so that a form
nested deeper cannot exhaust the stack before the model is checked. Whatever walks a
value or binds a class therefore never goes deeper than 32.

A coordinator's note, after review round 2: the text of a descriptor is parsed to a depth
of 128, four times the depth of a declaration, where a valid descriptor nests at most 36
deep; a declaration too deep by far, 100 deep for one, is therefore still refused by the
rule above with its field named. Text nested deeper than 128 is refused as a whole,
`InvalidSchema`, with a message that says so and no field named. Without the bound the
time to refuse such a text grew with the square of its depth, and a text of a few hundred
kilobytes took a minute.

`Element`, `Key` and `Value` take what a field takes except `Id` and `Name`, and may
themselves be a List, a Map or a Set. A Map key and a Set element are one of three: a
scalar that is not Binary, an Enum, a Reference. A Map key is never nullable.

An enum type declares at least one member: `"Members": []` is `InvalidSchema`. This
completes the declaration of enum types of S02, where an empty list compiled.

`SchemaVersion` is optional and at least 1; without it the catalog version is 1. That a
migration's target has a greater version than the stored one belongs to the contract of
S37. In the contract commit the documentation of `LatticeSchema.Version` becomes:

> The catalog version: a whole number from 1, declared by the descriptor as
> `SchemaVersion`, or 1 when the descriptor does not declare it. A later version of a
> schema has a greater number. It is not part of `Fingerprint`.

By the owner's answer to Q11 the binary form of a compiled schema carries a digest of
itself. From this sprint the form is, in this order:

| Part | Encoding |
| --- | --- |
| Magic | The four bytes `MLSC`, as before |
| Format version | `Int32`, little-endian: 5 |
| Length of the body | `Int32`, little-endian, not negative: the number of bytes of the body |
| Body | Everything else the form holds, from the catalog version on. Its encoding stays internal to `SchemaBinaryFormat` |
| Digest | 32 bytes: SHA-256 over every byte before it, from the magic to the last byte of the body |

`Load` goes through the form in this order and refuses with `InvalidSchema` at the first
rule that does not hold:

1. The magic and the format version, as before. A form of another format, that of S02
   included, is refused: nothing was released, so such a schema is compiled again.
2. The length. A negative length is refused. So is a length greater than one array can
   hold: such a body cannot be read, and it is damage like any other. The length says where the digest stands and
   is used for nothing else: it counts the bytes to read. No allocation is sized by it.
   The rule of S02, that nothing is allocated by a declared size, holds for it as for
   every count and length inside the body: the body is read in pieces, and memory grows
   with the bytes that arrived. Data that ends before the body and the digest are
   complete is refused.
3. The digest. It is computed over the bytes that were read and compared with the 32
   bytes that follow them. A difference is refused, and nothing of the body is decoded
   before the digest agreed. The magic, the format version and the length are the only
   values taken from the data before that: they are what locates the digest, and the
   digest covers each of them.
4. The body, with every rule of the reader of S02 unchanged: marks, counts, lengths,
   well-formed UTF-8, policies that fit their type. A digest shows damage, not who wrote
   the bytes, so a form whose digest is right is still read strictly. The schema fills
   the body exactly: bytes left in the body after the schema, and a schema that needs
   more bytes than the body holds, are refused.

`Load(Stream)` leaves the stream right after the digest, and `Load(string)` refuses a
file that has data after the digest: the rules of S02 on where a saved schema ends, with
the digest as its end.

In the contract commit the documentation of `LatticeSchema.Load(Stream)` loses the
sentence "The compiled form has no checksum: a change that still reads as a valid schema,
such as another name, is loaded as that schema." and has in its place:

> The compiled form ends with a digest of itself, SHA-256. A change of any byte is damage
> and is refused, also when the changed bytes would still read as a schema; nothing is
> decoded from data whose digest does not agree. The digest detects damage. It is not a
> signature: it does not show who wrote the data.

```csharp
public enum LatticeFieldTypeEnum : byte
{
    // 1..20 as after S02
    Owned = 21,
    List = 22,
    Map = 23,
    Set = 24,
    Reference = 25,
}

public sealed class LatticeOwnedType
{
    public int Id { get; }
    public string Name { get; }
    public string? ClrTypeName { get; }
    public IReadOnlyList<LatticeField> Fields { get; }
    public bool TryGetField(string name, [NotNullWhen(true)] out LatticeField? field);
}

// LatticeValueDeclaration gains
public LatticeOwnedType? OwnedType { get; }                  // Owned
public string? ReferencedEntityTypeName { get; }             // Reference
public LatticeValueDeclaration? Element { get; }             // List, Set
public LatticeValueDeclaration? Key { get; }                 // Map
public LatticeValueDeclaration? Value { get; }               // Map

// LatticeSchema gains
public IReadOnlyList<LatticeOwnedType> OwnedTypes { get; }
public bool TryGetOwnedType(string name, [NotNullWhen(true)] out LatticeOwnedType? ownedType);

// Moltaro.Lattice.Access: an owned value without a .NET class, as LatticeRecord is for an entity
public sealed class LatticeOwnedValue
{
    public LatticeOwnedValue(LatticeOwnedType ownedType);
    public LatticeOwnedType OwnedType { get; }
    public object? this[string fieldName] { get; set; }
    public T? Get<T>(string fieldName);
}
```

What a record holds at a position, which is also what `ValueType` reports for it:

| Declared | In a record | Accepted when set |
| --- | --- | --- |
| `Owned` | `LatticeOwnedValue` | `LatticeOwnedValue` of that owned type |
| `Reference` | The key of the target, in the .NET type of that key | The same |
| `List` of T | `List<T>` | Any `IEnumerable<T>`; copied |
| `Map` of K, V | `Dictionary<K, V>` with the comparer of the key declaration | Any `IEnumerable<KeyValuePair<K, V>>`; copied; a duplicate key is `ArgumentException` |
| `Set` of T | `HashSet<T>` with the comparer of the element declaration | Any `IEnumerable<T>`; copied; equal members keep the first |

T is the `ValueType` of the inner declaration, or `Nullable<>` of it when that position
is nullable and its type is a value type. That is what a record holds, at every depth: an
owned value inside a List or a Map of a record is a `LatticeOwnedValue`, whether or not
its owned type names a class. `ValueType` reports this and nothing about a bound class.

The element, key and value types of a collection are exact: a `List<int>` is not accepted
for a List of Int64, nor a Map with `int` keys for a Map of Int64 keys; each is
`ArgumentException` with the field named. An integer of another width is accepted when it
fits only where a value is given by itself: a field of a record, a field of a
`LatticeOwnedValue`, a primary key, a part of a search key.

The comparer of a Map key or a Set element that is a Reference is the comparer of the key
of its target: when that key is a String compared without case, the Map or the Set
ignores case too. Nothing is added to a fingerprint for it. The layout fingerprint never
covers a comparison, and the fingerprint of the schema already covers the comparison of
the target's key and the identity of the target.

What a record holds of what it is given. A record copies every value that is set into it,
at every depth, and holds nothing of the caller's. A collection is rebuilt as the
record's own `List`, `Dictionary` or `HashSet` with the comparers of the schema, and so
are the collections inside it. A `LatticeOwnedValue` is copied field by field, as if each
of its fields were set again, so what it holds is copied and checked in the same way. A
byte array is copied. A change made afterwards to the value that was given — to the
collection, to the owned value, to the array — is not seen through the record, and one
`LatticeOwnedValue` set into two records, or twice into one collection, gives independent
copies. A `LatticeOwnedValue` treats what is set into it in the same way.

What a record returns is its own, and the same object every time: the collection, the
owned value or the byte array it holds. A change made through it is a change of the
record, and that is how a value inside a record is edited after it was set:
`record.Get<LatticeOwnedValue>("Home")["City"] = "Lviv"`. A change made through a
returned collection is not checked when it is made. A `LatticeOwnedValue` returns in the
same way: its own collection, owned value or byte array, the same object every time, and
a change made through it is a change of the owned value. An owned value that was made
invalid through a collection it returned is refused when it is set into a record, into
another owned value or into a collection, with `ArgumentException` and the field named,
because setting it copies and checks what it holds. The field named is the field of the
owned value whose value does not match its declaration, as if that field were set by
itself; the coordinator read this from "as if each of its fields were set again" for the
tests. In the same way, from "a change made through a returned collection is not checked
when it is made": a value added to a collection that a record or an owned value returned
is neither copied nor checked at that moment, an owned value added that way included;
the write validates and copies it.

A record is not the boundary of the database. The boundary is a write — a method of a
set, or adding to a write batch — and a read. There everything is validated again and
copied at every depth: nothing changed afterwards reaches what was submitted, and what a
read returns shares nothing with the database or with another read.

A bound class holds the same data in its own types. One rule gives the type of every
position, a property as well as an element, a key or a value:

| Declared | In a bound class |
| --- | --- |
| A scalar or Binary | Its `ValueType`, or `Nullable<>` of it, as in S02 |
| `Enum` | The underlying integer type, or a .NET enum that agrees with the enum type, as in S02 |
| `Owned` | The class named by `ClrType` of the owned type |
| `Reference` | The key type of the target |
| `List` of T | `List<T>` or `T[]` |
| `Map` of K, V | `Dictionary<K, V>` |
| `Set` of T | `HashSet<T>` |

T, K and V are found by the same table. The class of an owned type is bound as the class
of an entity type is: a public parameterless constructor and, for every field of the
owned type, a property found by the rule of Q14; its own owned fields and collections
follow the table. An owned type that names no `ClrType` has no form in a class: a class
with a field of such a type at any position is `BindingFailed`, with the property and the
owned type named, and so is a property, an element or a value of type
`LatticeOwnedValue`. Any other type is `BindingFailed` with the property named. Until S18
and S19 a write of an entity type that declares one of these fields throws
`NotSupportedException`; compiling, saving, loading, records and binding work. The
validation comes first: an object or a record with an invalid value at any depth is
`ValidationFailed`, through a set as when it is added to a write batch, and only a valid
one ends in `NotSupportedException`. The same order holds for every entity type whose
writes are refused as not implemented, those of S02 included — a unique index, a
comparison that ignores case, Dictionary storage, an enum field, a schema in UTF-16: a
write first checks its arguments and validates the entity against the schema, and only a
write that passes ends in `NotSupportedException`. A check that belongs to what is not
implemented is not made: whether an integer is a declared value of its enum type, and
whether a unique index already holds the key.

In the "Not implemented yet" parts of the public documentation, the sentence "every write
of such an entity type throws `NotSupportedException`" becomes, by the answer to Q25:

> Until then no write of such an entity type is carried out: after the checks every write
> makes of its arguments and of the entity against the schema, it throws
> `NotSupportedException`.

A part of an index may be a field inside an owned value, by decision P9. In the
descriptor an element of `Fields` is then a list of identities instead of one identity,
from a field of the entity type down to the indexed field:

```json
"Indexes": [ { "Id": 2, "Name": "ByCity", "Fields": [ [5, 1] ] } ]
```

An element of `Fields` is one identity or a list of identities. A list of one identity is
the field itself: `[[2]]` and `[2]` declare the same part. An empty list is
`InvalidSchema`. An index names a part once: the same path twice, in either spelling, is
`InvalidSchema`, as the same field twice is since S02. Two paths that differ in any step
are different parts.

```csharp
// One component of an index key.
public sealed class LatticeIndexPart
{
    public IReadOnlyList<LatticeField> Path { get; }         // from a field of the entity type to the indexed field; one element for a plain field
    public LatticeField Field { get; }                       // the indexed field, the last of the path
}

// LatticeIndex gets Parts and loses Fields
public IReadOnlyList<LatticeIndexPart> Parts { get; }
```

Every step of a path but the last is an `Owned` field; a path through a List, a Map or a
Set is `InvalidSchema`. The fingerprint covers the paths of the parts.

The field a part ends at, which for a plain part is the field itself, is one of three: a
scalar that is not Binary, an Enum, a Reference. A Reference is compared as the key of
its target, with the comparison of that key. A part that ends at an Owned field, a List,
a Map or a Set is `InvalidSchema`, as one that ends at a Binary field is since S02: an
owned value is indexed through a path to a field inside it, and the members of a
collection are outside the milestone.

A search key may give null for a part exactly when a value of that part can be null: the
indexed field is nullable, or a step of its path is a nullable owned value, which is
absent in such an entity. Null for any other part is `ArgumentException`, as before S03.

From this sprint on the layout fingerprint of an entity type covers everything an entity
of that type stores. For an Owned field it covers the identity of the owned type and
every field of it, in the way S02 gives for the fields of an entity type, and so on
through owned values inside it, which ends because an owned type does not contain itself. For a List, a Map or a Set it covers the element, the key and the value in
the way S02 gives for a field: type, nullability, string storage and the underlying type
of an enum, never a comparison; an element, a key or a value that is itself an owned value,
a reference or a collection follows the rules of this paragraph. For a Reference it covers the type of the key of its
target and nothing else of the target. So it changes when a field is added to an owned
type the entity type uses, when the declaration of an element changes and when the key
type of a referenced entity type changes.

By the owner's answer to Q5 the layout fingerprint also covers, from this sprint on, which
field is the primary key: the identity of the key field. The key decides how an entity is
addressed in the saved base, so a schema in which another field becomes the key has to
convert every entity of that type, and a migration sees that through this fingerprint.
The identity of the entity type is not covered. Layout fingerprints are compared for one
entity type in two versions of a schema; an entity type is recognized by its identity,
and an identity is never reused. Two entity types with the same fields and the same key
field therefore have the same layout fingerprint. The fingerprint of a schema is not
touched by this answer: it has covered the identity of an entity type and its primary key
since S02.

### Stage 2. One node, in memory

#### S04 · Arena

**Track:** full, engine component · **Needs:** S01 · **Decisions:** D7 · **Owners:** E01, E03

- **Goal.** The engine can obtain, address and release native memory for the life of a
  database without leaking and without scanning.
- **Contract.** Internal types of the engine: segments of 4 MiB, the size classes,
  regions with free-slot tracking, the 32-byte block header, relative addresses, the
  pages of 4,096 root entries with generations, and the counters that tell live bytes,
  class slack, prepared capacity, retired capacity, free regions and the empty reserve
  apart.
- **To read.** [Native layout and allocation](../design/milestone-1-engine-contract.md#native-layout-and-allocation);
  [arena protocol](../design/arena-protocol.md); [Q16](../decisions/physical-storage.md);
  [size classes and empty segments](../decisions/allocation-and-compaction.md);
  evidence: [arena lifecycle](../design/prototype-evidence.md#bounded-arena-lifecycle),
  [root entries](../design/prototype-evidence.md#bounded-root-entry-storage-comparison).
- **Acceptance.** A block of every size class is allocated, freed and its slot reused.
  An empty region returns to its segment; an empty segment enters the reserve of one
  segment or is freed. Address zero is null; an invalid address, count or length is
  refused before it is dereferenced. A root handle of a reused slot does not match the
  old generation; an exhausted generation retires the slot. Adding a page of root
  entries copies no existing entry. Allocation beyond the given budget fails and leaves
  the counters as they were. After a deterministic churn of allocations and frees the
  counters equal an independent tally and nothing is live that the test did not keep.
  By the owner's answer to Q3 of the [open questions](open-questions.md): a test of
  `Moltaro.Lattice.Engine.Tests` reads `src/Directory.Packages.props` and states that
  `Arc.Collections` is declared with the version `[2.2.0]`, brackets included: the exact
  pin, which a range or a bare version would not be. The test of the resolved version in
  `DependencyTests` stays as it is. The test finds the file from its own location
  upwards, with no machine path; it states the invariant of decision O1 and falls under
  exception X3.
- **Measured.** Asserted: the actual size of a root entry against the 48 bytes of the
  specification, and of a block header against 32; no managed allocation on allocate and
  free after warm-up.
- **Out of scope.** What a block contains (S05). Locks and readers (S06–S08). Moving blocks
  (S23).

**Types and members of S04.** Internal, namespace `Moltaro.Lattice.Storage`, in the engine
assembly. These are what the tests address; members beyond them are the implementer's.

```csharp
// Segment in the high 32 bits, byte offset in the low 32 bits. The default value is null.
internal readonly struct ArenaAddress : IEquatable<ArenaAddress>
{
    public ArenaAddress(uint segment, uint offset);
    public uint Segment { get; }
    public uint Offset { get; }
    public bool IsNull { get; }
    public ulong Value { get; }
}

// Generation in the high 32 bits, slot in the low 32 bits.
internal readonly struct RootHandle : IEquatable<RootHandle>
{
    public RootHandle(uint generation, uint slot);
    public uint Generation { get; }
    public uint Slot { get; }
}

internal enum BlockKindEnum : ushort { Root = 1, Value = 2, Chunk = 3, ChunkDirectory = 4, SharedText = 5 }

internal struct BlockHeader                                  // 32 bytes, asserted
{
    public RootHandle Root;
    public ArenaAddress Parent;
    public uint DescriptorOffset;
    public uint PayloadLength;
    public BlockKindEnum Kind;
    public ushort Flags;
    public uint LayoutVersion;
}

internal enum RootStateEnum : uint { Free = 0, Live = 1, Prepared = 2, Deleted = 3, Retired = 4 }

internal struct RootEntry                                    // 48 bytes, asserted
{
    public ArenaAddress Root;
    public ulong LastMutationSequence;
    public ArenaAddress KeyDescriptor;
    public long StoredRootId;
    public uint Generation;
    public uint TypeId;
    public RootStateEnum State;
    public int ClaimCount;
}

internal sealed class ArenaOptions
{
    public long MemoryLimit { get; init; }
    public int SegmentSize { get; init; } = 4 * 1024 * 1024;
    public int ReservedSegments { get; init; } = 1;
}

internal readonly record struct ArenaCounters(
    long LiveBytes, long ClassSlackBytes, long PreparedBytes, long RetiredBytes,
    int FreeRegions, int ReservedSegments, int Segments);

internal sealed class Arena : IDisposable
{
    public Arena(ArenaOptions options);
    public const int MaxBlockSize = 64 * 1024;               // header included
    public static int BlockSizeFor(int payloadLength);       // the size class that holds this payload
    public bool TryAllocate(int payloadLength, BlockKindEnum kind, RootHandle root, out ArenaAddress address);
    public void Free(ArenaAddress address);
    public bool IsValid(ArenaAddress address);               // a live block of this arena
    public ref BlockHeader Header(ArenaAddress address);     // ArgumentException when not valid
    public Span<byte> Payload(ArenaAddress address);         // ArgumentException when not valid
    public ArenaCounters Counters { get; }
    public void Dispose();
}

internal sealed class RootTable
{
    public const int EntriesPerPage = 4096;
    public RootHandle Add(uint typeId);                      // a free slot with its next generation, or a new page
    public bool IsCurrent(RootHandle handle);                // the slot still has this generation and is not free
    public ref RootEntry Entry(RootHandle handle);           // ArgumentException when not current
    public void Release(RootHandle handle);                  // the slot becomes reusable; retired when its generation is exhausted
    public int Count { get; }
    public int Pages { get; }
}
```

**What the members do where the block above does not say.** The planner's answers to Q30
of the [open questions](open-questions.md), 15 November 2026. The tests state them.

- **A current handle.** A handle is current when the entry of its slot has the handle's
  generation and the slot is neither free nor retired: that holds for a handle from the
  moment `Add` gives it out until it is released. A released slot is not current, whether
  it became free or was retired. The first generation of a slot is 1, so `default(RootHandle)` is never
  current. A slot released at generation `uint.MaxValue` is retired: never given out
  again and not counted by `Count`. The test of the exhausted generation sets
  `Entry(handle).Generation` through the ref, because it cannot be reached otherwise, and
  names the slot with the constructor of `RootHandle`; no test reinterprets a `ulong`.
- **A new entry.** `Add` writes the generation and the type id, zeroes the other fields
  and gives the entry the state `Prepared`: an entry that was given out belongs to a write
  that has not published it yet. `Release` leaves it `Free`, or `Retired` when the
  generation is exhausted. `Release` does not look at `ClaimCount` or at `State`; who may
  release and when is the caller's, with S06 to S08. `Release` of a handle that is not
  current is `ArgumentException`.
- **A block.** In S04 a block is allocated or free: `IsValid` means allocated and not
  freed, and `PreparedBytes` and `RetiredBytes` are 0 until the sprints that add those
  transitions. `TryAllocate` writes the whole header, also in a reused slot: root, kind
  and payload length as given, every other field zero. The payload is not initialized;
  the caller writes every byte it later reads, and no test states what a new payload
  holds. `TryAllocate` does not check the kind or the root handle.
- **Lengths.** `BlockSizeFor` and `TryAllocate` refuse a negative length, and one that
  does not fit `MaxBlockSize` with the header, with `ArgumentOutOfRangeException`;
  `TryAllocate` returns false only for the budget. A payload length of 0 is allowed; its
  block is 32 bytes, the smallest class. The largest payload is `MaxBlockSize` − 32 =
  65,504 bytes, the chunk payload of the specification. `Payload` spans the header's
  `PayloadLength` and never beyond the capacity of the slot: a header whose length
  exceeds it is `InvalidOperationException` before any byte of the payload is reached.
  That is the test of "an invalid length is refused before it is dereferenced".
- **Refusals.** `Free` of an invalid address, or of a block already freed, is
  `ArgumentException`. After `Dispose` the other members throw `ObjectDisposedException`;
  a second `Dispose` does nothing. A coordinator's note, after review round 1: `Free` of a
  block whose header gives a length beyond the capacity of its slot is
  `InvalidOperationException`, as `Payload` refuses it, and the block stays allocated; the
  implementer chose it and the coordinator accepted it, because the arena has no other
  length to take the block out of the counters with.
- **Managed memory.** A coordinator's note, after review round 1, on "no managed
  allocation on allocate and free after warm-up": warm-up ends when the arena has its
  segments. `Free` allocates no managed memory in any case; `TryAllocate` allocates none
  unless it obtains a segment, whose metadata is managed memory, about 18 KB for a segment
  of 4 MiB. Tests state both.
- **Native memory.** A coordinator's note, after review round 1: every segment obtained
  from the native allocator is returned exactly once, by `Free` when a segment beyond the
  reserve empties and by `Dispose` for all that is held; a failure of the native allocator
  leaves the arena as it was. The tests see it through an internal fault point in the
  sense of exception X3: the object through which the arena obtains and returns native
  memory, which a test replaces by one that counts and can fail.
- **Options.** The constructor of `Arena` refuses null options with
  `ArgumentNullException`, and with `ArgumentOutOfRangeException` a `MemoryLimit` smaller
  than one segment, a `SegmentSize` smaller than `MaxBlockSize` and a negative
  `ReservedSegments`. The limit is required: there is no "0 means unlimited", so the
  default is refused. `MemoryLimit` bounds the native capacity obtained, `Segments` ×
  `SegmentSize`, the reserve included. Any further condition on `SegmentSize` is the
  implementer's and is documented at the option; the tests use the default.
- **Counters.** `LiveBytes` is the header plus the payload length of every allocated
  block, as given at allocation; `ClassSlackBytes` is the rest of their slots. A
  coordinator's note: nothing in S04 changes the length of a live block, and the arena
  keeps no length of its own for a slot — the specification puts the logical length into
  the header and only class, capacity and state into the allocator's metadata. So no test
  states how the two counters divide the slot of a block after a caller wrote another
  `PayloadLength` through `Header`; by the planner's answer a caller does not do that, and
  S20 gives the arena the member that changes a length and the counters with it. `Payload`
  still reads the length from the header and guards the capacity of the slot. `Segments`
  counts reserved segments too; `FreeRegions` does not count the regions of reserved
  segments.
- **Order and timing.** `TryAllocate` takes a free slot of the class, then a free region,
  then a reserved segment, then a new segment. An emptied segment enters the reserve when
  it has room and is freed at once otherwise, inside `Free`; the background release of
  Q17 comes with S23.

#### S05 · Root layout and values

**Track:** full, engine component · **Needs:** S03, S04 · **Decisions:** D7 · **Owners:** E01, E05

- **Goal.** An entity of scalar fields is written into the arena from its schema and read
  back equal, whatever the size of its values.
- **Contract.** Internal: the compiled root layout — header, nullable bitmap, fixed
  fields in field-identity order, 16-byte descriptors; inline values up to 64 bytes;
  one external block up to 4 KiB; chunks with a bounded directory beyond that; Plain
  text in the database encoding; Binary; enum and Flags fields at the width of their
  underlying type. New project `Moltaro.Lattice.Measurements` with the schema and the
  deterministic generator of the reference fixture, so that every later sprint measures
  the same entities.
- **To read.** [Native layout and allocation](../design/milestone-1-engine-contract.md#native-layout-and-allocation);
  [record layout, relative addressing, large payloads](../decisions/physical-storage.md);
  [Q04.1, text validity](../decisions/strings.md);
  [scalar catalog, timestamps, Binary](../decisions/schema-and-types.md).
- **Acceptance.** Every scalar type at its declared width, with its minimum, maximum and
  null, survives a round trip; a small integer is not widened. Text is preserved byte
  for byte in UTF-8 and in UTF-16 databases; malformed Unicode is refused before
  anything is allocated and no character is substituted. A value crosses the inline,
  single-block and chunk thresholds in both directions and reads back equal; a chunk
  boundary inside a multi-byte character does not damage it. Writing an entity again and freeing the old root frees everything the old one owned,
  shown by the counters. An entity above the 1 MiB starting
  limit is refused, and one just below it is stored (superseded by decision O19 after
  this sprint closed: an entity has no size limit, `RootWriter` loses `maxEntitySize` in
  the branch of S06, and what this block says of that parameter no longer holds). An entity type whose fixed
  part does not fit one block is refused with `InvalidSchema` when its layout is
  compiled, before any data is admitted. The generator produces the same
  121,000 roots from seed 42 on every run, checked by a digest.
- **Measured.** Reported: bytes retained per entity for the user and the setting of the
  reference fixture without their collections, against the sum of their logical field sizes.
- **Out of scope.** Dictionary text (S13). Collections and owned types (S18, S19). The
  durable and wire encodings (S10, S11, S28, S30). Concurrency (S06–S08).

**Types and members of S05.** Internal, namespace `Moltaro.Lattice.Storage`. A root is written
and read field by field, so that neither a class nor a record has to box its values.

By the planner's answer to Q30 of the [open questions](open-questions.md):
`BlockHeader.DescriptorOffset` of an owned block is the byte offset of its descriptor from
the start of the parent block, header included, as `RootLayout.FixedSize` counts; an
inline `Location` is counted from the same start. A root has `Parent` null and
`DescriptorOffset` 0.

```csharp
internal enum DescriptorKindEnum : uint { Null = 0, Inline = 1, Block = 2, Chunked = 3, Text = 4 }

internal struct ValueDescriptor                              // 16 bytes, asserted
{
    public DescriptorKindEnum Kind;                          // kind and flags
    public uint Length;                                      // bytes, or elements for a collection
    public ulong Location;                                   // a relative address, an inline offset, or a text handle
}

internal sealed class RootLayout
{
    public static RootLayout Compile(LatticeEntityType entityType, LatticeSchema schema);   // InvalidSchema when the fixed part exceeds one block
    public LatticeEntityType EntityType { get; }
    public int FixedSize { get; }                            // header, null bitmap, fixed fields, descriptors
    public uint LayoutVersion { get; }
    public const int InlineLimit = 64;
    public const int SingleBlockLimit = 4 * 1024;
    public const int ChunkPayload = 65_504;
}

// Collects the values of one root, validates them and allocates what they need.
internal ref struct RootWriter
{
    public RootWriter(Arena arena, RootLayout layout, RootHandle root, int maxEntitySize);
    public void SetNull(LatticeField field);
    public void SetBoolean(LatticeField field, bool value);
    public void SetInteger(LatticeField field, long value);  // every signed width and Enum; range checked
    public void SetUnsigned(LatticeField field, ulong value);
    public void SetFloat32(LatticeField field, float value);
    public void SetFloat64(LatticeField field, double value);
    public void SetDecimal(LatticeField field, decimal value);
    public void SetGuid(LatticeField field, Guid value);
    public void SetDateTimeOffset(LatticeField field, DateTimeOffset value);
    public void SetDateOnly(LatticeField field, DateOnly value);
    public void SetTimeOnly(LatticeField field, TimeOnly value);
    public void SetTimeSpan(LatticeField field, TimeSpan value);
    public void SetString(LatticeField field, ReadOnlySpan<char> value);   // ValidationFailed on malformed UTF-16
    public void SetBinary(LatticeField field, ReadOnlySpan<byte> value);
    public ArenaAddress Complete();                          // ValidationFailed when a required field is missing; WriteRejected when over the size limit or out of memory
    public void Abandon();                                   // frees everything allocated so far
}

// Reads one root in place. Valid only while the caller holds the protection of that root.
internal readonly ref struct RootReader
{
    public RootReader(Arena arena, RootLayout layout, ArenaAddress root);
    public bool IsNull(LatticeField field);
    public bool GetBoolean(LatticeField field);
    public long GetInteger(LatticeField field);
    public ulong GetUnsigned(LatticeField field);
    public float GetFloat32(LatticeField field);
    public double GetFloat64(LatticeField field);
    public decimal GetDecimal(LatticeField field);
    public Guid GetGuid(LatticeField field);
    public DateTimeOffset GetDateTimeOffset(LatticeField field);
    public DateOnly GetDateOnly(LatticeField field);
    public TimeOnly GetTimeOnly(LatticeField field);
    public TimeSpan GetTimeSpan(LatticeField field);
    public string GetString(LatticeField field);
    public byte[] GetBinary(LatticeField field);
    public int GetLength(LatticeField field);                // encoded bytes of a String or Binary value
}

internal static class RootStorage
{
    public static void Free(Arena arena, RootLayout layout, ArenaAddress root);   // the root and every block it owns
}
```

In the measurement project, internal to it:

```csharp
internal static class ReferenceFixture
{
    public const int Seed = 42;
    public static string SchemaJson { get; }                 // User, Group, Role, Setting with their indexes, as the milestone contract gives them
    public static string ScalarSchemaJson(bool unique);      // the same entity types and indexes without collection fields, every String Plain; the Unique marks only when asked
    public static IEnumerable<LatticeRecord> Generate(LatticeSchema schema);   // 121,000 records for either schema, the same on every run
    public static string Digest(IEnumerable<LatticeRecord> records);          // SHA-256 over the generated values
}
```

**What the members do where the block above does not say.** The coordinator's readings
of 15 November 2026, entry Q34 of the [open questions](open-questions.md), from the
engine contract and the decisions named under "To read"; the planner's session could not
be reached, so they are reported to the owner at the close of the sprint. The tests state
them.

- **The fixed part.** `FixedSize` = the 32 bytes of the block header + a null bitmap of
  ⌈n/8⌉ bytes for the n nullable fields + the fixed fields at the widths of the scalar
  catalog, in field-identity order, without padding + one 16-byte descriptor for every
  String and Binary field, in field-identity order. A Boolean is one byte, 0 or 1. An
  Enum field has the width of its underlying type. A nullable fixed field keeps its place
  when it is null. A value of more than one byte is stored in the byte order of the
  machine, as the block header and the descriptors are; the byte order of the durable and
  the wire forms belongs to their sprints (Q35 of the open questions). No test states a
  byte order. Packing without padding is chosen because memory comes before speed
  in the engine and no measurement speaks against it; an implementer who wants alignment
  brings the number. `Compile` refuses with `InvalidSchema` a `FixedSize` above
  `Arena.MaxBlockSize`, with the entity type named.
- **What `Compile` does not lay out yet.** An entity type with a field S05 cannot store —
  Owned, List, Map, Set, Reference, a String with Dictionary storage — is
  `NotSupportedException` with the field named; S13, S18 and S19 extend it. An entity
  type that is not of the given schema is `ArgumentException`. `LayoutVersion` is 1 for
  every layout of S05 and is written into the header of every block of the root; how a
  changed layout is numbered belongs to S37.
- **Where a value goes.** A String or Binary value of at most `InlineLimit` encoded bytes
  is stored in the root block after the fixed part, when the root block still fits one
  block; an empty value is inline with length 0. Any other value of at most
  `SingleBlockLimit` bytes has one block of its own. A longer one is chunked: blocks of
  `ChunkPayload` bytes, every chunk but the last full, and one directory block of 16-byte
  descriptors, one per chunk; a value that needs more chunks than one directory block
  holds is `WriteRejected`, reported by `Complete`. A descriptor's `Location` of an inline value is its offset
  from the start of the root block. The parent of a value block and of a directory is the
  root; the parent of a chunk is its directory. A null String or Binary is null in the
  bitmap and its descriptor is all zero. No member of the contract shows where a value
  went. The tests written before the implementation see it through the counters of the
  arena and state no more than they show; the implementer's tests of ownership — the
  parent, the descriptor offset and the layout version of every block, the zero
  descriptor of a null — read the blocks through the arena, at the offsets this part
  gives.
- **Which member serves which field.** `SetInteger` and `GetInteger`: Int8 to Int64 and
  an Enum whose underlying type is signed. `SetUnsigned` and `GetUnsigned`: UInt8 to
  UInt64 and an Enum whose underlying type is unsigned. `SetNull` and `IsNull` serve
  every field, `GetLength` a String and a Binary field, and every other member the one
  field type of its name. A member called for a field of another type, or for a field of
  another entity type, is `ArgumentException`. A value outside the range of the field's
  width is `ValidationFailed`. Whether an Enum value is a declared member is checked from
  S07 on; the tests of S05 use declared members.
- **Null and missing.** `SetNull` of a field that is not nullable is `ValidationFailed`
  at the call. At `Complete` a nullable field that was never set is null, and a field
  that is not nullable and was never set is `ValidationFailed`. A `Get…` member and
  `GetLength` of a null field are `InvalidOperationException`; the caller asks `IsNull`
  first.
- **Text.** In a UTF-8 database a String is stored as the UTF-8 of its characters, in a
  UTF-16 database as its code units. Malformed UTF-16 — a surrogate without its pair — is
  `ValidationFailed` at `SetString`, nothing is allocated for that value (the counters of
  the arena are the same before and after the call), no character is substituted, and
  the writer stays usable. `GetLength` is the number of encoded bytes as stored.
- **The size of an entity.** What `maxEntitySize` bounds is `FixedSize` plus the encoded
  bytes of every String and Binary value: it does not depend on where a value went. An
  entity of exactly `maxEntitySize` bytes is stored; one byte more is `WriteRejected`.
  The size and the memory of the arena are reported by `Complete`, as the block above
  says: no `Set…` member fails for either. When `Complete` fails, everything the writer
  allocated is freed: `LiveBytes` and `ClassSlackBytes` are as they were before the
  writer; a segment the arena obtained for the writer may stay in the reserve, so
  `Segments` and `ReservedSegments` may differ. An `OutOfMemoryException` of the native
  allocator passes through whichever member meets it. Met inside `Complete`, it is a
  failed `Complete`: everything is freed already. Met inside a `Set…` member, it passes
  through after the member has freed everything it allocated for that value: the field is
  not set, as after any refused call, and the writer stays usable — the caller may set the
  field again, complete or abandon (decided after review round 1, which showed that a
  half-stored value left in the writer leaks or makes a null field name a block).
- **Managed memory.** A coordinator's note, after review round 1: after warm-up a write —
  the constructor, every `Set…` member, `Complete`, `Abandon` — and `RootStorage.Free`
  allocate no managed memory, and a reader allocates nothing but the string or the array
  that `GetString` or `GetBinary` returns. The implementer's tests assert both, in the
  test collection of remark 9.
- **The life of a writer.** A field is set once: a second `Set…` or `SetNull` of a field
  that was set is `InvalidOperationException`. A call that was refused — malformed text,
  a value out of range, `SetNull` of a field that is not nullable — did not set the
  field, and a valid value may follow. After `Complete`, whether it returned or failed,
  and after `Abandon`, every `Set…` member, `SetNull` and `Complete` are
  `InvalidOperationException`. `Abandon` after a `Complete` that returned is
  `InvalidOperationException` as well: the root belongs to the caller then. `Abandon`
  after a failed `Complete`, and a second `Abandon`, do nothing. The constructor refuses a null arena or layout with
  `ArgumentNullException` and a `maxEntitySize` below 1 with
  `ArgumentOutOfRangeException`.
- **A reader.** The constructor refuses with `ArgumentException` an address that is not a
  valid block of the arena, a block whose kind is not `Root`, and a block whose layout
  version is not the layout's.
- **`RootStorage.Free`** frees the root block and every block it owns. It does not touch
  the table of root entries. It refuses what a reader's constructor refuses, with
  `ArgumentException` and before it frees anything.
- **The fixture.** The entity types, their counts, their field types, text formats and
  indexes are those of the table of the engine contract, "Reproducible first fixture";
  what the table does not give — names it leaves open, identities, the synthetic value
  sets — is the implementer's and is stated by no test. No field is nullable; an index is
  unique only where the table says so. The scalar form keeps every index, with every
  String Plain. `Generate` uses a generator of the program's own, not `System.Random`,
  whose sequence is not promised across versions of .NET, and yields the records in the
  same order on every run; a schema that is neither of the two forms is
  `ArgumentException`. `Digest` is the SHA-256 of the values in an encoding that does not
  depend on the order in which a Set or a Map enumerates, as lowercase hexadecimal. The
  test author states that two runs give the same records and the same digest; the digest
  of seed 42 is recorded in a test by the test author once the implementation has
  produced it, as the fingerprints of S02 and S03 were: a recorded value cannot be
  written before the code that produces it, so this one test follows the implementation.
- **The measurement.** The implementer writes the development manifest — it names the
  machine by its processor and its memory, the operating system and the versions of
  .NET, with no host name, no name of a user and no path — as a committed file of the measurement project, and the
  scenario that reports the figure of "Measured"; the program is told the scenario on its
  command line. The implementer also adds the job of the measurement lane to the
  workflow, started by hand, which shows that the program runs.

**Carried by this sprint besides its own work.** While S05 was open the owner asked why
the solution has a project `Moltaro.Lattice.Engine` and, in the core library, a folder
`Engine`, with `Storage` and `Replication` beside it under the names the engine project
uses. Those folders were the placeholder. The coordinator answered that it would give
them one folder that says so unless the owner objected, and had it done in commit
`548224e`: the placeholder is `src/Moltaro.Lattice/Placeholder`, with `Placeholder/Storage`
and `Placeholder/Replication`; every type moved is internal, the public surface is
unchanged, and S12 deletes the folder whole. `StoredEntity` and `WriteKindEnum` stay in
`src/Moltaro.Lattice/Storage`, because the sets and the write batch use them. This is more
than decision O16 lists as a small change — it moves source files — and it is named here
for that reason. The branch also carries what the owner and the planner answered while it
was open: decision O17 with the brief of S09, the answers to Q29 and Q32 and to point 1
of Q35 with the brief of S06, and the answer to Q36 with the extension of exception X3.

A sprint before S19 measures the scalar form, because the collections and Dictionary
strings of the full form cannot be stored yet, and before S16 it measures that form
without its Unique marks, because an entity type with a unique index cannot be written
yet; a brief that says "the fixture's users"
means the form its sprint can store. The gates use the full form only. The development
manifest is a committed file of this project.

#### S06 · Reads and writes of one entity

**Track:** full · **Needs:** S05 · **Decisions:** O2, D3, D4 · **Owners:** E03, E04, E08

- **Goal.** A MemoryOnly master built on the engine inserts, reads, replaces, upserts and
  deletes entities of scalar fields by primary key, by class and by name, with the
  guarantees the contract states.
- **Contract.** No change of signatures. The documentation of `LatticeRevision` says what
  a caller may conclude from one. Until S12 the tests open the engine through an internal
  entry of the engine assembly; exception X4 moves to **In force** in the
  [exceptions register](exceptions.md).
- **To read.** [Reader protection and publication](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction);
  [revision semantics](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
  [Q06, Q06.1](../decisions/memory-and-access.md); [primary index](../decisions/index-structures.md);
  [Q13 write modes, delete, revisions, keys](../decisions/mutation-commands.md);
  evidence: [primary index integration](../design/prototype-evidence.md#primary-index-integration-and-selection),
  [arena and index integration](../design/prototype-evidence.md#bounded-arena-and-index-integration).
- **Acceptance.** The tests of `CrudTests`, `DynamicSetTests` and `OpeningAndClosingTests`
  that open a MemoryOnly master and no child pass on the engine with their assertions
  unchanged; the tests of those classes that open a child keep running on the placeholder
  only, until S12. To run
  them there, the test author routes the opening of a database in these classes through
  one helper of the test project, which opens the placeholder and the engine in turn;
  nothing else in the tests changes. In addition: a new database has a valid position
  before its first write; the first write returns another position, which is then the
  position of the database. The sequence of that position is 0 before the first write and
  1 after it, shown by `PublishedSequence` (what an application sees of the position is
  stated by a scenario test through the public API, the two numbers by a test of
  `Moltaro.Lattice.Engine.Tests`; the planner's answer to Q38). A revision binds its entity: a revision read from one entity never satisfies a condition
  on another; a recreated entity has a new one. A read is coherent while the entity is
  being replaced. An enumeration of entities that nobody changes returns each of them
  once. No lock is held across an `await`. Until S07 the engine accepts
  the key types the existing tests use, and refuses with `NotSupportedException` a write
  of an entity type with another key type, with a key compared without case or with an
  Enum field; the whole list of what is refused until a later sprint is under "What the
  engine does where the block above does not say". Until S08 an enumeration during which the index changed fails
  with `NotSupportedException` instead of returning a set it cannot vouch for, and the
  primary index grows under exclusive protection. By the planner's answer to Q30 of the
  [open questions](open-questions.md): when every write has returned and no read is in
  progress, `PreparedBytes` and `RetiredBytes` are 0 and the counters of the arena equal
  an independent tally of what the entities hold; after a write that failed validation or
  was refused, nothing of it is left in any counter. By the planner's answer to Q32: a
  read that found an entity which is deleted before the read reaches it returns absence,
  never the values of another entity; after the deletes have returned and no read is in
  progress, the table of root entries holds no entry of a deleted entity, shown by its
  count. By the planner's answer to point 1 of Q35: a database whose schema declares an
  entity type with a fixed part larger than one block does not open: `InvalidSchema` with
  the entity type named. A database whose schema declares an entity type the engine
  cannot store yet opens, and its other entity types are read and written. By the owner's
  answer to Q29 a property binds with a set accessor of any accessibility — public,
  `internal`, `protected`, `private` or `init` — and an entity read from the database has
  the stored values in such properties. A property without a set accessor, and one whose
  get accessor is not public, are `BindingFailed` with the property named. A class that
  overrides only the get accessor of a virtual property binds, and its value is set
  through the accessor of the base class. A class in which a public field hides a
  property of its base class is `BindingFailed` with the member named. By the owner's
  answer to Q39 on the position of a write that changes nothing: a delete of an absent
  key returns `Changed` false and a position of its own — not the position before it, and
  then the position of the database — and the revision of no entity changes. The
  placeholder keeps what it does and is not changed; a test of this runs on the engine
  only, until S12. By decision O19, with which an entity of tens of mebibytes is an
  ordinary case: an entity with a String value of 32 MiB and a Binary value of 32 MiB is
  inserted, read back equal by class and by name, replaced by a small one and deleted,
  and after that the counters of the arena are what they were before it; the test sets a
  memory limit that holds it (until S09 gives the option, the internal limit of this
  sprint holds it on every machine the tests run on). One String or Binary value above
  268,173,376 bytes, the capacity of one directory block of chunks, is `ValidationFailed`
  with the field named: a fixed rule of the storage format, the same on every node, which
  a retry never helps. A coordinator's note, corrected after the second review of S06,
  which showed that a check of the engine alone stands after the conditions of a write
  and leaves the public entry storing what the documentation forbids: the bound is a
  validation of the contract, by the planner's reasons for Q41. The validation of the
  core checks it at a write and at adding to a batch, on the placeholder and on the
  engine alike, before the conditions of the write and before a write is refused as not
  implemented; it needs no pass over the text unless the text is long enough to reach the
  bound. The root writer keeps its own refusal as the guard of the stored form.
  Whether the bound holds for each element of a collection and each field of an
  owned value is the planner's, with the contracts of S18 and S19 (Q35, point 7). By the
  planner's answer to Q41: a write of an entity with text that is not well-formed is
  `ValidationFailed` with the field named whatever else is wrong with the write: with the
  key of a stored entity on insert, with an absent key on replace, with a wrong revision,
  and for an entity type the engine does not store yet; by class and by name, on the
  placeholder and on the engine. A read or a delete by a String key that is not
  well-formed is `ArgumentException`.

**Carried by this sprint besides its own work**, under decision O16, after its
implementation commit and tests first: the removal of the size limit of an entity
(decision O19). The contract author takes `maxEntitySize` out of `RootWriter` and states
the bound of one value in the documentation of the String and Binary field types; the
test author removes the tests of S05 and of this sprint that state the limit of an
entity, with the decision named, restates the bound of one value as `ValidationFailed`
with the field named, and writes the test of the large entity; the implementer removes
the limit and its internal value. What stays is the refusal for memory — `Complete` with
`WriteRejected` when the arena cannot give the blocks — and the bound of the format. The
same sequence carries the sentence of decision O18 in the documentation of
`ILatticeWriteBatch`.
- **Measured.** Asserted: a read by key allocates the returned copy and nothing else.
  Reported: read time by key for 100,000 entities on one thread, and the same with a
  writer running.
- **Out of scope.** Key types beyond those the existing tests use, enumeration while
  entities change, the traversal budget and the growth of the index (S07–S08). Concurrent
  preparation and grouping (S09): writes here are executed one after another. Children.
  Secondary indexes. Compaction.

**Types and members of S06.** No public member is added or changed. The documentation of
`LatticeRevision` gains this paragraph, word for word:

> A revision identifies one state of one entity. Two revisions of the same entity can be
> compared for equality, and nothing else may be concluded from a revision: it has no
> order, it cannot be turned into a position, and the revision of one entity means
> nothing for another.

By the owner's answer to Q29 of the [open questions](open-questions.md), carried by this
sprint under decision O16, the remarks of `ILatticeDatabase.Set<TEntity>()`, where the
binding of a class is stated once, gain this rule, and it holds for the class of an owned
type as for the class of an entity type:

> The property of a field has a public get accessor and a set accessor of any
> accessibility: public, `internal`, `protected`, `private` or `init`. The database sets
> the value through that accessor when it builds an object, so a class may keep its
> properties closed for writing by the application. The accessors are those C# sees for
> the property on the class, inherited ones included: a class that overrides only the get
> accessor of a virtual property is set through the accessor of its base class. When the
> public member C# finds on the class by the name of a field is not a property — a public
> field that hides a property of a base class — the class is refused: the hidden property
> is not bound in its place.

The first sentence is what the binding does today and gets its tests and its
documentation; the last two are fixes of the binding in the core library — today the
overridden getter is `BindingFailed` and the hidden property binds. The tests come first,
from the test author, as for any change of behavior.

By the planner's answer to Q41 of the [open questions](open-questions.md), after the
first review of S06: text that is not well-formed — a surrogate without its pair — in a
String value is refused by the validation of a write like every other invalid value:
`ValidationFailed` with the field named, for an object and for a record, at every depth
(a field, an element, a key or a value of a collection, a field of an owned value),
before the conditions of the write are looked at and before the write is refused as not
implemented. The validation of the core makes the check, so it holds on the placeholder
and on the engine alike and for an entity type the engine does not store yet. The root
writer keeps refusing such text: that is the guard of the stored form for whoever calls
it inside the engine, found while the text is encoded, and not a second validation of
the contract. A key given as an argument — to a read or a delete, and later to a search
— that is not well-formed text is `ArgumentException`, as a key of another type is. The
check is made at the write and at adding to a batch, where every other validation of an
object is made; a record does not check it when a value is set. How the check is made is
the implementer's; if the extra pass over the text shows in the write figures of S09, it
is reported there and decided with a number. The remarks that list `ValidationFailed`
— the write members of the sets and of the batch, and the member of `LatticeErrorEnum` —
gain the sentence: "Text that is not well-formed — a surrogate without its pair — is
refused in the same way, at any depth, and is never stored and never replaced by another
character."

By the planner's word on the first review of S06, a stated rule and not only a fix: the
engine runs no code of the application inside a gate or the close barrier: under the
protection of a root it copies the values into its own memory, and the constructor and
the set accessors of the class run after the protection is released.

Internal, in the engine assembly:

```csharp
namespace Moltaro.Lattice.Engine;

// The engine's database. Until S12 this is how a test reaches it; from S12 on LatticeDatabase returns it.
internal sealed class EngineDatabase : ILatticeDatabase
{
    public static EngineDatabase Create(LatticeDatabaseOptions options);   // a MemoryOnly master in this sprint
    public EngineCounters Counters { get; }
}

// What the engine holds, for tests and later for diagnostics. Later sprints add members.
internal readonly record struct EngineCounters(
    ArenaCounters Arena, long PrimaryIndexBytes, int RootEntries, long PublishedSequence);
```

`RootEntries` is the count of the table of root entries; `PublishedSequence` is the
sequence of the last published position, 0 for a database without a write and 1 after the
first. Both were added by the planner's answer to Q38 of the
[open questions](open-questions.md), so that a test can read what the Acceptance states.

In the test project, written by the test author: one helper, `TestDatabases`, that opens
a database of the placeholder and of the engine for the same test. `Moltaro.Lattice.Tests` gets a
reference to the engine assembly in this sprint, and the engine assembly grants it friend
access.

**What the engine does where the block above does not say.** The planner's answers to
Q38 of the [open questions](open-questions.md), 15 November 2026, to the points the
contract author found. The tests state them.

- **Which tests run on the engine.** Every test of `CrudTests`, `DynamicSetTests` and
  `OpeningAndClosingTests` runs on both, with three exceptions. Two open a child and stay
  on the placeholder: `DynamicSetTests.Child_serves_records_by_name_with_the_schema_of_its_master`
  and `OpeningAndClosingTests.Closing_a_child_ends_its_wait_for_the_master`. The third,
  `Durable_persistence_is_not_implemented_yet`, opens a Durable master; it runs on both
  as well, because both refuse. Two tests create a database without opening it, so the
  helper has a form that creates without opening.
- **`Create`.** In the order of `LatticeDatabase.Create`: `ArgumentNullException` for null
  options; `Validate`, which gives `InvalidConfiguration`; then `NotSupportedException` at
  `Create` for Durable persistence (until S27), for a child role (until S10) and for a
  `ListenUri` (until S10).
- **States.** Created, Loading during `OpenAsync`, Ready, Closed. Connecting and
  CatchingUp do not occur; Faulted only through a failed publication, which S09 tests. A
  failed open closes the instance. Before the open an operation is `Unavailable`; after
  a close it is `ObjectDisposedException`. A close waits for the reads in progress before
  it frees the arena.
- **Position and revision.** A new random non-zero history at each MemoryOnly open;
  sequence 0, plus 1 for each published write, also one that changed nothing (Q39). A
  revision is the history and the root
  entry's `LastMutationSequence`: in S06 every position changes one entity, so the
  revision of one entity never satisfies a condition on another and a recreated entity
  has a new one. `LatticeRevision` is two opaque 64-bit values, so its composition is
  internal and can change; how a revision binds its entity when several entities change
  at one position belongs to the contracts of the stage with the batch.
- **The limit before S09.** The arena gets half of
  `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, read at open, as an internal value
  that S09 turns into an option; a refusal is `WriteRejected` for memory, without `Limit`
  until S09. By decision O19 the engine has no size limit of an entity. The tests of S06
  do not depend on the memory of the machine beyond what the large entity of the
  Acceptance needs, which every machine the tests run on has.
- **What is refused until a later sprint.** The key types the three classes use are Int64
  and String with Ordinal comparison, Plain storage and UTF-8. The engine refuses with
  `NotSupportedException` everything the public documentation says is not stored yet, and
  every other key type: the other key types, a field compared without case, an Enum field
  and a schema in UTF-16 until S07; Dictionary storage until S13; an entity type with a
  unique index until S16; the field types of S03 until S18 and S19. Every write of such
  an entity type — insert, replace, upsert and delete, with or without a revision — is
  refused at the write, after the argument checks and the validation, in the order of
  Q25; a read by key finds nothing and an enumeration is empty. Binding a class and
  `Index(name)` work.
- **Enumeration until S08.** It is lazy, holds nothing between items and takes for each
  entry, one at a time, the lookup claim of a read by key; what S08 adds is what an
  enumeration needs while entities change. "The index changed" means the primary index of the
  enumerated entity type: an insert or a delete changes it, a replace does not. The
  enumeration fails from the `MoveNext` that sees the change; what it returned before
  stays valid.
- **Sessions and the write batch.** On a master the engine implements the members that
  take a session, and `WaitForPositionAsync`: a master has applied every position it gave
  out. `CreateWriteBatch` returns the batch of the core; building works and the commit is
  `NotSupportedException` until S17.
- **The allocation of a read.** "The returned copy" is the object or the record with
  everything reachable from it, and nothing else is allocated; a key boxed by a public
  signature that takes `object` is the caller's allocation, outside the measured read.
  *Corrected on 17 November 2026:* that sentence was wrong. A signature that forces a box
  on every call of a read path is a defect of the contract, and access by name is no
  second-class path; S09, part 2 gives the sets by name a form that takes a key without
  a box (Q51 of the open questions).
  The sets of the core box the key and pass an array, so the engine has sets of its own.
- **The coherent read** is stated by a stress test with a bounded number of replaces and
  no sleep: a writer replaces one entity with states whose fields are derived from one
  counter, a String above 64 bytes and one above 4 KiB among them, and readers by class
  and by name see exactly one written state each time.
- **A write that changes nothing.** By the owner's answer to Q39 of the open questions:
  every write the engine accepts and executes is published at a position of its own,
  whether it changed the database or not; `PublishedSequence` grows by one for a delete
  that deleted nothing. The placeholder keeps what it does and is not changed: a position
  that advances without a change would also have to reach its children, or a session
  waiting for it on a child would never be served; that is stand-in logic, and the
  placeholder goes in S12.

By the planner's answer to Q30 of the [open questions](open-questions.md): a block
allocated for a write that is prepared and not yet published is valid from its
allocation: `IsValid` means allocated and not freed in every state. S06 moves the state
of a root entry: `Prepared` to `Live` when an insert is published, `Live` to `Deleted`
when a delete is published. By the planner's answer to Q32: a read by key uses the lookup
claim of the milestone contract: it claims the entry under the catalog gate, releases
that gate, takes the protection of the root, checks the generation and the state again,
and drops the claim after it released the protection of the root; it never waits for a
root while it holds the catalog gate. The entry of a root whose delete was published is
released when no claim is held on it: at once when there is none, otherwise by the reader
that drops the last one. S08 adds the claims of an enumeration and the tests of reuse
while entities change; its sentence that a deleted entry is reused only after its claims
are gone holds for reads by key from this sprint on.
`RootEntry.LastMutationSequence` is the sequence of the position of the last published
mutation of the root; the specification's "last mutation position" is the same eight
bytes, because the history of a position is the database's and is not repeated per root.

A block is counted in exactly one place. From its allocation until its write is published
it is prepared: its whole slot is in `PreparedBytes`, and nothing of it is in `LiveBytes`
or `ClassSlackBytes`. Publication moves it: its header and payload length to `LiveBytes`,
the rest of its slot to `ClassSlackBytes`. A block that has left the published state and
is kept until what still refers to it is gone is retired: its whole slot is in
`RetiredBytes` until it is freed. A write that is abandoned frees its prepared blocks, and
they leave `PreparedBytes`. So the bytes of the slots in use are always
`LiveBytes + ClassSlackBytes + PreparedBytes + RetiredBytes`. "Every allocated block" in
the description of `LiveBytes` in S04 holds for S04, where a block is only allocated or
free and `TryAllocate` counts it as live; the members of the arena that allocate a block
as prepared and move it between these states are the implementer's, as S04 leaves members
beyond the listed ones to the implementer, and they do not change what the members of S04
do. In S06 a write is executed alone and published at once, so a prepared block is not
observable between writes; the rule is stated here because S06 is the first sprint with
the transition, and S09 and the Durable sprints rely on it when prepared writes wait.

By the planner's answer to point 1 of Q35: the engine compiles the root layout of every
entity type when the database opens, before any data is admitted. An entity type whose
fixed part does not fit one block makes the open fail with `InvalidSchema`, with the
entity type named. An entity type that has a field the engine cannot store yet does not
fail the open: it gets no layout, every write of it is refused with
`NotSupportedException` as this brief says, a read of it by key finds nothing and an
enumeration of it is empty, because nothing of it can be stored. The size of its fixed
part is checked from the sprint in which the engine can store it.

A coordinator's note from the review of S05: a `RootWriter` is a mutable `ref struct`
that holds a pooled array and the blocks it allocated. It is passed by `ref`, never by
value, also into helpers: a copy that is ended as well returns the array to the pool
twice.

#### S07 · Every key type and declared value

**Track:** full · **Needs:** S06 · **Owners:** E04, E05

- **Goal.** Every key type the schema allows works as a primary key, compared by the
  policy the schema declares, and a value the schema does not allow is refused on write.
- **Contract.** No public member. Internal: the one comparison and hash service of the
  schema, which the primary index uses here and every later sprint reuses.
- **To read.** [Index preparation and traversal, comparison and hash](../design/milestone-1-engine-contract.md#index-preparation-and-traversal);
  [primary-key types, enum and Flags rules](../decisions/schema-and-types.md);
  [Q04](../decisions/strings.md); [floating-point special values](../decisions/queries.md).
- **Acceptance.** Rewritten by the planner on 15 November 2026, before the sprint started,
  against the engine as S06 left it. Every key type of the catalog is a primary key by
  class and by name: the eight integers, String and Guid; an entity is inserted, read,
  replaced, upserted, deleted and enumerated under each. A key given as an integer of
  another width that fits finds its entity; one that does not fit, a text for a Guid key
  and a key of another type are `ArgumentException`. A UInt64 key above the largest Int64
  and an empty String key work.

  A String key compared without case finds its entity under any spelling, and what it
  finds agrees with `string.Equals` under `StringComparison.OrdinalIgnoreCase` for letters
  with and without case, outside ASCII and outside the basic plane; the test computes its
  expectation with .NET. An insert under another spelling of a stored key is
  `DuplicateKey`. A replace and an upsert through another spelling keep the stored
  spelling of the key and replace the other fields; a delete through another spelling
  deletes. A String key of 100,000 characters is found with either comparison. All of it
  holds alike in a UTF-8 and in a UTF-16 database, and an entity written to a UTF-16
  database reads back equal, by class and by name.

  An entity type with a String field compared without case and one with an Enum field,
  ordinary or Flags, nullable or not, are written and read back equal. A write with an
  integer its enum type does not declare, or with a Flags bit no member declares, is
  `ValidationFailed` with the field named, by class and by name, through a set and when
  added to a batch, on both implementations and before any other refusal of the write;
  zero is a valid Flags value. A missing required value and a null in a field that is not
  nullable are refused in the same way.

  Comparing a key allocates nothing that outlives the read.

  The sprint ends refusals that tests of S02, S03 and S06 state, and the test author
  changes those tests in the tests commit. In `NotStoredYetTests` the cases of the seven
  other integer keys, of the Guid key, of the String key compared without case, of the
  text compared without case, of the schema in UTF-16 and of the Enum field are removed;
  the cases of Dictionary storage, of the unique index and of the field types of S03
  stay. `ValidationBeforeRefusalTests.Integer_that_its_enum_type_does_not_declare_is_not_refused_yet_and_the_write_ends_as_not_implemented`
  is replaced by its opposite: such an integer is `ValidationFailed` before the write is
  refused as not implemented. Between the tests commit and the implementation commit the
  removed cases are stated by nothing, which is the state of a refusal that is about to
  end. The tests of `TextPolicyAndEnumWriteTests` and the tests of
  `MalformedTextWriteTests` and `ValueBoundWriteTests` that open the placeholder state
  what the placeholder does and stay until S12; where one of them also opens the engine
  with a schema this sprint makes storable, the test author moves its engine case to the
  tests of this sprint. A test the sprint breaks outside this list is reported to the
  coordinator, who enters it in the brief, and is then edited in the same way.
- **Measured.** Asserted: a read by a String key allocates the returned copy and nothing
  else, with and without case, after a first read with a key of that length, which may
  reserve the workspace of the comparison. Reported: read time by a String key compared
  with and without case.
- **Out of scope.** Enumeration while entities change and the growth of the index (S08).
  Secondary indexes (S14).

**Types and members of S07.** Replaced by the planner on 15 November 2026: the block this
brief had, `ComparisonScratch` and `ValueComparer`, was written before there was an engine
and did not fit it — a stored text is not one span but is reached through its descriptor,
the index of S06 looks a key up with the caller's text, and order belongs to the secondary
indexes. No public member, and no internal type that the tests address: everything this
sprint delivers is stated through the public API, by class and by name, and the tests of
the allocation of a read are written as in S06. The milestone contract asks for one
comparison and hash service of the schema. In this sprint that is one place in the engine
that knows the equality and the hash of a primary key under the comparison its field
declares and in the encoding of the database; the primary index uses it and nothing else
compares a key with stored text. Its members are the implementer's. The contract of S14
adds the order of values and the other types, and names what its tests address; S19 uses
the same service for Map and Set.

From this sprint the engine stores an entity type with any key type of the catalog — the
eight integers, String with either comparison, Guid —, with a String field compared
without case, with an Enum field, and in a schema of the UTF-16 encoding. It still
refuses, as S06 says: Dictionary storage until S13, a unique index until S16, the field
types of S03 until S18 and S19. The placeholder is not changed: it keeps refusing a
comparison without case, an Enum field and UTF-16 until it goes in S12, so the tests of
those three run on the engine only, and the tests of the key types run on both.

A key is given as the type its field declares, or as an integer of another width when it
fits, as before; a value that does not fit, a negative one for an unsigned key included,
is `ArgumentException`. A Guid key is given as a `Guid` and as nothing else: no text is
parsed. A UInt64 key above the largest Int64 works like any other.

A String key compared without case was first written to follow
`StringComparison.OrdinalIgnoreCase` of .NET; the owner's decision on Q46, under "Also
carried by this sprint" below, replaced that with the case table of Lattice's own. It was
to have, and has, the same result in a UTF-8 and in a UTF-16
database. Two keys that are equal under it are one key: an insert under another spelling
of a stored key is `DuplicateKey`; a read, a replace, an upsert and a delete under another
spelling address the stored entity. The primary key is immutable (accepted decision): the
spelling the entity was created with stays, a replace or an upsert through another
spelling replaces every other field, and the entity reads back with the spelling it was
created with, by class and by name. An empty String is a key like any other.

Whether an integer is a value its enum type declares is checked by the validation of the
core from this sprint on, with every other validation of a value and in the order of Q25,
on both implementations: a value an ordinary enum type does not declare, and for a Flags
type a bit that no member declares, is `ValidationFailed` with the field named; for a
Flags type zero is valid, for an ordinary enum type only when it is declared; null is
valid where the field is nullable. It is checked at a write and at adding to a write
batch, not when a value is set into a record. A .NET enum property can hold a value its
type does not name, by a cast, and is checked in the same way. `LatticeEnumType.IsValid`
is the rule. The remarks that list `ValidationFailed` gain one sentence: "An integer that
the enum type of its field does not declare — for a Flags type, a bit that no member
declares — is refused in the same way."

Positions, revisions, states, the lookup claim, enumeration until S08 and the counters are
as S06 has them and S07 does not touch them. Two things the last review of S06 left to
this sprint: the remarks that list `ValidationFailed` say of the bound of one value "of
a field of the entity" until point 7 of Q35 is answered, since a value inside a
collection or an owned value is not judged yet — the sentence is corrected with the
commit of this sprint that touches those remarks; and the bound in a schema of the
UTF-16 encoding gets its pair of cases here, where such a schema is first written:
134,086,688 characters pass and 134,086,689 are `ValidationFailed`. The public "Not implemented yet" remarks on
`OrdinalIgnoreCase`, on Enum fields and on UTF-16 stay true of the public entry until S12
and are not removed in S07; the remarks of `LatticeEnumType` gained the sentence that an
invalid value is `ValidationFailed` before that refusal, because the validation of the
core now checks it.

**Carried by this sprint besides its own work**, under decision O16, after its
implementation commit and tests first: the planner's answer to Q45 of the
[open questions](open-questions.md). A batch identifies an entity by its entity type and
its primary key as the schema reads the key, not as the caller spelled it: two operations
address one entity when their keys are equal after the key is brought to the declared
key type and compared under the comparison the schema declares for the key field. For a
String key compared without case "Main" and "MAIN" are one entity, and for an integer
key 5 given as Int32 and 5 given as Int64 are one entity. The second operation is an
`ArgumentException` when it is added, and the batch stays as it was. The check lives in
the core, in the batch, so both implementations refuse alike, and it uses the
normalization a read by key applies; no second rule of equality is written. The test
author adds the scenario tests (a String key without case; an integer key of two widths;
by class and by record; the batch unchanged after the refusal) and writes the remarks of
`ILatticeWriteBatch` in the same commit, replacing the sentence that begins "A batch
takes one operation per entity": "A batch takes one operation per entity. Two
operations address the same entity when they name the same entity type and their primary
keys are equal as the schema compares them: an integer key by its value whatever the
width it was given in, a String key under the comparison declared for the key field.
Adding a second operation for the same entity is an `ArgumentException`, and the batch
stays as it was." The implementer then changes the check of the batch.

**Also carried by this sprint**, tests first: the owner's decision on Q46 of 15 November
2026, written out in the decision on strings (`docs/decisions/strings.md`, Q04, the
addition of that date). Text compared without case is compared by a case table of
Lattice's own and not by the runtime: two texts are equal without case when they are equal
scalar value by scalar value after each scalar value is replaced by its simple uppercase
mapping of one named Unicode version (`UnicodeData.txt`, the one-to-one mapping; no full
mapping, no folding of several characters, no culture, no normalization); supplementary
planes are mapped as scalar values, in both encodings. The table ships with the core, is
the same in every process, globalization mode and runtime version, and has a version, an
Int32 that starts at 1 and changes whenever the table changes. No call of the runtime
that depends on the globalization mode is on the path of this comparison or of its hash:
equality, the hash of a String key and, from S14, the order read the one table, through
the one rule of `LatticeValueDeclaration.TextComparison`. The version is a fixed rule of
the schema language, not an option; the Hello of S10 and the saved base of S30 carry it,
and the planner adds that to their briefs. The public name of the comparison stays; its
documentation becomes: "Text is equal whatever its case. Lattice compares with a case
table of its own, the simple uppercase mapping of Unicode [version], character by
character, so the result is the same in every process, on every node and under every
globalization setting. No culture is applied and nothing is normalized. For a small
number of rare characters the result can differ from `StringComparison.OrdinalIgnoreCase`
of the running process."

The tests: the shipped table equals the simple uppercase mappings of the named Unicode
version, from the data file kept in the test project; through the public API on both
implementations, keys and field values equal without case by the table, with one pair the
modes of the runtime disagree on and one pair of a supplementary plane, in a UTF-8 and in
a UTF-16 database. A coordinator's note: the Acceptance above says the tests of the
comparison compute their expectation with .NET; with this decision they compute it from
the table's rule and data, so that they pass under every globalization mode — the tests
of this sprint that asked .NET are restated by the test author. The implementer reports
with figures: the Unicode version chosen (the one the data of .NET 10 uses, unless there
is a reason against); on how many scalar values the table and `OrdinalIgnoreCase` of .NET
disagree under ICU, NLS and invariant globalization; and the read by a String key
compared without case before and after, on the scenario of this sprint. A slower read is
reported, not accepted silently.

#### S08 · Enumeration under change and the growth of the index

**Track:** full · **Needs:** S07 · **Decisions:** O12 · **Owners:** E03, E04

- **Goal.** An enumeration stays correct and bounded while entities change, and the
  primary index grows and reuses its entries without stopping or misleading a reader.
- **Contract.** Options for the read budget, none set by default: an application may
  limit the temporary memory and the engine time of a read, for the database or for one
  enumeration; and an error that says which budget a read ran out of (the owner's
  decision O20; the time is counted only inside the engine).
- **To read.** [Index preparation and traversal](../design/milestone-1-engine-contract.md#index-preparation-and-traversal);
  [primary index, selected implementation](../decisions/index-structures.md);
  [Q07.1, Q07.1.1, Q07.3.5](../decisions/queries.md);
  [arena protocol, lock and lifetime order](../design/arena-protocol.md#lock-and-lifetime-order).
- **Acceptance.** An enumeration during inserts, replaces and delete-and-reinsert returns
  no identity twice and every unchanged entity. With a limit set, one that exceeds its
  memory or its time
  fails with the budget named: the items it returned before stay valid, and it never
  ends as if it were complete; a caller that is slow
  between two items does not use up the time budget. Without a limit an enumeration of
  any length runs to its end, and its temporary memory is counted in the instance
  budget. The primary index grows by building
  a replacement table while readers use the old one, and both tables are counted until
  the old one is released. A deleted entry is reused only after its claims are gone, and
  an old claim never follows a key that was created again.
- **Measured.** Reported: enumeration time for 100,000 entities with and without a
  writer; how long index mutations wait while the index grows from 100,000 entries;
  reads by key per second on 1, 4 and 16 threads without a writer, next to the figure of
  S06 (Q42 of the open questions).
- **Out of scope.** Concurrent preparation (S09). Search through an index (S14, S15).
  Compaction (S23).

**Types and members of S08.**

```csharp
namespace Moltaro.Lattice.Access;

// The temporary memory and the engine time one enumeration or one search may use.
// A null member takes the value of the database options.
public readonly record struct LatticeReadLimits(long? Memory = null, TimeSpan? Time = null);

// ILatticeSet<TEntity> gains (ILatticeSet gains the same two with LatticeRecord)
IEnumerable<TEntity> Enumerate(LatticeReadLimits limits);
IAsyncEnumerable<TEntity> EnumerateAsync(LatticeSession session, LatticeReadLimits limits, CancellationToken cancellationToken = default);

namespace Moltaro.Lattice;

// LatticeDatabaseOptions gains
public long? ReadMemoryLimit { get; init; }                             // bytes of temporary memory; null: no limit
public TimeSpan? ReadTimeLimit { get; init; }                           // time inside the engine; null: no limit

// LatticeErrorEnum gains
ReadLimitExceeded = 14,

// Which limit refused an operation. Later sprints add members; none is ever renumbered.
public enum LatticeLimitEnum
{
    ReadMemory = 1,
    ReadTime = 2,
}

// LatticeException gains
public LatticeLimitEnum? Limit { get; init; }
```

`Validate` refuses a limit that is set and not positive. A limit of `LatticeReadLimits` that is
not positive is `ArgumentOutOfRangeException`. An enumeration that exceeds a limit throws
`LatticeException` with `ReadLimitExceeded` and the `Limit` it exceeded, from the
`MoveNext` that would exceed it; what was returned before stays valid, and the
enumeration does not end as if it were complete. Cancellation stays
`OperationCanceledException`. On the placeholder the two new
members throw `NotSupportedException` until S12; their tests run on the engine through
the helper of S06. `EngineCounters` gains `long ReplacementIndexBytes`, the
table being built while the primary index grows.

**Added by the planner on 15 November 2026, before the sprint started**, against the
engine as S06 left it. Where a sentence above says otherwise, these replace it.

- **What the two limits count.** The memory limit of a read counts the temporary memory
  the engine holds for that read: the identities it has returned, with the text of their
  keys, the candidates it took from the index and its workspace. It does not count the
  copies it returns, so an entity larger than the limit is read. The time limit counts
  time inside a step of the enumeration, waiting for a gate included; it does not count
  the wait for the position of a session, which has its own timeout, nor the time between
  two steps. A member of `LatticeReadLimits` that is null takes the value of the options,
  and when that is null too there is no limit; one call cannot lift a limit the options
  set.
- **After a failure.** After an enumeration failed with `ReadLimitExceeded`, a further
  step fails with the same error; it never returns false as if the enumeration had ended.
- **After a close** (the planner's answer to Q44 of the [open questions](open-questions.md)).
  A step of an enumeration after the database was closed is `ObjectDisposedException`, as
  every operation after a close is; an enumeration never goes on over a closed database.
  The placeholder returns what it took at its start and is not changed, so the test runs
  on the engine only until S12. Acceptance: an enumeration that is continued after its
  database was closed, from another thread or from inside the walk, fails at its next
  step with `ObjectDisposedException`.
- **Nothing is held between two steps.** An enumeration holds no gate and no claim between
  two steps, whatever it takes from the index at once: an enumerator that is abandoned
  without being disposed leaves nothing behind, and a deleted entry is not kept from reuse
  by it. The engine runs no code of the application inside a gate, as in S06. Acceptance:
  after an enumeration was abandoned halfway and its entities were deleted, the table of
  root entries holds none of them.
- **The refusal that ends.** The three tests of `EnumerationTests` that state the failure
  until S08 —
  `Enumeration_by_class_during_which_an_entity_is_inserted_fails_and_what_it_returned_stays_valid`,
  `Enumeration_by_name_during_which_an_entity_is_deleted_fails_and_what_it_returned_stays_valid`,
  `Enumeration_with_a_session_during_which_an_entity_is_inserted_fails_and_what_it_returned_stays_valid`
  — change what they assert, so the test author replaces them in the tests commit by the
  tests of this sprint. A test the sprint breaks outside this list is reported to the
  coordinator, who enters it in the brief, and is then edited in the same way.
- **What "every unchanged entity" is measured against.** The test runs writers of
  inserts, replaces, deletes and delete-and-reinsert of a second set of entities while it
  enumerates, with the index growing at least twice during the pass, and states: every
  entity of the first set, which nobody changes, is returned exactly once; no key is
  returned twice; every returned entity is one written state.
- **Documentation.** The documentation of `Enumerate` gains one sentence in the contract
  commit: "The engine remembers the keys it has returned for the length of the pass, so
  that none is returned twice; that memory grows with the pass and is what a memory limit
  of a read bounds."
- **Notes of the coordinator for part 2, 16 November 2026.** The time limit is the sum of
  the time inside the steps of one pass, waits inside the engine included (a read takes
  no gate since part 1; it can wait for a slot of the epoch table and for a write of its
  entity between its stores). An option without an upper bound may be `TimeSpan.MaxValue`,
  so a deadline is computed without overflow. The sentence of the Acceptance "its temporary
  memory is counted in the instance budget" waits for the budget: the option arrives in
  S09 and S24 extends it to the scratch of reads; in this sprint the temporary memory of a
  read is counted for its own limit only and nothing is refused for the budget; no
  counter of the instance sums it, because the memory is the enumerator's own and an
  enumerator that is abandoned must leave nothing counted. An enumeration steps through the primary index of part 1: a
  step enters and leaves the epoch and holds nothing between two steps; a growth or a
  change of the index during the pass no longer fails it. The listing speaks of "one
  enumeration or one search": searches take their limits in S15.
- **The limits of a read are options of the database only** (the owner, 16 November 2026,
  in the coordinator's session: "Убрать вариант «на вызов»"). `LatticeReadLimits`,
  `Enumerate(LatticeReadLimits)` and `EnumerateAsync(session, limits, cancellationToken)`
  of the listing above are not part of the contract; they were written by the contract
  author and removed again in this sprint, since nothing is released. What stays:
  `ReadMemoryLimit` and `ReadTimeLimit` of the options, `ReadLimitExceeded`,
  `LatticeLimitEnum` and `LatticeException.Limit`; `Enumerate()` and
  `EnumerateAsync(session, cancellationToken)` are subject to the limits of the options.
  Where this brief speaks of a limit "for one enumeration", of a null member or of a call
  that cannot lift a limit, read it as removed. The owner's reasons: no consumer needs
  different limits for different calls today; "larger or smaller than the options, but
  never none" was a weak rule; and S15 would have added a second form for searches. Whether
  a search takes limits of its own in S15 is to be re-read against this by the planner.
- **Carried by part 2 under O16, tests first: the planner's answer to Q50** of the
  [open questions](open-questions.md). Each database instance draws a seed from the
  random source of the system when it opens. Every hash of the primary index is computed
  with it, for every key type and both comparisons: integer and Guid keys mix it in
  before the finalizer, text hashes start from it. A String key compared with Ordinal no
  longer uses the hash of .NET for the index: one seeded hash of the engine's own over the
  text, so that the index depends on nothing a process sets. The seed is never stored,
  never sent to another node and never shown; a child and a reopened database draw their
  own. The order in which an enumeration returns entities is unspecified and may differ
  between two instances with the same data; if the documentation of `Enumerate` does not
  say so, it gains "The order is unspecified.", and a test that asserts an order is
  reported and restated. The hash function for text is taken from an established source
  under a permissive licence and named, by the licence rule; no hash is invented. An
  engine test fixes the seed through the internal options the tests already open the
  engine with. Acceptance: 2,000 Int64 keys built to collide under seed A are spread
  under seed B, shown by the chain length the index reports or by `PrimaryIndexBytes`,
  not by time; a UTF-8 and a UTF-16 database, a key with and without case, find every key
  under two different seeds; no test asserts an order of enumeration. Reported: reads by
  key a second before and after, Int64 and String keys, on the scenario of part 1; a
  seeded text hash slower than the hash of .NET beyond noise is reported before merging.

**The scaling of reads, by the owner's decision on Q42 of 15 November 2026**, given
through the planner's session: reads by key have to scale with threads, and the work is
done in this sprint, not judged at S25. The owner's reason: that more threads read less
in total is a very bad signal, and open-source databases that are very fast are there to
be studied: how they do it is to be learned from their source and, in essence, repeated.

- **Goal, added.** Reads by key scale with the number of threads that read.
- **To read, added.** The review of S06 on the scaling of reads (Q42), and "Reader
  protection, publication and compaction" of the milestone contract, which calls
  `ReaderWriterLockSlim` the initial synchronization.
- **Types and members, part 1** (the owner's decision of 16 November 2026, after the
  first implementation was stopped: its reads scaled, and its writer fell from 1.10 M to
  0.087 M replaces a second beside 20 readers, while reads beside a writer of inserts
  fell tenfold). One mechanism: protection of reads by epochs with deferred reclamation,
  repeated from `LightEpoch` of Garnet/Tsavorite — its source is read, and what is taken
  from it and what differs is written in the pull request. A read by key enters the
  epoch, looks up, copies and leaves; it takes no lock and writes to no memory another
  reader writes to. The publishing thread publishes a new root, a changed index entry or a
  replacement table and retires the old one; it does not wait for readers. A lookup in
  the primary index runs while the writer inserts, deletes or grows the index: growth
  builds the replacement while readers use the old table and retires the old table by
  the same epoch. How the index lets a reader look up during a change is taken from an
  established source too and named; nothing is invented. Internal members:
  `EngineCounters.RetiredIndexBytes`, `EngineCounters.ReplacementIndexBytes`,
  `EngineDatabase.DrainRetired()`. No public member changes. The rule is in the
  [arena protocol](../design/arena-protocol.md#deferred-reclamation-16-november-2026).
  The primary index is the engine's own hash table after the hash index of
  Garnet/Tsavorite (the owner's decision on Q48); `Arc.Collections` is not used for it,
  and the package reference, the pin test of Q3 and the sentence of `AGENTS.md` stay
  until the ordered indexes of S14 settle whether the package leaves the engine.
  Reported: bytes per entry of the index before and after, for an Int64 key and a String
  key, at 100,000 and 1,000,000 entities. `PrimaryIndexBytes` keeps its meaning. Carried
  under O16: `THIRD-PARTY-NOTICES.md` is packed into the package of the core library
  beside `README.md` (the owner's answer to Q47).
- **Sources and licences, part 1** (the planner, 16 November 2026; the rule is in the
  [development process](process.md#code-modelled-on-another-project)). Garnet/Tsavorite
  is MIT and is the source of the epochs and of the hash index. The Linux kernel (RCU,
  percpu-rwsem, seqcount) and liburcu are GPL or LGPL: their code is not opened and
  nothing is ported from it. The first implementation, which named those among its
  sources, is reverted; the new one is written from Tsavorite's source and takes no
  code from the reverted commit. After review round 1 the sequence of a root entry and the
  version of the primary index, which had kept the shape of the reverted commit, were
  matched to a named source by an agent that had not seen that commit: the `SeqLock` of
  crossbeam (MIT), at a pinned commit named in `EntityTable.cs`; its notice is in
  `THIRD-PARTY-NOTICES.md`. The 64-bit mixing of integer and Guid keys is the finalizer
  of MurmurHash3, which is in the public domain.
- **Tests the sprint breaks, part 1.** `TextKeyTests` addresses the type `TextKey`, which
  goes with the index of the package, and `RootTableTests` and `LayoutTests` name
  `RootEntry.ClaimCount`, which is no claim count any more while the entry keeps its 48
  bytes. The implementer makes the smallest change that lets them compile against the new
  types, changes no assertion of behavior, and lists each such edit in its report; the
  test author checks the list afterwards. The assertion on which slot a new entity gets
  (`HeldReadTests`) relies on the allocation order of the root table of S05; if that
  order changes, it is reported and restated by the test author. On a refusal of the
  arena a write drains and retries, and waits for the reads that still hold retired
  memory before it is refused. The wait has no option and no timeout: it is finite
  because it waits only for steps of reads, which run engine code alone, wait for nothing
  and run no code of the application; it honors the cancellation of the write's token
  while the write has not passed its commit point.
- **Added by the planner on 16 November 2026, after the implementer's study.** The epoch
  is not bumped by every write: the writer bumps and looks at the readers when the items
  retired since its last look reach the number of slots or their bytes reach one
  segment, and whenever memory is short, a drain is asked for or the database closes.
  The thresholds are derived from the table and the segment and are not options. Test:
  after more retired items than the threshold with no read in progress, `RetiredBytes`
  falls without a drain being called. The table has max(128, 2 × processors) slots,
  rounded up to a power of two so that a processor number picks a slot without a
  division (the coordinator, at the close: the code is the rule); a
  reader that finds no free slot waits for one, never fails and takes no part in any
  refusal. Test: more reading threads inside at once than slots, through the switch that
  holds reads inside. These two tests are written by the test author after the
  implementation commit, because the working tree was the implementer's when they were
  asked for, and before the review.
- **Counters and the drain, part 1.** No new counter for arena memory:
  `ArenaCounters.RetiredBytes` is the one, and it stops being 0. `RetiredIndexBytes` is
  the replaced tables of the primary index not freed yet. `RootEntries` goes on counting
  the entry of a deleted root until it is released. `DrainRetired()` frees every retired
  thing no read in progress can reach and returns; with no read in progress it leaves
  nothing retired. It is what a write under memory pressure calls, what a close calls
  after it has waited for the reads, and what a test calls before it counts. It does not
  wait and takes no timeout: a test that must count while reads run ends its reads
  first, and no test sleeps or polls for the counters. The tests that counted at once
  are restated by the test author as "after the writes returned and a drain"; that is a
  change of what they assert which the decision forces, entered here as such. The
  comments of `EngineCounters` and of `ArenaCounters.RetiredBytes` say the same.
- **Acceptance, part 1.** On the scenario `read-by-key-threads`, 1, 2, 4, 8, 16 threads
  and the number of processors, three runs each: (1) the total of reads by key grows
  with the reading threads up to the number of processors, without a writer and beside a
  writer of each kind — replaces, inserts and deletes without growth, inserts with
  growth; it need not be linear; (2), as restated by the planner on 16 November 2026
  after the first figures showed that the sentence which stood here measured the machine:
  nothing waits — the thread that publishes does not wait for readers on its ordinary
  path, and a reader does not wait for it. Beside N readers of its own database, the
  writes a second of a writing thread — replaces, inserts and deletes, inserts with
  growth — are not lower by more than 30% than those of the same thread beside N
  readers of another database in the same process, for N of 1, 4, 8, 16 and the number
  of processors; and they are not lower than on the protection of S06 for inserts and
  deletes and for growth. The control is part of the scenario. Each point says how many
  threads ran against how many logical processors; a point at which the threads exceed
  the processors is reported and is not the one the 30% is judged at. (3) reads
  beside a writer of any kind are not lower than on the protection of S06 at the same
  thread count. A step that breaks (1), (2) or (3) stops the sprint for the owner.
  Tests: the storms and the close cycles of S06 pass; after the writes have returned and
  a drain with no read in progress, nothing is retired and the counters equal the tally;
  a read that began before a replace, a delete or a growth returns one written state; a
  deleted entry is not reused while a read that could see it is in progress, shown
  through the internal switch that holds a read inside the protection; a close under
  reads frees everything retired. The primary index grows by building a replacement
  table while readers use the old one, and both tables are counted until the old one is
  released. The figures are reported to the owner at the close in any case and do not
  gate the merge when the conditions hold.
- **Measured, part 1.** The scenario above, before and after; how long index mutations
  wait while the index grows from 100,000 entries.
- **Split by the coordinator on 15 November 2026**, as the sentence that stood here
  allowed: the sprint is done as two, each with its own branch, test author, implementer,
  review and pull request. Part 1, `epic-1/sprint-08-read-protection`: the protection of
  reads and its measurement; no public member changes. The storms of the reviews of S06
  were probes of the reviewer and are in no test project, so the test author of part 1
  first writes them as tests of the engine, sized as the process asks: readers, writers
  and deletes on few keys with reuse of root entries, and close cycles under reads; they
  pass on the protection of S06 before the implementer changes it, and are not skipped.
  The scenario of the measurement on 1, 2, 4, 8 and 16 threads is written by the
  implementer. As amended on 16 November 2026 — part 1: the protection of reads, the
  index that is read while it changes, growth of the primary index included, deferred
  reclamation and their measurement. Part 2, `epic-1/sprint-08-enumeration`, cut from
  `main` after part 1 is merged: enumeration under change and the read limits, on the
  new protection. The first implementation of part 1, per-processor reader slots with a
  writer that waited for the reads, was stopped and reverted on the branch; its figures
  are in the bullet on types and members above.

#### S09 · Write coordinator and write limits

**Track:** full · **Needs:** S08 · **Decisions:** D7 · **Owners:** E03, E08

- **Goal.** Many callers write at once and the master orders, bounds and publishes their
  writes as the design requires, including when something fails.
- **Contract.** Options: the instance memory budget (by default half of the memory the runtime reports as available to the process). `WriteRejected`
  says which limit refused the write. The commit stage has a storage step that completes
  at once in MemoryOnly; it is internal.
- **To read.** [Write coordinator](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
  [publication](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction);
  [Q09.1](../decisions/write-acknowledgment.md); [Q14 master memory pressure, root size](../decisions/write-admission.md);
  [stable inputs, cancellation at the commit boundary](../decisions/mutation-commands.md);
  [initial settings](../design/milestone-1-engine-contract.md#initial-bounded-settings).
- **Acceptance.** Writes to different entities prepare concurrently and are published in
  one order of positions. Writes to one entity execute in the order they were admitted.
  A group holds at most 32 operations or 8 MiB and waits at most 1 ms to fill.
  Cancellation before dispatch withdraws the write and confirms that it did not run;
  after dispatch the call returns the real outcome. A write the memory budget cannot hold
  is refused before it has any effect and never runs later. Writes submitted faster than
  they complete are not refused: 10,000 writes submitted at once all complete, and those
  to one entity in the order they were admitted; what waits is counted in the memory
  budget. A preparation that fails releases
  everything it reserved, shown by the counters. A failure injected during publication
  puts the instance into `Faulted`; no reader sees a partial state, and operations
  report `Unavailable` until a new instance is opened. A `MemoryLimit` that is not
  positive or is smaller than one segment of the arena is `InvalidConfiguration`.
  Publication performs no I/O, no allocation of pool capacity and no user code. That
  preparation runs concurrently and that a group waits at most 1 ms are checked by the
  reviews; the size of a group is asserted through the counters. An instance opened
  without `MemoryLimit` has half of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` as
  its limit, and one opened with it has the given value.
- **Measured.** Reported: completed writes per second and p99 for 1, 4 and 16 callers of
  single small writes in MemoryOnly; reader p99 while they run.
- **Out of scope.** The batch (S17). The memory budget for the indexes, the text
  table and reads (S24), and compaction (S23). Operation tokens (S26). PostgreSQL.

**Types and members of S09.**

```csharp
namespace Moltaro.Lattice;

// LatticeDatabaseOptions gains
public long? MemoryLimit { get; init; }                        // bytes for the whole instance; null: half of the memory the runtime reports as available

// LatticeLimitEnum gains
Memory = 3,
```

By the owner's decision of 15 November 2026, given through the planner's session: the
default is half of `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, read when the
instance opens. By the owner's answer to Q37 the default is defined by that figure and
not by the limit of the container: on a machine without limits it is the physical memory,
in a container by default three quarters of the container's limit, and a configured heap
hard limit of the collector lowers it. The limit in force is fixed at open and does not
follow later changes. An explicit `MemoryLimit` that is not positive is refused by `Validate`.
`EngineCounters` gains `long MemoryLimit`, the limit in force, so that a test can state
the default. The documentation of the option says in plain words the three cases above,
that the default is per instance, that a process with several instances, production and
anyone who limits the heap of the collector set the value explicitly, and that the budget
counts what the engine holds, not the managed heap of the application. The limit in force is computed
once, when the instance opens, and the validation of that open uses the same value.

By the owner's decision O20 the queue of writes has no limit of its own: the options
`WriteQueueLimit` and `WriteQueueMemoryLimit` and the members `WriteQueue` and
`WriteQueueMemory` of `LatticeLimitEnum` that an earlier form of this brief had are gone.
After O19 and O20 the enum has `ReadMemory = 1`, `ReadTime = 2` and `Memory = 3`; nothing
was released, so the numbers are free. A write waits for its turn, what waits is counted
in the memory budget, and a write is refused only when the budget cannot hold it.

By decision O19 there is no option for the size of an entity, no member of
`LatticeLimitEnum` for it, and nothing for `Validate` to check of it.

`Validate` refuses a `MemoryLimit` that is not positive or is smaller than one segment of
the arena.
`WriteRejected` carries the `Limit` that refused the write. In this sprint the
memory budget bounds the arena, prepared capacity included: a write the arena cannot hold
is refused with `Limit` set to `Memory`. S24 extends the same budget to the indexes, the
text table, the pages of root entries and the native scratch of reads, if any exists by then; until then those are not
counted, and the documentation of `MemoryLimit` says so. Retired memory counts against
the budget; a write that meets the budget drains first and waits for the reads in
progress before it is refused (the owner's decision of 16 November 2026 on deferred
reclamation). Internal, in the engine
assembly:

```csharp
namespace Moltaro.Lattice.Engine;

// One write as the commit stage sees it: prepared, with everything its publication needs reserved.
internal sealed class PreparedWrite
{
    public int EncodedSize { get; }
}

// A short run of prepared writes that share one storage commit. A batch is never split across two.
internal sealed class CommitGroup
{
    public const int MaxOperations = 32;
    public const int MaxEncodedSize = 8 * 1024 * 1024;
    public static readonly TimeSpan MaxDelay = TimeSpan.FromMilliseconds(1);
    public IReadOnlyList<PreparedWrite> Writes { get; }
}

internal readonly record struct CommitOutcome(ulong FirstSequence);

internal interface ICommitStorage
{
    // Makes the group durable and assigns its positions. Completes at once in MemoryOnly.
    ValueTask<CommitOutcome> CommitAsync(CommitGroup group, CancellationToken cancellationToken);
}

internal enum EngineFaultPointEnum { BeforePublication = 1, DuringPublication = 2 }

// EngineDatabase gains
public void ArmFault(EngineFaultPointEnum point);              // the next pass through the point throws; tests only
// EngineCounters gains: int QueuedWrites, int LargestCommitGroup, long LargestCommitGroupBytes, long MemoryLimit
```

**Added by the planner on 15 November 2026, before the sprint started** (brief of S09).
Where a sentence above says otherwise, these replace it.

- **`PrepareWorkers` is no public option.** By the owner's decision O20 the database asks
  for as few settings as it can, and this one never refuses anything; it is tuning. How
  many writes are prepared at once is internal; the starting value is the 2 of the
  milestone contract, and the write figures of this sprint for 1, 4 and 16 callers show
  whether it bounds them. The planner reported it to the owner as its reading of O20.
- **A write larger than a group.** A write whose encoded size is above
  `CommitGroup.MaxEncodedSize` forms a group alone; the size of a group bounds what
  accumulates and never refuses a write. Acceptance: an entity of 32 MiB is written while
  small writes run, and all complete.
- **What is counted** (point 2 of Q35). `PreparedWrite.EncodedSize` is the bytes of the
  slots the write prepared in the arena, the root and every block of its values. A write
  that waits and is not prepared yet holds nothing in the engine: its input is the
  caller's, by the accepted rule of stable inputs. So "what waits is counted in the
  memory budget" is the prepared capacity of the arena, `ArenaCounters.PreparedBytes`,
  and there is no second figure: `EngineCounters` gains `int QueuedWrites`,
  `int LargestCommitGroup`, `long LargestCommitGroupBytes` and `long MemoryLimit`, and not
  `QueuedWriteBytes`.
- **No managed copy of an input** (the planner's answer to Q43, from the accepted
  decision "Stable write inputs until completion"). The engine takes the values of a
  write from the object or the record as the caller gave them and copies each once, into
  the arena; it makes no managed copy of a Binary value on the way. An input changed
  while its write runs gives a stored value that mixes the two states, never a damaged
  database: every byte is copied within the length taken at the start. The write batch
  keeps the copy it makes when an operation is added, until S17. The remarks of the write
  members gain, in the contract commit: "Do not change the entity, its collections or
  its byte arrays until the write completes: the database reads them while the call
  runs." Acceptance: a write of an entity with a Binary value of 32 MiB allocates no
  managed copy of the value. Two rules of this sprint, checked by the reviews: the values
  are encoded outside the lock that orders writes, and an insert takes the catalog gate
  exclusively once, at publication.
- **Order.** "Writes to one entity execute in the order they were admitted": admitted is
  the order in which the calls were made; a test makes the calls from one thread without
  awaiting them and awaits them afterwards.
- **Faulted.** In `Faulted` every operation, reads included, reports `Unavailable`; a
  close works. A MemoryOnly instance that faulted has lost its data, and the documentation
  of the state says so. The fault points are armed by tests of
  `Moltaro.Lattice.Engine.Tests` under X3; the scenario tests keep to the public
  interfaces.
- **The placeholder** ignores `MemoryLimit` until S12; the documentation of the option
  says "not enforced yet" for the public entry, as the other options of this stage do.

**Added by the planner on 16 November 2026, after S08.** Where a sentence above says
otherwise, these replace it.

- **Gates.** "The values are encoded outside the lock that orders writes, and an insert
  takes the catalog gate exclusively once, at publication" becomes: the values of a write
  are encoded outside the lock of the writes, and publication under that lock only
  installs what was prepared: the stores of S08 in their order, the position, the
  retirement of what was replaced.
- **Who may touch what.** S08 left the arena, the table of root entries, the indexes, the
  position clock and the retired list with one changing thread, the holder of the lock of
  the writes. S09 keeps that rule and does not make any of them safe for several threads.
  A write passes three steps. *Admission*, short, under the lock of the coordinator: the
  write reserves its identity (entity type and key, as the schema compares keys) in a
  first-in-first-out queue of that identity; a write whose identity is reserved by an
  earlier one waits its turn there and holds nothing in the engine. *Preparation*, of
  several writes at once, each holding its reservation so that no other write of that
  entity can overtake it: it looks the key up and reads what it needs of the current
  entity as a read does, inside the protection of reads; it checks its condition; it
  encodes its values into prepared blocks. Whatever changes a structure of the engine it
  does under the lock of the writes, held for that change alone and never while bytes are
  copied or a value is encoded: each allocation of a prepared block, the free of blocks
  it abandons, for an insert the new root entry and the room in the index (which may grow
  it), the room in the retired list. *Commit*, in one order: groups, the storage step,
  then publication under the lock of the writes. The lock of the coordinator and the lock
  of the writes are never held together; a type that takes both says so. No lock of its
  own for the arena is built: if the figures for 4 and 16 callers show the lock of the
  writes bounding preparation, that is reported with the number, and an arena lock
  ordered after the lock of the writes is the next step, not a part of this sprint. The
  reservation makes "captures needed current values" of the milestone contract safe
  without a gate: nothing else changes that entity until the write is published or
  abandoned, and its root cannot be freed under a read inside the protection.
- **The memory of reads is not in the budget.** The memory budget of the instance counts
  the native memory the engine holds. What an enumeration remembers is managed memory of
  its enumerator: it is bounded by `ReadMemoryLimit` when that is set and is no part of
  the instance budget. The documentation of `MemoryLimit` says the budget counts what the
  engine holds, not the managed heap of the application, "nor what an enumeration
  remembers". The sentence of the Acceptance of S08 on counting it in the instance budget,
  and an addition to this brief that carried it, are withdrawn; S24 counts native scratch
  of reads, if any exists by then.
- **The refusal.** `MemoryLimit` becomes the limit of the arena the engine opens (the
  internal option the tests of S08 already set), so the refused-write path of S08 is the
  path: no second check beside it. `WriteRejected` from it carries `Limit` = `Memory`.
  With several writes prepared at once the rule of when a write is refused is restated,
  since other writes now hold prepared memory and retire while it waits: a write the
  arena refuses frees what is retired and retries; while it is still refused it waits,
  outside every lock, for the reads that hold retired memory and for every write admitted
  before it to complete, and retries; it is refused only when every write admitted before
  it has completed, nothing retired is left and the arena still has no room. That is
  finite — the writes ahead of it are finite — and the token of the write cancels the
  wait. A write refused so has had no effect and never runs later. Acceptance: on a small
  arena, a write that fits only after an earlier, still unpublished replace has retired
  its old root and it was freed, completes; one that does not fit an otherwise idle
  database is refused with `Limit` = `Memory`. Exception X6 is re-read by the contract
  author against this path: the wait for reads now happens outside the lock of the
  writes.
- **`Enumerate`.** The contract says what the engine does. In `ILatticeSet<TEntity>` and
  `ILatticeSet` the sentence with "may or may not be reflected" becomes: "The enumeration
  is not a snapshot. An entity that exists from the start of the pass to its end is
  returned exactly once, in one state that was written: the one before or the one after a
  replace made during the pass. An entity inserted or deleted during the pass may or may
  not be returned. No key is returned twice." The contract author checks that the
  placeholder holds it and reports before changing anything if it does not. The sentence
  of `ILatticeIndex` about a search stays until S14 and S15.
- **No wait to fill a group in MemoryOnly.** "A group holds at most 32 operations or 8 MiB
  and waits at most 1 ms to fill" becomes: a group is formed from the writes that are
  prepared when the commit stage becomes free, at most 32 operations or 8 MiB, a larger
  write alone; the commit stage never waits for a group to fill. A delay to fill a group
  is a matter of the durable storage step and is decided with its sprint (S27), with
  figures. `CommitGroup.MaxDelay` is not built in S09, and "that a group waits at most
  1 ms" leaves the list of what the reviews check.
- **A fault during publication and the reads that retry.** Publication is several stores,
  and between the mark of a write in progress and the new stamp a read of that entity
  retries. Added to "Faulted": a fault during publication never leaves a read retrying: a
  read that retries checks the state of the instance and reports `Unavailable` once it is
  `Faulted`. "No reader sees a partial state" means: no read returns a mixture of two states
  of an entity, and none is left retrying. A read that overlaps a faulted publication
  returns the state before the write, the whole state of the write, which the storage step
  had taken, or `Unavailable`; a read that begins after the instance is `Faulted` reports
  `Unavailable`. Acceptance, for a replace, a delete and an insert: with
  `DuringPublication` armed for a replace, a read of that entity started before and one
  started after both end, with the old state or `Unavailable`, within the time of the
  test.
- **Measured, added.** Completed writes a second for 1 caller next to the figures of S08,
  part 1 on the same scenario (replace, insert and delete), and reads by key on 16
  threads while 1, 4 and 16 callers write, next to S08. A fall of the single caller from
  the hand-over between threads is reported with its size and cause; reads by key do not
  fall against S08 beyond noise (reads first, the rule of the owner of 16 November 2026).
  A fall is handled as the owner said in the coordinator's session on the same day and
  as the [development process](process.md#when-to-stop-and-ask-the-owner) now states:
  the cause is found, existing solutions are studied, alternatives are tried; the sprint
  stops for the owner only when nothing helped, the fall is not what the problem costs
  everywhere, and a requirement is missed.
- **Answers of the planner to the contract author, 16 November 2026**, from standing
  decisions.
  - *Sizes.* `PreparedWrite.EncodedSize` is `long`, and so is every sum of it;
    `CommitGroup.MaxEncodedSize` is a `long` constant. No rule bounds an entity (O19).
  - *The computed default* is never refused: half of the memory the runtime reports, and
    not less than the minimum of `MemoryLimit`, one segment. The sentence "the validation
    of that open uses the same value" is dropped; `Validate` checks only an explicit
    `MemoryLimit`, and the documentation of the option gains "and never less than" the
    minimum.
  - *The internal `Create` with `ArenaOptions`.* They are the options of the arena and
    `MemoryLimit` of the public options is not read; `EngineCounters.MemoryLimit` reports
    the limit in force. Tests of a small arena use `ArenaOptions`; tests of the option use
    the public open.
  - *The fault points* both lie after the storage step and both fault the instance.
    `BeforePublication`: the storage step of the group has completed and no store of its
    publication has been made; in MemoryOnly "committed" means that the storage step
    returned. "A preparation that fails releases everything and the instance stays Ready"
    needs no fault point: it is shown by real failures of a preparation (a refusal of the
    arena, a cancelled write, a value the writer refuses). `DuringPublication`: for a
    replace, after the store of the root and before the new stamp, the mark set; for a
    delete, after the store of the state and before the new stamp, the mark set; for an
    insert, after the store of the index entry and before the position is published. An
    armed point throws `InvalidOperationException` with a message naming the point.
  - *After a fault.* A write of the group whose stores were all made before the fault gets
    its real result: it is published. The write whose publication faulted and the writes
    of the group after it get `LatticeException` with `IndeterminateOutcome`: the storage
    step took them and the instance cannot show them; the injected exception is the
    `InnerException` of the first of them. Writes prepared and not yet taken by the commit
    stage, writes being prepared and writes waiting for their identity get `Unavailable`:
    they did not run, and everything they held is released. Every later operation is
    `Unavailable`; a close works.
  - *The counters.* `QueuedWrites` is a current number: the writes admitted and not yet
    completed — waiting for their identity, waiting to be prepared, being prepared,
    prepared, in the commit stage; 0 when every call has returned. `LargestCommitGroup` and
    `LargestCommitGroupBytes` are maxima since the open, taken independently of each
    other; a lone large write is a group of one and counts in both.
  - *Dispatch* is the moment the commit stage takes the write into a group and hands the
    group to the storage step: the call of `ICommitStorage.CommitAsync`. Before it a
    cancelled write is withdrawn, releases what it prepared and ends in
    `OperationCanceledException`; from it on the call returns the real outcome and the
    token is not looked at. No switch is added: the internal `Create` takes an
    `ICommitStorage`, a real boundary of the engine, and the storage of a test holds a
    commit until the test releases it. While it is held, later writes are admitted and
    prepared and wait for the commit stage, so a test can cancel one before its dispatch
    and state through `PreparedBytes` and `QueuedWrites` that nothing of it is left, state
    that several are prepared at once, and form groups of a known size. The default
    storage of MemoryOnly completes at once. The storage of the tests lives in the engine
    test project.
  - *Positions.* The writes of a group take consecutive positions from `FirstSequence`, in
    the order the commit stage took them, which for one entity is the order of admission;
    a delete of an absent key takes one as any write does.
  - *The order of writes to one entity* is a public guarantee, documented on the write
    members of both sets in the contract commit: "Writes to one entity are executed in the
    order their calls were made: a call that is made after an earlier call has returned
    its task is executed after it, whether or not the earlier task was awaited. Calls that
    race on different threads have no order between them. Writes to different entities
    have no order beyond their positions."
  - *The refusal and decision Q14* of the decision on write admission: a dated addition
    there says that the write waits for reads and for the writes admitted before it; the
    wait for earlier writes is the planner's reading of O20, reported to the owner.
  - *Coordinator:* the commit types live in `Engine/Writes`, by the folder rule.
- **Added by the planner on 16 November 2026, after the implementation.**
  - *One write path.* The three steps hold for both modes, and the figures of the path are
    judged again with S27, where the question is writes a second per storage commit. No
    short path is built for MemoryOnly: it would be a second implementation of order,
    cancellation and faults for the mode that is not the default of a master.
  - *One bounded pass at the bookkeeping*, after the review, each change kept only if a
    figure moves beyond noise and nothing restructured: a write that completes without
    waiting allocates nothing for its completion, and the record of a write in the
    coordinator is reused from a pool; adjacent acquisitions of the lock of the
    coordinator with nothing between them become one; the identity dictionary is skipped
    for a write that finds no other in flight only if that keeps the first-in-first-out
    rule without a second code path. Reported: bytes allocated per write before and
    after, for a lone caller and for 16. If the pass moves nothing, that is the result.
  - *The retry of a refused write is bounded by the writes admitted before it.* A write
    the arena refuses frees what is retired and retries. While it is refused it waits for
    every write admitted before it to complete and for what was retired up to that moment
    to be freed — the reads then in progress leave within one step each — and retries
    once more. If the arena still has no room it is refused, whatever later writes have
    retired since. One mark taken when the last earlier write completes, the newest
    retired item at that moment, is enough; the wait ends when the retired list has been
    freed up to the mark. Acceptance: on a small arena a write that cannot fit is refused
    while another caller keeps replacing a small entity, within the time of the test.
  - *A storage step that throws* leaves the outcome of its group unknown: the whole group
    gets `IndeterminateOutcome` and the instance is `Faulted`. That is the rule for an
    exception the storage does not classify.
  - *A close waits for a storage step in progress;* a close is complete.
  - *One reused `CommitGroup`.* The documentation of `ICommitStorage.CommitAsync` says:
    "The group and its list belong to the commit stage and are reused: a storage reads
    them until its task completes and keeps no reference afterwards."
  - *Preparing workers.* There are 2 places of preparation; the number is internal and no
    rule of the contract bounds it. With 64 places nothing was gained.
- **When a write is refused for memory: the rule after review round 1** (the planner, 16
  November 2026; the first reviewer found that the rule above refused a write that fits
  when a later write held prepared memory). This replaces every earlier wording of it in
  this brief: "The refusal" of the block after S08, and "The retry of a refused write is
  bounded" of the block after the implementation.
  A write is refused for memory only when it does not fit beside the stored data: never
  because of memory that other writes, earlier or later, hold only for a time, and never
  because of memory that is retired and not freed yet. A write the arena refuses gives
  back everything it prepared, frees what is retired and retries. If it is refused again,
  it is resolved alone: (1) it waits for every write admitted before it to complete; (2)
  it holds back the preparation of later writes: writes admitted after it are admitted
  and wait, and none begins to prepare; those already prepared or in the commit stage
  complete, and it waits for them; (3) it waits for the reads then in progress to leave
  the protection, frees what is retired, and retries once, with no other write holding
  prepared memory; (4) if the arena has room, it goes on as any write; if not, it is
  refused with `WriteRejected` and `Limit` = `Memory`. In both cases the later writes go
  on at once. While it waits it holds no prepared memory. Two writes refused at the same
  time are resolved one after the other, in the order they were admitted. The wait is
  bounded by the writes in flight when step 2 begins and by one step of the reads then in
  progress; no write admitted later prolongs it. The token of the write cancels the wait
  at any of its steps, and the later writes then go on. A refusal is rare, so holding
  preparation back for it costs the ordinary path nothing: the ordinary path pays one
  check.
  Acceptance, tests first: on an arena of 8 MiB, an insert of 3 MiB that is the oldest
  write while a later replace of 3 MiB is prepared and held completes; without hooks, a
  5 MiB insert into an empty 8 MiB arena beside a 9 MiB insert that can never fit: the
  5 MiB insert always completes and the 9 MiB one is always refused, over the rounds the
  test author sizes; two replaces that both fit in either order both complete; a write
  that cannot fit is refused within the time of the test while another caller keeps
  replacing a small entity; after a refusal the writes held back complete and
  `QueuedWrites` returns to 0.
- **Cancellation before dispatch** (the planner, the same day, after the same review).
  Before dispatch, the token of a write is honored at every wait — for its identity, for
  a place to be prepared, for earlier writes, for the writes in flight in a resolution,
  for reads that hold retired memory — and when its preparation ends. A cancellation that
  arrives while its values are being encoded takes effect when the encoding ends, before
  the write can be dispatched: it is withdrawn, what it prepared is freed, and it ends in
  `OperationCanceledException`. Tests first: a write cancelled while it is being prepared
  is withdrawn and leaves nothing in the counters; a write cancelled during the wait of a
  resolution ends, and the writes held back go on. Every wait of a write before dispatch
  has the token. Without a timeout are the waits for steps of reads and the waits of a
  write for other writes — its identity, a place to be prepared, the commit stage, and
  those of a resolution — each of which ends when the writes it waits for end (X6; the
  coordinator's correction after the implementer listed them).
- **The order of writes and the wait of a session** (the planner, 16 November 2026, after
  review round 1). No identity is reserved across a wait for a position: a reservation
  held for up to the timeout of a session would hold every later write of that entity
  behind a wait that may fail. The sentence on order of both sets becomes: "Writes to one
  entity are executed in the order their calls were made: a call that is made after an
  earlier call has returned its task is executed after it, whether or not the earlier task
  was awaited. A write that first waits for the position of its session takes its place in
  that order when the wait ends, so a later call may be executed before it. Calls that
  race on different threads have no order between them. Writes to different entities have
  no order beyond their positions." Written by the implementer with the fixes. One test: a
  write with a session ahead of the database and a later write without a session to the
  same entity — the later one completes first, the first completes when its position is
  reached, and both results are those of that order.
- **Unchanged and confirmed again.** `PrepareWorkers` internal, starting at 2; a write
  larger than a group forms one alone; what is counted (`PreparedBytes`, no
  `QueuedWriteBytes`); no managed copy of an input; order of admission; the placeholder
  ignores `MemoryLimit` until S12.

#### S09, part 2 · The write path after a proven source

**Track:** full, two reviewers · **Needs:** S09 · **Decided by:** the owner, 17 November
2026, in the planner's session. The owner read the figures of S09 and the comparison with
the industry and asked why the approaches of the industry are not simply repeated to keep
writes as fast as they can be. For reads the sources were studied and repeated; the
coordinator of S09 — a queue per identity, places of preparation, a commit stage — came
from the design documents, written before any measurement, and was never held against a
working system. Of three ways — this sprint now, the question left until S27, or left as
it is — the owner chose the first. S09 closes as it is.

- **Goal.** Writes keep every guarantee of S09 and follow a write path that an
  established system with one order of writes uses; several callers write no less in
  total than one.
- **Step 1, a study, before any code** — as in S08, part 1. The implementer reads the
  source, not summaries, of: the write group of RocksDB (the write thread: joining a
  group, leader and followers, the pipelined form); the execution of commands on one
  thread in Valkey; the single writer of LMDB; and Tsavorite for contrast. For each it
  writes what the system guarantees about the order of writes and about conditions
  (insert only if absent, an expected revision), what it costs per write, and what it
  does when a log has to be written first. It then proposes the path for Lattice and says
  for each guarantee of S09 how the path keeps it, and in a paragraph each how the batch
  of S17 (all its entities at one position), the partial update of S20 (which needs the
  current state) and the durable storage step of S27 fit. The coordinator and the planner
  read the report before anything is built. Licences by the rule of the process: each is
  checked at the commit read; where a project offers several, the permissive one is
  taken; a source under a licence that is not permissive is not opened.
- **What does not move.** The public contract of S09 and its Acceptance: the order of
  writes to one entity with its sentence on the wait of a session, consecutive positions,
  cancellation before dispatch and not after, the rule of when a write is refused for
  memory, the faults and what each caller sees, a close that is complete, no managed copy
  of an input, `ICommitStorage` as the boundary of the storage step and the bounds of a
  group it is handed. The scenario tests of S09 pass with their assertions unchanged. A
  test of the engine that states the internal shape of the coordinator and not a
  guarantee — what `QueuedWrites` counts, how many writes are prepared at once — is
  reported and restated by the test author.
- **What may go.** The queue per identity, its dictionary, the places of preparation, the
  number of acquisitions of the two locks: whatever the proven path does not need. If
  conditions are checked when a write is published, as in S08, and the path keeps the
  order of calls without a reservation, the reservation goes; the milestone contract,
  "Write coordinator and revision semantics", is then corrected with the sprint — the
  planner sends the text after the study — and the owner has agreed to the direction.
- **Acceptance, measured**, as restated by the planner on 17 November 2026 after the study
  (the line "the total of 4 and of 16 callers not lower than that of one caller" was
  wrong: no path whose work in memory is serial can meet it, that of S08 and of LMDB
  included). On the scenario of S09, on a quiet machine, next to S08 and S09: writes a
  second of one caller not lower than S08 beyond noise; the total of 4 and of 16 callers
  not lower than that of S08 at the same count; reads by key on 16 threads not lower than
  S09; bytes allocated per write as S08. Reported: p99 of a write for 1, 4 and 16
  callers, alone and beside readers; and what a large entity costs the others — how long
  the lock of the writes is held by one write of 32 MiB, and p99 of small writes of
  another caller while a caller writes entities of 32 MiB one after another; that pair
  decides whether a parallel form is ever needed, so it is measured carefully. A figure
  that misses is handled by the rule of the process. The implementation report also
  says: that the size of a write is counted by the writer and not walked afterwards; and,
  in a paragraph on fairness, whether a fair lock was measured and what it cost — the
  lock is not fair and p99 with 16 callers returns to the 1.8 ms of S08 — kept only if a
  figure justifies it.
- **The path, decided after the study** (the planner, 17 November 2026): the path the
  study recommends, the writer of LMDB with the group commit of PostgreSQL and Pebble at
  the storage boundary. It is written out in the milestone contract, "Write coordinator
  and revision semantics", which this sprint corrects. A leader with followers, as in
  LevelDB, was prototyped and dropped on its figures. Sources read at pinned commits,
  licences checked first: RocksDB (under Apache-2.0), LevelDB, Valkey, LMDB, Pebble,
  PostgreSQL, Garnet/Tsavorite; Redis was not opened, its licences are not permissive.
  What changes in the brief of S09: its Acceptance line "Writes to different entities
  prepare concurrently and are published in one order of positions" becomes "Writes are
  prepared and published one at a time, under the lock of the writes, in one order of
  positions"; "the values of a write are encoded outside the lock of the writes" becomes
  "a write is prepared under the lock of the writes, in the hold that checks its
  condition; publication only installs what was prepared"; "Who may touch what", with its
  three steps and the lock of the coordinator, is withdrawn whole. The refusal for memory
  keeps its rule, only when a write does not fit beside the stored data: in MemoryOnly no
  other write holds prepared memory when a caller prepares, so it is the path of S08 —
  free what is retired, wait for the reads that hold it, retry, refuse — and the
  resolution alone is kept, in a smaller form, for when the storage step is busy; its
  tests pass unchanged.
- **`ICommitStorage.CommitAsync`**, its documentation: "Called under the lock of the
  writes. The call itself only takes the group: it does not block, performs no I/O and
  runs no code that waits; whatever takes time is behind the task it returns. A task that
  is already completed means the group is committed, and the caller publishes it in the
  same hold. The group and its list belong to the commit stage and are reused: the
  storage reads them until its task completes and keeps no reference afterwards."
- **Counters.** `QueuedWrites` keeps its meaning, admitted and not completed. In
  MemoryOnly `LargestCommitGroup` is 1 (*corrected after review round 1:* except after a
  write refused for memory was resolved, when the writes it held back are prepared in one
  hold and published as one group), and the tests that form larger groups do so
  through the held storage. Tests of S09 that state the shape that goes are restated by
  the test author before the implementer starts: `CommitGroupTests.Writes_prepared_while_
  a_call_runs_the_commit_stage…` (it uses a storage that blocks inside its call, which the
  rule above forbids: it becomes "writes prepared while the storage step holds a group,
  whose calls returned their tasks, are dispatched and published by whoever completes the
  storage step"), `MemoryOnlyPositionsTests` (it waits for a group of more than one write
  under the storage of MemoryOnly, which never forms: it becomes "sequences are exactly
  1..N, one per write"), and comments that say which thread holds the lock of the writes.
- **Carried by this sprint under O16, tests first, as its first commits and independent
  of the study: three findings of an outside review of `main` at `6564720`** (the owner ran
  it on 17 November 2026; no P1, three P2; the planner agrees with all three, and the
  owner said they are fixed in this sprint, and merged all the same if the sprint ends
  with the coordinator of S09 staying).
  1. *A second `CloseAsync` completes before the memory is freed.* The first call sets
     `Closed` at once, so a second call returns a completed task while reads are still
     inside and a segment is still allocated; after the wait of the first call was
     cancelled, a `DisposeAsync` cannot wait for the release either. The rule: the close of
     a database is one operation. Every call of `CloseAsync` and `DisposeAsync` after the
     first joins it and completes when it completes; the token of a call ends the wait of
     that call only and never the close. Tests: with a read held inside the protection the
     first close waits, a second call is not completed, both complete after the read
     leaves and the arena reports nothing allocated; a first call whose token is cancelled
     ends in `OperationCanceledException` and a later `DisposeAsync` still waits for the
     release. The same is checked on the `LatticeDatabase` wrapper if it keeps a state of
     its own.
  2. *The cancellation of an enumeration is lost inside the wait for an epoch slot.* The
     token is checked before a step and not passed on; with every slot taken a cancelled
     `MoveNextAsync` goes on waiting, and after a slot is free it reports
     `ReadLimitExceeded` where the contract promises `OperationCanceledException`. The
     rule: the token of an asynchronous enumeration is honored inside every wait of a step
     — for the position of the session, for a slot of the protection — and between the
     rounds of a step; a cancellation is reported before a limit is. Test: every slot held
     through the internal switch, `MoveNextAsync` cancelled before `ReadTimeLimit` — it
     ends in `OperationCanceledException` while the slots are still held. X6: the wait for
     a slot has the token wherever the caller gave one; the synchronous forms have none.
  3. *Reads by name box a numeric key:* 100,000 reads of absent Int64 keys allocate
     2,400,000 bytes by name against 0 through a class. The contract changes; nothing is
     released. Every member of `ILatticeSet`, and of the batch by name if it takes a key
     as `object`, that takes a primary key gets a form that takes it without a box. The
     shape is chosen by a figure between two plain ones, in the contract step: (a) the key
     parameter becomes generic, `Get<TKey>(TKey key) where TKey : notnull` and so on,
     replacing `object key` — a boxed key still works as `TKey` = `object`; one member per
     operation, but a generic method of an interface is dispatched more slowly; (b)
     overloads beside `object key` for the key families `long`, `ulong`, `Guid` and
     `string` — more members, plain dispatch. Whichever reads by name with 0 bytes and is
     not slower per call than the form through a class beyond noise is taken; if both are,
     (a), for fewer members. The rule of the key stays: an integer of any width that fits,
     a Guid, a string; nothing parsed. Tests: the allocation test of reads by key gains
     the cases by name for Int64, Guid and String keys, present and absent, 0 bytes. The
     measurement scenario of reads by key measures by name as well from now on.
     *Chosen in the contract step, 17 November 2026: (a), the generic key.* Both shapes read
     by name with 0 bytes, and neither is slower than the read through a class (ratios to
     the class read of the same round between 0.87 and 0.99 for both, on a loaded machine);
     the form that took `object` allocated 24 bytes for an Int64 key and 32 for a Guid on
     every call. Against (b) stood, beside 40 members instead of 8: the language would
     convert `char` and `nint` to the `long` overload, so by name would accept what a class
     refuses, and a literal `null` would bind to `string`. What a caller sees: every call
     compiles as before; a literal `null` key no longer compiles. The delete of the batch by
     name changed alike; the delete of the batch through a class keeps `object` until S17
     decides how a batch stores its keys. `IndexKey` and `IndexBound` keep their values as
     `object` and allocate on every search: not this defect, since a search is not
     implemented, and a matter of the contracts of S14 and S15.
- **Out of scope.** The durable step (S27), the batch (S17), a second path for
  MemoryOnly.

### Stage 3. Master and children

#### S10 · A child loads the database

**Track:** full · **Needs:** S09 · **Decisions:** O7, O8, D3 · **Owner:** E07

- **Goal.** A child connects to the master over TCP, loads the whole database of a
  master that is not being written to, and becomes ready.
- **Contract.** `ListenUri` and `MasterUri` name a TCP endpoint. The open timeout of a
  child is an option, 30 seconds by default, and the documentation of `OpenAsync` no
  longer lets a child wait without end. Internal: the 32-byte frame header, the handshake
  that binds database, history, schema fingerprint, format and role, data frames of at
  most 64 KiB, and the transfer of arena blocks with their relocation.
- **To read.** [Replication transport and initial loading](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading);
  [Q08](../decisions/replication-and-hosting.md), [Q15](../decisions/replication-and-hosting.md);
  [logical encoding](../design/milestone-1-engine-contract.md#independent-base-journal-and-recovery).
- **Acceptance.** A child receives the schema and every entity and serves no read before
  it is complete. A child of an empty master becomes ready at sequence zero. A child
  whose master is unreachable fails with `Unavailable` when the open timeout passes, and
  is closed. A peer with another database, history, schema fingerprint or format is
  refused before anything is allocated for it. A frame with a wrong length, kind or
  checksum closes the connection and applies nothing. No lock is held while bytes are
  sent or received. Entities travel as binary arena blocks, not field by
  field: the child places each block in its own arena and repairs the relative addresses,
  and the loaded entities equal the master's for every scalar type. An invalid address,
  count or length inside a received block is refused before it is used. The product
  documentation states the precondition of deployment: the
  replication port is reachable only from the nodes of the database, because the engine
  authenticates nobody.
- **Measured.** Reported: time from the child's open to Ready for 100,000 scalar entities
  on loopback, with the bytes transferred and the peak memory of both sides.
- **Out of scope.** Loading while the master writes, and following it afterwards (S11).
  Writes through a child (S12). Reconnect and reload (S34). Until S11 a master of the
  engine refuses a write with `NotSupportedException` while a child is connected or
  loading, and a loaded child stays at its readiness position.

**Types and members of S10.**

```csharp
namespace Moltaro.Lattice;

// LatticeDatabaseOptions gains
public TimeSpan OpenTimeout { get; init; } = TimeSpan.FromSeconds(30);   // a child: from the open call to Ready

// ILatticeDatabase gains
// The address a master accepts its children at, with the port it actually bound; null for a
// master without ListenUri and for a child.
Uri? ListenUri { get; }
```

For the engine `ListenUri` and `MasterUri` use the scheme `lattice` and name a host and a
TCP port: `lattice://host:7400`. Port 0 in `ListenUri` lets the system choose; the chosen
port is read from `ILatticeDatabase.ListenUri`. The engine refuses another scheme, a path
or a missing port with `InvalidConfiguration` when it opens. `Validate` itself keeps
accepting what it accepts today, because until S12 the placeholder is behind the entry
point and its tests use other addresses; S12 moves the rule into `Validate`. `OpenTimeout`
must be finite and positive. The placeholder implements `ListenUri` by returning the configured address.

The second paragraph of the remarks of `ILatticeDatabase.OpenAsync` becomes:

> A child connects to the master at `MasterUri`, loads the whole database and only then
> becomes readable. When it is not ready within `OpenTimeout`, the call fails with
> `Unavailable` and the instance is closed.

Internal, namespace `Moltaro.Lattice.Replication`:

```csharp
internal static class Protocol
{
    public const uint Magic = 0x5454414C;                    // the bytes 'L', 'A', 'T', 'T'
    public const ushort Version = 1;
    public const int MaxDataBody = 64 * 1024;
    public const int DigestSize = 32;                        // SHA-256, the last bytes of every body
}

internal enum FrameKindEnum : ushort { Hello = 1, HelloReply = 2, Schema = 3, Blocks = 4, LoadEnd = 5, Refuse = 6 }



internal enum RefuseReasonEnum : ushort { Database = 1, History = 2, Schema = 3, Format = 4, Role = 5 }

// The body of Hello and of HelloReply. An empty child sends an empty database identity,
// history zero and an empty schema fingerprint: it has no schema before it loads one.
internal readonly record struct HelloBody(
    Guid DatabaseId, ulong History, LatticeFingerprint SchemaFingerprint,
    int WireFormat, int LayoutFormat, int CaseTableVersion, LatticeRoleEnum Role);

// Writes and reads whole frames. The digest is SHA-256 over the 32 bytes of the header
// followed by the body without its last 32 bytes, which hold the digest.
internal static class FrameCodec
{
    public static void Write(IBufferWriter<byte> output, FrameKindEnum kind, ulong correlationId, ulong streamSequence, ReadOnlySpan<byte> body);
    // False when more bytes are needed. ValidationFailed on a wrong magic, version, kind, length or digest.
    public static bool TryRead(ref SequenceReader<byte> input, out FrameHeader header, out ReadOnlySequence<byte> body);
    // The four below write and read a body only; Write frames it with its kind.
    public static void WriteHelloBody(IBufferWriter<byte> output, in HelloBody hello);
    public static HelloBody ReadHelloBody(ReadOnlySequence<byte> body);
    public static void WriteRefuseBody(IBufferWriter<byte> output, RefuseReasonEnum reason);
    public static RefuseReasonEnum ReadRefuseBody(ReadOnlySequence<byte> body);
}

internal struct FrameHeader                                  // 32 bytes, asserted
{
    public uint Magic;
    public ushort ProtocolVersion;
    public FrameKindEnum Kind;
    public uint Flags;
    public uint BodyLength;                                  // at most 64 KiB for a data frame
    public ulong CorrelationId;
    public ulong StreamSequence;
}

// Writes one root with every block it owns as binary arena blocks. Relative addresses become
// block numbers of the transfer; no native address leaves the process.
internal static class BlockExport
{
    public static void Write(IBufferWriter<byte> output, Arena arena, RootLayout layout, ArenaAddress root);
}

// Places the blocks of one root in the receiving arena and repairs their relative addresses.
internal sealed class BlockImport
{
    public BlockImport(Arena arena, LatticeSchema schema);
    // ValidationFailed, with nothing left allocated, when a length, a count, a kind or a
    // block number is not valid; nothing is dereferenced before it is checked.
    public ArenaAddress Read(ref SequenceReader<byte> input, RootLayout layout, RootHandle root);
}
```

**Added by the planner on 15 November 2026, before the sprint started** (brief of S10),
on the owner's word, against the engine as S06 left it. Where a sentence above says
otherwise, these replace it.

- **Identity.** A MemoryOnly master takes a new random database identity with its new
  history at each open; a Durable master reads both from its store (S27). A child has
  neither before it loads, so in this sprint a refusal for another database, history or
  schema cannot come from a real child: the tests of `Moltaro.Lattice.Engine.Tests` send a
  Hello they make themselves.
- **Byte order** (the planner's answer to point 3 of Q35). Blocks travel as the bytes of
  the arena, in the byte order of the sending machine. `LayoutFormat` of the Hello
  includes the byte order, and a peer of another byte order is refused with `Format`. No
  conversion is built.
- **The version of the case table** (the owner's decision on Q46; added by the planner on
  15 November 2026). The Hello carries the version of the case table
  (`CaseTable.Version`) of the sender. A peer whose version is another is refused as a
  peer of another layout format is, with the same error, before any block is sent: two
  nodes with different tables would hold different keys equal. The version is compared
  for equality only; nothing is converted. Acceptance: a peer that announces another
  case table version is refused with the error of an incompatible peer, and no data of
  it is applied — a test through the internal frame, as the byte-order test is written.
- **A root larger than a frame.** A block with its framing does not fit one data frame,
  and by decision O19 a root of tens of mebibytes is ordinary. The export of one root is
  one stream, cut into `Blocks` frames of at most `MaxDataBody`; bit 0 of
  `FrameHeader.Flags` marks the last frame of a root. The master copies a root coherently
  under its protection into a buffer, releases the protection and then sends; the child
  puts the frames of one root together and then imports it. Each side holds one root at
  a time outside its arena. `BlockExport.Write` and `BlockImport.Read` keep their
  signatures.
- **What the child's caller sees.** While the master cannot be reached the child keeps
  trying, with the back-off of the milestone contract, until `OpenTimeout`: a child may
  be started before its master. A refusal by the master, a frame that is not valid and a
  database the child's memory limit cannot hold end the open at once, without waiting
  for the timeout: `Unavailable`, with the reason in the message, and the instance is
  closed. Before it is Ready a child is `Loading` and every operation is `Unavailable`.
  Until S12 a write on a child of the engine is `NotSupportedException`.
- **The master's address.** A master binds the address the host of `ListenUri` names. A
  failure to bind, a port in use for one, is reported as the system reports it and the
  instance is closed. `OpenTimeout` is read by a child only.
- **A loaded child** has the history of its master and the sequence at which the load
  ended; `Schema`, `Set<TEntity>()` and `Set(name)` work on it as on a master.
- **Acceptance, added.** An entity of 32 MiB is loaded by a child and equals the
  master's. A child started before its master becomes Ready when the master opens within
  the timeout. A child whose memory limit cannot hold the database fails its open at once
  with `Unavailable`.

**Added by the planner on 17 November 2026, against the engine as S09, part 2 left it.**
The owner asked for this check of the briefs of S10, S11 and S12. Where a sentence above
says otherwise, these replace it.

1. **Hello.** `HelloBody` gains `int CaseTableVersion` (the text of 15 November says so;
   the record did not follow). Refusal: `RefuseReasonEnum.Format`.
2. **One changing thread on the child.** The arena has one changing thread at a time (S04)
   and that is whoever holds the lock of the writes (S09, part 2). The loader of a child
   places and publishes roots under the child's lock of the writes, as a write does; it
   uses no storage step. No second way of changing an arena is built.
3. **The capture is not a read of the application.** The master walks the slots of the
   root table, not the primary index, and not through the public enumeration:
   `ReadMemoryLimit` and `ReadTimeLimit` do not apply to it and it remembers no keys. It
   enters the protection of reads for one root, takes the entry as one state the way
   `EntityTable` does, copies the root with the blocks it owns into its buffer, leaves the
   protection, then sends. It never holds a slot of the epoch table across a send or
   between two roots. "Under its protection" and "release gates" in the milestone contract
   mean this.
4. **Revisions.** The transfer of a root carries its last mutation sequence
   (`RootEntry.LastMutationSequence`), and the child stores it. Acceptance, added: the
   revision of every loaded entity read on the child equals the revision read on the
   master. (S12 needs it: an expected revision taken on a child is checked on the master.)
5. **Nothing hashed travels (Q50).** The child builds its primary index with its own seed
   from the keys it receives; no hash, tag or bucket of the master is sent. Acceptance,
   added: a master and a child opened with different `IndexSeed` hold the same entities.
6. **Where "a write is refused while a child is connected or loading" is decided:** under
   the master's lock of the writes, and the registration of a child takes that lock too,
   so a write is either published before the child's capture starts or refused; none is
   in between.
7. **Close is one operation** (the rule of S09, part 2, finding 1) — it now covers the
   network. A master's close stops accepting, ends every connection and completes only
   when no task of it runs and its port is free; calls of `CloseAsync`/`DisposeAsync` join
   it. An open of a child that fails, times out or is cancelled by its token leaves no
   connection, no task and nothing allocated; a cancelled open ends in
   `OperationCanceledException`. A child that vanishes in the middle of a load leaves
   nothing registered on the master. Acceptance, added: each of the three as a test (the
   port can be bound again at once after the close completes; the allocator of the tests
   reports everything returned).
8. **Tokens** (finding 2). The token of `OpenAsync` is honored inside every wait of the
   open: the connect, the back-off between attempts, each receive.
9. **Memory.** The buffer that holds one root outside the arena, on either side, is
   managed memory of the transfer and is not counted in the instance budget (as the keys
   of an enumeration are not). The child places a root by the rule of a write refused for
   memory (free what is retired, retry) before it gives up; before Ready there are no
   reads to wait for. "A database the child's memory limit cannot hold" is that refusal.
10. **A waiting sender.** Until S33 a send to a child that does not read has no deadline;
    a close ends it. Entered in X6 with S33 as its end.
11. **Milestone contract**, "Initial bounded settings", row "Scan/query temporary memory
    and execution" is stale twice: "for the database or for one call" (the owner removed
    the per-call form) and "The temporary memory of reads is counted in the instance
    budget" (withdrawn). Its last two sentences become: "An application may set a limit on
    the memory an enumeration remembers and on the engine time of a read, for the database
    (`ReadMemoryLimit`, `ReadTimeLimit`); only time inside the engine counts, not the time
    a caller spends between two items. That memory is not counted in the instance budget."
    Rides in the branch of S10 under O16.

**Read by the coordinator on 17 November 2026, after the contract step.**

- **The state of a child before Ready.** `Connecting` while it has no connection to its
  master, the back-off between attempts included; `Loading` from the accepted handshake
  until the load is complete. In both every operation is `Unavailable`. This is what the
  states of `LatticeDatabaseStateEnum` say; the sentence "Before it is Ready a child is
  `Loading`" above means the second of them. Since the fixes after S13 the child is
  `CatchingUp` from the end of its load until it has applied the changes up to its
  readiness position, as the decision on replication says; every operation stays
  `Unavailable` in it.
- **A port in use.** The engine reports a failure to bind as the system reports it; the
  placeholder reports an address held by another master of the process with
  `InvalidConfiguration` and keeps doing so until it is removed in S12. Tests of a failed
  bind are tests of the engine only.
- **`OpenTimeout` and the placeholder.** The placeholder validates the option and does not
  use it; tests of the timeout are tests of the engine only.

**Decided by the planner on 17 November 2026 after the study of S10.** Where a sentence
above says otherwise, these replace it. The study read the source of Valkey (BSD-3-Clause,
commit 40e00fc), PostgreSQL (PostgreSQL Licence, commit 11591f2) and Garnet (MIT, commit
2572760); no file is adapted from them. Points 1, 2 and 4 were reported to the owner, who
agreed with all three on 17 November 2026, through the planner's session.

1. **The check of a frame is CRC32C.** Every frame ends with a CRC32C (4 bytes,
   little-endian) over the 32 bytes of the header and the body before it.
   `Protocol.DigestSize` becomes `CheckSize = 4`; `MaxDataBody` (64 KiB) includes the
   check. A frame whose check fails closes the connection and applies nothing, as before.
   The check guards against accidental damage and framing faults only; the engine
   authenticates nobody, so a cryptographic digest without a key adds nothing, and no
   source studied puts one on a frame. Milestone contract, "Independent base, journal and
   recovery": "framing uses the same digest in its body envelope" becomes "A frame of the
   replication transport carries a CRC32C over its header and body (decided after the
   study of S10, 17 November 2026)". SHA-256 stays for persisted roots and the complete
   journal payload (S27 on). Whether a fragmented transaction of S11 needs a check of its
   own beyond its frames is a question of the study of S11. Header and Hello are
   little-endian explicitly; only root payload travels in the byte order of the sender,
   announced in `LayoutFormat`.
2. **No address on the wire** — within Q08: the stored bytes travel and no field is
   decoded or encoded. A root travels as: its type id, its last mutation sequence, its
   layout version, the bytes of its root payload, then the bytes of each out-of-line value
   in the order of its descriptors. Nothing on the wire is an address or a block number.
   The child takes an entry of its own, writes the fixed part and stores each out-of-line
   value through the store the write path uses (`ValueBlocks`), which writes every header
   and location itself. Bytes of a slot that hold no value are not sent. Checked before
   use: the type id and layout version against the schema, each descriptor's kind against
   its length, the null bitmap against the descriptors, the total of the bytes against the
   announced length of the root. The Acceptance sentence "the child places each block in
   its own arena and repairs the relative addresses" becomes "the child stores the
   received bytes through its own allocation and no received value is used as an address";
   "An invalid address, count or length inside a received block" becomes "An invalid kind,
   count or length inside a received root". Milestone contract, "Replication transport":
   "Wire block IDs and relocation metadata let the child fix relative descriptors into its
   own arena; no assumption of equal process addresses." becomes "No address and no block
   number travels: the child stores received bytes through its own allocation, which
   writes every location (decided after the study of S10)." Measured, added: the same
   load reported as bytes on the wire per entity next to the stored bytes per entity.
   Shared text and owned collections (S13, S18, S19) follow the same rule when they
   arrive.
3. **Roots and frames.** The first frame of a root announces the total length of the
   root; the child allocates once and checks the end. Small roots share a `Blocks` frame,
   which begins with their count. A schema larger than a frame is cut by the same rule as
   a root. `LoadEnd` carries the sequence at which the load ended and the number of roots
   sent; a child whose count differs fails its open. Acceptance, added: a child that
   receives fewer or more roots than `LoadEnd` announces fails with `Unavailable` and
   holds nothing.
4. **`OpenTimeout` changes its meaning**, by principle 9 (no default turns ordinary load
   into a refusal) and the study: a database of ordinary size does not load in 30 seconds.
   Documentation of the option: "How long a child waits for its master while it opens: to
   reach the master and be accepted by it, and afterwards for the next part of the
   database to arrive. A load that keeps arriving is not ended by this timeout, however
   long it takes; the cancellation token of the open call bounds the whole open. 30
   seconds by default; finite and positive." Remarks of `OpenAsync`, second paragraph: "A
   child connects to the master at `MasterUri`, loads the whole database and only then
   becomes readable. When the master cannot be reached, or nothing arrives from it, for
   `OpenTimeout`, the call fails with `Unavailable` and the instance is closed." Milestone
   contract: the sentence in "Replication transport" and the row "Child open" follow ("30
   s to reach the master and between two parts of the load, configurable"). Acceptance:
   "fails with `Unavailable` when the open timeout passes" stays for an unreachable
   master; added: a master that accepts and then sends nothing fails the open after
   `OpenTimeout`; a load that takes longer than `OpenTimeout` in total while frames keep
   arriving becomes Ready (a small timeout and the internal hold of the sender between
   frames). A contract commit of its own. For the brief of S33: a child that waits for a
   free capture is sent a frame that counts as progress, or its wait is stated otherwise
   there.
5. **A silent peer is bounded,** with no new option: the master reads `OpenTimeout` too. A
   peer that connects to a master and has not sent a valid Hello within the master's
   `OpenTimeout` is disconnected. "`OpenTimeout` is read by a child only" is withdrawn;
   the documentation of the option gains "A master gives a connecting node the same time
   to introduce itself." Test through a raw connection. The waiting sender stays in X6
   until S33.
6. **The implementer's:** the child sizes its index from the root count the master
   announces at the start (in `HelloReply` or a first frame of the load; `LoadEnd` still
   verifies it); one reused buffer per connection; the test of the port is "a new master
   opens on the same port right after the close completes".

#### S11 · A child loads under writes and follows the master

**Carried from the review of S10:** `WriteExecutor.Install` publishes an entry with plain
stores, sound while no read reaches an entry before the index maps its key. The capture
reaches entries by slot; in S10 no write runs beside it. Once the capture runs under
writes it must not take an entry whose stores are not complete: the stamp protocol of
`TryTakeLive` is checked against `Install` for weakly ordered hardware. The refusal of
writes while a child is registered relied on a storage step that completes at once; it
goes with this sprint.

**Track:** full · **Needs:** S10 · **Decisions:** P3 · **Owner:** E07

- **Goal.** A child that loads while the master keeps writing ends exactly at a fixed
  position of the master, and from there applies the master's changes in order.
- **Contract.** None public. Internal: the capture with its start position, catalog fence
  and watermarks; the logical encoding of effects in its first version; the
  acknowledgment that means *applied*.
- **To read.** [Replication transport and initial loading](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading);
  [Q08 initial capture and readiness handoff, Q08.1.1](../decisions/replication-and-hosting.md);
  evidence: [replica bootstrap](../design/prototype-evidence.md#bounded-replica-bootstrap-correctness).
- **Acceptance.** A child that loads while the master inserts, replaces, deletes and
  recreates entities ends equal to the master at its readiness position: nothing deleted
  comes back, nothing inserted outside the capture is missing. Two children that load at
  the same time, while the master writes, each end equal to the master at their own
  readiness position: every capture has its own start position, fence and watermarks.
  Changes after readiness are applied in order, each whole. A transaction larger than one
  frame arrives in fragments and is applied only after its end and its digest; a
  connection cut in the middle applies nothing of it. The master counts a change as
  acknowledged only when the child has applied it. No lock is held while bytes are sent
  or received, and no payload is pinned for the length of a load. The encoding of effects
  round-trips every write the engine has so far and refuses malformed input.
- **Measured.** Reported: time to Ready for 100,000 scalar entities while the master
  takes 200 writes a second; the master's read p99 during one load and during two.
- **Out of scope.** Writes through a child and waiting for positions (S12). Bounded
  delivery history, the limit of captures (S33), reconnect and reload (S34). Until S33 the
  delivery history has no limit of its own: a child that stops acknowledging makes it
  grow, and from S12 on `README.md` lists it among what is not implemented yet. Real processes in
  number (S35).

**Types and members of S11.** No public member. Internal, namespace
`Moltaro.Lattice.Replication`; `FrameKindEnum` gains `Transaction = 7`, `TransactionEnd = 8`,
`Applied = 9`.

```csharp
internal enum EffectKindEnum : byte { Write = 1, Delete = 2, Unchanged = 3 }

// What one committed operation did to one entity, in the logical encoding of format 1:
// values by the rules of the milestone contract, never a native address.
internal readonly struct RootEffect
{
    public uint TypeId { get; }
    public EffectKindEnum Kind { get; }
    public ReadOnlyMemory<byte> Key { get; }
    public ReadOnlyMemory<byte> Entity { get; }              // the complete entity for a write; empty otherwise
}

internal sealed class CommittedTransaction
{
    public ulong Sequence { get; }
    public IReadOnlyList<RootEffect> Effects { get; }        // one for a single write, several for a batch
}

internal static class EffectEncoding                         // format version 1
{
    public const int Version = 1;
    public static void Write(IBufferWriter<byte> output, CommittedTransaction transaction);
    // Refuses a truncated or malformed transaction as a whole, with ValidationFailed.
    public static CommittedTransaction Read(ref SequenceReader<byte> input, LatticeSchema schema);
}

// EngineDatabase gains
public IDisposable SuspendApplying();                        // a child holds back the application of received transactions; disposing resumes it
// EngineCounters gains: int ActiveCaptures, long PendingDeliveryBytes
```

A complete entity is encoded from the arena through `RootReader` and decoded through
`RootWriter`; no value is boxed on the way.

**Added by the planner on 15 November 2026, before the sprint started** (brief of S11),
on the owner's word, against the engine as S06 left it. Where a sentence above says
otherwise, these replace it.

- From this sprint a master of the engine accepts writes while children are connected or
  loading.
- `EffectKindEnum.Unchanged` is a write that changed nothing: by the owner's answer to
  Q39 it has a position of its own, and a child applies that position like any other.
- With no child connected and no capture registered the master encodes and retains
  nothing for delivery; `PendingDeliveryBytes` is 0.
- A transaction of any size is delivered: it is cut into frames and applied only after
  its end and its digest (decision O18). A child puts one transaction together at a time.
- Until S24 gives a child its pause, a child whose arena cannot hold a received change
  stops following: it keeps serving its reads at the position it has, a wait for a later
  position on it times out, and it does not resume by itself.
- **Acceptance, added.** A write of an entity of 32 MiB on the master reaches a child
  whole. A delete of an absent key on the master moves the position of the child.

**Added by the planner on 17 November 2026, against the engine as S09, part 2 left it.**
The owner asked for this check of the briefs of S10, S11 and S12. Where a sentence above
says otherwise, these replace it.

1. **The stream is ordered by the lock.** A committed transaction is appended to the
   delivery stream in the hold of the lock of the writes that publishes it, so the stream
   has the order of positions with no other mechanism. A capture is registered (its S, its
   fence of root slots) in a hold of that lock as well. R is the published position read
   after the last root was copied.
2. **Where an effect is encoded.** In the hold that prepares the write, from the values
   being written, once, and only when a child is connected or a capture is registered;
   with none the master encodes and retains nothing (already in the brief). The send is
   outside every lock. This is what established stores with replication do (the executing
   thread appends to the replication buffer); it costs the write path, so it is measured —
   point 6.
3. **The effect carries what the child needs for equal revisions:** a transaction has its
   sequence, and each write effect is stored on the child with that sequence as its last
   mutation; `Unchanged` moves the position and leaves the revision. Acceptance, added:
   after following, the revision of every entity on the child equals the master's, for an
   insert, a replace, a recreate and a write that changed nothing.
4. **The child applies under its own lock of the writes,** through the publication of the
   write path, with no storage step, one transaction at a time; reads on the child are
   protected by epochs as on a master and are never stopped by an application. Retired
   memory on the child is freed by the applying thread.
5. **Memory.** `PendingDeliveryBytes` is counted and reported; in this sprint it is not
   charged to the rule of a refused write: a write of the master is never refused because
   a child is slow. Bounding it is S33 (the slow child loses its claim; the write is not
   refused), and S24 states the budget as a whole. The sentence of the milestone contract
   "the copy for delivery to children included" gets the note "from S24/S33; until then
   counted and reported only". The child, when its arena cannot hold a change, first
   applies the rule of a write refused for memory (free retired, wait for the reads that
   hold it, retry) and only then stops following, as the brief says.
6. **Measured, added:** writes a second of 1 and of 16 callers on a master with one child
   following and with two, next to the same scenario with no child (the figures of S09,
   part 2); the cost of one write of 32 MiB under the lock with a child following.
   Reported; a fall is handled by the rule of the process. Reads by key on 16 threads on
   the master beside it: not lower than S09, part 2.
7. **Close and tokens as in S10:** the close of a master or child with a stream in flight
   completes only when its tasks have ended; `SuspendApplying` held at a close does not
   keep the close from completing.
8. **The capture pass** of S10, point 3 holds under writes because a published root is
   never changed in place (Q49) and retired memory is not freed while a reader is inside
   the protection: the copy of one root is coherent without any gate. The watermarks
   decide what of S..R is replayed; the test author states the outcomes (nothing deleted
   comes back, nothing inserted is missing, revisions equal), not the watermark mechanism.

**Decided by the planner on 17 November 2026 after the study of S11.** Where a sentence
above says otherwise, these replace it. The study read the source of Valkey (BSD-3-Clause,
commit 97ff16e), PostgreSQL (PostgreSQL Licence, commit 11591f2), Garnet (MIT, commit
fcc018a) and etcd (Apache-2.0, commits 65fe41f and, for raft, 99f9ffb); no file is adapted
from them. The owner agreed on 17 November 2026 to withdraw the deletion metadata of the
initial capture (watermarks of deletions, no reuse of fenced slots, the claim on the
catalog); the decision on replication and hosting carries the amendment. In Acceptance
"its own start position, fence and watermarks" becomes "its own start position, fence
and end position".

1. **Contract line.** Internal: the capture with its start position S, its fence of root
   slots and its end position R; the shared tail of committed transactions; the
   acknowledgment that means *applied*.
2. **Start.** A capture is registered in one hold of the lock of the writes: it takes S,
   the published sequence, the fence of root slots and its cursor into the tail. From this
   sprint no write is refused because of a child.
3. **Copy.** As in S10: one root at a time inside the protection of reads, sent outside
   it. Nothing is retained for a capture: no slot, no deleted root, no mark of a deletion.
4. **End.** R is read in a hold of the lock of the writes after the last root was copied,
   because a root can carry a sequence the published clock does not show yet. `LoadEnd`
   carries S, R and the number of roots sent.
5. **Tail.** A committed transaction is encoded and appended to one shared tail in the
   hold of the lock of the writes that publishes it, in the order of sequences, and only
   while a child or a capture is registered. It is encoded once for all children and
   framed per connection, on that child's sender task, outside every lock: the frame
   of S10 carries the stream sequence of its connection under its check (corrected on 17
   November 2026 with the implementation; "framed once for all children" stood here). Every
   child has its own cursor; a loading child is sent its stream after `LoadEnd`, on the
   same connection. The master releases the tail up to the lowest position among its
   children and registered captures. The tail is managed memory outside the instance
   budget, unbounded until S33; `PendingDeliveryBytes` is what was appended less what was
   released. A write of the master is never refused or delayed because a child is slow.
   Added on 18 November 2026 with the implementation: the tail retains whole blocks of 1
   MiB — at most the bytes not yet applied, rounded up to blocks, plus the block being
   filled, a block a sender still reads, and one spare; with no child and no capture it
   retains nothing. The size of a block is an internal constant. `PendingDeliveryBytes`
   stays the bytes of transactions not yet applied by every child.
6. **Effects.** A Write carries the root in the load format of S10; a Delete the type id
   and the key; a write that changed nothing only its position. A transaction is checked
   by the check of its frames, its announced length and its sequence, which follows the
   one before; `TransactionEnd` is not built, and "its end and its digest" becomes "its
   announced length". A transaction larger than a frame is put together whole before it
   is applied. `RootEffect.Entity`, `EffectEncoding` through `RootReader`/`RootWriter` and
   "the logical encoding of format 1" leave this brief; the internal types are restated as
   S10 was built. The machine-independent encoding of the journal is decided in S27 with
   its study; whether delivery then shares it is decided there by a figure.
7. **Apply.** The child applies a copy and an effect by one rule, under its lock of the
   writes: a state of a key at sequence s is skipped when the child holds that key with a
   last mutation not earlier than s; otherwise a Write inserts or replaces and a Delete
   removes the key if it is held. The child addresses an entity by the key in the root,
   never by a slot of the master, so a key that arrives twice in a load is resolved by the
   rule, not refused (this replaces the refusal of S10). The clock of a loading child
   starts at S; it is Ready when it has applied R.
8. **Acknowledgment** per received burst: `Applied(sequence)` when the child has applied
   all it has received; never before R.
9. **Divergence after R** (an effect not later than the state held) is a broken
   invariant, not lag: the instance becomes `Faulted`, the connection is closed, every
   operation is `Unavailable` with the reason in the message. Not "stops following". Test
   through a hand-made stream.
10. **`Install`.** State = Live is stored last, by a release store, because the capture
    reaches entries by slot; the check that the assignment in `RootTable.Add` cannot tear
    the state and the three comments that become false ride in this sprint. x64 hides the
    hazard, so no test shows it there.
11. This replaces points 1, 2 and 8 of the additions above; point 3 stands (revisions
    equal), with the sequence of the copy or effect as the last mutation.
12. **Measured, added:** the peak of `PendingDeliveryBytes` during a load at 200 writes a
    second and at the full rate; the transactions in (S, R], the time from `LoadEnd` to
    Ready and the share of effects skipped; the child's rate of applying against the
    master's rate of writing; p99 and writes a second of 1 and 16 callers with no child,
    one and two (the copy into the tail and its allocation under the lock; a write of 32
    MiB); the time one root of 32 MiB is copied inside the protection.
13. **Milestone contract**, "Independent base, journal and recovery": "and wire" leaves
    the sentence "Durable and wire logical values use explicit little-endian…"; added: "The
    replication stream carries stored bytes in the layout and byte order the Hello has
    checked (decided after the studies of S10 and S11)".

**Decided at the review of S11, 18 November 2026.** A child publishes the position of a
transaction once, after every insert of it is installed and every replaced or deleted
entity is marked, and stamps them afterwards, so a read on a child never returns a state
at a position at which the master did not have it. After Ready a child acknowledges
before every read of its socket whenever it has applied something new, not only when a
burst ends (this replaces "per received burst" in point 8). The text of a received key
is checked as strictly in a root as in a Delete.

**Read by the coordinator on 17 November 2026, with the tests of S11.** The root of a
Write of the stream carries the sequence of its transaction as its last mutation (point 3
of the planner's additions), and the child checks it: a Write whose root says otherwise
is a malformed transaction, refused whole. With that check and consecutive sequences an
effect not later than the state held cannot arrive after R, so point 9 above has no case:
the `Faulted` path of a divergence is not built and has no test. A malformed transaction
after Ready applies nothing and ends the connection; the child stays Ready at its
position and does not follow again (reconnection is S34), as a child whose arena cannot
hold a change does.

#### S12 · Writes through a child, sessions; the placeholder is removed

**Carried from S09:** a child forwards the writes of one entity in the order of their
calls, each taking its place when the wait for the position of its session has ended;
the guarantee of S09 holds through a child.

**Carried from the review of S08, part 1:** when the engine library becomes packable in
this sprint, `THIRD-PARTY-NOTICES.md` is packed into its package as it is into the
package of the core: the engine holds the files adapted from Garnet/Tsavorite.

**Track:** full · **Needs:** S11 · **Decisions:** O1, O2, P10 · **Owners:** E07, E08

- **Goal.** Everything the public contract does today is done by the engine, on a master
  and on a child, and the placeholder no longer exists.
- **Contract.** No new members. `LatticeDatabase` opens the engine and moves to the
  engine assembly, keeping its namespace. The internal switch that holds back a child's
  application moves with it.
- **To read.** [Session consistency](../decisions/session-consistency.md);
  [Q08.1.2, completion of a write forwarded through a child](../decisions/replication-and-hosting.md);
  [initial loading, last paragraph](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading).
- **Acceptance.** `MasterAndChildTests`, `ConsistencyTests` and every other test that
  runs today pass on the engine with their assertions unchanged, as do the tests of the
  dependency injection package and the running tests of the Identity package. One
  assertion changes, by the owner's decision of 15 November 2026 (Q39 of the
  [open questions](open-questions.md)): in
  `ConsistencyTests.Every_result_carries_a_position` the position of the delete that
  deleted nothing is no longer the position before it. The test author changes it in the
  tests commit of S12, with the decision named in the commit message:
  `Assert.NotEqual(inserted.Position, nothingDeleted.Position)`, and the comment above
  the last assertions becomes "Every write moves the position, also one that changed
  nothing." The engine-only test of S06 is removed in the same commit, because the test
  of the contract states it from then on. In the contract commit of S12 the documentation
  of `LatticeWriteResult.Position` gains the sentence "A write that changed nothing has a
  position of its own as well." A write
  through a child returns after that child applied it, and does not wait for other
  children. A timeout after the master committed reports `SynchronizationTimeout` with
  the committed position and does not roll back. A wait has a fixed target and does not
  follow newer writes. A position of another history is refused. A session only moves
  forward when answers arrive out of order. The placeholder's types are deleted in the
  same change, and so is the internal entry the engine's tests used: exception X4 moves
  to **Ended** in the [exceptions register](exceptions.md); the rule for
  addresses of S10 moves into `Validate`, and the test author changes the address helpers
  of the two test projects to loopback addresses with a port, and the option tests that share an `https` address, in the tests step, where they still
  pass on the placeholder. The other forced edits of this sprint follow the rule on edits
  that state nothing new, P10, and change no assertion. In the contract commit the
  contract author turns the reference to `LatticeDatabase` in the documentation of
  `ILatticeDatabase` into plain code text, because the class leaves that assembly and the
  reference would no longer resolve. In the implementation commit the implementer adds a
  reference to the engine assembly to the dependency injection package and to
  `Moltaro.Lattice.AspNet.Identity.Tests`, which calls `LatticeDatabase` and reached it
  only through the Identity package; moves the two `Lagging` helpers of `ConsistencyTests`
  and `IndexTests`, which name the placeholder's class, to the engine's class; moves
  every use of the internal entry `EngineDatabase.Create` — in the helper of S06, which
  from then on opens only the engine, in `Moltaro.Lattice.Engine.Tests` and in the
  measurement program — to `LatticeDatabase.Create`; and removes the friend access the
  contract assembly gave to `Moltaro.Lattice.Tests` for the placeholder's switch. A use
  the build finds outside this list is reported to the coordinator, who enters it in the
  brief, and is then edited in the same way; it is not edited silently; the samples run to
  the end; `README.md`, `AGENTS.md` and the [architecture](../design/architecture.md) no
  longer describe a placeholder, and the owner approves the new wording of `AGENTS.md` in
  this sprint.
- **Measured.** Reported: p50 and p99 of a single small write through a child, MemoryOnly,
  on loopback: the first figure on the path of G3.
- **Out of scope.** Reconnect and reload between a wait and a read (S34): until
  then a child whose connection ended keeps serving its local reads and does not come
  back by itself, and a write through it fails with `Unavailable`. Operation
  tokens for a lost reply (S26).

**Types and members of S12.** No public member is added. `LatticeDatabase` moves to the engine
assembly with its namespace `Moltaro.Lattice` and its two members:

```csharp
public static class LatticeDatabase
{
    public static ILatticeDatabase Create(LatticeDatabaseOptions options);
    public static ValueTask<ILatticeDatabase> OpenAsync(LatticeDatabaseOptions options, CancellationToken cancellationToken = default);
}
```

`LatticeDatabase.Create` returns the `EngineDatabase`, and the internal entry
`EngineDatabase.Create` of S06 is removed with its uses. In the documentation of
`ILatticeDatabase` the reference to `LatticeDatabase` becomes plain code text. The friend access granted in S06 stays, and with it the tests reach
`SuspendApplying()`. `LatticeDatabaseOptions.Validate` gains the rule for addresses of
S10: the scheme `lattice`, a host, a port, no path; anything else is
`InvalidConfiguration`. The dependency injection package references the engine assembly.

**Added by the planner on 15 November 2026, before the sprint started** (brief of S12),
on the owner's word, against the engine as S06 left it. Where a sentence above says
otherwise, these replace it.

- **Forwarding had no frames in the brief.** Internal, namespace
  `Moltaro.Lattice.Replication`: `FrameKindEnum` gains `WriteRequest = 10` and
  `WriteReply = 11`, and

  ```csharp
  // The body of a forwarded write and of its answer, in the logical encoding of S11.
  internal static class ForwardEncoding
  {
      public static void WriteRequest(IBufferWriter<byte> output, WriteKindEnum kind, uint typeId, ReadOnlySpan<byte> key, ReadOnlySpan<byte> entity, LatticeRevision? expectedRevision, LatticePosition? minimumPosition);
      public static void WriteReply(IBufferWriter<byte> output, LatticeWriteResult result);
      public static void WriteReply(IBufferWriter<byte> output, LatticeException refusal);
      // Each refuses a truncated or malformed body with ValidationFailed.
      public static ForwardedWrite ReadRequest(ReadOnlySequence<byte> body, LatticeSchema schema);
      public static ForwardedReply ReadReply(ReadOnlySequence<byte> body);
  }
  ```

  `ForwardedWrite` and `ForwardedReply` hold what the two writers take.
  `FrameHeader.CorrelationId` pairs a reply with its request. A refusal travels with its
  error code, its message, `Limit`, `OperationIndex` and `CommittedPosition`, and the
  child throws a `LatticeException` equal in all of them.
- **What a caller of a child sees.** The child checks the arguments and validates the
  entity before it sends anything, in the order of Q25; the master validates again. A
  child that has no connection to its master refuses a write with `Unavailable`. A
  cancellation before the request is sent withdraws the write; after it is sent the call
  waits for the real outcome, as on a master. When the connection ends after the request
  was sent and before the reply arrived, the outcome is not known: `IndeterminateOutcome`.
  S26 gives the caller a way to learn it. By the answer to Q44 a step of an enumeration
  after a close is `ObjectDisposedException`; the engine-only test of S08 becomes a test
  of the contract.
- **What ends with the placeholder, beyond what the brief lists**, as forced edits of the
  Acceptance. The test author removes `DatabaseImplementationEnum` and the parameter it
  gave to the tests in the tests step: every test that ran on both runs once, a test
  marked for the engine only loses the mark, and a test that ran on the placeholder only,
  because it opened a child, runs on the engine. No assertion changes. The tests that
  state a refusal of the placeholder which the engine does not make are removed by the
  test author with the reason named: the tests of `TextPolicyAndEnumWriteTests` on a
  comparison without case, an Enum field and UTF-16 "not writable yet", and the
  placeholder cases of `MalformedTextWriteTests` for those
  schemas; what they state of validation is already stated by the tests of S07. In the
  contract commit the "Not implemented yet" remarks that S07 to S09 made untrue of the
  engine are removed: on `OrdinalIgnoreCase`, on Enum fields and enum types, on the
  UTF-16 encoding, and "not enforced yet" on `MemoryLimit` and on the read limits. The
  remarks on Dictionary storage, unique indexes, index search, the write batch and the
  field types of S03 stay. `README.md` follows. A use the build or a test finds outside
  the lists is reported to the coordinator, as the brief already says.

**Added by the planner on 17 November 2026, against the engine as S09, part 2 left it.**
The owner asked for this check of the briefs of S10, S11 and S12. Where a sentence above
says otherwise, these replace it.

1. **The internal entry does not go whole.** The brief says `EngineDatabase.Create` is
   removed with its uses; since S08/S09 the tests of the engine give an instance test
   inputs through `Create(options, EngineTestInputs)` (arena options, allocator, index
   seed, commit storage). That form stays, for `Moltaro.Lattice.Engine.Tests` and the
   measurement program only — it is the access AGENTS.md gives them — and the exception
   that covers it stays (X3 by its comment). What ends is X4: no scenario test of
   `Moltaro.Lattice.Tests` and no helper opens through an internal entry;
   `Create(options)` without inputs is removed and its uses move to
   `LatticeDatabase.Create`. If `LatticeDatabase` needs to open a child or a master with
   inputs for a test of the engine, `OpenAsync` gets the same internal overload; nothing
   public.
2. **Order through a child** ("Carried from S09"), how it holds. The child sends requests
   on its one connection in the order of the calls, without waiting for replies. The
   master's receiver of a connection submits each forwarded write to the write path in
   arrival order and does not await one before it submits the next: the call returns its
   task, the next is submitted. Acceptance, added: two writes of one entity made through
   one child without awaiting the first are executed in the order of the calls, with the
   master's storage step held (the held storage of the tests).
3. **A request larger than a frame.** A forwarded write is cut into frames of at most
   `MaxDataBody` as a root is in S10 (bit 0 of `Flags` marks the last frame); the frames
   of one request are not interleaved with those of another request, acknowledgments may
   pass between them. The master holds one request per connection outside its arena while
   it puts it together; managed, not counted in the budget. The key is encoded on the
   child from the generic key without a box. Acceptance, added: a write of an entity of
   32 MiB through a child succeeds and is read back on that child.
4. **Close of a child with writes in flight** (close is one operation): a write not yet
   sent ends as a write not yet dispatched ends at the close of a master; a write sent and
   not answered ends in `IndeterminateOutcome`; the close does not wait for the master. A
   close of the master: forwarded writes dispatched there complete with their real
   outcome and the reply is sent before the connection ends if the child still reads; the
   child otherwise reports `IndeterminateOutcome`.
5. **Tokens.** The wait of a forwarded write for its local application, after the reply,
   ends by `SynchronizationTimeout` (the option) with `SynchronizationTimeout` and the
   committed position; the token of the call is not looked at after the request was sent,
   as on a master after the dispatch.
6. **Faults of the master travel as they are:** `IndeterminateOutcome` and `Unavailable`
   of S09 reach the caller of the child with the same code.
7. **Notices.** When the engine becomes packable, check that `THIRD-PARTY-NOTICES.md`
   names every source a file of the engine says it is adapted from, the write path of
   S09, part 2 included (a file modelled on a design without adapted code needs no notice;
   a file that names project, file and commit does).
8. **`Arc.Collections`** is still referenced by the engine project and no source file
   uses it. Leave the reference (S14 decides the package's fate) but do not let the new
   AGENTS.md wording of this sprint say the engine uses it; say what is true then.
9. **Measured, added:** beside p50/p99 of a write through a child, writes a second of 16
   callers through one child, and the master's own writes beside them — the first figure
   of forwarding under load.

**Decided by the planner on 18 November 2026 after the study of S12.** Where a sentence
above says otherwise, these replace it. The study read the source of PostgreSQL
(PostgreSQL Licence, commit 11591f2), etcd (Apache-2.0, commits 65fe41f and, for raft,
99f9ffb), Valkey (BSD-3-Clause, commit 97ff16e), Garnet (MIT, commit fcc018a) and TiKV
(Apache-2.0, commit 4d8fde7); no file is adapted from them.

1. **The entity of a `WriteRequest`** is the root in the load format of S10; "the logical
   encoding of S11" leaves the comment of `ForwardEncoding`. The child writes the last
   mutation sequence as 0 and the master stamps it at preparation. "The master validates
   again" means exactly: (a) the check a child makes of a root received in a load — type
   id, layout version, structure, lengths inside the announced length, every out-of-line
   value inside its bytes, the sizes a value may have; (b) the key of the request equals
   the key in the root, strict text; (c) the conditions of the write path (duplicate, not
   found, expected revision, memory). The value rules of the schema (well-formed text,
   declared enum members) are checked once, on the child, which is the same engine with
   the schema agreed in the Hello; the master trusts a child's values to the degree a
   child trusts a master's in a load. No second validator over root bytes is built. If
   check (a) does not bound every read the master later makes inside the root, that is a
   P1 of memory safety and goes to the planner.
2. **The reply comes before the transaction.** A forwarded write carries its origin (the
   connection and the correlation id) into the write path; the hold that publishes it
   records the reply for that connection before the commit of the tail; the sender of the
   connection sends the recorded replies before it frames its next transaction.
   Invariant, with a test: a child never receives the transaction of a write it forwarded
   before that write's reply. Not the id on the Transaction frame (the tail is shared and
   encoded once), and not a child that receives independently of applying.
3. **One sender on the child.** One task owns the send side of the child's connection: a
   queue of requests in the order of the calls and the newest applied sequence. It sends a
   request a frame at a time. The acknowledgment rule of S11 is reworded and keeps its
   guarantee and its test: the follower publishes its applied sequence and wakes the
   sender; the sender sends the newest applied sequence before each request frame it
   sends and whenever it is woken with nothing else to send; never before R. The follower
   accepts a `WriteReply` anywhere, also between the frames of a transaction.
   `FrameHeader.CorrelationId` pairs a reply with its request: ids start at 1 on each
   connection and 0 stays the load and the stream. The table of pending requests is
   emptied on every way out and closes when the link ends; a registration after that is
   refused.
4. **The close of a master** (no new public option). After its admitted writes have
   ended, for each connection: it sends the recorded replies, shuts down its sending side
   (not a reset) and waits until the child has ended its side; a child that reads the end
   marks itself without a master before it ends its side. So when `CloseAsync` of the
   master returns, every child that reads has observed the end, and
   `Child_keeps_serving_reads_after_the_master_closes` holds as written: the child refuses
   with `Unavailable` because it has no connection, and nothing is sent. The wait for a
   connection is bounded by an internal constant of 5 s, after which the connection is
   reset; all connections are waited together, not in turn. The constant is entered in X6
   until S33, where the deadline of a send to a child that does not read becomes an option
   and bounds this wait too. If a test of the contract with `SuspendApplying` held and a
   master's close would wait out the 5 s, the planner is told which before anything is
   changed.
5. **A write the master did not execute is never `IndeterminateOutcome`.** A request a
   closing master did not execute is answered `Unavailable`, "not executed". An exception
   that is not a `LatticeException`, thrown by the submit of a forwarded request before
   its dispatch, is answered `Unavailable` with its message: it is a defect of the child's
   validation.
6. A `WriteReply` larger than a frame is cut into frames like a request.
7. **Two moments of "sent".** Taken by the sender (its first frame begins): a cancellation
   no longer withdraws the write. Last frame accepted by the socket: a connection that
   ends before it gives `Unavailable` ("not executed"), after it `IndeterminateOutcome`.
   The master never executes a request whose last frame it did not receive; stated and
   tested.
8. **A child's close with a write that has its reply and waits for its application:** the
   call returns its real result, the success with revision and position, and so does a
   refusal already received. The wait for the local application exists so that a read on
   this instance sees the write; a closed instance serves no read. A write sent and not
   answered stays `IndeterminateOutcome`; one not taken by the sender ends as a write not
   dispatched.
9. **No timeout travels in the request.** The master uses its own
   `SynchronizationTimeout` for the wait for `MinPosition`; on a master of the same
   history that wait ends at once, and another history is refused — on the child before
   the request is queued, and again on the master.
10. **X6** gains the wait of a forwarded write for its reply when the master vanishes
    without a reset, until S34.
11. **A switch that cuts the child's link**, for the test of point 7, is allowed in
    `Moltaro.Lattice.Engine.Tests` only, through `EngineTestInputs` or the internal
    overload of `OpenAsync`; the friend access of `Moltaro.Lattice.Tests` keeps only
    `SuspendApplying`.
12. **A request received whole is a submitted write:** it runs although its child's
    connection ended; its reply is dropped, and the child has reported
    `IndeterminateOutcome`. No token of a connection goes into the write path. An
    incomplete request is dropped.
13. **An announced length above the master's `MemoryLimit`** is refused at the first frame
    with `WriteRejected`, `Limit = Memory`; the master reads and drops the remaining
    frames of that request and keeps the connection. For every other request the buffer
    grows with the bytes received, never by the announced length, and a request whose
    frames exceed what it announced is malformed and ends the connection.
14. **`PositionClock`.** It wakes every waiter at every publication. Measured first with
    0, 100 and 10,000 waiters on a child; replaced only with the figure, by waiters
    ordered by target with a published lowest target (the form of PostgreSQL's
    `xlogwait.c`: one read when nobody is reached, a full fence between publishing the
    sequence and reading the lowest target, waiters of one target sharing a signal, a
    waiter that timed out leaving), and then with a session read on a child measured
    beside it: that read must not pay.
15. **History.** A position carries its history and the clock refuses another one; a
    MemoryOnly master draws a new history at every open. Enough for this sprint.
16. **Measured, added:** the parts of a small forwarded write (child, network, master,
    application, wake-up); the clock with 0, 100 and 10,000 waiters, at spread and at
    equal targets; small forwarded writes beside writes of 32 MiB through the same child,
    and how late an Applied arrives during one; the time of a master's close with
    forwarded writes in flight and of a child's close with pending requests; allocation
    per forwarded write on both sides; reads by key on the child on 4 threads beside 16
    callers forwarding through it, against the same child with none; 1,000 runs each of
    `Timeout_after_the_master_committed_does_not_undo_the_write` and
    `Child_keeps_serving_reads_after_the_master_closes`.

The planner reported points 1, 4 and 8 to the owner as its decisions, which he may
overturn; the owner agreed with all of them on 18 November 2026, as the planner relays.

**Settled by the planner on 18 November 2026, with the contract commit.** A write of a
child that its sender has not taken, or has sent only in part, ends at that child's close
in `Unavailable`, not executed; `ObjectDisposedException` stays for a call made after the
close (this replaces "ends as a write not yet dispatched ends at the close of a master").
The wait of a forwarded write for its application on the child is bounded by the
`SynchronizationTimeout` of the session when the call has a session that sets one,
otherwise by the option; it does not travel (this corrects point 5 of 17 November). An
address already in use is `InvalidConfiguration` with the system's message inside (this
replaces "with the error the system reports" of S10); the test of two masters at one
address takes a loopback port by binding port 0, reading the port and releasing it.

**Settled by the planner on 18 November 2026, with the tests of S12.**

- A malformed `WriteRequest` — truncated, an unknown kind or type id, a key that is not
  the key in its root, a root that fails the check of a received root, the correlation id
  0 or one still in flight — is a defect of the peer: the master executes nothing of it
  and ends the connection. Only an announced length above `MemoryLimit` is answered and
  keeps the connection. A malformed `WriteReply`: the child ends its connection, and its
  writes end by the two moments of point 7.
- Point 4 is widened: at its close the sender of a master does its ordinary work to the
  end — the recorded replies, then what the tail holds for that child up to the close —
  and then shuts down its sending side, all inside the same 5 s; a child that reads
  applies and returns the success. When the link ends in any other way while a call
  holds its reply and waits for its application, the wait ends at once with
  `SynchronizationTimeout` carrying the committed position: the write is committed and
  must not be repeated. Carried to S34: with reconnection the wait continues across a new
  connection within its timeout.
- An `OpenAsync` that waits when its instance is closed ends in
  `OperationCanceledException`: the close cancels the wait. `ObjectDisposedException` is
  for a call made on a closed instance. The engine of S10 changes to that.
- Forced removals of tests that state the placeholder or what S12 replaces, each named
  with its reason in its commit: in `ValidationBeforeRefusalTests` the parts that expect a
  refusal of a comparison without case, an Enum field and UTF-16;
  `MemoryLimitTests.Placeholder_validates_the_memory_limit…` and
  `ReadLimitsTests.Placeholder_validates_the_read_limits…`; of S10
  `WritesOnAChildTests.Write_on_a_child_is_NotSupportedException…`,
  `ListenAddressTests.Address_with_another_scheme…when_the_instance_opens` and
  `ListenAddressTests.Master_whose_port_is_in_use_fails_its_open_with_the_error_the_system_reports…`.
- Three tests of the engine take about 5 s each, because a master waits for a child that
  cannot read before it resets it; accepted as slow.
- Reasoned and not tested (point 7): a connection cut after the child's socket accepted
  the last frame of a request and before the master received it. No deterministic signal
  tells a test that the child's sender has seen its last send complete.
- The shapes the tests assume are in `Replication/Forwarding/ForwardPayload.cs` of the
  engine's tests and are accepted.
- An operation in flight on an instance that is being closed, which the close does not
  let finish, ends in `Unavailable`: a wait for a position, the first step of an
  `EnumerateAsync` that waits for one, and on a child a write its master has not taken. A
  started write of a master reaches its outcome. An `OpenAsync` that still waits is
  cancelled: the caller has no instance yet to be unavailable. The behavior was there;
  the contract now says it.

The owner agreed with the decisions of the tests step and with this rule of the close on
18 November 2026, as the planner relays. Carried to S27: with Durable, say whether a master can be behind a position a
session holds.

**Settled by the planner on 18 November 2026, with the implementation of S12.** Accepted
as the implementer decided them: the wire format is 3, so a peer of the format of S11 is
refused in the handshake; the master requires the correlation id of each request to be
the previous one plus 1 and anything else is malformed; a reply whose position belongs to
another history is a malformed reply and the child ends its connection; a child writes
the root of a forwarded write without its arena; no switch that cuts a link was needed;
`PositionClock` is replaced on its figure. Two items go to the fixes after the review:

1. **One result for "committed, will not arrive on this node".** A forwarded write that
   holds its reply and waits for its application ends at once in `SynchronizationTimeout`
   with `CommittedPosition` when the child has faulted, as it does when the master is lost
   or the timeout elapses; the session moves to the committed position in each. Not
   `Unavailable`: an application retries on `Unavailable`, and this write is committed.
   The sentence of the contract becomes "On a child that has lost its master or has
   faulted the wait ends at once, because the position cannot arrive."
2. **A forwarded replace has no rule of its own for the spelling of a key.** A test states
   that a replace through a child gives the stored entity the same key spelling as the
   same replace made on the master directly. Where the two paths differ, the forwarded
   one is the defect.

**Decided at the review of S12, 18 November 2026** (two reviewers on commit 3740090; no
P1). Both confirmed that a closing master left writes it had not executed as
`IndeterminateOutcome` on the child, against point 5 after the study. The planner settled:

1. **A refusal is recorded like a success:** the reply of a forwarded write, whatever its
   outcome, is recorded before the write leaves the barrier of the master.
2. **The frame `Closed`.** A closing master, for each connection and in this order: closes
   its barrier; lets its admitted writes end, each with its reply recorded; sends the
   recorded replies, then what the tail holds for that child; then one frame `Closed` —
   the master closed in order, and every request of this connection not answered before
   this frame was not executed and will not be; then shuts down its sending side and
   waits for the child's end as in point 4, within the same 5 s. After `Closed` is queued
   the master executes no request of that connection: what arrives later, whole or in
   part, is read and dropped, with no reply of its own. A child that receives `Closed`
   ends every sent and unanswered request in `Unavailable`, "not executed", marks itself
   without a master and ends its side. A connection that ends without `Closed` — a reset,
   a crash, a vanished master, the 5 s elapsed — leaves a sent and unanswered request
   `IndeterminateOutcome`. An end in order of a connection alone is not a statement about
   execution: a killed process can produce one. `Closed` has no body, and a child reads nothing
   after it: a reply after it, or a second `Closed`, changes nothing. The wire format
   stays 3.
3. Invariants with tests: after a master's close in order no write through a reading
   child ends in `IndeterminateOutcome`; a write whose reply was lost without `Closed` is
   never reported "not executed".
4. Fixed with tests in the same round: a write refused because the child has no master
   kept its encoded request reachable from the caller's token; `PositionClock` could
   report a lost master for a position it had reached, and its new mechanism had no
   tests; an Insert that carries an expected revision is malformed; a `ListenUri` whose
   host resolves to no address is `InvalidConfiguration`; what the placeholder left in
   the core (`EntityBinding.Materialize` and its setters, a constructor of
   `LatticeRecord`, `FieldValues.Copy`) is removed.
5. Left as is: a child refuses a closed instance before a session of another history and
   a master the other way round; no order is decided.
6. The second round (two reviewers on commit 929fe60) found no P1 and no P2 and every
   item above closed; a faulted master sends `Closed` too, because its fault ends every
   admitted write with its reply recorded. Tests were added for what was right and
   untested: the wait of a forwarded write on the master for the position of its
   session, a master that faults and closes, and `Closed` before Ready. The sentence of
   the contract of point 1 above the review was made exact: a wait for a position on a
   faulted child is `Unavailable`; only a write through a child that faulted after its
   master committed the write ends in `SynchronizationTimeout` with its position.
7. **The Linux job of the pull request failed on a test of S10**, a paced load that got no
   frame for its timeout of 1 s. The cause, reproduced in a Linux container on 4
   processors, was not the engine: two soak tests of S11 ran loops on pool threads
   whose awaits complete synchronously, held every worker of the pool and stalled the
   timers and socket continuations of other tests for seconds. The pair fails at the
   commit of S11 too: the green Linux runs of the pull requests of S10 and S11 passed by
   the luck of scheduling and are not evidence. The loops were moved off the pool,
   assertions unchanged. What the engine reports when a deadline fires over bytes that
   wait is carried to S34.

### Stage 4. Indexes and batches

**The stage, S13 to S17, was approved by the owner on 18 November 2026, as the planner
relays.** Its contract is the public contract that stands today: `StringStorage`,
`set.Index(name)`, `Find` and `FindAsync`, `IndexKey`, `IndexBound`, unique indexes and
`ILatticeWriteBatch` have been in it, with documentation, samples and skipped tests,
since the plan was approved. The internal types and members of each sprint are written
in its contract step, after its study, and reviewed in the sprint, as in S10 to S12.
What changes something the owner approved comes back to him inside the stage: the
structure of secondary indexes with the fate of `Arc.Collections`, and the form of
`IndexKey` and `IndexBound` if a measurement changes it, both in S14. The planner's pass
over the open questions on the same day found none that the stage touches: Q31 and Q33
are decided with S24; of Q35 the open points are 4 (S20), 5 (S37) and 7 (S18, S19); Q40
is S26 to S36; Q52 is the owner's after S27.

#### S13 · Text entries and Dictionary strings

**Added by the planner on 17 November 2026, against the engine as S10 left it.** Where a
sentence above or below says otherwise, these replace it. The owner asked for this check
of the briefs of S13, S14 and S15. The sprint begins with a study by the rule of the
process, and the milestone paragraphs it owns are rewritten by the sprint after its
study.

Common to S13, S14 and S15:

- Reads take no gate (16 November). Wherever this brief or the milestone contract says
  "dictionary gate", "index gate", "index protection", "claim root lifetimes" or "under
  the root gate", read: a read enters the protection of reads, and a structure is
  changed only by the thread that holds the lock of the writes; what is removed is
  retired and freed after the readers that could see it have left, and its bytes are
  counted as retired.
- A structure a read walks is one a read can walk without a lock while it is changed. A
  structure that needs a lock around readers is not taken; reads on 16 threads beside a
  writer are measured for every new read path, next to the read by key.
- Every hash is seeded per instance (Q50). Nothing derived travels to a child (S10): a
  child builds its text table and its indexes itself, under its lock of the writes, from
  what it receives.
- The memory of a new structure is counted in the instance budget, the rule of a write
  refused for memory applies to it, and its retired bytes are reported.
- Tokens are honored inside every wait of a step; a close completes only when the
  structure's memory is returned.

For this sprint:

1. **No gate of its own.** The text table is looked up by content only by a write, under
   the lock of the writes; a read reaches a text through the reference its root holds,
   inside the protection of reads, and never looks up by content. The candidate to hold
   the study against: a plain hash table changed and searched under the lock of the
   writes alone, entries reached by readers directly, an entry retired when its last
   reference goes and freed by the deferred reclamation. "Use one dictionary gate
   initially" in the milestone contract is withdrawn by the decision of 16 November.
2. **Generations.** "Handles carry generations" guarded a rebinding that deferred
   reclamation may already exclude: a root is never changed in place and an entry is not
   freed while a read that could see it is inside. The study says whether a generation is
   still needed and for what; it is not built because the brief of 14 November lists it.
3. **Counts.** Stored, prepared and index references are changed only under the lock of
   the writes. A write prepared and then withdrawn, cancelled or ended by a fault releases
   what it took; a test states it with the held storage.
4. **A child.** Text travels as bytes with its root, in the load format, for a copy and
   for an effect of the stream; the child stores each distinct text once through its own
   table. "A child loads shared text once" means once in its memory. Whether the wire
   carries a repeated text once is decided by the figure of the study (bytes on the wire
   for the measured case), by transfer ids that are not addresses if at all.
5. **Study, before the contract step:** how an established store keeps equal values once
   with counted references under one changing thread and concurrent readers — Valkey
   (shared objects, reference counts), DuckDB (dictionary strings; MIT), ClickHouse
   (LowCardinality; Apache-2.0), Arrow (dictionary encoding; Apache-2.0). Questions: where
   the count lives, when an entry is freed, what a reader pays to reach the text, what a
   write pays to find it.
6. **Measured, added:** a read by key of an entity with a Dictionary field next to the
   same entity with a Plain field, 1 and 16 threads — a Dictionary read that is slower
   beyond noise is a finding (reads win over writes); writes a second with the field as
   Plain and as Dictionary.

**Carried from S10:** what travels to a child holds no address and no block number; the
child stores received bytes through its own allocation.

**Track:** full · **Needs:** S12 · **Owner:** E02

- **Goal.** Equal text of Dictionary fields is stored once per database, and any text an
  index will use has a stable entry that is reclaimed only when nothing refers to it.
- **Contract.** None new: `StringStorage` from S02 takes effect. Internal: the text
  table, handles with generations, separate counts of stored, prepared and index
  references.
- **To read.** [Collections and strings](../design/milestone-1-engine-contract.md#collections-and-strings);
  [Q04.2, Q04.3, Q04.4](../decisions/strings.md); [arena protocol, reclamation](../design/arena-protocol.md#reclamation-and-relocation).
- **Acceptance.** A Dictionary field reads back exactly the text that was written,
  including case, when its comparison ignores case. Two entities with equal text share
  one entry; the entry is freed after the last reference and the last protected reader
  are gone, and not before. A handle is never rebound to other text while an old
  reference exists. A child loads shared text once and maps it to its own handles.
  Reference counts equal an independent tally after a churn of writes and deletes.
- **Measured.** Reported: bytes retained for 100,000 values drawn from 16 distinct texts as Plain
  and as Dictionary.
- **Out of scope.** Moving text during compaction (S23). Index keys (S14–S15).

**Decided by the planner on 18 November 2026 after the study of S13.** Where a sentence
above says otherwise, these replace it. The study read the source of Valkey
(BSD-3-Clause, commit 3fc96b1), DuckDB (MIT, commit d12578e), ClickHouse (Apache-2.0,
commit 75fcef2), Apache Arrow C++ (Apache-2.0, commit dd7dfc6) and the package `unique`
of Go (BSD-3-Clause, commit 62495ed); no file is adapted from them. Taken as ideas:
counts changed only by the one thread that may write, the reader taking none (Valkey); a
table by content that holds no bytes and serves writes only (ClickHouse, Arrow, DuckDB);
an entry at zero leaves the table at once and is never revived (Go). None of the five
keeps a generation.

1. **A root stores the relative address of the entry** for a Dictionary field, in the
   descriptor of 16 bytes every String field has, with a kind of its own (Text) and the
   length of the text. No handle and no address table: a Dictionary read pays what a read
   of a Plain value stored out of line pays. The milestone contract is rewritten by this
   sprint: "relative address or stable text handle (8)" becomes "relative address (8);
   for a Dictionary field the address of the shared entry"; "Handles carry generations
   and cannot be rebound…" becomes "An entry is freed only when the last block that
   refers to it is freed, which is after the last protected reader that could reach it
   has left; so a reference never resolves to other text. No generation is kept."; "Use
   one dictionary gate initially. Moving shared bytes updates the stable entry" and
   "shared-text movement remains possible through stable handles" become "Shared text is
   not moved in this milestone unless S23 decides how."
2. **The entry** is immutable memory of the arena that holds the exact stored bytes and
   nothing else: one block up to 4 KiB, a directory with chunks above, by the placement
   rule of a Plain value that does not lie inline. Its blocks are marked as shared text
   and their header names the slot of the entry in the text table. No count and no hash
   lie in the entry: a read reads nothing a write changes. An empty text stays inline
   with length 0 and has no entry. A Dictionary text may be as long as a value may be.
3. **The text table** is used by writes only, in managed memory, and no read touches it:
   slots (address, references as Int64, hash, and the length of the text, so that a
   lookup passes over texts of another length without touching the arena; a free slot
   keeps the link of the free list where an entry keeps its length: as built since part 2
   of S13R, 24 bytes a slot and no state, because the arena says whether the block of an
   entry is prepared) and a
   bucket table from hash to slot. The bucket table is the `IndexTable` of the primary
   index, which moves with its companions to a folder below both users; it is not
   written a second time. The hash is the engine's seeded hash over the stored bytes
   with the seed of the instance. An entry is keyed by its exact bytes always: "North"
   and "north" are two entries, and the comparison a field declares applies only where
   text is compared.
4. **One count for each entry, by one rule:** a block holds one reference for each Text
   descriptor it contains, from the moment it is written until it is freed. The rule is
   worded for any block, because S18 and S19 put descriptors into owned values and
   collections, and an index entry of S14 and S15 holds its reference the same way. The
   "separate counts of stored, prepared and index references" of 14 November are
   replaced: the descriptors of a prepared block are the tally of what its write took. A
   write that is withdrawn, cancelled or ended by a fault drops its references where its
   blocks are freed.
5. **No retirement of an entry on its own, and no generation.** A replaced or deleted
   root is retired as today; when the drain frees it, its references go, and an entry
   that reaches zero leaves the table and is freed in the same pass. So an entry is
   freed after the last reference and the last protected reader are gone, because its
   last referring block is freed only then. A later write of the same text makes a new
   entry. "A handle is never rebound" is stated by a test: a read held across the replace
   of the last referrer, then new texts of the same size class written, reads its text.
6. **Where counts change (the P1 of this sprint).** A count is changed only by the thread
   that holds the lock of the writes, on a master and on a child. That must be true of
   every free of a block as well: the drain of retired memory, the release of a
   withdrawn write, a close, a reader that helps a drain if one does. The implementer
   says where each runs today; where one runs outside the lock, the change of the count
   is made safe by the same lock. The place is stated in the code and by a test.
7. **Memory.** The bytes of entries are bytes of the arena and counted as such; the rule
   of a write refused for memory covers a new entry. The managed bytes of the text table
   (slots and buckets) and the number of entries are reported by counters in this
   sprint. Whether they are charged to the budget is S24, with the primary index; the
   sentence of the option's documentation ("not counted yet") is not changed here.
8. **A child.** Text travels as bytes with each root: for a Dictionary field a Text
   descriptor with location 0 and its bytes out of line. The child checks that form and
   builds its own entries through its own table with its own seed, for a copy of a load,
   an effect of the stream and a forwarded write on the master alike. No transfer ids,
   no state for a connection, the wire format unchanged. Measured for the reference
   fixture, 100,000 users with one field of 16 texts of 9 bytes: 15.41 MB as bytes
   against 14.51 MB with ids. If a load target is ever missed for a fixture heavy in
   Dictionary fields, the first thing to try is keys sent once for a frame of 64 KiB
   before the roots that use them, with state that lives for one frame; not built.
9. **Acceptance, restated.** "Reference counts equal an independent tally after a churn
   of writes and deletes": the tally counts the descriptors in live blocks, in blocks
   retired and not yet freed, and in prepared blocks. "A child loads shared text once"
   means once in its memory.
10. **Measured, and what a finding is.** A read by key of an entity with a Dictionary
    field against the same entity with a Plain field of the same length stored out of
    line, at 1 and 16 threads: slower beyond noise is a finding. Against a Plain value
    small enough to lie in the root the figure is reported, with the length at which the
    two cross; "inline up to N bytes" is a lever and is not built. 16 readers beside a
    writer that allocates in the size class of the entries (the metadata of a region on
    lines the readers read): if it shows, it goes to the planner before a lever is chosen
    (reading by segment base and offset, regions of their own for entries, short texts
    inline). Added to the list of the brief: a read with 100,000 distinct texts, cold
    entries; time and managed bytes for each write that hits, misses, replaces keeping
    the text, deletes; the time of a drain whose roots hold references; on a child the
    time to Ready, the bytes on the wire and the number of entries; memory for each
    distinct entry at 16 and at 1,000,000 distinct texts, arena and table together,
    against Plain; one repeated text of about 100 KB.

**Decided at the review of S13, 18 November 2026** (two reviewers on commit 9d6c036; no
P1). Both confirmed by tracing and by probes that no count is changed outside the lock of
the writes, that no read reaches freed or reused text, that a received root is refused
before anything is placed, and that no read touches the text table. By the planner's
decision of the same day the sprint had one round of fixes, for P1 and P2 only, and no
second review; what else was named went where its kind belongs.

1. **Three kinds of free path that drop a text reference had no test** (both reviewers):
   a preparation that took a reference and then fails, the abandon of a child with a root
   placed, the free of a placed forwarded root on a master. Tests were added and pass on
   the code as it was: the code was right.
2. **Dead text that only retired roots still name was not counted toward the next look of
   the drain** (one reviewer, by probe): one entity whose text of 1 MiB was replaced 100
   times held 101 MiB against 4 MiB as Plain, until the 128th retired item. Fixed, test
   first: a retired root counts toward the next look the slots of its own blocks and
   those of the entry of every Text descriptor it holds. Shared text is counted for each
   root that names it, which only makes a look come earlier. `RetiredBytes` keeps its
   meaning, the bytes of retired blocks.
3. **`LiesInChunks` compares the length alone.** As built it also read the kind, one more
   comparison on every read of a non-empty String or Binary value, Plain included. A
   value is placed by its length for every kind — the writer places it so and a received
   root of another form is refused — so the length decides. Taken into the sprint because
   the read by key of the reference fixture did not close against the commit before the
   implementation (see the measured results); a proposal to test for an inline value
   first is superseded by it.
4. **Carried to the fix package after S13**, because they change behavior and a
   refactoring sprint may not: the slot array of the text table doubles past 2^30 slots
   into a truncated array where the write has to be refused for memory; a closed instance
   that is kept alive still holds the text table (it joins item 10 of the package); the
   test of "a reference never resolves to other text" does not read through the old
   root, and a stronger one does.
5. **Carried to S13R** ([refactoring backlog](refactoring.md), T1): the state Prepared or
   Published of a slot repeats what the arena knows of the entry's block.
6. **Left as it is:** the text table is owned by the arena. Both reviewers were asked
   whether a table owned by the database is plainer and both found it is not: every free
   of a block has the arena at hand and has to drop the references of that block.
7. **The hardest part to follow**, named by both reviewers: the life of an entry from
   prepared to published — the state in the slot, the branch of `TryAcquire` that
   publishes on a hit, `TextTable.Publish` called for each descriptor from
   `RootStorage.Publish`, and the prepared accounting of the arena have to be read
   together to see that every path publishes an entry before a read can reach it. T1
   answers it.
8. **Remarks, recorded and not acted on:** the table and the slot array never shrink
   (S23); `TextTable.Publish` asserts no lock; `Marvin`'s append of bytes in pieces and
   two texts of one 32-bit hash have no test of their own (the reviewers' probes pass);
   the branch of `TryAcquire` for a write that is not prepared is reached by no product
   path; the head of `IndexTable` still speaks of the primary index only; the encoded
   size of a forwarded write does not count a new entry, which only grouping reads; a
   write of a Dictionary text clears 512 bytes of stack it does not use in UTF-16.

#### After S13 · Findings of the outside review

**Entered on 18 November 2026 as the planner wrote it.** An outside reviewer, who was
given none of the reviews of this epic, read `main` at commit e7b6e0e (after S12). It
reported 16 findings; the planner verified each against the code at that commit, refuted
none and corrected the severities. This is a fix package of its own on the full track —
tests first, by another agent than the implementer — on a branch of its own, started
right after S13 is merged, because S13 changes files the package changes. It is merged
before S14 starts; item 5 at the latest before S17.

No finding is a P1 in the scale of this epic: nothing is lost, doubled or returned stale
in ordinary use. Four of the reviewer's five P1 need a peer that sends what this engine
never produces; they are closed anyway where the check is exact and cheap.

**Fix, each with a test that fails before the fix.**

1. A committed write can end in `HistoryMismatch` (`WriteCoordinator`, `Deliver` and the
   synchronous `Outcome`; the same in the `finally` of `Forwarder.WriteAsync`). An empty
   session is admitted without being bound; a concurrent write on another database binds
   it; `Advance` throws after the commit. Rule: a session is bound to the history of the
   database at admission, before the write is executed; a session of another history, or
   one that loses that race, is refused with `HistoryMismatch` before execution; after a
   commit the result is never replaced by a failure of the session. One sentence is added
   to `LatticeSession`: "The first operation with a session binds it to that database's
   history." P3.
3. A success reply to a forwarded write whose sequence is not above the sequence the
   child has applied when it receives the reply is malformed: the child ends its
   connection (`PendingRequests.Receive`). P4, hardening, exact.
4. `Closed` that arrives while a reply or a transaction is being assembled is malformed:
   the connection ends as one that ended without `Closed`, so sent and unanswered writes
   are `IndeterminateOutcome` (`Follower`). The reviewer's premise is wrong — a success
   reply is one frame and a correct master never puts `Closed` inside a message — and the
   check is still right. P4.
5. `ReceivedChanges`: "a transaction names each key once" holds for skipped effects too —
   a delete of an absent key (`PrepareDelete`) and every skipped state record their key
   for the duplicate check. Test rows: a delete of an absent key and then a write of it;
   a skipped write and then anything on that key. P3; S17 relies on it.
6. `RetiredMemory.Drain` resets both triggers even when a reader kept it from freeing,
   and writes that retire nothing never look again (16 MiB stayed through 2,000 deletes
   of absent keys). Rule: while retired items remain after a look, a later published
   write of any kind looks again; the sentence of the arena protocol "what waits between
   two looks is bounded by those two thresholds … until its next operation" becomes
   true. P2.
8. `ChildConnection`: `cursor.Sent` moves when the last frame of the transaction is
   added, not before the first, so an Applied for a transaction not wholly sent is
   refused. Accounting only. P4.
9. `DeliveryTail`: `_taken` is cleared at the end of `Commit` and in the branch of
   `Release` without a cursor, and blocks let go are reset; a test with weak references
   states that after the last child has left the tail retains nothing, as the brief of
   S11 and the milestone contract say. The wider case the verification found is checked
   too: the whole unreleased tail of a child that lags, reachable through `_taken`. P2.
10. A closed database drops what it held: after the arena is disposed, the references to
    the root table, the index, the sets, the delivery tail, the retired list and the
    coordinator are released under the lock of the state; every entry point already ends
    in `ObjectDisposedException` before it touches them, and each is verified. A test
    with weak references on a closed instance that is kept alive. P3.
11. `ReadMemoryLimit` counts what is live at once (the decision on queries: "Sum
    simultaneously live buffers … check before growing"): during a growth of
    `ReturnedKeys` the old table is counted with the new. The exact thresholds of
    `ReadMemoryLimitTests` move; the test author restates them from the rule. P3.
13. The compiled schema is immutable, as `LatticeSchema` says: the nine public
    collections (`EntityTypes`, `EnumTypes`, `OwnedTypes`, `LatticeEntityType.Fields` and
    `.Indexes`, `LatticeOwnedType.Fields`, `LatticeEnumType.Members`,
    `LatticeIndex.Parts`, `LatticeIndexPart.Path`) are not arrays a caller can cast and
    write. The array is kept privately for the engine where a path indexes it; the read
    by key and the write are measured before and after — no fall. P2.
14. `CatchingUp`. The accepted decision on replication ("Connecting → Loading →
    CatchingUp → Ready") and the documentation of the enum say a child shows it while it
    applies changes up to its readiness position; the code folds it into `Loading` by a
    reading of 17 November 2026 that is recorded nowhere. The code follows the accepted
    decision: the state of the child is `CatchingUp` from the end of the load until R is
    applied; operations stay `Unavailable`. A test that asserts `Loading` in that phase is
    reported to the planner before it is touched. P2, low effect.
15. A step of the enumeration does not copy an entity it then cannot remember: room for
    the key of the entry is made before the copy, from the key in the entry, so the first
    entity of every pass and every growth boundary are copied once. Charging the limit
    for capacity grown for an entry that then turns out deleted is accepted: capacity is
    memory held; the comment of `EntityTable` is reworded to that. Asserted by
    allocation: `Enumerate().First()` of an entity of 1 MiB allocates no more than a
    read by key plus the table. P3.
16. `ChildArenaFullTests`: the sleep is replaced by an observable — a counter or a signal
    of "the applier waits for reads that hold retired memory", reachable by the tests of
    the engine — so the test proves the wait. P4.

17. From the review of S13. The slot array of the text table (`TextTable.TakeSlot`)
    doubles its length without a bound: at 2^30 slots the doubling overflows and the array
    is cut to 4 slots. The behavior: the write is refused with the memory refusal the
    engine already has (`WriteRejected`, memory) before anything is taken, never a
    truncated table; no new error and no option — it is a limit of the structure, stated
    once in the engine contract. The test reads the growth function at the boundary, not
    a billion entries. P3.
18. From the review of S13. The test of "a reference never resolves to other text" holds
    a read before it has taken its root, so the released read copies the new root. The
    stronger form: take the old root inside the hold, replace, write and delete texts of
    the same size class, drain, and read the old text through the old root, for one block
    and for a chunked entry. The reviewer's probe of that form passes; no fix is expected.
    P3.

Item 10 also covers the text table of S13: a closed instance that is kept alive holds
its slots and buckets today, as it holds the root table and the index.

**Document, no code.**

2. Values inside a received root are trusted (point 1 after the study of S12; decision
   O8: the replication port authenticates nobody). It lived only in this document. A
   dated paragraph goes into `docs/decisions/replication-and-hosting.md`: "A node checks
   the structure of what it receives so that no read leaves the received bytes; it does
   not validate field values again: a peer is the same engine with the schema agreed in
   the handshake, and a child validates before it forwards. A peer that sends invalid
   values can make a node hold an entity whose read throws or shows replacement
   characters; it cannot make it read or write outside its memory. Authentication of the
   port and what a hostile peer can do are outside this milestone (O8)." The header
   comment of `StoredText` ("only well-formed text is stored") says on which assumption.
   The verification found no memory unsafety and did not trace malformed chunked UTF-8
   and UTF-16 of odd length to the end: both are added to the tests of a received root
   as "throws a managed exception or is refused, never reads outside".
7. The read of an absent key repeats its lookup while writes are published during it
   (`EntityTable`), inside one protected section, without looking at the token; unbounded
   only at a rate of publication no lock of the writes reaches. The arena protocol gains,
   beside "a read holds retired memory back only for the length of one step": "a read of
   an absent key repeats its lookup when a write was published during it; the step ends
   with the first lookup no write crossed." P4.
12. One receive deadline covers a whole frame and is not renewed by partial reads, so "a
    load that keeps arriving is not ended" is false below about 17 kbit/s at the default
    and for a small `OpenTimeout`. The note carried to S34 from S12 is extended with this
    case: the deadline becomes a time without bytes, decided there with the case of
    bytes that wait. Until then one sentence at `OpenTimeout`: "A part is a frame of at
    most 64 KiB; it has to arrive whole within the timeout."

**Remarks for the coordinator.** The reviewer verified 1, 6, 9, 10, 11, 13 and 15 by
probes. The two reviewers of each sprint missed 6, 9, 10, 11, 13 and 15, which are
defects of ordinary operation; the report of the package says so, with the kind of check
that would have found each: a test with weak references of "retains nothing", a probe of
a limit at a growth boundary, a cast of a public collection. The coordinator tells the
planner only: a contradiction with a brief, a test of the contract that asserts the old
behavior (items 1, 11, 14), and the figures of item 13.

**Carried out and reviewed on 18 November 2026.** Tests first by another agent than the
implementer; two reviewers on commit cbed8db, a second round by one of them on 85dfb13.
Where a sentence above says otherwise, these replace it.

1. **Item 6 was narrowed twice by the planner, on what the implementer and the reviewers
   showed.** As written above, every published write would have looked while anything
   retired waited, which moves the epoch — a line of memory every read loads — on every
   write beside a held read. The rule that is built: a look resets its two counts; what
   it could not free stays retired and a later look frees it; a look that leaves retired
   items behind arms a second trigger — until a look leaves nothing behind, a published
   write that retires nothing counts as one item, so the next look comes within one
   window of writes of any kind. Without such a look a write that retires nothing counts
   nothing, and every load without a held read keeps the rate of looks it had. The bound:
   what waits between two looks is at most what reads held at the last look and one
   window. Counted by the reviewers as moves of the epoch for each 10,000 writes, before
   and after: replaces 78 and 78, with a read held or not; deletes of an absent key or
   inserts beside a held read 0 and 78; 99 inserts to 1 replace, pure inserts and
   nothing retired 0 and 0; after the held read left, 8,688 bytes kept for ever before
   and freed at the 128th write after.
2. **A P1 of the first round: item 1 was not closed for a write through a child.** The
   forwarder read the session as empty, a write on another database bound it, and the
   binding then returned without checking the history: the master executed the write and
   the call ended in `HistoryMismatch` (13 to 75 of 20,000 racing rounds; none on a
   master). Binding now checks the history of a session that has a position and binds an
   empty one, in one step for the session; a master and a child each bind once before
   execution. After the fix: none in 80,000 rounds. A test at the clock states it, and a
   bounded race guards it — put back on the old code it fails every time.
3. **The sentence of `LatticeSession`,** as the planner worded it: "The first read or
   write with a session binds it to the history of that database. Waiting with a session
   that is not yet bound waits for nothing and does not bind it; waiting with a session
   bound to another history fails as a read with it does." A write that completes in the
   hold of its own call has no test of its own for the binding: it runs before execution
   on every path and nothing can hold a write there.
4. **Item 11 is exact, and covers the list of chunks of String keys.** The limit counts
   the largest of the three moments while room is made — the places double, the list of
   chunks grows, the chunk is taken — because each releases what it replaces before the
   next allocates. `ReadMemoryLimitTests` stated no exact threshold, so none moved.
5. **Item 13:** the nine collections are read-only views over private arrays, made once
   for a schema; the engine reads the arrays through an internal span where it read an
   interface before. Its measurement is owed (see below).
6. **Item 17:** the slot array grows to the longest array there is and a write beyond it
   is refused for memory before a slot is taken or a block stored. **Item 10** covers the
   text table.
7. **No existing test asserted an old behavior** of items 1, 11 or 14; two comments that
   stated the old reading of item 14 were corrected.
8. **Two tests that failed once under load were wrong, the engine right,** each shown by
   a probe. The race of a wait with a publication and a loss made 50,000 handoffs between
   two threads inside a bound of one minute, which a loaded machine does not manage; it
   now races within a budget of time. The test of a child that closes before applying a
   write whose reply has arrived read the master's position after seeing the insert
   through a read — an insert is visible before the position moves — and let the default
   synchronization timeout run; it now names the sequence before the write and waits
   beyond the bound. One more test had the first pattern and was corrected.
9. **Left:** the test of an acknowledgment for a transaction not wholly sent still relies
   on 16 MiB being more than the buffers of the loopback hold; the only observable would
   be a switch in the sender of the master.
10. **Probes of the reviewers, all passing on the last commit:** weak references to every
    part of a closed instance that is kept alive, master and child, with and without a
    child having been connected (24 to 53 parts, none alive), and to the blocks of the
    delivery tail after the last child left; `ReadMemoryLimit` one byte below, at and one
    byte above what is live at every growth for Int64, Guid and String keys (87 runs);
    casts of every public collection of a compiled and a loaded schema; a churn of
    writers with shared and fresh sessions on two databases with a close in the middle.
11. **The hardest part to follow,** named by both reviewers: what counts toward the next
    look in `RetiredMemory`. Its head was rewritten as separate statements in the round.
12. **Carried on:** the close clears fourteen fields one by one and a part added later
    has to be added to that list — entry B4 of the [refactoring backlog](refactoring.md);
    notes for S14 and S17 at their briefs.
13. **To measure on a quiet machine,** which the owner could not give on 18 November
    2026; kept by the coordinator and run in one session: the read by key against the
    commit before S13 and the base against itself (S13); the read by key and the write
    before and after item 13; writes beside 16 readers and the read by key on 16 threads
    beside a writer for item 6.

#### S13R, part 2 · Refactoring that owes a measured figure

**Carried out and reviewed on 19 November 2026.** The entries of the
[refactoring backlog](refactoring.md) that owed a figure, each commit with its figure,
measured on a rented machine (see the measured results). Each step was reviewed by one
independent reviewer with one question, "is the behavior what it was"; no review found a
P1.

1. **D1, with A2 in its wider cut** (670617c): one struct for a prepared change,
   `PreparedChange` with `ChangeKindEnum`, and the sealed `ChangePublisher`, which
   reserves, releases and publishes the changes of one publication over a span. The
   first cut cost 2 to 3 % on a write; 70fcc30, babd343, 0d63331 and f4ae9f3 removed
   that cost: the change is written straight into the record of the write, not built
   aside and copied. The review of the third step of D1 named three findings of P3 and
   P4, fixed in f4ae9f3.
2. **D4** (42b6317): the retired memory notes whether anything was retired since the
   previous publication, and `AfterWrite` takes no argument.
3. **D2** (55b48a7): one shape for a write refused for memory on a child, and both
   callers ask the retired memory. A P2 of its review: the switch of the tests, moved to
   `ReadEpochs` as the entry proposed, kept a reference there that hollowed the
   weak-reference probe of "a closed database retains nothing"; it lives in
   `RetiredMemory` (4b784ef).
4. **D3** (5a88dd4): the check of a received layout is removed. The reviewer showed its
   condition by reading: the layout that reaches the check is always taken from the
   node's own schema, never selected by received bytes.
5. **A6** (37071e5, 0151c9f): the primary keys of a type leave `EntityTable`. A P2 of its
   review: a sealed class for the keys added dependent loads to a read by key; they are a
   readonly struct `PrimaryKeys` of 24 bytes held by value in the table, and the
   comparison stays in `EntityTable.KeyEquals`.
6. **T1 with B3** (808b619): `TextSlot` is 24 bytes and keeps no state; the arena says
   whether the block of an entry is prepared (`Arena.IsPrepared`).
   `ValueBlocks.TryStoreText` takes the slot as a number and writes the header of the
   block; `ValueBlocks.TextSlotOf` beside it reads it.

**Checked by the coordinator on f4ae9f3:** the build with 0 warnings, the format clean,
2,492 tests with 2,336 passed, 156 skipped and 0 failed, the samples in Debug and in
Release, 20 repeats of the suite in Release, all clean. One failure is not explained:
in the tenth of ten repeats in Release on 0d63331 one test failed, and the run did not
keep its name. It did not appear again in 40 more repeats with their output kept, 20 on
0d63331 and 20 on f4ae9f3. At the time the workstation also ran a build and the full
suite of two agents. From now on the output of every repeat is kept.

**Not done:** A9 and B2, taken into S14R; B2 before the scenarios of S15 are written.

#### S14 · Secondary indexes and exact search

**Added by the planner on 18 November 2026: Epic 1 does not build this, and does not
close the way to it** ([Epic 3](epic-3-after-the-first-milestone.md)). One criterion is added to the study: an entity may
contribute more than one entry to one index. Entries are already unique by (index key,
entity identity); the maintenance of an index on a write is stated as "the set of keys
the old state contributed against the set the new state contributes", which for a scalar
field is one against one. Not built: no index on the members of a collection in Epic 1.

**Added by the planner on 17 November 2026, against the engine as S10 left it.** Where a
sentence above or below says otherwise, these replace it. The owner asked for this check
of the briefs of S13, S14 and S15. The sprint begins with a study by the rule of the
process, and the milestone paragraphs it owns are rewritten by the sprint after its
study.

Common to S13, S14 and S15:

- Reads take no gate (16 November). Wherever this brief or the milestone contract says
  "dictionary gate", "index gate", "index protection", "claim root lifetimes" or "under
  the root gate", read: a read enters the protection of reads, and a structure is
  changed only by the thread that holds the lock of the writes; what is removed is
  retired and freed after the readers that could see it have left, and its bytes are
  counted as retired.
- A structure a read walks is one a read can walk without a lock while it is changed. A
  structure that needs a lock around readers is not taken; reads on 16 threads beside a
  writer are measured for every new read path, next to the read by key.
- Every hash is seeded per instance (Q50). Nothing derived travels to a child (S10): a
  child builds its text table and its indexes itself, under its lock of the writes, from
  what it receives.
- The memory of a new structure is counted in the instance budget, the rule of a write
  refused for memory applies to it, and its retired bytes are reported.
- Tokens are honored inside every wait of a step; a close completes only when the
  structure's memory is returned.

For this sprint:

0. **The package reference is gone** (the owner's decision of 18 November 2026, made with
   S13: a first release on nuget.org would have carried a dependency no code uses). The
   study still measures `OrderedMultiMap` of `Arc.Collections` behind a reader-writer
   protection for contrast, in a prototype or in the measurement program with a pinned
   reference of its own. If the study chose the package after all, the reference would
   come back with the code that uses it, and principle 11 of `AGENTS.md` ("depends on the
   contract and on no package: its structures are its own") would go back to the owner.
1. **The structure is not decided.** The milestone contract names `Arc.Collections`
   `OrderedMultiMap` "until the contract of S14 reviews it"; that structure needs a lock
   around every reader, which the rule above excludes. By the owner's word of 16 November
   the default is a structure of the engine's own after a named open-source one. Study,
   as the first step: ordered structures with one changing thread and readers that take
   no lock — the skip list of LevelDB/RocksDB memtables (BSD / Apache-2.0), the
   copy-on-write B+tree of LMDB (OpenLDAP licence), a B+tree with optimistic versions
   (LeanStore, MIT), the adaptive radix tree (DuckDB, MIT); and, for contrast,
   `OrderedMultiMap` behind a reader-writer protection, measured, not assumed. For each:
   bytes per entry for the fixture's indexes, read rate on 1 and 16 threads beside a
   writer, the cost of an insert and a delete, how a removed node is reclaimed, how a
   range is walked while it changes. The planner takes the result to the owner with the
   scope before the contract step ends: it changes an accepted line (index structures),
   the row of `AGENTS.md` on the engine and `Arc.Collections`, and the package reference.
2. **Entries are unique by (index key, identity of the entity):** the candidate that makes
   a group of equal keys an ordered run, which S15 needs. The study confirms or replaces
   it.
3. **What a search guarantees,** restated for readers without a lock: an entry is a hint;
   the search reads the root and returns it only if its current state has the searched
   key. So "a reader never finds an entity under a key it no longer has" holds by that
   check, and an entity that had the key for the whole search is found. "One publication"
   means: the entries of a write are installed in the hold of the lock that publishes its
   root.
4. **Preparation.** Nodes and text are taken in the hold that prepares the write and
   released if it is withdrawn; with the storage step busy a prepared, unpublished write
   holds them and no reader can reach them. "Staging/removing package nodes" leaves the
   milestone text with the package.
5. **Comparison** is the one service built in S07 (`ValueComparer`), with the case table
   of the core (Q46). The milestone paragraph "initial UTF-8 IgnoreCase comparisons decode
   into pre-reserved UTF-16 scratch and use .NET ordinal span comparison … Do not
   substitute … an unverified custom case-folding algorithm" is stale since Q46 and is
   replaced now, under O16, with "Comparison without case uses the versioned case table of
   the core ([strings](../decisions/strings.md), Q46), the same on every node; no .NET
   culture or runtime table takes part."
6. **The contract is not only "Exists".** `IndexKey.Equal<T…>` is generic in its signature
   and stores its values as `object` in an array: a box per value and two objects per
   search. The contract step measures a search by a full key and decides the form of
   `IndexKey` and `IndexBound` so that building a key boxes nothing, as it did for the key
   by name in S09, part 2; the Measured line "allocates its result and nothing per
   comparison" includes the key. A change of the two types is a contract commit of this
   sprint.
7. **A child** builds its indexes while it loads and keeps them while it follows; an index
   entry the stream contradicts is the divergence of S11 and faults the child.
8. **Measured, added:** exact search on 1 and 16 threads, alone and beside a writer;
   writes a second of the fixture's users with 0, 1 and all their indexes; bytes per index
   entry; the read by key on 16 threads not lower than before the sprint.

**Track:** full · **Needs:** S13 · **Owners:** E04, E08

- **Goal.** Every declared index is kept with the data on every node, and an application
  finds entities by the full key of an index.
- **Contract.** Exists: `set.Index(name)`, `Find`, `FindAsync`, `IndexKey.Equal`.
- **To read.** [Index access](../decisions/index-access.md);
  [index preparation and traversal](../design/milestone-1-engine-contract.md#index-preparation-and-traversal);
  [secondary index, search-key ownership](../decisions/index-structures.md);
  [Q07](../decisions/queries.md); [Q19](../decisions/index-lifecycle.md);
  evidence: [secondary index comparison](../design/prototype-evidence.md#bounded-secondary-index-comparison).
- **Acceptance.** The tests of `IndexTests` that search by equality on the full key pass
  with their assertions unchanged; the test author moves the remaining ones under a
  shared reason that names S15. In addition: a write changes the entity and all its
  index entries in one publication; a reader never finds an entity under a key it no
  longer has. Index nodes and their text are prepared before the commit; nothing grows
  inside publication. A replaced value with an equal key under another spelling is found
  and returns the new spelling. A search through an index the entity type does not
  declare is refused; nothing falls back to reading every entity. A child builds its
  indexes before it is ready, and a search with a session waits for the position first. Until S15 a search by leading fields only, with a range, in reverse or with
  Offset or Limit is refused with `NotSupportedException`, and its tests stay skipped.
- **Measured.** Asserted: an exact search that returns one entity allocates its result
  and nothing per comparison. Reported: index build time for 100,000 users.
- **Out of scope.** Ranges, order, paging, budgets and the agreement of all access paths
  (S15). Uniqueness (S16).

**Carried from the architect's review after S13 (18 November 2026), as notes and not as
design — the study of this sprint may change the form.** The text table acquires a
reference only by content; an index entry that names a text entry its root already
references needs "one more reference to the entry at this address". `TextTable.Release`
uses only the address of a descriptor, so it can take an address. A retired block of an
index that holds text references is a kind of retired item of its own, with its bytes
toward the next look of the drain, as a retired root has since the review of S13.

**Added to the study step by the planner on 18 November 2026.** How an index holds a
text key is measured in three forms on the prototype: (1) the entry names the root and a
comparison reads the key from the root; (2) the entry holds a copy of the key, or of a
bounded prefix with the root as the tie-breaker; (3) the entry and the root reference one
shared text entry. For each: bytes per entry, search by key, range scan, write, at key
lengths 8, 32 and 100 bytes. A form that forces an indexed Plain value of 64 bytes or
less out of the root is reported with what that costs a read by key. The choice comes to
the owner together with the structure; if it is not form (3), it comes with the dated
note for `docs/decisions/index-structures.md` and the sentences of the milestone contract
it replaces. Point 3 of the brief (the entry is a hint checked against the root) holds
in all three forms. Until figures say otherwise the accepted record stands: an index and
a root share stored text through stable references.

**Carried from the fixes after S13.** `Access/Sets/IndexCriteria` reads the parts of an
index through the read-only view of the schema, two interface calls for a part; when a
search becomes a read path it takes a span over the private array, as the fields do, if
its measurement asks for it.

**The study of S14, 18 November 2026: what it found; nothing is decided.** The choice of
the structure and of the form of a text key is the owner's and waits for the timing
figures, which need a quiet machine. The prototype is
`src/Prototypes/SecondaryIndexComparison/Ordered`; its sources, with licence and commit,
are named in its README.

- **Read:** the skip list of LevelDB (BSD-3-Clause) and RocksDB (under Apache-2.0), the
  copy-on-write B+tree of LMDB (OpenLDAP Public License), the B+tree with optimistic
  versions of LeanStore (MIT), the adaptive radix tree of DuckDB (MIT) and its
  synchronization after Leis and Scheibner (Apache-2.0), nbtree of PostgreSQL,
  `OrderedMultiMap` of `Arc.Collections` (MIT), `ImmutableSortedSet` of .NET (MIT).
- **Recommended by the study:** a B+tree of the engine's own whose published nodes never
  change — a write copies the node it changes and publishes the copy with one store of
  its address into the live parent; a reader takes no lock, checks no version and never
  retries. After LMDB, with the publication by one store of the radix tree's
  synchronization and LeanStore's walk from leaf to leaf by a new descent at the fence.
  Second: the same tree changed in place under optimistic versions — cheaper writes,
  readers that validate and restart beside a writer, fixed keys only. Third: a skip list
  — 68 to 81 bytes for an entry in the arena, and every backward step a new search from
  the head. Not built: a radix tree, which needs keys whose bytes compare in the order
  of the values, a second definition of order beside the comparison service. Excluded:
  `OrderedMultiMap` behind a lock around readers; 72 to 88 managed bytes for an entry.
- **Measured, bytes for an entry** (1,000,000 entries, nodes of 1 KiB, the key and an
  identity of 8 bytes): both B+trees 18.8, 25.1 and 38.6 for a key of 4, 8 and 16 bytes
  in random order; 12.9, 17.2 and 26.5 ascending; 46.3 for an Int64 key after a random
  half was deleted.
- **Correct under change:** a walker forwards and backwards beside a writer, 30 s for a
  case in Debug and Release — no entry out of order, none twice, none missed that was
  present for the whole walk, also inside one key of 6,000 identities.
- **What a write costs in the recommended tree:** the copy of one leaf for each index, a
  retired node for each index write, a reservation before the commit of at most
  2·(h+2)+1 nodes. Several entries of one write are published each by its own store, in
  the order: insert the new entries, publish the root, remove the old entries; the check
  of a found entry against its root covers the windows between.
- **A text key.** The form in which an entry names the root and a comparison reads the
  key from the root, and the form of a bounded prefix with the root as the tie-breaker,
  are not safe: a separator of an inner node goes on steering searches after its entity
  is gone, and a replace that changes the key leaves the entry where the old key
  belongs. They stay in the timing run as reference lines only. Left for the owner: a
  copy of the key in the entry when the value lies inline in the root, 64 bytes or less,
  and a shared text entry with a prefix of 8 bytes above that (the study's
  recommendation; it departs from "Search key ownership" of the decision on index
  structures for short values) — or a shared text entry throughout, which puts an
  indexed Plain value of 8 bytes out of the root at 96 bytes more for an entity and one
  more dependent load on a read by key. Bytes for an entity at keys of 8, 32 and 100
  bytes: a copy 92, 164, 420; shared 185, 201, 281; shared with a prefix 199, 215, 294.
  A shared entry needs a reference held for an entry of the index and released when the
  retired leaf that last holds it is freed; the rule of one reference for each
  descriptor of each block would add and release about 60 on every write to every index.
- **Not modelled by the prototype:** the cost of the comparison service, entries of
  variable length, nulls, composite keys, several entries in one hold, ARM64, and the
  checks `Arena.Payload` makes on every read, which the path of an index pays at every
  level unless an address is translated more cheaply — measured together with the
  figure for that validation.
- **Third-party notices:** decided at the contract step of this sprint by what the code
  then follows; if it follows the code of LMDB closely, the notices gain the OpenLDAP
  licence. The prototype ports no code.

**Chosen by the owner on 19 November 2026, as the planner relays:** the copy-on-write
B+tree. He was shown the two trees side by side, the price of this one — a write beside
sixteen readers about twice as dear, one retired node for every write to an index — and
that every figure is one run of 5 s. The decision is recorded in
[index structures](../decisions/index-structures.md); the contract step of this sprint
writes the internal types for this tree. The form of a text key is his next question.

**The form of a text key, chosen by the owner on 19 November 2026, as the planner
relays:** short values are copied, long ones shared. An index entry holds a copy of a
text key of 64 bytes or less, whatever the storage mode of the field — a Dictionary
field of that length is copied into the entry like a Plain one, and no reference is
built for it; a longer key references the text entry the root already references. He
was shown the search of a copy against a shared entry at 8, 32 and 100 bytes, the bytes
for an entity, what sharing short values costs the read by key, the price — two ways
to hold a key, a departure from the accepted record for short values — and that every
figure is one run. Whether a shared key carries its first 8 bytes beside the reference
is settled in this sprint by a measurement in the engine on keys with random and with
common heads. For the plan: long keys need "one more reference at this address", a
release by address and the retired item of a leaf, so T1 with B3 of the
[refactoring backlog](refactoring.md) comes before the implementation of this sprint.
A field that is part of an index stores a value longer than 64 bytes as a text entry,
whatever its storage mode; it goes through the text table, the mechanism that exists —
a counted entry outside the table would be a second lifetime to prove. Its known cost,
about 330 ns for a new value and about 50 bytes of table for each distinct value, is
reported with the figure of this sprint for a load of long distinct indexed values and
is not solved in advance. Adding or dropping an index therefore changes the layout of
the fields it names: nothing stored outlives a process yet, so there is no migration
now, and the sprint that persists roots takes the note. The trap of `LiesInChunks` for
S18 is unaffected.

**The public change of the stage, approved by the owner on 19 November 2026, as the
planner relays.** `IndexKey` and `IndexBound` are structs that hold their values without
a box, with the names and factories they had; a search allocated four to six objects
for its key before and allocates none now (counted by reading; the allocation test of
this sprint states it). The key is about 230 bytes and is passed by value. A default
key is an error: passed to a search it is an `ArgumentException`, by class and by name;
"every entry of the index" is said only by `IndexKey.All`, which is distinguishable
from the default; reading a property of a default key or bound is an
`InvalidOperationException`. Confirmed by the planner as rules of the schema language
and of the contract: an index whose four widest entries cannot fit a node of 64 KiB
fails the open with `InvalidSchema`; an index node holds one reference for each shared
key it contains, as the rule of counts says, and the write of an index of long keys is
measured before that rule is questioned. What only a timing decides, each built with
its default: a prefix beside a shared key (none), `Find(in IndexKey)` (by value),
references for a node or for an entry (for a node), the size of a node (1 KiB), the
checks of an address at every level of a descent (as they are).

**The timing of the prototype, 19 November 2026.** Every figure is ONE run of 5 s for a
case, without repeats, on 20 processors not pinned, with 7 to 20 % of the machine busy
with other work before a case (two cases at 44 and 51 %): differences below about a
tenth decide nothing. The structure that is chosen is measured again in the engine, in
this sprint, against the same scenarios. 1,000,000 entries, an Int64 key, nodes of 1 KiB
unless said.

- **The read by key beside writes to an index** (a stand-in that shares only the line of
  the epoch with the writer), reads a second: one reader 14.6 M with no writer, 11.3 M
  beside the copy-on-write tree, 11.7 M beside the tree changed in place; 16 readers
  31.0 M, 36.6 M and 29.6 M. The moves of the epoch that the copy-on-write tree causes,
  78 for 10,000 writes to an index, do not show as a cost of the read by key.
- **One write to an index**, ns, beside 0, 1 and 16 searching readers: copy-on-write
  insert 374, 531, 4,207; delete 420, 483, 4,891; a replace that changes the key 877,
  1,023, 9,956. In place: 1,077, 924, 2,810; 907, 895, 2,264; 1,935, 2,583, 4,799. One
  writer alone in the group of writes: copy-on-write 375 and 372 for a random insert and
  delete, 61.5 and 60.8 ascending; in place 318 and 259, 41.9 and 45.8. Beside 16 readers
  a write of the copy-on-write tree costs about twice the other.
- **The readers beside those writes**, searches a second: one reader 2.0 to 2.2 M on the
  copy-on-write tree against 0.67 to 0.90 M in place; 16 readers 6.0 to 6.9 M against
  5.8 to 7.0 M. Readers of the tree changed in place restarted 2 to 8 times in 100,000.
- **Exact search**, a second, 1 thread alone, 1 beside a writer, 16 alone, 16 beside a
  writer: copy-on-write 1.90 M, 0.94 M, 7.32 M, 6.19 M; in place 1.66 M, 0.91 M, 7.51 M,
  6.87 M; the skip list 0.38 M, 0.24 M, 2.28 M, 1.95 M; `OrderedMultiMap` behind a
  reader-writer lock 0.72 M, 0.23 M, 1.80 M, 1.63 M, its writer making 64 pairs of
  writes a second beside 16 readers against 133,574 and 219,968 of the two trees. With
  nodes of 4 KiB: copy-on-write 2.15 M, 0.85 M, 8.26 M, 6.93 M; in place 2.10 M, 0.91 M,
  9.47 M, 8.79 M.
- **A range of 100 entries**, walks a second, the same four cases: copy-on-write
  forwards 745 k, 345 k, 4.21 M, 3.81 M and backwards 683 k, 286 k, 2.90 M, 2.61 M; in
  place forwards 677 k, 201 k, 3.32 M, 3.02 M and backwards 599 k, 288 k, 3.66 M, 3.45 M;
  the skip list forwards 67 k, 71 k, 388 k, 350 k; `OrderedMultiMap` 48 k, 16 k, 342 k,
  321 k.
- **A text key**, exact search a second on 1 thread and on 16, no writer: at 8 bytes a
  copy 933 k and 5.61 M, a shared entry 270 k and 2.63 M, a shared entry with a prefix of
  8 bytes 1.09 M and 5.09 M; at 32 bytes 585 k and 3.68 M, 179 k and 2.61 M, 952 k and
  4.53 M; at 100 bytes 337 k and 2.42 M, 176 k and 2.69 M, 755 k and 5.76 M. Keys of 32
  bytes that share their first bytes, as numbered logins do: a copy 868 k and 4.25 M, a
  shared entry 351 k and 3.13 M, with the prefix 229 k and 2.88 M — the prefix loses
  where keys have a common head, which long values such as addresses and paths have, so
  the form above 64 bytes is not settled by the random keys. The entry that names the
  root, kept as a reference line only, 32 bytes: 253 k and 1.86 M. One write, ns, at 8,
  32 and 100 bytes: a copy 1,204, 2,142, 1,755; shared 2,362, 2,591, 2,968; shared with
  the prefix 1,808, 1,609, 910.
- **What a read by key pays when an indexed text lies in a text entry and not in the
  root**, reads a second on 1 thread and on 16: at 8 bytes 4.60 M against 3.95 M and
  23.2 M against 17.3 M; at 32 bytes 2.55 M against 2.07 M and 18.8 M against 17.4 M; at
  100 bytes 2.38 M against 1.96 M and 13.5 M against 13.2 M.

**Carried out and reviewed on 19 November 2026.** Where a sentence above says otherwise,
these replace it.

1. **What was built.** A copy-on-write B+tree of the engine's own: its nodes and entries
   in arena memory in `Storage/Ordered` (`IndexNode`, `NodeWriter`, `NodeStorage`,
   `IndexLayout`, `NodeHeader`), the tree, its order and the index of an entity type in
   `Engine/Indexes` (`OrderedTree`, `SecondaryIndex`, `IndexOrder`, `SearchKey`). The order
   of stored values is that of the comparison service, `ValueComparer.CompareStored` and
   `CompareText`, over the stored form of a fixed value in `Storage/Values/StoredScalars`.
   The search by the full key is `Engine/Entities/IndexSearch` with
   `EntityTable.TrySearch`, reached through `Engine/Sets/SetIndex` and `IndexMatches`. The
   upkeep of every index of a type is in one place, `ChangePublisher`.
2. **Text keys.** A text key of 64 bytes or less is copied into the entry, whatever the
   storage mode of its field; a longer one is shared as a text entry, its references
   counted for each node. A field that is part of an index stores a value longer than 64
   bytes as a text entry; `RootLayout.Version` is 2.
3. **The public form.** `IndexKey` and `IndexBound` are readonly structs. A default key or
   bound is an error: passed to a search, an `ArgumentException`; a member of one used, an
   `InvalidOperationException`.
4. **A rule of the schema language, new:** an index whose four widest entries do not fit
   64 KiB fails the open with `InvalidSchema`.
5. **The review.** Two independent reviewers in parallel with the same brief, then a
   second look: "fit to merge once the layout version is raised"; it is raised. Fixed on
   their findings: the end position of a search could precede a delete the search had
   seen (73ad394); calls had been added to the write of a type without an index
   (8b60534). An intermittent test of refused reservations was restated: the managed
   capacity of the text table may stay grown after a refused write. Three wrong tests
   were corrected by the test author.
6. **The hardest part to follow,** named by both reviewers: the accounting of reserved
   nodes in `OrderedTree`.
7. **The merge of part 2 of S13R.** The branch took part 2 of S13R (merge 8ea11dc, which
   reshaped the record of a prepared change), and the resolution of that merge was
   reviewed separately with one question. Does the upkeep see, for every kind of change, the replaced root
   and the new root it saw before, on a master's write, a forwarded write, a load and
   a child's transaction? It does: no P1, P2 or P3. The merge changed only the three
   preparations of `WriteExecutor` and the record of a change; `ChangePublisher`,
   `WriteCoordinator` and `ReceivedChanges` are byte for byte what was reviewed, and the
   upkeep takes the replaced root from the root entry, not from the record. A probe of
   the reviewer refused a replace, an insert and a delete on a reused record and found
   the indexes agreeing with the stored entities. Two P4 go to S14R: nothing asserts
   that a record written for a preparation changes no entity yet, and one comment of
   `PreparedWrite` is inexact. The reviewer named what the tests of the sprint do not
   cover: a refused replace or delete of an indexed type, a refusal at a later index, a
   throw after the indexes are reserved, a refused forwarded write and a refused change
   of a child; those tests are written first in S14, part 2. By the listings of the JIT
   the write of a type without an index makes the calls it made at the head of part 2
   of S13R (a replace and a delete one fewer); what is added is the test for indexes,
   two loads and two branches, at four places.
8. **Confirmed by the owner on 19 November 2026** after the figures on ARM64: the
   copy-on-write tree ([index structures](../decisions/index-structures.md)).

**Checked by the coordinator:** on 376b44c the build with 0 warnings, the format clean,
2,656 tests with 2,506 passed, 150 skipped and 0 failed, the samples in Debug and in
Release. Ten repeats of the suite in Release while three agents built and ran the suite
on the same workstation had three failures, none in a test of the indexes:
`ClosedFrameTests.Closed_that_is_not_valid_ends_the_connection_and_a_request_without_a_reply_in_IndeterminateOutcome`
once (the master could not listen: the system lacked buffer space for a socket) and
`OpenTimeoutTests.Load_that_takes_longer_than_the_OpenTimeout_while_its_parts_keep_arriving_becomes_ready`
twice (no frame within its open timeout of one second); the reviewer of the merge saw
one failure of `AnnouncedLengthTests` in the handshake under the same load. Twelve
repeats with nothing else running were clean. These tests depend on the load of the
machine, which is a defect of the tests: a task of its own makes them independent of
it, and the single failure without a name in part 2 of S13R is most likely one of them.

**Accepted and known.**

- An entity deleted and created again during a search may be returned twice; S15 is
  required to end it (its brief).
- A step of a search waits for the stamp of an entry as a read does.
- The memory reserved for index upkeep grows quadratically with the number of changes
  prepared and not yet published: 600 waiting writes 16 to 18 MiB with one index and 205
  to 280 MiB with seven; 2,000 waiting writes on seven indexes 2.0 to 3.05 GiB; 43,780
  nodes reserved against 28,747 used. The requirement is moved to S17.

**What is weak.** The write of a type with indexes, and the cost of a search. No
acceptance of S14 set a target for either; they are the subject of S14, part 2. On the
rented machine (see the measured results):

- A replace that changes every indexed key, one caller: 952 k writes a second with no
  index, 210 k with one, 85 k with three, 47 k with six — about 3.5 µs an index on top of
  1.05 µs, where the bare tree of the prototype does such a replace in 1.14 µs: the
  upkeep of one index costs 3 to 3.5 times the tree's own work.
- A replace that keeps every key still pays for the indexes: 814 k with none, 648 k with
  one (−20 %), 307 k with six (−62 %).
- A search by a full Int64 key: 450 k a second on one thread, against 2.1 M for the bare
  tree and 1.5 M for a read by primary key of the same items; a text key of 8 bytes 163 k.
  A search allocates 720 managed bytes through the class and 936 by name, against 88 for
  a read by key.

**After S14, on the light track: the samples as a learning path** (asked by the owner on
19 November 2026, as the planner relays; the form decided by the planner). The samples
project is a flat list of seventeen files today. It gets folders by topic, each a
namespace and one responsibility, not numbered steps — a number in a folder is a number
in a namespace, and inserting a sample would renumber folders: `Basics` (BasicCrud,
KeyTypes, ClosedProperties, NestedValues), `Schema` (Schema, SchemaDeclarations,
DictionaryStrings, DynamicAccess, with `directory.schema.json`), `Indexes` (Index,
UniqueIndex), `Writes` (WriteBatch, MemoryLimit, ReadLimits), `Replication`
(MasterAndChild), `Hosting` (DependencyInjection, Identity); `User.cs` and `Program.cs`
stay in the root; the grouping follows the content of a sample where it says otherwise.
The order lives in two places that cannot drift apart: `Program.cs` runs the samples in
the learning order and prints "Sample 3 of 17: …" before each, and a `README.md` of the
samples project lists the same order from simple to complex — for each sample one line
on what it teaches, what to read before it, and whether it runs to the end today or
reports where it stops. The README of the repository points to it in one sentence. No
behavior of a sample changes, and `dotnet run --project Moltaro.Lattice.Samples` still
runs to the end. It starts after S14, part 2 is merged and before S14R, on a branch of its
own (the planner, 20 November 2026): part 2 may touch `IndexSample` through `FindAsync`,
and two agents must not edit the project at once. Done on 20 November 2026, with entry F2
of the [refactoring backlog](refactoring.md). Sixteen samples in seven folders; where the
content said otherwise the grouping followed it: `DynamicAccess` in `Basics`, as the twin
of `BasicCrud`; `NestedValues` in `Schema`; `MemoryLimit` and `ReadLimits` in a folder
`Limits` of their own. The samples program checks before its first sample that its
`README.md` lists the same samples in the same order.

**After S14, not part of any sprint: SDK 11 as a change of its own.** `src/global.json`,
the three jobs of CI, and the same commit built by both SDKs and compared on the
measurement scenarios.

#### S14, part 2 · The cost of index upkeep and of a search

**Track:** full, a critical sprint · **Needs:** S14 · **Decided by:** the planner, 19
November 2026, on the figures of S14 ("What is weak" in its record). It comes before S14R
and S15 and is planned for the copy-on-write tree.

**The brief, as passed by the planner on 19 November 2026 after the study.** The study is
the record that follows this brief; its figures, its traps and its numbered proposals are
not repeated here, and where it proposes, this brief decides. E1 to E4 and E8 are entries
of the architect's review after S14 in the [refactoring backlog](refactoring.md).

- **Goal.** The upkeep of the indexes of a write and a search by a full key cost what
  their work needs, measured against the same type without indexes and against the bare
  tree of the prototype.
- **Review.** A critical sprint: one reviewer, and beside it a breaker, a second agent
  that writes probe tests against the guarantees this brief states and reports only what
  fails ([development process](process.md#review)). Its second target, after the end of a
  run of (d) (the planner, 20 November 2026): with `Need` the exact target, the reservation
  has no slack, so after any one publication — an insert that splits the root, a remove
  that merges into it, in either order among the waiting writes — the pool and the places
  still hold at least `Need` of what waits, at the tree as it then is. The probes are
  sequences that publish removes before inserts, and inserts that grow the tree while
  others wait. A third: a write refused at a later index after an earlier index topped up
  its pool — the top-up must not be what made it fail (the rule of (b)). A fourth, the
  boundary of the masks of (a): types of 63, 64 and 65 indexes, with none, a move and a
  rewrite mixed on both sides of the 64th, and a write that changes only an index beyond
  it (the planner, 20 November 2026). The reviewer checks one bound of (d): the UTF-8 bytes
  of a text part are compared as bytes up to `SearchKey.TextBytesPerPart` and decoded
  beyond it, so a key of 256 and of 257 bytes, and a part exactly at the bound in a
  composite key, give the same order and the same search as the oracle (the planner,
  20 November 2026).
- **Acceptance.**
  - (a) A replace that keeps every indexed key costs what it costs for the same type
    without indexes, within the gauge (today −20 % with one index, −62 % with six). The
    rule for skipping the upkeep of an index is the byte equality of its two entries, the
    one the replaced root contributes and the one the new root contributes, decided once
    in the preparation after the new root is written. Equality under the
    comparison of the index is not the rule, because an entry copies stored bytes, and a
    change of case only, the scale of a Decimal, +0 and −0 or the bits of a NaN change
    those bytes while the values compare equal. The decision is recorded with the
    prepared change, one for each index, and the reservation, the release, the
    installation and the removal read it (E2). A type may have more indexes than the bits
    of the record: from the last bit on, the bit is shared and says "decide again", and
    deciding again reads the same two roots, so it cannot differ from the preparation. The
    comment at the record states this rule; no limit goes into the schema language (the
    planner, 20 November 2026).
    As measured on 20 November 2026 on rented-arm64-1: a replace that keeps every indexed
    key costs, above the same type without indexes, about 30 ns for each index — 0.96,
    0.94 and 0.86 of its rate with one, three and six indexes, where it was 0.80, 0.56 and
    0.39. With one index this is within the gauge; with three and six it is not. The owner
    accepted it as measured (20 November 2026). What is left is the decision itself: it
    looks up the stored fields of each part on every write. The stored fields of an index
    are resolved once when its layout is compiled, in S16, after S14R has made the
    comparison of a part one, and the same line is measured there (entry F6 of the
    [refactoring backlog](refactoring.md)).
  - (b) The upkeep of one index for a write that changes its key costs at most 1.5 times
    the bare tree's work measured by the prototype on the same machine (today 3 to 3.5
    times). Its base comes first: the reservation as one closed formula (E1), with a test
    of the engine that holds the closed form against the loop. Then a standing reserve of
    nodes instead of topping up and trimming on every write, on three conditions: it is
    counted where prepared memory is reported and against the memory limit, like any
    other bytes; it is given back at close; and it is never the reason of a refusal (the
    owner, 20 November 2026, on the planner's proposal; it follows from the decision on
    write admission, "a write is refused for memory only when it does not fit beside the
    stored data, never for memory other writes hold for a time"). The rule: a preparation
    takes the need of its write and then tops the pool up to the reserve as far as the
    arena gives; a top-up that cannot be had is not a failure. Before a write is refused
    for memory — in its resolution alone, after what is retired was freed, and for a child
    before it stops following — every index gives back what its pool holds above the need
    of the writes still prepared, and the write is tried once more, taking its own need and
    no top-up. A write is refused only when that try fails. So a database with many
    indexes and a small limit opens, and writes to it whatever fits by itself. A write
    refused in its last try leaves every pool at the need of the writes still prepared,
    which for a write resolved alone is nothing: the nodes an earlier index took for it go
    back to the arena, not into its reserve (the planner, 20 November 2026; a refused write
    has no effect). Each entry is built once for a write.
  - (c) One object holds a search, with the key in it once; a search for one entity
    allocates that object and its result. The allocation test pins this to a number of
    bytes, so that one more object fails it. This answers the study's question on (c): a
    lazy `Find` holds its key between two steps, and holds it once. With it, a defect of
    consistency, fixed test first (E8): `FindAsync` delivers its failures by the rule of
    every asynchronous member (the owner, 20 November 2026, on the planner's proposal): an
    asynchronous member throws at the call only what the caller can know from its own
    arguments — a null or invalid argument, a default key, a key or options the index
    cannot serve; everything that depends on the state of the database comes through the
    returned task — a `LatticeException` of any code, also on a path that needs no wait,
    and `ObjectDisposedException` after a close. A close can come between the call and its
    first step, so the caller handles the task's path in any case, and it is what
    `ReadAsync`, the writes, `EnumerateAsync` and `OpenAsync` already do. The rule is
    written into `AGENTS.md`, "Shape of the public API", with this sprint (the owner,
    20 November 2026, on the review: it lived only here and in comments). Where the XML
    documentation of `FindAsync` or `WaitForPositionAsync` says how a failure arrives, the
    implementer corrects it in its commit. The test author checks every
    other asynchronous member that has a fast path written by hand against the same rule
    and lists what it finds. The form of `SearchKey` changes with it (E4). The list found
    the same defect in `EngineDatabase.WaitForPositionAsync`, both overloads: it joins
    this sprint, test first, and is fixed with `FindAsync` (the planner, 20 November 2026;
    the scope of E8). Its tests: Unavailable before the open and once faulted,
    HistoryMismatch, the lost master and a close come through the task; a timeout after a
    wait stays as it is.
  - (d) Targets on rented-arm64-1, one thread, `index-search`: a search by an Int64 key
    at least 700 k a second (today 450 k), by a text key of 8 bytes at least 450 k
    (163 k), by one of 100 bytes at least 200 k (119 k). A target that is missed is
    reported with the breakdown of what is left; it is not hidden, and it is not by itself
    a reason to change the layout. The means, from the study, in this order: (1) the
    comparison chosen once for an index; (2) the end of a run seen in the step that finds
    the entry — it touches what the fix of the end position in S14 protects (73ad394):
    those tests stay as they are, and the breaker takes it as its first target; (3) a leaf
    copied as a block; (4) text: no zeroing of unused stack, no unused buffers, and for
    Ordinal a comparison of bytes. What the Ordinal order is, the decision on queries says
    in [String ordering and query overrides](../decisions/queries.md#string-ordering-and-query-overrides):
    "Use explicit .NET StringComparer.Ordinal or StringComparer.OrdinalIgnoreCase
    semantics, as selected by the field, for ordering and supported range predicates." The
    same paragraph adds: "UTF-8 and UTF-16 storage produce the same logical order; physical
    byte ordering is not the contract." So an oracle test comes first, with the existing
    `CompareText` as the oracle: exhaustive over the code points around the surrogate range
    and U+E000 to U+FFFF, and over strings that differ first at such a place, for both
    storage encodings. For text stored as UTF-16 a comparison of little-endian bytes is not
    the order of the code units, so the implementation states what the fast path is there,
    or that there is none. Ordinal only: a comparison without case stays on the decoded
    form. `SecondaryIndex.HasKey` becomes "the order is zero" (E3).
  - (e) Answered by the study: `Find(in IndexKey)` saves 2 to 3 ns, and the contract does
    not change; `Arena.Payload` at every level costs 17 ns a descent, about 2 % of a
    search, and stays as it is. Left for the machine: the size of a node, 1 KiB against
    2 KiB, with readers beside a writer on ARM64 deciding; a prefix beside a shared key;
    text references for a node against for an entry. A prefix for each node with heads of
    4 bytes in a slot array changes the layout: it is not in this sprint, and it is put to
    the owner, with the figures, only if the text targets of (d) are missed after the
    comparison of bytes.
- **Tests first,** the first commits of the sprint, by the test author, in this order and
  before any implementation:
  1. The refusals the tests of S14 do not cover (point 7 of its record), which must pass
     on `main` as it is; a failure is reported to the planner at once, not skipped: a
     refused replace and a refused delete of an indexed type; a refusal at a later index,
     which releases the indexes already reserved; a throw after the indexes are reserved;
     a refused forwarded write; a refused change of a child.
     **The planner, 20 November 2026, on the rule of (b) the owner accepted that day.** A
     refusal happens with the pools at the need of what waits, which for a write resolved
     alone is nothing, so at that moment the tree is as on `main`. The premise of every
     refusal test of a master, in words for its comment: the fill ends by a refusal of a
     filler and leaves no free slot of the class of the nodes. A refused replace and a
     refused delete, a refused forwarded write and `RefusedReservationTests` of S14 keep
     their assertions; where a fill stops by a count without a refusal, only the fill is
     restated. A refusal at a later index stays if freeing one slot at a time shows it,
     with the write refused at least once before it succeeds; otherwise a type whose two
     indexes have nodes of two size classes, the plainer of the two, with the reason. A
     throw after the reservation is a cancellation, and also an OutOfMemoryException of
     the tests' allocator (`CountingNativeAllocator.FailNextAllocation`, an existing switch
     of the tests, not a fault point of the engine), which reaches the catch blocks of
     `TryReserveWithIndexes` and `TryPrepareReplace`. Two tests of `IndexPreparationTests`
     of S14 (a write withdrawn while it waits; one cancelled while it is prepared) take
     their state after the same writes were prepared and withdrawn once, as the test of
     the cancellation does, and where a node must be seen to be taken the waiting write is
     the first of its index; their assertions stay. A child that cannot hold a change
     stops following, as Q14, option A, says until the pause is built. Every one of them
     passes on `main` and under the rule; one that cannot be given such a premise is named
     to the planner, not skipped.
  2. The closed form of the reservation against the loop. Then, in a commit of its own,
     the figures of the reservation tests of S14 restated from the new formula, on the
     planner's word: the formula reserves fewer nodes.

     **The planner, 20 November 2026.** The formula is the closed form of today's
     `OrderedTree.Need()` and nothing else. With h the height now, r the room of the root
     (`Room()`), L = `LeastSeparators`, p waiting inserts and q waiting removes, every
     division integer and the whole computed in `long`:
     m = max(0, p − 1 − r); a = m / L, b = m % L; S = m + L·a·(a − 1)/2 + a·b, the sum of
     `Growth(k)` for k = 0 … p − 1; G = 0 when p ≤ r, else 1 + (p − 1 − r) / L, the height
     the removes see; Need = p·(2h + 1) + 2·S + q·(h + G + 1).
     The need is compared in `long`: a need the pool cannot count is a reservation that
     cannot be made, refused with nothing taken, as for an arena that cannot hold the nodes
     (the planner, 20 November 2026, on the review: narrowed to `int` it wrapped and
     reserved too few).
     The member its test addresses, the internal contract of this step:
     `internal static long Need(int height, int room, int leastSeparators, int inserts, int removes)`
     on `OrderedTree`. The instance `Need()` becomes a call of it, and it is the one target
     of `TryReserve` and `Trim`; `Bound` goes (E1). A contract commit of this one member,
     throwing `NotSupportedException`, comes before this step, by a contract author; step
     1 needs no member. The test keeps the loop as today's `Need()` and `Growth()` define
     it and compares it with the static member over a grid: height 0 to 4; room 0, 1,
     L − 1 and L; L of 2 and of the real layout; inserts 0 to 3L + 2; removes 0 to 3. It is
     skipped with the sprint's one shared reason until the implementation. A member and not
     the pool measured against the loop, because the standing reserve of (b) keeps the
     pool above the need of the waiting writes: the formula is held where it does not
     depend on the policy of the pool.

     The standing reserve, the rule its tests state: the reserve of an index is the need of
     one change of key at the tree as it is, `Need(h, room, L, 1, 1)` nodes. It is taken by
     the first preparation that touches the index, not at open. While writes wait, the pool
     of an index holds at most the larger of the need of those writes and the reserve; with
     nothing waiting it holds at most the reserve. A publication keeps the reserve; a
     withdrawal gives back what its write took, the reserve with it, down to the need of the
     writes still prepared; the next preparation tops it up; all of it is given back at
     close (the planner, 20 November 2026: `ContradictionTests` of S14 needs a withdrawal to
     leave the arena as it found it, and no flag on the tree). Its tests are part of this step, skipped with the same reason: (i)
     after every write is published, the prepared bytes of an indexed type are above zero,
     at most the reserve of its indexes, and counted in `Counters.Arena.PreparedBytes`; (ii)
     the reserve counts against the memory limit while it is held, and a write that fits
     only without the reserves of the indexes succeeds, after which the pools are below
     their reserve and nothing is refused; (iii) a close gives it back, and the arena ends
     with nothing prepared; (iv) a database with many indexes and a limit below their
     reserves opens, a first write that fits by itself succeeds though its reserve cannot
     be taken, and a write whose own need cannot be had is refused with nothing left taken
     ((ii) and (iv) as the rule of (b) restates them, the planner, 20 November 2026). The
     grid test and these go to `Engine/Indexes/Reservation` of the engine tests.

     The commit of the restated figures holds two changes, both on the planner's word: the
     helper `BoundOf` of `IndexPreparationTests` is replaced by the formula, with the room
     of the root the test knows from the tree it built, and `RefusedReservationTests` the
     same where it states a count of nodes; and every assertion that the prepared bytes are
     0 after the publication of writes to an indexed type becomes "at most the reserve of
     its indexes". A type without an index keeps 0. Assertions stay "within" (≤) where they
     are; nothing else of those tests changes. A test whose restated figure fails on `main`
     is skipped with the same reason, not loosened. The list of every assertion changed
     under this paragraph goes to the planner, and the reviewer checks that nothing else
     moved.
  3. The tests of (a), each asserting the content of the index through the test member
     (`EngineDatabase.IndexNodes`) and the result of a search: a replace that keeps,
     changes and swaps keys; a change of case only under a comparison without case; the
     scale of a Decimal; +0 and −0; NaN with different bits; null to null, where the bytes
     under a null are arbitrary and the null bit decides; a Plain descriptor is never
     compared as bytes; a long shared key replaced by an equal text, which is the same
     entry, so the upkeep is skipped and the references of the text count exactly as after
     a full upkeep; a replace that swaps the keys of two entities in two writes.
  4. The delivery rule of `FindAsync`, and the allocation of a search pinned to bytes.
     **The planner, 20 November 2026:** a ceiling of 400 bytes now for a search of one
     entity, the object and its result, skipped; after the implementation the test author
     pins the exact figure in a commit that states what the bytes are made of. The reviewer
     checks the composition — one object holding the key once, and the result — not only
     that the number passes, and that 24 bytes more fail it.
  5. The oracle test of the comparison of bytes. **The planner, 20 November 2026:** no new
     member. The oracle addresses the existing forms of `IndexOrder.Compare`, the walk order
     of a built index and a search for every key, against `ValueComparer.CompareText`, in a
     UTF-8 and a UTF-16 database. If the engine refuses or changes an unpaired surrogate on
     the way in, the test author reports what it does and tests what can be stored.

  The new tests of the master go to sub-folders of `Engine/Indexes` of the engine tests by
  area — `Refusals`, `Reservation`, `Upkeep`, `Search` — and those of a forwarded write
  and of a child to `Engine/Replication/Indexes`. The existing files of `Engine/Indexes`
  move at S14R ([refactoring backlog](refactoring.md)).
- **Before the plan of the machine is used:** B2 of the
  [refactoring backlog](refactoring.md). Done on 20 November 2026: every scenario of the
  plan runs on the one window. Its proof by listings stands; its series was not taken,
  because the workstation was not quiet, and is series 1 below.
- **The machine.** One start, at the end, by a plan written here before it: the series,
  their order, the expected length and what each decides. It starts after the review and
  the breaker have closed with no P1 to P3, on the head that passed them: a fix after the
  series would change the measured code and owe the series again (the planner,
  20 November 2026). The coordinator fills in the
  lengths. The rules for a start of a rented machine in [Acceptance](#acceptance) apply:
  one queue on the machine, at most N/2 readers judged, and a fall called only after a
  series of three builds rotating.

  | # | Series | Expected length | Decides |
  | --- | --- | --- | --- |
  | 1 | The harness of B2, the old (`main` at f3d767f) and the new on the same engine, beside two builds of one commit as the gauge: read-by-key-threads at 1 and 16 threads, write-callers, index-read-by-key, and index-search at 1 and 8 readers. Owed: the workstation could not give it. dictionary-reads is not run: before B2 and after it, it uses the window index-search uses (`ReadWindow`, then `StepWindow`), readers with a writer beside them, so it adds no part of the harness | about 30 minutes: for each scenario three rounds of the old and the new and one pair of the gauge, 8 runs | that the new harness measures what the old one did |
  | 2 | Removed: the gauge is a second build of the same commit inside each series, rotating in its rounds, a stronger gauge than a spread taken earlier (the planner, 20 November 2026) | — | — |
  | 3 | `index-writes`, `index-search`, `index-read-by-key`, `index-build` over `main` and the head; and in each round once the bare tree of the prototype (`src/Prototypes/SecondaryIndexComparison`, `ordered writecost`, the copy-on-write tree, Int64, 1,000,000 entries, 500,000 writes, no reader, nodes of 1 KiB), whose replace is the denominator of (b), taken in the same start | about 35 minutes, three builds rotating (`main`, a second build of `main`, the head), three rounds | (a), (b), (d) |
  | 4 | The size of a node, 1 KiB against 2 KiB, in the engine: the head against the head with nodes of 2 KiB, a variant of one constant (`IndexLayout.SmallestNodeBytes`); `index-search`, whose 8 readers beside a writer decide, `index-writes` and `index-build` | about 30 minutes, three builds rotating (the head, a second build of the head, the variant), three rounds | (e) |
  | 5 | `write-callers` and `read-by-key-threads` over `main` and the head | about 60 minutes, three builds rotating (`main`, a second build of `main`, the head), eight rounds | reads by key and writes of the fixture's users, a type with three indexes: the reads are unchanged, the writes are this sprint's gain. That a type without indexes loses nothing rests on the lines of no index of `index-writes` in series 3; neither scenario writes such a type (entry F7) |
  | 6 | Text keys longer than 64 bytes, in the scenario `index-long-text`: search and writes with a text key of 100 and of 200 bytes, with distinct heads and with a common head. The head against two variants of it: a prefix of 8 bytes beside the shared key, and the references to a text held for each entry by its root instead of by each node again | about 20 minutes, four builds rotating (the head, a second build of the head, the two variants), five rounds | (e), the two questions left |

  The series run in the order 3, 5, 4, 6, 1, the most decisive first, so that a start cut
  short still answers the sprint (the planner, 20 November 2026). The variants are patches
  of the head, kept with the outputs of the start and never in the repository; a variant is
  built without its tests, whose statements of the layout fail by design. Eight builds —
  f3d767f, two of `main`, two of the head and its three variants — and the prototype take
  about 15 minutes more. The whole start is about three hours and ten minutes. The lengths
  are estimated from the start of 19 November 2026, which took a run of
  read-by-key-threads in about 96 seconds, of write-callers in 50 and of the prototype's
  writecost of two trees and three counts of readers in 20, and from the workstation for
  the scenarios of the indexes: index-writes about 124 seconds, index-search 64,
  index-long-text 55, index-read-by-key 25 and index-build about 15. A series is added
  during the start only for a reason the planner agrees to.

- **After it.** S14R holds structure only ([refactoring backlog](refactoring.md)); what the
  reviews of S14 found as cost is this sprint's. Then S15.

**The study of S14, part 2, 19 November 2026: where the cost of upkeep and of a search goes;
nothing is decided.** The figures are of the workstation (x64, i9-13900H, .NET 10.0.15,
Release, the process held to logical processors 0 to 7 as `development-5` says), for the
breakdown only, never compared with the rows of the rented machine. No sampling profiler is
installed (`dotnet-trace` and `dotnet-counters` are not), so they come from a stopwatch
harness outside the repository that calls internal members of the engine at 53b404c, and
from listings of the JIT (`DOTNET_JitDisasm`). A figure is the minimum of 11 rounds, the cases
of a group run in turn in every round; medians lie 5 to 20 % above. Other agents kept the
machine 13 to 53 % busy before the runs: differences under about a tenth decide nothing.

- **Read for (d):** LMDB, `libraries/liblmdb/mdb.c` and `lmdb.h` at
  `74dd6c3ec648c31857fe0a100f11c2a34619e2d7` (OpenLDAP Public License 2.8); LeanStore,
  `backend/leanstore/storage/btree/core/BTreeNode.hpp`, `BTreeNode.cpp`, `BTreeGeneric.hpp`,
  `BTreeGenericIterator.hpp`, `backend/leanstore/Config.cpp` and `frontend/shared/Types.hpp`
  at `fcb64bf39cf69c435173f76e062cbb305a9a998f` (MIT); RFC 3629 for the order of UTF-8. No
  code is ported.
  - LMDB chooses the comparison once for a database: `mdb_default_cmp`, called from
    `mdb_dbi_open`, stores `mdb_cmp_memn` (memcmp over the shorter length, then the lengths),
    `mdb_cmp_memnr` for `MDB_REVERSEKEY` or `mdb_cmp_cint` for `MDB_INTEGERKEY` (native
    unsigned integers of one size) in `md_cmp`. `mdb_node_search` is a binary search over the
    16-bit offsets of a page, one call of `md_cmp` a probe; on a branch page it starts at 1,
    the first key being empty, and for integer keys switches to the aligned `mdb_cmp_int` or
    `mdb_cmp_long`; the last probe's result gives the exact match, nothing is compared again.
    `mdb_cursor_set` compares the key with the first and the last key of the leaf the cursor
    is on and searches that leaf alone when the key lies between them. No prefix, no
    normalisation, no heads: keys are compared as stored.
  - LeanStore compares byte strings only (`cmpKeys`: memcmp, then the lengths); a typed key is
    folded by the caller into bytes in its order (`fold`, an integer byte-swapped with its
    sign bit flipped). A node stores both fences and `prefix_length`, their common prefix,
    removed from every key; a slot holds the offset, the lengths and a `head`, the first
    4 bytes after the prefix byte-swapped so that an unsigned compare gives memcmp's order.
    `lowerBound` compares the key with the prefix once a node, then searches comparing heads
    as integers and reads the rest of a key only on equal heads; above 32 slots a hint array
    of 16 sampled heads narrows the range first (`searchHint`). A seek tests the key against
    the current leaf's fences before it descends (`compareKeyWithBoundaries`).
  - What fits a tree whose published nodes never change, with entries (values…, identity):
    (1) a comparison chosen once for an index when its layout is compiled, after `md_cmp`: a
    descent written for the common shapes — one integer part never null, fixed parts, a text
    part — instead of a loop over the parts with a call by type at every probe; the order is
    the same and the layout unchanged. (2) For a copied text compared Ordinal, a comparison of
    bytes that is the existing order: UTF-8 bytes sort as code points (RFC 3629), which is the
    order of UTF-16 code units except where, at the first byte that differs, one side has a
    lead byte 0xEE or 0xEF (U+E000 to U+FFFF) and the other 0xF0 or above (a supplementary
    character): there the order turns around. A UTF-16 copy compares as its code units.
    Without case, bytes compare directly while both sides are ASCII (the case table maps ASCII
    by `Ascii.ToUpper`); at a non-ASCII character the present path takes over, since one may
    map to ASCII (ı to I). (3) A prefix for each node and heads in a slot array suit nodes
    written whole and never updated better than LeanStore's nodes changed in place — computed
    once when the node is written — but they change the layout (question 1). (4) A hint across
    steps does not fit, since a search holds no node between steps; inside a step the leaf it
    holds already tells whether the run of the key ends (proposal 2).
- **A search by a full Int64 key** (100,000 items, one index, distinct keys, as `index-search`;
  height 4: 1, 3, 78 and 1,640 nodes, 61 entries a leaf, 18.3 comparisons a descent). The
  scenario here: 649 k a second on one thread, 1.54 µs; a read by key in the harness 0.57 µs.
  Each part measured alone, with the root cold:

  | Part | ns | Made of |
  | --- | --- | --- |
  | Find and GetEnumerator | 297 to 325 | the key checked and bound, three objects; Find alone 260 here and 80 in a loop over a database of 1,000 items, not separated further |
  | Step 1: search key, protection | 22 + 9 to 14 | |
  | Step 1: descent | 512 | 18.3 comparisons of 18 ns; 4 `Arena.Payload` of 4.1 to 4.6 ns; the rest the cache lines of the leaves |
  | Step 1: entry by its stamp, `HasKey` | 8 + 120 | mostly the first load of the root, which the copy needs anyway |
  | Step 1: copy, Build, Return | 243 to 252 | what a read by key does |
  | Step 1 as a whole, `TrySearch` | 1,100 | |
  | Step 2: a second descent from the root, which finds the next entry has another key | 564 to 620 | |
  | The whole search | 1,717 to 1,853 | |

  - The comparison, `IndexOrder.Compare` inlined into `ChildOf` (listing at tier 1, 1,391
    bytes): at every probe it loads the layout and its parts again, slices with checks of
    bounds, spills, and calls `ValueComparer.CompareStored`, a switch over eleven types, with
    both spans on the stack. A descent written for one Int64 part — the long, then the
    identity, addresses translated without checks — takes 184 ns on the same tree, against
    512, and agrees with `TryFindFirst` on every key but the 1.6 % that need the fence.
  - `Arena.Payload`: 4.1 to 4.6 ns against 0.6 to 0.7 ns for the same span without the checks
    of the address; 4 a descent, about 2 % of a search today and about 8 % of a descent
    written for its key.
  - The second step: in 98.4 % of these searches the next entry lies in the leaf the first
    step held; otherwise the fence the first step passed shows it when its key differs.
  - The named candidate "a hint that misses and descends again": a first step descends twice
    when its entry is the first of its leaf, 1.6 % here, about 8 ns on average; an entry passed
    over because its root no longer has the key costs a whole descent, only in the moments of
    a publication.
  - Allocation: IndexMatches 288 bytes, the enumerator 64, IndexSearch 280, the Item 88 = 720.
    IndexMatches and IndexSearch each hold the bound key, 232 bytes. Three objects of those
    sizes cost 68 to 71 ns; a copy of the key 2 to 3 ns. By reading, the key is copied about
    eight times a search: into Find by value, the bound key, IndexMatches, IndexSearch, each
    SearchKey, the copy of the probe, each `After`.
  - The code of a search through the class is shared generic code
    (`SetIndex<ClassCopy<__Canon>, __Canon>` in the listing), as a read by key's is.
  - The wait for the stamp and the position: 8 ns alone. Beside a writer the scenario loses
    14 % (556 k); the clock's line and the copied nodes, not separated (read only).
- **A text key, Plain, Ordinal** (height 4, 19 comparisons a descent). At 8 and at 100 bytes:
  the whole search 4.03 and 6.13 µs (scenario 260 k and 205 k a second); a descent 1.66 and
  2.85 µs; the second step 1.36 and 1.99 µs. A comparison through the order 69 and 83 ns;
  `CompareText` alone 51 and 73 ns; the same without zeroing its stack 23 and 38 ns; the UTF-8
  bytes of a copied key against the key's 6 ns. `CompareText` of a stored and a given text
  zeroes three buffers of 512 bytes — the piece, and two for the mapping without case that
  Ordinal never uses — and of two stored texts four; the engine has no `SkipLocalsInit`. It
  decodes the whole stored text into a piece of 256 code units, though these keys differ in
  their first 7 characters. A search calls it 38 times (two descents) and `TextEquals` once:
  57 KiB of stack zeroed and 39 decodes. At 100 bytes the key is shared: the descent's 1.2 µs
  more are the decode of 100 bytes (about 0.4 µs) and the loads of the text entries (about
  0.8 µs).
- **A write with one Int64 index** (100,000 items loaded ascending, one caller, as
  `index-writes`; height 4). Added by the index: a replace that changes the key 3,245 ns (the
  type without index writes in 0.75 µs), an insert or a delete 1,225 ns, a replace that keeps
  the key 257 ns.

  | Part of a replace that changes the key | ns | Made of |
  | --- | --- | --- |
  | The reservation | 466 | 15 blocks taken, 2 used, 13 given back by the trims, as many places in the retired list; a block 27 ns. At height h: (3h + 3) for a change of key, (2h + 1) for an insert (9 here, 242 ns), (h + 1) for a delete (5, 136 ns) |
  | Entries built | 440 | 6: `ChangeOf` in `TryReserve`, `InstallNew` and `RemoveOld`, two entries each; one entry 60 ns |
  | Two descents | about 980 | of the insert and of the remove, as a reader's |
  | Two copies of a leaf of 61 entries | about 900 | each `BytesOf` twice (69 ns) over `EditedLeaf.At` and 61 `NodeWriter.Add` (275 ns); the leaf copied as one block 9 ns |
  | The rest | about 460 | two retired leaves, the trims, the stores, the searches inside the leaves, misses; not separated |

  A rewrite alone (reservation of an insert, descent, leaf copy, publication, retire, trim)
  1,774 ns. An insert or a delete is one descent, one leaf copy, three entries built.
- **A replace that keeps every key** touches no node: each index builds both entries and
  compares their bytes three times, six entries an index (`ChangeOf` to None 129 ns alone):
  the 257 ns are that; with six indexes 36 entries a write. The earliest point at which "no
  indexed value changed" is known exactly is in the hold that prepares the write, once the new
  root is written, where `ChangePublisher.TryReserve` runs. "Changed" must mean: the entry the
  new root contributes is not byte for byte the old root's. Not "unequal under the comparison":
  equal under it in other bytes is a rewrite, never a skip — a text changed only in case under
  IgnoreCase, a Decimal of another scale, −0.0 against +0.0, NaNs of other bits; an entry holds
  the bytes of its own root. Traps for a cheaper test on the stored fields: a null field may
  hold any bytes, so nulls are compared first (null to null is no change, null to "" is one); an
  inline Plain text compares by its bytes, never by its descriptor, whose offset depends on
  the root; a Dictionary text or a shared long text may compare by the address of its text
  entry, which the text table finds by the bytes; a DateTimeOffset stores its UTC ticks, so an
  offset changed alone changes no entry.
- **Proposals, by expected gain** (estimates from the figures above):
  1. (b, d) The comparison chosen once for an index, when its layout is compiled: a descent
     and a leaf search for one integer part never null, one for fixed parts at offsets resolved
     once, and the text path; the order unchanged. A descent 512 → about 190 ns: a search −0.6
     µs (two descents), a replace that changes the key −0.6 µs, an insert or a delete −0.3 µs.
  2. (d) The step that returns an entity also looks, inside the protection, at the next entry
     of its leaf, or at the fence when the leaf ends there; another key, with the sequence of
     the database unchanged over the step, ends the search at that sequence, as the second
     step would, without its descent. No guarantee changes: it is the end step's own
     condition. A lazy Find then ends at the sequence of its last step, not of a later
     MoveNext. −0.55 of 1.7 µs (Int64), −1.4 of 4.0 and −2.0 of 6.1 µs (text).
  3. (b) A leaf copied as blocks: with fixed parts its bytes are its count times the width and
     a copy is at most two block moves around the changed entry; with text parts the entries
     move as one block and the offsets shift by one constant. About −0.4 µs a leaf copy, −0.8
     µs a replace that changes the key, −0.4 µs an insert or a delete.
  4. (d) Text: `[SkipLocalsInit]` on the comparisons, the buffers of the mapping only for a
     comparison without case, a piece no longer than the decision needs, and for a copied
     Ordinal key the comparison of bytes with its one turn (what fits, point 2), with tests of
     surrogate pairs against U+E000 to U+FFFF. A comparison 69 → about 10 ns; with 2, a search by 8 bytes 4.0 → about 1.1 µs.
  5. (a, b) The change of each index decided once, in the preparation, and kept with the
     prepared change (a bit an index); the publication builds only the entries it inserts or
     removes: 6 → 2 entries a change of key (−0.25 µs), and nothing in the publication for a
     replace that keeps the key: 257 → about 90 ns an index, about 10 with the test on the
     stored fields above. (a) needs the latter: within the gauge with six indexes is about
     10 ns an index on 1.23 µs.
  6. (b) A standing reserve: the pool of each index is kept at the bound of one change of key
     and not trimmed below it; a write takes what it uses, 2 nodes for a change of key, and the
     next preparation adds that many. −0.4 of 0.47 µs; about 15 KiB stay prepared for each
     index at height 4, counted in the budget.
  7. (c) Find returns one object that is the enumerable and its first enumerator, with the
     state of the search and the bound key once: 720 → about 380 bytes, about −40 ns. FindAsync
     runs its search on the stack: the list, its array, the result and the entities.
  8. Smaller: SearchKey refers to the bound key instead of copying it (about 20 ns); the last
     probe of the leaf decides the match without `SameKey` (one comparison of 19).
  Together, by the estimates on this machine: an Int64 search 1.7 to 1.85 → about 1.1 µs, a
  search by 8 bytes of text 4.0 → about 1.1 µs, the upkeep of a change of key 3.25 → about 1.2
  µs, which on ARM64 would be about 1.15 times the bare tree's 1.14 µs.
- **A target for (d)** that the breakdown supports without a change of layout, on
  rented-arm64-1, one thread, `index-search`: an Int64 key 700 k a second or more (today 450 k);
  a text key of 8 bytes 450 k or more (163 k); of 100 bytes 200 k or more (119 k), without a
  prefix.
  After S15, part 1 the same start reads 692 k for an Int64 key against the target of 700 k
  and 426 k for a text key of 8 bytes against 450 k, on a machine that read 5cc64c8 itself 6
  to 8 % lower than on 20 November (730 k and 474 k against 796 k and 503 k). The targets
  stay; they are measured again in the start of S16 (the owner, 21 November 2026).
- **The questions of (e):**
  - The size of a node: not answered. A descent compares about log₂ n times whatever the
    size; a larger node saves a level (a `Payload` and a miss) and doubles the copy of a leaf,
    which proposal 3 makes 9 ns a KiB. Measured after 3, on ARM64, readers beside a writer.
  - A prefix beside a shared key: at 100 bytes a descent spends about 0.8 µs loading text
    entries; a prefix decides random heads, not common ones. Measured in the engine on random
    and common heads after 1, 2 and 4, which change the ratio; LeanStore's head after the
    node's prefix serves both, at a change of layout (question 1).
  - `Find(in IndexKey)`: answered: it saves one copy of 232 bytes, 2 to 3 ns of 1.7 µs.
  - Text references for a node or for an entry: not measured (the write measured has no text
    key). By reading, a copy of a leaf with 28 shared keys takes 28 references and its free
    releases 28; to be measured on `index-writes` with keys of 100 bytes.
  - `Arena.Payload` at every level: 4 a descent, 17 ns of a descent's 512 today, about 8 % of
    one after proposal 1; its checks are the validation of an address read from a node, left
    by the owner to this timing.
- **Questions.** For the owner: (1) a node layout with a common prefix for each node and a
  head of 4 bytes for each entry in a slot array, after LeanStore, for indexes with text parts:
  a change of layout and of the order's code; not for fixed parts, whose entries already are
  the array of the key. For the planner: (c) "no copy of the key on the heap" cannot hold for
  Find, which is lazy and holds the key between two steps; proposal 7 holds it once.
- **Not done.** No sampling profile; no figure on ARM64; the 16 keys, by name and beside a
  writer only through the scenario; nothing of nodes of 2 KiB, text references or writes of
  text keys measured.

**Carried out and reviewed on 20 November 2026.** Where a sentence above says otherwise,
these replace it.

1. **What was built.**
   - (a) The change of each index is decided once, in the preparation, by the bytes of
     the two entries, and kept with the prepared change in two masks (no change, a move,
     a rewrite). From the 64th index of a type on, the last bit is shared and says
     "decide again", which reads the same two roots.
   - (b) The reservation is the closed form `OrderedTree.Need`, its one target. A
     standing reserve of one change of key is kept for each index and is never the
     reason of a refusal. A publication keeps it; a withdrawal gives it back; before a
     refusal every index gives back what it holds above the need. A need the pool
     cannot count is refused with nothing taken.
   - (c) One object holds a search, with its key once: 328 bytes, and the entity it
     returns, through a class; 320 and the record by name. `SearchKey` refers to the bound
     parts. `FindAsync` and both `WaitForPositionAsync` deliver everything that depends on
     the state of the database through the task.
   - (d) The comparison of an index of one number of 8 bytes is chosen once. The end of a
     run is seen in the step that finds the entry. A leaf is copied as blocks. An Ordinal
     text of a UTF-8 database is compared as bytes with its one turn; text stored as
     UTF-16 is compared decoded. The check of a found root is "the order is zero".
   - Proposal 1 is done in that form only: no scenario measures fixed parts at offsets
     resolved once, so the plainer form stays; it is not a debt. The second half of
     proposal 8, and a first piece of 32 code units for decoded text, were built and
     taken out again on the review: their figures lay inside the noise.
   - For the machine: the scenario `index-long-text`, and three variants as patches kept
     outside the repository (a node of 2 KiB, a prefix of 8 bytes beside a shared key,
     the references of a text held by its root for each entry).
2. **Tests first.** The test author's commits came before the implementation, 16 of them
   skipped with the sprint's reason until their step. The figures of S14 restated on the
   planner's word moved only where the planner named them, which the reviewer checked. The
   allocation of a search is pinned to its exact bytes. `NothingHeldAfterCloseTests`, which
   failed four times in 20 runs of a diagnostic series, has its cause shown: the shared
   array pool trimmed between its two measurements, the difference equal to the bytes it
   freed, the instance holding nothing. That the first failure was the same event is likely,
   not shown. It now measures in a process of its own, a test marked explicit that an
   ordinary run counts as skipped, which is why 151 tests are skipped and not 150. One
   failure of `OpeningAndClosingTests` was not reproduced in 30 runs on the head and 30 on
   `main`: entry F2.
3. **The review: two rounds.** An independent reviewer and a breaker.
   - Round 1: one P1 from the breaker; seven P3 and six P4 from the reviewer, all taken or
     placed.
   - **The P1.** `IndexOrder.HasKey`, written in this sprint when the check of a found root
     became "the order is zero", did not move its place in the search key past a fixed part
     that was null. So a search by a composite key with a null fixed part before another
     fixed part missed the entity that had the key and, while a writer changed it, returned
     entities under a key they did not have. On `main` the check was right: this sprint
     brought it. The tests of the brief did not see it, because none had a composite index
     with a null before another part; the reviewer's first round did not either. Its tests
     came first (df0cf43, fdd4f8c): a scenario through the public API, and every comparison
     of a search held against every root on composite indexes with a null in each place.
     The fix is 96be5e7.
   - Round 2: no P1, P2 or P3; three P4, placed in F3 and F4.
   - The check that the end of a run is reported at the sequence before the step only when
     the sequence did not move has no test in this sprint. A mutant that drops it passes
     every test and every probe through the public API, since no public path sees that
     sequence alone. A probe of the engine catches it in every run, but only through private
     fields, and no member is added for it now: its test is owed to S15, entry F5.
   - Four commits of the branch do not build, df0cf43 to def8b32: tests written against an
     earlier head were brought onto the branch without a build. fdd4f8c mends it; the merge
     squashes them.
4. **The hardest part to follow.** For the first round, the early end of a run in
   `EntityTable.TrySearch`, whose reason needs three orders from three files. For the
   second, the place of each fixed part in the scratch of a search key, worked out by three
   loops; E3 joins them at S14R.
5. **A small change of the sprint.** The rule of every asynchronous member is written into
   `AGENTS.md`, "Shape of the public API" (the owner, 20 November 2026).

**Checked by the coordinator:** on 471b756, whose code the head carries, the builds in
Debug and Release with 0 warnings, the format clean, 2,710 tests with 2,559 passed, 151
skipped and 0 failed in both, the samples to the end.

**Accepted and known.** The managed room of a refused reservation, F3, for S17; the need
grows with the square of the changes waiting, for S17's linear bound. The head of
`ValueComparer`, F4, for S14R. The top-up near the memory limit, for S24. The test of the
sequence a search ends at, F5, for S15. The stored fields of an index resolved once, F6,
for S16. A line of `write-callers` over a type without indexes, F7, for the next start. The
references of a shared text for each entry, F8, the owner's question.

**Measured on the rented machine, 20 November 2026**, the head 9e34443 against `main` at
3229ba1, by the plan above, in the order 3, 5, 4, 6, 1. The start took 3 hours and 11
minutes against the plan's 3 hours and 10, and all 163 runs reached their end line. The
figures are in [Measured results](#measured-results).

Two mistakes of the plan:
- Row 5 named a type without indexes, and its scenarios wrote the fixture's users, which
  have three.
- Series 1 had one pair of the gauge, too few to judge a single line.

- (d) is met. Even the lowest single run of each search is above its target: Int64
  768 k against 700 k, text of 8 bytes 492 k against 450 k, of 100 bytes 288 k against
  200 k. The gauge on these lines is 0.96 to 1.03. So the question of a prefix for each
  node is not put to the owner.
- (b) is met: the upkeep of one index is 1.27 times the bare tree's replace of the same
  start, its spread 1.23 to 1.31, against 1.5.
- (a) is met with one index and not with three and six: 0.94 and 0.86. The owner accepted
  it as measured (acceptance (a)).
- A type without indexes loses nothing. This rests on the lines of no index of
  `index-writes` in series 3: the head over `main` 1.005 to 1.037, against a gauge of
  0.982 to 1.018. Series 5 wrote the fixture's users, which have three indexes: there
  the reads by key are unchanged, and the writes are the gain of this sprint.
- The questions of (e):
  - A node stays at 1 KiB: 2 KiB read at most 4 % faster beside a writer, at the edge of
    the gauge, and wrote slower. Nobody reopens it without a new reason.
  - The prefix of 8 bytes beside a shared key is not taken: it wins on distinct heads
    and loses on common ones, which long keys usually have.
  - The references of a shared text held for each entry gain 1.45 to 2.1 on writes of
    long keys. That changes a rule of the decision on strings, so it goes to the owner
    after this sprint (F8).
- The harness of B2 agrees with the old one on read-by-key-threads up to 8 threads, on
  write-callers and on index-search. One line does not agree: index-read-by-key with 0
  indexes, 1 thread, alone, reads 0.888 of the old, in rounds of 0.94, 0.87 and 0.89. Its
  gauge is a single pair, at 1.066.
  - This series cannot confirm a fall: it has two builds rotating and one pair of the
    gauge, not three builds.
  - It is not the engine: both sides run the same engine, and the two harnesses read that
    one line differently.
  - So the figures of that line recorded before B2 are not compared with figures after it
    until the difference has a cause.
  - The cause is looked for by reading, before the next start of the machine (entry F9).
- Not judged:
  - the readers above 8 on 16 cores, among them 0.903 for readers of the other database
    beside one caller that inserts and deletes, which is bimodal in both builds of `main`,
    and in series 1 read-by-key-threads at 16 threads, 0.898 alone [0.90 1.13 0.89] and
    0.882 beside a replace [0.88 1.08 0.88];
  - insert and delete with a common head of `index-long-text`, whose gauge is bimodal;
  - one round of 0.56 at 200 bytes.
- Not explained:
  - In the breakdown of (a) on the workstation, a decision costs 4 to 5 ns more inside a
    write than alone.
  - The rented figures of (a) do not lie on one line: 56, 28 and 33 ns an index for one,
    three and six indexes, where the workstation's do.

#### S15 · Ranges, order, paging and the binding rules

**Added by the planner on 21 November 2026, for S15, part 2 · The binding rules.** The
Acceptance below is unchanged. Full track, not critical, no study; one implementer (Opus
5.5, high: the first sprint of the effort trial), one reviewer, a breaker. Branch
`epic-1/sprint-15-binding`.

Where the code stands at 5f1907d. `IndexValue.Bind` takes the type of the field exactly or
an integer of another width that fits. The rest of the accepted allowlist is not built, an
integer that the enum type of an Enum part does not declare binds and finds nothing, and a
`Nullable` without a value loses its type in `IndexValue.Of`, so a null of an incompatible
type is accepted. Reference parts and parts behind an owned value cannot be stored before
S18: their binding is stated by the acceptance of the key, as `IndexPartTypeTests` does
today, and nothing is added for them.

1. **What is built.** One place, `IndexValue.Bind`, used for a value of `Equal` and for a
   bound alike:
   - integer to Decimal, always;
   - integer to Float32 and to Float64 when the field type holds exactly that integer;
   - Float32 to Float64;
   - Float64 to Float32 when the finite value converts back to itself; NaN, each infinity
     and the sign of zero are kept across both widths;
   - refused, with the `ArgumentException` of today: Decimal to floating point and back;
     Decimal or floating point to any integer, 42.0 included; overflow; anything not
     numeric for a numeric field;
   - an Enum part: after the integer is bound to the underlying type,
     `LatticeEnumType.IsValid` decides, as it does for a write; an integer it does not
     allow is refused, as a value and as a bound, with the words of `EnumValues.Undeclared`;
   - a null that has a type — a `Nullable<T>` without a value, or a null given as
     `string` — is accepted only where a value of that type could be, by the pairs above,
     and only where the part can be null; a null given as `object` has no type and follows
     the part alone. `IndexValue` keeps the type of such a null without growing:
     `IndexKey` and `IndexBound` keep their size, which a test states. If it cannot, it
     comes to the planner with the figure.

   The same-type branch of `Bind` stays first and makes the calls it makes today. A value
   that is converted is not boxed. Not built: the same conversions for a value set into a
   record or written (`FieldValues.Normalize`): the decision speaks of query binding, not
   of stored data. A primary key is an integer, a String or a Guid, so the rules a read by
   key has today are already the whole allowlist for it.
2. **Decided sentences of documentation**, written by the implementer in step 1
   ([development process](process.md), "A sentence of documentation needs no step of its
   own"):
   - `IndexKey`, remarks, the sentence "Values are given in the order of the index fields
     and have the types of those fields; an integer of another width is accepted when it
     fits." becomes: "Values are given in the order of the index fields and have the types
     of those fields. A number of another type is accepted when it is the same number in
     the type of the field: an integer of another width that fits; any integer for a
     Decimal field; an integer that a Float32 or a Float64 field holds exactly; a Float32
     for a Float64 field; a Float64 for a Float32 field when it converts back to itself,
     with NaN, an infinity and the sign of zero kept. Nothing else is converted: a Decimal
     or a floating-point value is not accepted for an integer field, 42.0 included, nor a
     Decimal for a floating-point field, nor the reverse. A value for an Enum field is its
     integer, and it has to be one the enum type allows; a .NET enum value is not
     accepted."
   - The same remarks, the paragraph on null, gain: "A <see langword="null"/> that has a
     type, a <see cref="Nullable{T}"/> without a value or a null <see cref="string"/>, is
     accepted only where a value of that type would be."
   - `IndexBound`, remarks, gains: "Its value is accepted and refused as a value of an
     <see cref="IndexKey"/> is."
   - The last sentence of the head comment of `IndexValue` ("… is not taken here yet")
     goes, and the head says the rule in its own words.
3. **Tests first**, by the implementer in step 1, in
   `Moltaro.Lattice.Tests/Indexes/ValueBinding` (a folder of its own; nothing more is added
   to the root of `Indexes`), with their schema beside them. Every case runs through a
   class and by name; a refusal is stated for `Find` and `FindAsync` as
   `IndexKeyArgumentTests` does. An accepted value is proven by the entity it finds, never
   by the absence of an exception. A case an existing test already holds is named in the
   record, not written twice. Groups, in order:
   1. **One value, every way.** For each key family — the eight integers, String, Guid —
      an entity is read by its key and found by an index on a field of the same type
      holding the same value, at the smallest and the largest value of the type. For each
      other family an index part may end at and the engine stores — Boolean, Float32,
      Float64, Decimal, DateTimeOffset, DateOnly, TimeOnly, TimeSpan, Enum, Flags — the
      entity is found by an index, at the smallest and the largest value.
   2. **The allowlist at its boundaries**, one theory of (field type, value given, bound or
      refused), the same rows as a value of `Equal`, as a lower and as an upper bound:
      - integer to integer, for each target one under and one over each end: UInt8 from
        -1, 0, 255, 256; Int64 from UInt64 2^63-1 and 2^63; UInt64 from -1; Int8 from
        -129, -128, 127, 128;
      - integer to Decimal: Int64.MinValue and UInt64.MaxValue, each found;
      - integer to Float32: 16,777,216 bound, 16,777,217 refused, 16,777,218 bound;
        Int64.MinValue bound, Int64.MaxValue and UInt64.MaxValue refused;
      - integer to Float64: 9,007,199,254,740,992 bound, …993 refused, …994 bound (the
        cases of Q07.3.6);
      - Float32 to Float64: 0.1f finds the entity that holds (double)0.1f and not the one
        that holds 0.1; NaN, both infinities, -0f;
      - Float64 to Float32: 0.5 bound; 0.1 refused; (double)float.MaxValue bound; 3.5e38
        refused, not bound as an infinity; double.Epsilon refused, not bound as zero; NaN,
        both infinities and -0.0 bound;
      - refused: Decimal 0.5 and Decimal 42 for Float32 and Float64; Float32 and Float64
        for Decimal; 42.0, 42f and 42.0m for every integer type; a Boolean, a String, a
        Guid and each temporal type for a numeric field, and a number for each of them;
        DateTime for DateTimeOffset.
   3. **Floating-point values as keys and bounds**, Float32 and Float64, against an oracle
      ordered by `CompareTo`: an exact search of NaN finds the entities that hold NaN; of
      0.0 and of -0.0 each finds both zeros, every entity returned with its own sign; the
      walk in both directions; NaN, each infinity and -0.0 as an inclusive and as an
      exclusive bound, lower and upper.
   4. **Enum and Flags.** The order is numeric against an oracle: an enum over a signed
      type with negative members, one over UInt64 with a member above Int64.MaxValue, a
      Flags type with combinations, both directions, and a range. An integer the enum type
      does not declare is refused as a value and as a bound; for Flags a bit no member
      declares is refused, a combination of declared bits that no member names is bound,
      and zero is what `IsValid` says of it. An integer of another width that fits binds.
      A .NET enum value is refused.
   5. **A null with a type.** `Nullable<double>` without a value for a nullable Int32 part
      refused; `Nullable<long>` for it bound and finds the entities without a value;
      `Nullable<int>` for a nullable Decimal, Float32 and Float64 part bound; a null
      `string` for a nullable Int32 part refused and for a nullable String part bound; a
      null `object` bound for every nullable part; each of them refused for a part that is
      never null. A null in every position of a composite key of three parts, typed and
      untyped.
   6. **Shapes that are refused**, beside what `IndexKeyArgumentTests` and
      `IndexPartTypeTests` hold: a value of a type no scalar field has — a class, a tuple,
      a `char`, an `Int128`, a `Half` — as a value and as a bound; a bound whose type the
      allowlist refuses while the other bound is good.
   7. **A range for every family** not yet held (Int32, Int64 and String are): inclusive
      and exclusive bounds against the oracle, with the smallest and the largest value of
      the type as a bound, and for one family a bound given in another type of the
      allowlist.
   8. **Text by its field policy, in a UTF-8 and in a UTF-16 database**, the same
      assertions on both: equality and order of Ordinal and of OrdinalIgnoreCase; a bound
      that differs from stored values only by case; and the pair U+FF5E and U+10000, whose
      order differs between UTF-16 code units and UTF-8 bytes, in a walk and at a bound.
      Where `TextKeySearchTests` already holds one of these, it is named, not repeated.
   9. **Allocation and size.** A search whose value is converted (an Int32 for a Decimal
      part, a Float32 for a Float64 part) allocates what the same search with the type of
      the field allocates, in the collection of the allocation tests; `Unsafe.SizeOf` of
      `IndexKey` and of `IndexBound` is what it is at 5f1907d.

   The sample of the indexes gains one search by a number of another type and one by an
   Enum part.
4. **The breaker** takes the Acceptance as its list, within the bound the coordinator
   sets, in this order:
   1. a value that is accepted and finds nothing or the wrong entity — silent rounding
      near 2^24 and 2^53, subnormals, Decimal values equal at different scales (1.0 and
      1.00), UInt64 above Int64.MaxValue;
   2. NaN, infinities and the two zeros at bounds, in both directions and inside a
      composite key;
   3. Enum and Flags: undeclared integers, the high bit, the order;
   4. values given as `object` and as a boxed `Nullable`, against the same value given by
      its type;
   5. the same key through a class and by name, by `Find` and by `FindAsync`, in a UTF-8
      and in a UTF-16 database, and through a child;
   6. a box or a larger key on any accepted path.
5. **Measured.** Nothing reported by this part. The allocation tests of group 9 are
   asserted. On the workstation the implementer shows by the JIT listing that the
   same-type path of `IndexCriteria.Prepare` makes the calls it made. The start of S16
   takes `main` before this part as one of its builds for `index-search`.
6. **The record of the sprint** states the review rounds and the findings by severity
   beside those of part 1 (the effort trial, [development process](process.md)).

**The record of S15, part 2** (closed on 21 November 2026). Step 1 ended at 8fe7288, its
documentation sentences (9e8a001) and its tests written by the implementer from the brief
before the implementation; no assertion of a test of step 1 changed after it, and the
skips went with the implementation. The cases the brief names that an existing test
already held are named in the heads of the new tests: `IndexPartValueTests`,
`KeyTypeTests`, `IndexKeyArgumentTests`, `IndexPartTypeTests`, `TextKeySearchTests`,
`OrderAndBoundsTests` and `ExactSearchTests`. The allocation test of group 9 stands in
`Moltaro.Lattice.Engine.Tests/Engine/Indexes/Search`, where the collection of the
allocation tests is. The JIT listing on the workstation: `IndexCriteria.Prepare` makes the
calls it made at 5f1907d, and `Bind` is now inlined into `Equal` and `Range`, so a value
of the type of its field makes one call fewer; nothing was timed.

Taken by the coordinator from the implementer's report, as following from the brief: a
null keeps its type in a byte of the padding of `IndexValue`, so `IndexKey` and
`IndexBound` keep their 232 and 40 bytes; a `Nullable` without a value of a type no scalar
field has is refused for every part; for a part that is never null the error of a null
comes before the binding; the check of an Enum part sits in the branch of the same type
and calls `IsValid` only for an Enum part; the sample opens a small database of its own.
After the review the same rule was taken for a null given as any reference type other than
`string` and `object` (Q54 of the [open questions](open-questions.md)).

Review: two rounds and a last look at the documents. Round 1 found no P1, one P2 (a null
given as a class was accepted as a null without a type), five P3 (comments that cited the
brief and the Acceptance; the row of Q07.3.6 in `docs/design/questions.md`; the comment of
`FieldValues` on a search key; two builders of a bound; the ends of each type written
three times) and six P4; round 2 found nothing above P4, and three P4; the last look found
one P3 of a document, a sentence of `README.md` and of the architecture that did not say a
Decimal and a floating-point value never bind to each other, fixed in the same pass. The
breaker wrote 37 probes in its first round and 46 in its second, the new rule of a null
included, and none failed; a fault put in by hand failed 8 of the first and 3 of the new
ones. The hardest part to follow, by the reviewer: how a null reaches `IndexValue`, by
four paths in `IndexValue.Of`.

The effort trial (the [development process](process.md), "Models and effort by role"):
this part, implemented at Opus 5.5 high, took two rounds of review, with 1 P2 and 5 P3 in
the first and nothing above P4 in the second, a P3 of a document at the last look, and no
failure of the breaker. S15, part 1, critical and implemented at xhigh, took three rounds
and one on the fixes of its fall, with 1 P1, 3 P2 and 5 P3 in the first round and 3 P3 in
the second, and five failures of its breaker, three in its first round and two in its
second. The two parts are not of the same difficulty: part 1 was a traversal under
concurrent change on the read path, part 2 the binding of a value.

Small changes made in this branch while the sprint was open: the architect's review after
S15, part 1, gone through by the planner and the coordinator (the [refactoring
backlog](refactoring.md)); the term of the planner in [the development
process](process.md), "New sessions at a sprint boundary", on the owner's word of 21
November 2026; Q54.

Listed for the owner at the close, under the rule on a draft contract and the limits on
questions:
- The remarks of `IndexKey` and `IndexBound`: which numbers of another type are accepted,
  that a .NET enum value is not, and which nulls have a type.
- Q54: a .NET enum value refused for an Enum part; an undeclared integer refused as a
  bound as well as a value; a null given as `string` has a type, one given as `object` has
  none, and one given as any other reference type is refused.

**Added by the planner on 20 November 2026, after S14, part 2.** It comes above the text of
17 November; where they differ, this replaces it.

Why. Only six tests wait for S15: five in `IndexTests`, and `RecreatedDuringASearchTests`.
Its acceptance lists about a dozen behaviors that no test states. That is the shape that
made tests turn up missing in the middle of S14, part 2. The sprint is also two different
kinds of work.

1. **The split.** The acceptance is unchanged in sum; the Status table has two rows.
   - **S15, part 1 · The walk:** leading parts, ranges, both directions, Offset and Limit,
     the read limits, and a search that returns an entity at most once. Full track. It
     begins with a study and is critical, since it is a traversal under concurrent change
     on the read path: one reviewer and a breaker. Needs S14R.
   - **S15, part 2 · The binding rules:**
     - every scalar and key family found the same by key, by index, through a class and
       by name;
     - the numeric allowlist with overflow, 42.0 for an integer field, NaN and signed zero;
     - an unsupported key or bound shape refused;
     - enum order numeric;
     - text by its field policy in UTF-8 and UTF-16.
     Full track, no study, not critical: one reviewer, and a breaker that takes the
     Acceptance as its list, as the development process says. It needs part 1 only for
     bounds. Its tests of exact keys may be written while part 1 is implemented, by the
     implementer of part 2, where the coordinator judges that it shortens the way.
2. **The study of part 1** (Fable) may start on `main`, read-only, beside S14R: it reads the
   tree, not the loops S14R merges. Its questions, beside those below:
   - which of the three ways gives "at most once across a delete and a re-creation", each
     with its cost in bytes for an entry and in nanoseconds for a step. A way that adds
     bytes to every entry of an index is a question for the owner, which the planner brings
     with the figures; the study does not decide it;
   - whether the early end of S14, part 2, the end seen in the step that finds the entry,
     has a form for a range, and what its sequence means there;
   - the one descent of a reader in either direction (E6), and what it does to the exact
     search just measured;
   - what a traversal allocates: one object, as a search does, and nothing for each entity
     beyond what it returns.
3. **Tests first for part 1**, by its implementer in step 1, with the contract and after
   the study, in this order; each sentence of the acceptance gets its smallest set:
   1. The six that wait, unskipped by the implementer, their assertions unchanged.
   2. Order and bounds against an oracle of values:
      - composite indexes of two to four parts with a null in every position;
      - searches by leading parts, and ranges with inclusive and exclusive bounds;
      - both directions against the oracle, the reverse being the mirror of the index
        order;
      - UTF-8 and UTF-16.
      The P1 of S14, part 2 was a composite key with a null before another part. This is the
      test that would have caught it, and it is written before the code.
   3. Under change: no identity twice and every unchanged match, also inside a run of equal
      keys, in both directions. The paused form is deterministic, as `PausedSearchTests` is.
   4. At most once across a delete and a re-creation: `RecreatedDuringASearchTests`, and its
      range form.
   5. Offset and Limit. Offset skips without returning; Limit ends the walk without reading
      the rest. Both are stated by what is read (the nodes retired or the steps taken), not
      by time.
   6. The limits: over `ReadMemoryLimit` and over `ReadTimeLimit`, each an explicit failure;
      a cancellation is reported before a limit.
   7. The sequence a search ends at (F5 of the [refactoring backlog](refactoring.md)), with
      the member this sprint gives it; the probe of the breaker of S14, part 2, kept with its
      outputs, is the starting point.
   8. Allocation: a ceiling now, pinned to bytes with its composition after the
      implementation, as `SearchObjectAllocationTests` was.
   9. The rule of every asynchronous member in `AGENTS.md`, for the asynchronous forms of a
      range.
4. **Measured,** corrected by the rules written after the starts of 19 and 20 November:
   - "on 1 and 16 threads" becomes: 1 and 8 readers judged on 16 cores, 16 reported and not
     judged;
   - the exact search loses no more than was measured in the third start against 5cc64c8:
     4 % on an Int64 key, 10 % on a text key of 8 bytes, 5 % on a text key of 100 bytes and
     17 % on the first of many, one thread alone. This is the price of the read limits and
     of at most once on every search, accepted by the owner on 21 November 2026, in place of
     "the exact search of S14, part 2 must not lose". Beside a writer a search of one entity
     loses nothing;
   - a range of 100 entities alone and beside a writer; a page at Offset 10,000 beside the
     first page;
   - one start, at the end, after the review and the breaker have closed. Every series
     carries its second build as the gauge. The plan is written in the brief before it and
     read by the planner and the coordinator. It also carries the line of F7, and F9 (the
     one line of the B2 harness, three builds) unless reading has found its cause.
   - S14R has no start of its own. What it must show unchanged in figures is checked on
     the workstation by direction and listing, and decided at this start: its builds are
     `main` before S14R (22b8ee1), `main` after S14R and the head, each with a second build
     as the gauge, so that a loss is attributed to the sprint that made it.
5. **The breaker of part 1,** its targets in order:
   1. twice or missed under change, both directions, inside equal keys;
   2. a delete and a re-creation;
   3. composite keys with nulls at the bounds, both directions, the reverse being the
      mirror;
   4. the end sequence against its mutant;
   5. a step that holds nothing: weak references after a paused traversal, and the growth of
      the tree between two steps;
   6. the limits and the order of cancellation.
6. **Unchanged** from the texts of 17 and 19 November: points 1 to 4 of "For this sprint",
   and the carried texts of the study of S14 and of the architect's review (E6 here, E3 at
   S14R).

**The plan of the start of S15, part 1** (the coordinator, 21 November 2026, for the
planner to read before it). One start of the rented machine, after the review and the
breaker have closed with no P1 to P3, on the head that passed them. Builds: `main` before
S14R (22b8ee1), `main` after S14R (5cc64c8), the head and its variants. Every series
rotates its builds in rounds, with a second build of one of them as the gauge. At most 8
readers are judged on the 16 cores, 16 are reported and not judged, and a fall is called
only after a series of three rounds.

Before the start, in the measurement program, by the implementer of the sprint and
reviewed with it:
- the lines of the walk in `index-search`: a range that returns 100 entities, forwards and
  in reverse, alone and beside the writer of the scenario; a page of 100 at Offset 10,000
  beside the first page; and the share of the steps of a range of 100 that begin with a
  descent, reported;
- F7: a line of `write-callers` over a type without indexes, with its callers and its
  readers beside them;
- F9: its cause read in the old and the new harness for that one line; series 6 tests the
  hypothesis reading gave.
The variants of `EntityTable.EntriesPerRound` are patches of the head, kept with the
outputs of the start and never in the repository.

| # | Series | Expected length | Decides |
| --- | --- | --- | --- |
| 1 | `index-search`, its exact lines, at 1 and 8 readers, alone and beside the writer, over 22b8ee1, 5cc64c8, the head and a second build of the head as the gauge | about 15 minutes, four builds, three rounds | that the exact search of S14, part 2 does not lose, neither to S14R nor to this sprint, against its figures of 20 November 2026 |
| 2 | The lines of the walk, over the head, a second build of the head and two variants of `EntriesPerRound`, 64 and 1,024 against 256 | about 20 minutes, four builds, three rounds | the figure of `EntriesPerRound`, which replaces the placeholder in one commit after the start with the figure beside it; the cost of the range of 100 alone and beside a writer, of the deep page, and of the share of descents; the cost of the first entity of a walk read twice |
| 3 | `write-callers` and `read-by-key-threads` over 22b8ee1, 5cc64c8, the head and a second build of the head as the gauge, with the line of F7 | about 40 minutes, four builds, four rounds | that S14R and this sprint change neither the writes nor the reads by key of the fixture's users, and that a type without indexes loses nothing on many callers and readers (F7) |
| 4 | `index-writes` and `index-read-by-key` over the same four builds, the gauge a second build of the head; and in each round once the bare tree of the prototype (`src/Prototypes/SecondaryIndexComparison`, `ordered writecost`), as on 20 November 2026, its restore in the build stage | about 35 minutes, four builds and the prototype, three rounds | the upkeep of an index after S14R (G5) and this sprint, as a ratio to the bare tree taken in this start; a ratio whose denominator is from another day is not judged |
| 5 | `forwarding-costs` over 5cc64c8 and the head, with a second build of the head as the gauge | about 10 minutes, three builds, three rounds | that the forwarded write of F12, which counts its text entries and walks its descriptors once less, does not lose |
| 6 | F9: the line of 0 indexes, 1 thread, alone, of `index-read-by-key`, over the head, a second build of the head as the gauge, and a variant of the head whose step of the window (`IWindowStep.Run`) is marked NoInlining, a patch outside the repository and never a candidate for the code. Reading found that the harness of B2 calls a step inlined into a loop optimized only by on-stack replacement, where the old one called a lambda that reached Tier 1; a hypothesis. Tried once on the workstation before the start, not judged: the workstation was not quiet (another project's compiler on 7 to 12 of its 20 processors before five of the six runs), and the line ranged from about a third to its full rate within each build, so the try says nothing either way | about 10 minutes, three builds, three rounds | the line recovers with the variant: the difference is how a step is called, and figures of that line from before B2 stay incomparable for a known cause; it does not: the cause is still unknown and the entry stays open |

The series run in the order 1, 2, 3, 4, 5, 6, the most decisive first, so that a start cut
short still answers the sprint. Over the four builds, 5cc64c8 against 22b8ee1 is S14R, the
head against 5cc64c8 is this sprint with F12, and the two builds of the head are the
spread. Series 1, 3, 4 and 5 can show a loss, and a loss is called only when the three
rounds agree against the gauge; series 2 chooses a constant and reports costs with no
target, so nothing in it is met or missed; the lines of 16 readers are reported and not
judged (the planner, 21 November 2026). Six builds — 22b8ee1, 5cc64c8, two of the head and
the two variants — and the prototype take about 15 minutes more. The whole start is about
two hours and twenty-five minutes. The lengths come from the start of 20 November 2026: a
run of `index-search` about 64 seconds, `write-callers` 50, `read-by-key-threads` 96,
`index-writes` 124, `index-read-by-key` 25, the prototype's writecost about 20; the lines
of the walk are estimated at about a minute and a half for a run. A series is added during
the start only for a reason the planner agrees to.

**What the first start showed, 21 November 2026** (the rented ARM64 machine, six series,
about two hours). The exact search lost to this sprint, three rounds agreeing against the
gauge: head against 5cc64c8 0.88 for an Int64 key found once through the class, 0.92 by
name, 0.87 and 0.92 for texts of 8 and 100 bytes, and 0.52 for the first item of 6,250
equal keys, whose bytes went from 416 to 840; S14R was clean there. 256 entries a round
stays (series 2). Writes and reads by key lose nothing to this sprint, nor does a type
without indexes (F7, series 3). The upkeep of one index stands at 1.20 to 1.26 times the
bare tree taken in the same rounds (series 4), the forwarded write loses nothing to F12
(series 5), and F9 is not explained by how a step is called (series 6). One fall belongs
to S14R: inserts and deletes of write-callers at 0.92 to 0.96 against 22b8ee1, the head no
better; its cause is located on a branch of its own, and its fix is merged to `main`
before this sprint.

The costs of the walk, reported with no target: a range of 100 about 9,800 a second on one
thread alone and 0.89 forwards, 0.84 in reverse beside a writer, which is everything a
writer costs a reader, the tries among it; a page of 100 at Offset 10,000 about 26 times
the first page, the price of remembering the identities passed over that Q07.3.5 requires;
counting under a memory limit not visible for an entity of 112 stored bytes; a range of
100 makes 3.22 descents at a fence, so a harvest of a page at a time is not worth a
design.

The fixes of the fall of the exact search, taken by the planner after a table of the parts
of its path: group 1, the frame of the step (A1 the step with nothing to walk as a method
of its own, A2 one walk and one scratch); group 2, the calls (A3 the place given as out
values, A5 whether the key is full asked once, A4 an inline test whether any limit is
set); group 3, A7 Seek keeps the fence of its side; and (B), a lazy search by a full key
that takes the first of many keeps only that entity's primary key until its second step,
counted against `ReadMemoryLimit` as anything it remembers. A2's lean second path and A6
wait for the figures of the second start. A group is kept only if its figure shows outside
the gauge; A3, A5 and A7 are plainer and stay in any case.

**The plan of the second start of S15, part 1** (the coordinator, for the planner to read
before it). One start of the rented machine, after a review round of the fixes and the
breaker's probes on them have closed with no P1 to P3, on the head that passed them. The
owner is asked only to start and to stop the machine. Every series rotates its builds in
rounds with a second build of the last as the gauge; at most 8 readers are judged, and a
fall is called only on three rounds agreeing against the gauge.

| # | Series | Expected length | Decides |
| --- | --- | --- | --- |
| 1 | `index-search` over 5cc64c8, 5abb6c0 (the head of the first start), the head after group 1 (a6bbd62), after group 2 (18151c8), the head with group 3 and (B), and a second build of the head as the gauge. The line of 16 keys, the first item, is an Int64 key, which (B) covers; a String primary key takes the earlier path and is not measured here | about 30 minutes, six builds, three rounds | whether the exact search still loses to the sprint, and which group carries what: a group of the frame and A4 are kept only if their step shows outside the gauge |
| 2 | `write-callers` over 22b8ee1, 5cc64c8, 5cc64c8 with V1, 5cc64c8 with V2, and a second build of 5cc64c8 with V1 as the gauge; its insert-and-delete lines decide | about 18 minutes, five builds, four rounds | which fix of S14R gives back what it cost inserts and deletes |
| 3 | `index-long-text` over 5cc64c8, with V1 and with V2 | about 10 minutes, three builds, three rounds | that V2, which also changes the taking and the freeing of the references of shared keys, loses nothing where keys are long; the fixture's keys are short |

The cause of the fall of S14R, located by listings and exact block counts before the
start: E7 of S14R, part 1 gave `NodeStorage.Retire` a `stackalloc` beside its loop and a
call of `IndexLayout.SharedTexts` for each entry, so the method is compiled without
tiering, is never inlined into the retirement of a node, and makes about 54 calls more a
write than at 22b8ee1, on an insert and a delete of an index with a String part only. Two
fixes are measured as patches outside the repository: V1, the sum of the slot bytes of
22b8ee1 back, a second walk beside `SharedTexts`; V2, the shared keys of an entry walked
by a ref struct, as `RootDescriptors` walks a root, with no `stackalloc` in its four
users.

The rule that chooses, written before the start (the planner, 21 November 2026): V2 is
taken if it brings the insert-and-delete lines back to 22b8ee1 within the gauge and loses
nothing outside the gauge on `index-long-text` against 5cc64c8, since it keeps the walk
single, which is what E7 was for. V1 is taken if V2 fails either condition and V1 meets
the first; its second walk then carries a comment at its place with the figure that
justifies it, and E7 is recorded as half undone with that reason. If neither brings the
lines back, nothing is merged and the figures go to the planner. The one taken is
committed on a branch of its own from `main`, reviewed by one reviewer with one question,
is any behavior or assertion changed, and merged before this sprint, which then takes
`main` in; the variant series stands as its measurement only if the commit is the patch
line for line. The insert and delete of the head of this sprint, which still has the slow
form, are not judged on this start.

The series run in the order 1, 2, 3. Twelve builds take about fifteen minutes. The whole
start is about seventy minutes. The lengths come from the first start: a run of
`index-search` about 63 seconds for 5cc64c8 and 98 for the head, of `write-callers` about
50, of `index-long-text` about 55. Nothing else is added to the start, whatever series 1
shows.

Series 1 judges, in figures: the exact lines of the head against 5cc64c8 inside the gauge,
and the three targets of S14, part 2 still met on this machine, 700,000 searches a second
for an Int64 key, 450,000 for a text of 8 bytes and 200,000 for a text of 100 bytes, one
thread alone. If a loss remains outside the gauge after the three groups, it is reported
with what remains, and A6 and the lean path come back to the planner with that figure; the
start is not extended for them (the planner, 21 November 2026).

This sprint does not wait for the fix of S14R. If that fix has passed its review when the
review round of the fixes of this sprint closes, series 2 rides on this start; if not, the
start runs series 1 alone, about forty minutes, series 2 goes with the next start of the
machine, and the fix of S14R is not merged before its series is taken, since merging an
unmeasured change of the write path is how its loss got in. If the comparison of the
listings finds the cause and it is the price of something S14R had to do, the cost is
recorded with its reason and its figure and reported to the owner at the close as a known
cost of 4 to 8 % on an insert and a delete of an indexed type, and series 2 drops. If the
listings find no cause, the entry is not dropped: it goes to the planner with what was
compared, and stays open with a place.

**What the second start showed, and the plan of the third** (21 November 2026). Series 1:
group 1 gave the one-entity lines 2 to 6 %, at the edge of the gauge; group 2 gave
nothing, so A4 is taken out; group 3 with (B) gave nothing on one entity and lifted the
first item of many from 0.52 to 0.80 to 0.89 of 5cc64c8. What remains against 5cc64c8:
Int64 one entity 0.91, a text of 8 bytes 0.90 to 0.92; the targets of S14, part 2 at the
head, 688,000 against 700,000 and 435,000 against 450,000, missed, with 5cc64c8 itself
about 6 % lower on this machine than on 20 November. Series 2 and 3 chose V2 as the fix of
S14R, merged to `main` as a040f9d and taken into this branch.

The third start is the last for this fall: series 1 alone, `index-search`, three rounds,
over 5cc64c8, the head without A4 (6c73e8d), the head with a6 (`ReadLimits` without the
options and with a byte for its failure: the search 48 bytes, the object 352), the head
with lean (a step of its own for a full-key search that remembers nothing, handing over to
the walk), the head with split (the scratch of a step made apart from the loop of its
rounds, so that both methods tier), and a second build of the head as the gauge; about
thirty-five minutes. The variants are patches outside the repository. The rule that
chooses (the planner, 21 November 2026): a variant is taken only if its lines stand
outside the gauge against the head; split is taken over lean if it brings the one-entity
lines within the gauge of 5cc64c8 or within the gauge of lean, since one form beats two at
the same figure; lean only if it reaches the gauge of 5cc64c8 and split does not; a6 only
if it shows outside the gauge by itself. Judged against 5cc64c8 of the same start, the
absolute targets reported beside it. Whatever is taken gets its tests where it changes
anything observable, one review round and the breaker's probes before it is merged. After
this start nothing more is tried on the machine for this fall: what remains goes to the
owner as one question with the figures.

**What the third start showed** (21 November 2026, the last for this fall). The gauge, two
builds of the head, stood at 0.97 to 1.02. None of the three variants stood outside it
against the head: a6 1.00, lean at most 1.03 on one line and short of the gauge of 5cc64c8
everywhere, split 1.00; none is taken, and the patches stay outside the repository. A
single run on the workstation had promised split a third off the time of a search; the
machine did not confirm it, so a single run on the workstation selects nothing. Group 1
stays: it is in every build that was measured, and its gain in the second start was at the
edge of that start's gauge (F14 of the backlog). What remains against 5cc64c8, one thread
alone, class and by name: Int64 one entity 0.966 and 0.957, a text of 8 bytes 0.899 and
0.911, a text of 100 bytes 0.951 and 0.964, the first of many 0.826 and 0.869; beside a
writer a search of one entity loses nothing (0.99 to 1.06). The targets of S14, part 2 at
the head: Int64 692,000 against 700,000, a text of 8 bytes 426,000 against 450,000, a text
of 100 bytes 258,000 against 200,000; 5cc64c8 itself reads 730,000 and 474,000, against
796,000 and 503,000 on 20 November. Nothing more is tried on the machine for this fall;
the backlog carries the search for what an exact search of one entity does not need (F13),
and the remainder is the owner's question on the Acceptance.

**The record of S15, part 1** (closed on 21 November 2026). Step 1 ended at 7606ef1, its
tests written by the implementer from the brief before its implementation; two of them
were changed after step 1 on the coordinator's decision, each in a commit of its own
(df67c81, the ids inside a run of equal keys a page cuts; d2af035, FindAsync on a thread
of its own). Review: three rounds on the sprint and one on the fixes of the fall. Round 1
found one P1 (an entity added to an answer not checked against `ReadMemoryLimit`), three
P2 (an entity counted by its root block only; a cancellation reported as the limit; a lazy
`Find` keeping what it remembered after its end) and five P3; round 2 no P1 or P2 and
three P3 (the token inside a round, ranked P2 by the coordinator; the line of the cost of
counting; the comparison of F7); round 3 nothing above P4; the round on the fixes of the
fall nothing above P4. The breaker found three failures in its first round, two in its
second and none in its third or on the fixes. The hardest part to follow, by the reviewer:
`EntityTable.Step` with `StandAt`, now commented.

Listed for the owner at the close, under the rule on a draft contract and the limits on
questions:
- `LatticeFindResult.Position`: the position may be later than the last time the search
  looked at the index, so an entity written into the key while the search ran may be
  absent from an answer whose position covers that write.
- `ILatticeIndex.Find`: the search is not a snapshot; an entity written into the key while
  the matches are enumerated may be missing from them.
- `LatticeFindOptions.Limit`: the search returns at most that many, looks once more
  without copying for `HasMore`, and walks no further.
- `ReadMemoryLimit`: what a search counts, every entity of an answer by everything stored
  for it; an answer of one entity whose step saw the end and that passed over no match is
  not counted; the key a lazy search keeps between its first and second step counts.
- Taken by the owner in this session: the sentence of the Acceptance on the sequence a
  search ends at removed with its check (F5 closed, group 7 of the tests dropped);
  `Reverse` as the exact mirror of the index order; the measured price of the walk on the
  exact search, above; the requirement of S17 that a transaction of several effects is
  exposed whole (Q53 its narrower case).
- Dropped after the starts, kept only outside the repository: A4, a6, lean and split.
  Found for later: F13 (S15R), F14 (the architect's review), the rest of N3 (S15R), and F9
  still open.

The order after this sprint: the architect's review after S15, part 1, started beside S15,
part 2, the binding rules; the architect's review after part 2; then S15R; then S16 with
F6 and G2, whose start measures the targets of S14, part 2 again. The outputs of the three starts of the machine are kept
outside the repository by the coordinator.

**The study of S15, part 1, 20 November 2026: the walk.** Read at `main` 22b8ee1, nothing
measured; a figure is quoted from the measured results or estimated by reading. Systems
read, the licence checked first:
- PostgreSQL's nbtree (PostgreSQL licence, commit 65ebed1f): a scan takes the matches of
  a leaf at once, then holds no lock and usually no pin, and moves on by the sibling link;
  "no row twice" comes from its snapshot. One descent serves both directions. The end is
  seen from the high key. Offset and Limit are the executor's, linear.
- LeanStore (MIT, commit fcb64bf3): forwards and backwards a new descent at the fence.
- LevelDB (BSD-3-Clause, commit 29465588): a sequence of 8 bytes in every entry, and an
  iterator passes over the entries later than its start, holding a version meanwhile.
- SQLite (public domain, commit d0867680): a cursor saves its key, seeks again and knows
  whether the next step repeats or skips. An idea only, no adaptation, so
  `THIRD-PARTY-NOTICES.md` gains nothing.
None gives "an entity at most once" without a snapshot or a stamp of a version: a seek that
holds nothing gives "no entry twice", never "no entity twice".

What it found:
- The finding that changes the question of 19 November. Over a range, an entity whose key
  moves ahead of the walk is returned again under its new pair, as the same lifetime and
  the same handle. Q07.3.5 forbids it ("The same key must not be counted once in the
  skipped prefix and returned again after it moves in the index"). A run ordered by the
  primary key does not prevent it, nor does a stamp of creation for a lifetime. Only a
  stamp in every entry, after LevelDB (8 bytes an entry of every index, a leaf of Int64
  keys from 61 entries to 41), or the remembered set of S08 (no byte stored, about 110 ns
  an entity as measured for the enumeration, memory for each pass) gives it.
- The early end of S14, part 2 has a form for a range, as nbtree's high key: the next
  entry of the leaf outside the range, or the fence on the side of travel outside it or
  missing. Its sequence means the same, and it gives "no more" after a Limit without a
  further step.
- One descent with a side — before, after, or strictly after a (key, identity) — gives the
  leaf, the place and the fence in either direction; by reading it costs the exact search
  under 1 %. A step that only reports a remembered end writes the search key for nothing:
  looking first saves about 22 ns of an Int64 search.
- A step that holds nothing works in both directions on the tree as built; it needs a
  bound on the work of one protection period, in rounds, and a probe of the last entry in
  detached form.
- Offset is linear in every system read; counts for each subtree would copy the path at
  every write and count entries, not matches.
- The hardest part: reverse with nulls last in a composite index, a short sequence of
  seeks for each nullable part after the equal ones. The owner's answer of 21 November
  2026, that Reverse is the exact mirror of the index order, removed this piece from the
  sprint.

Decided by the planner on 20 November 2026, on the study:
- The way is the remembered set, for every walk, the exact search included: Q07.3.5
  names it ("A plan may, however, need an O(K) set of processed identities") and places
  it in the memory budget, and point 1 above already keeps it where the seek cannot give
  the guarantee. It stores no byte in any entry, and it is the mechanism the enumeration
  has. The set is made at the first step that returns an entity without seeing the end,
  so a search that finds one entity or none allocates what it does today. Identities
  passed over by Offset are remembered as well. The set counts against `ReadMemoryLimit`
  by its capacity, and over the limit the search fails explicitly; it never drops the
  set. A stamp in every entry stays on record with its figures.
- The tests of group 4 above add the range form and the moving key, forwards and
  backwards, returned and in the skipped prefix; it is the second target of the breaker.
- The read limits count what the search holds: until `FindAsync` returns, its list by the
  capacity of its array and each entity by everything stored for it (Q07.1.1); the lazy
  `Find` counts none. Corrected on 21 November 2026, after the breaker of this sprint:
  everything stored for an entity is its root block and every value kept outside it,
  values in blocks, chunked values with their directory, and a text kept as a shared or a
  Dictionary entry by the bytes of that entry, once for each entity that refers to it,
  since each copy in the answer holds its own text. It is a measure from the stored
  lengths a step already reads, not an account of the heap, and nothing of it is computed
  when no memory limit is set. Every entity added to what the answer holds is checked when
  it is added, and the token is looked at before the limit at every place a limit is
  looked at. The documentation of `ReadMemoryLimit` says what is counted. Before
  the implementer writes group 6, it checks the default of `ReadMemoryLimit` against an
  ordinary `FindAsync`, such as 10,000 entities of the fixture; a default that would refuse
  ordinary load comes to the planner first.
- Taken from the study: the early end for a range, the one descent with a side, the key
  written only when a step needs it, and rounds within one period of the protection, whose
  size the start measures. The pinned allocation figures may be restated for the 16 to 24
  bytes of the read limits, with their composition, after the implementation.
- Not taken: the harvest of a page at a time of nbtree, which needs an unproven protocol
  for a handle held outside the protection (the start measures the share of descents in a
  range of 100); counts for each subtree for Offset.

**A question for step 1 of part 1, before any test of group 7 is written (the planner, 21
November 2026): is the check of the sequence at the early end needed at all?**
The check, in `EntityTable.TrySearch`, decides only which sequence the end reports:
`before`, or a later one after one more step. The entities returned are the same either
way, since an entity whose entry was taken away during the step is missed in both. Its
comment gives the reason as "an end at before would hide an entity that had the key
there", which is a statement about the index at exactly one sequence, a snapshot. The
public contract promises less:
- `LatticeFindResult.Position`: "the node had applied everything up to it when it searched
  the index". `before` is always such a position.
- `ILatticeIndex`: a search "is not a snapshot: entities written while it runs may or may
  not be included".
- Q07.3.5: "there is no common snapshot".
- A session needs a position that is not overstated; `before` understates at worst.
The breaker of S14, part 2 found the same from the other side: no probe through the
public API tells the mutant from the code. So the check may enforce an exactness nobody
promised, at a price: beside a writer, the early end is lost whenever the sequence moves
during a step, and the code keeps a condition no public test can state, which is why F5
exists. The contract step:
1. answers from the code one question: does anything rely on "no entity of the key at
   exactly the reported sequence", as opposed to "the node had applied at least that
   sequence"? That means a public guarantee, a session, a child, or another part of the
   engine. The implementer reads, and the breaker of part 1 is asked the same question
   independently, as its first task;
2. if nothing does: the check goes, in the first structural commit with G4. The end is
   reported at `before` whenever the tree shows it. The comment and
   `docs/design/ordered-index.md` say what the sequence of an answer means, in the words of
   the public contract. F5 closes as removed with its subject, group 7 of the tests is
   dropped, and the range form of the early end is designed without the check from the
   start;
3. if something does: that something is the consumer F5 was waiting for, and group 7 is
   the test of it through that consumer.
Either way the finding goes to the planner before it is acted on.

**Answered by the planner on 21 November 2026, on the two answers.** The implementer and
the breaker, each on its own, found that nothing relies on "no entity of the key at exactly
the reported sequence": no public guarantee, no session, no child, no other part of the
engine. The breaker corrected two premises above. The check is visible through the public
Position, and it is not the same entities either way: with it, an entity that entered the
key ahead of the walk during a step is returned, while the same event behind the walk is
missed in 5.6 to 22 % of its answers. A property that holds on one side of the walk is no
guarantee, and the order of identities among equal keys is not specified. So point 2
holds, in this form, in the first structural commit with G4:
1. The end seen by a step that copies an entity is reported at the sequence of that step,
   not at `before`, and the range form of the early end is designed so from the start.
2. The end of a walk that passed over a deleted or moved hint and found no entity keeps
   its check: there `before` is the only sequence an empty answer reports, and the retry
   keeps a session at or after that write.
3. The comment at the step and `docs/design/ordered-index.md` say what the sequence of an
   answer is, in the words of the contract, and that the Position may be later than the
   search's last look at the index, so an entity written into the key while the search
   ran may be absent from an answer whose Position covers its write. The same sentence,
   in the words of a user, goes into the documentation of `LatticeFindResult.Position` and
   of `Find`: a clarification of what is promised, listed for the owner under the rule on
   a draft contract.
4. The head of `RunEndTests` is corrected, its assertions unchanged. F5 closes as removed
   with its subject. Group 7 of the tests and the breaker's fourth target are dropped; the
   breaker's probes are kept outside the repository as the evidence.
The owner removed the sentence of the Acceptance on the sequence a search ends at on 21
November 2026.

**The reverse order and nulls, the owner, 21 November 2026.**
`IndexTests.Options_choose_direction_and_page` expects the reverse of `ByAgeAndName` with
the entity without an age first, the plain reverse of the index, as the documentation of
`LatticeFindOptions.Reverse` says ("the reverse of the index order"), while this
Acceptance said nulls last in both directions. Q07.3.2 decides the order of a query and
says itself that the reverse of an index is not descending with nulls last. The owner
heard both readings and chose the exact mirror: nulls are last in the index order, and
Reverse is its exact reverse, so a null comes first there. The test and the documentation
of `Reverse` stay as they are. The default of a query, nulls last in both directions with
an override for each key, is the query layer's to build over the two regions when it
exists.

**The internal contract of step 1, read by the planner on 21 November 2026.** No name or
signature changes, but for what the reverse with nulls last needed: the implementer names
what of the four-argument `EntryProbe`, of `nullNext` and of the use of `KeyEdgeEnum` goes,
and commits the contract after it. Two departures from the text above:
- The form of G4: `SetOperations.Next` keeps the place of the search on its stack and puts
  it back if the build of the entity throws, so that a step that throws keeps the time it
  spent and a failure it recorded without the search paying for a staged move. This
  replaces the staged move first accepted, which made a search of one entity larger than
  the condition below allows (the planner, after step 1).
- A search that finds one entity or none grows only by the read limits and one reference.
  What only a walk needs, the remembered set and the detached last entry with its staging,
  sits in one object, `WalkProgress`, made at the same moment as the set, the first step
  that returns an entity without seeing the end; the Offset still to skip and the bytes an
  answer holds live there too. If a figure shows that this second object costs a walk
  more than its bytes would cost an exact search, it comes to the planner with the figure.
  Group 8 pins both: the search of one entity and a walk.
- So an answer of one entity whose step saw the end of the search and that passed over no
  match is not counted against `ReadMemoryLimit`, as a read by key is not; once the search
  remembers anything, an entity returned without seeing the end or a match passed over by
  Offset, everything the answer holds counts, the first entity included. The documentation
  of `ReadMemoryLimit` says it in those terms (worded so after the breaker found the first
  wording too narrow for an Offset), and group 6 states both sides: under a limit smaller
  than one entity, a search that finds exactly one succeeds and one that finds two fails
  with the error of the limit; and with an Offset of 1 over two matches, the one entity
  returned counts.

Two smaller points, from the same reading:
- The lazy `Find` takes no options, so Reverse, Offset and Limit exist only for
  `FindAsync`, while leading parts and ranges exist for both. The remembered set of a lazy
  `Find` lives in its search object between steps and counts against `ReadMemoryLimit`
  there; nothing of the database holds it.
- Group 5 can be stated through the public contract alone: Offset skips without returning,
  and Limit bounds without reading every match first. With a `ReadMemoryLimit` that admits
  the identities of Offset plus Limit but not those of the whole set of matches, the
  limited search succeeds and the unlimited one fails with the error of the limit. No
  counter of steps and no test member is needed. The implementer may find a plainer way;
  this one exists.

**Added by the planner on 17 November 2026, against the engine as S10 left it.** Where a
sentence above or below says otherwise, these replace it. The owner asked for this check
of the briefs of S13, S14 and S15. The sprint begins with a study by the rule of the
process, and the milestone paragraphs it owns are rewritten by the sprint after its
study.

Common to S13, S14 and S15:

- Reads take no gate (16 November). Wherever this brief or the milestone contract says
  "dictionary gate", "index gate", "index protection", "claim root lifetimes" or "under
  the root gate", read: a read enters the protection of reads, and a structure is
  changed only by the thread that holds the lock of the writes; what is removed is
  retired and freed after the readers that could see it have left, and its bytes are
  counted as retired.
- A structure a read walks is one a read can walk without a lock while it is changed. A
  structure that needs a lock around readers is not taken; reads on 16 threads beside a
  writer are measured for every new read path, next to the read by key.
- Every hash is seeded per instance (Q50). Nothing derived travels to a child (S10): a
  child builds its text table and its indexes itself, under its lock of the writes, from
  what it receives.
- The memory of a new structure is counted in the instance budget, the rule of a write
  refused for memory applies to it, and its retired bytes are reported.
- Tokens are honored inside every wait of a step; a close completes only when the
  structure's memory is returned.

For this sprint:

1. **Traversal after the enumeration of S08, part 2:** nothing is held between two steps —
   no node, no cursor of a package, no slot of the protection. A step enters the
   protection, seeks after the last returned (key, identity), returns the next, leaves.
   With entries unique by (key, identity) a traversal under change returns no identity
   twice and every unchanged match without remembering what it visited; the paragraph of
   the milestone contract on a "managed package-node cursor", an "index mutation version"
   and "inclusive reseek skipping visited identities" is rewritten by this sprint after
   the structure of S14. If the structure cannot seek so, the study of S14 says it and
   the remembered set stays.
   20 November 2026: this holds for one run of one key; over a range a key that moves ahead
   of the walk would be returned again, so the remembered set stays (the study of S15,
   part 1).
2. **Limits:** `ReadMemoryLimit` bounds what a traversal remembers (the last key; a set
   only if point 1 fails), `ReadTimeLimit` the engine time of its steps; a cancellation is
   reported before a limit (finding 2 of the outside review).
3. **Binding.** A value is bound once to the type of its field without a box, in the form
   S14 gave `IndexKey`/`IndexBound`; the allowlist is unchanged.
4. **Text order** is the order of `ValueComparer`; "in UTF-8 and in UTF-16 databases"
   stays as the acceptance says.
5. **Measured, added:** a range that returns 100 entities on 1 and 16 threads, alone and
   beside a writer; a page deep in a range (Offset 10,000) next to the first page.

**Track:** full · **Needs:** S14 · **Owners:** E04, E08

- **Goal.** An application searches by the leading fields of an index and a range, in
  either direction and in pages, within a budget, and gets the same answer whichever way
  it supplies a value.
- **Contract.** Exists. Nothing is added for the read budget: a search is subject to
  `ReadMemoryLimit` and `ReadTimeLimit` of the options, as an enumeration is.
- **To read.** [Index access](../decisions/index-access.md);
  [index preparation and traversal](../design/milestone-1-engine-contract.md#index-preparation-and-traversal);
  [Q07.1.1, Q07.3.1–Q07.3.6](../decisions/queries.md).
- **Acceptance.** Every test of `IndexTests` passes with its assertions unchanged, and
  the index sample runs to the end. In addition: for every scalar and key family the
  same value is found by key, by index, through a class and by name; the numeric
  allowlist binds or refuses exactly as specified, including overflow, 42.0 for an
  integer field, NaN and signed zero; nulls are last in the index order, and Reverse is
  its exact reverse, so a null comes first there (the owner, 21 November 2026); enum
  order is numeric; text compares by its field policy in UTF-8 and in UTF-16 databases. A
  traversal under concurrent change returns no identity twice and every unchanged match,
  also inside a group of equal keys. Offset skips matches and Limit bounds them without
  reading all matches first. A search over its budget fails explicitly. An unsupported
  key or bound shape is refused. The sentence on the sequence a search ends at, added on
  20 November 2026, is removed on the owner's word of 21 November 2026, with the check it
  was to hold.
- **Measured.** Reported: search time for exact and range lookups on the fixture's user
  indexes, with and without a writer.
- **Out of scope.** Uniqueness (S16). A query language, automatic choice of an index,
  continuation by key.

**Carried from the study of S14.** If the identity in an entry of an index is the handle
of the root, an entity that is deleted and created again with the same primary key under
the same index key during a pass has a new handle and can be returned a second time,
against "no identity twice". This sprint chooses: a run of equal keys ordered by the
primary key, 8 to 16 bytes more for an entry with an Int64 or a Guid key; lifetimes
created after the pass began are passed over, which needs a stamp of creation for a
lifetime; or a remembered set, as an enumeration has. A step that holds nothing works on
the studied tree: it enters the protection, seeks after the last key and identity in
either direction, and reaches the next leaf by a new descent at the fence. Offset is
linear in the entries passed over; nulls last in both directions is a rule of the walk
(the owner's answer of 21 November 2026 makes Reverse the exact mirror of the index order
instead, above).
The tree needs an order from the comparison service, beside its equality.

**Required of this sprint, decided by the planner on 19 November 2026.** As S14 is
built, the identity in an entry of an index is the handle of the root, so an entity that
is deleted and created again while a search runs may be returned twice; S14 states only
what it guarantees, and its documentation says so once. This sprint ends with: a search
returns an entity at most once, also across a delete and a re-creation — by one of the
three ways named above, chosen here with its figure. The test of three entities under
one key, the first deleted and inserted again during a paused search, exists in the form
that asserts exactly the three and is skipped until this sprint.

**Carried from the architect's review after S14 (19 November 2026).** The walk of this
sprint would land in `EntityTable`, which already has 772 lines. The read limits become one
small type that `EntityPass` and `IndexSearch` both hold, and `OrderedTree` gets one
descent of a reader that yields the leaf, the place and the fence in either direction;
today `TryFindFirst` returns only an identity and descends twice (entry E6 of the
[refactoring backlog](refactoring.md), joined to this sprint). The comparison of one part
of an entry — null, text, fixed — is written three times, twice in `IndexOrder` and once in
`SecondaryIndex.HasKey`; S14, part 2 makes `HasKey` "the order is zero", and S14R writes
the comparison once under the loops before this sprint starts (entry E3).

#### S16 · Unique indexes

**Carried from S09, part 2:** a write whose unique value is held by a prepared,
unpublished write of another entity waits behind it, as a write to the same entity does.

**Carried from the architect's review after S14, part 2 (20 November 2026), for the study
of this sprint:** `ChangePublisher.TryReserve` returns a bool whose false means "refused
for memory", at five call sites. A unique conflict is a third outcome — a refusal that
names the index, or a wait behind a prepared write — decided in the same loop. The study
says where it goes before the code grows a second out parameter. The first structural
commit of this sprint also records the replaced root once, by the reservation (entry G2
of the [refactoring backlog](refactoring.md)), beside F6; this sprint measures the lines it
touches anyway.

**Track:** full · **Needs:** S15 · **Owners:** E04, E08

- **Goal.** A value of a unique index has at most one owner, whatever callers do at the
  same time.
- **Contract.** Exists.
- **To read.** [Unique indexes](../decisions/unique-indexes.md); evidence:
  [unique-index extension](../../src/Prototypes/OperationRetry/README.md#unique-index-extension).
- **Acceptance.** Every test of `UniqueIndexTests` passes with its assertions unchanged
  and the sample runs to the end. In addition: of many concurrent writers of one value
  exactly one succeeds; a key with any null component is exempt and is still found by
  search; equality follows the comparison of every component; a replace that keeps its
  own key does not conflict with itself; a refused write changes nothing and names the
  index; a write through a child is refused by the master. A child that would have to
  apply a violation stops as on a protocol failure and repairs nothing. A child validates its unique indexes while it builds them, before it
  is ready; a violation found there stops the load in the same way.
- **Measured.** Reported: write time for the fixture's users with and without their
  unique index. The replace that keeps every indexed key, index-writes with 0, 1, 3 and 6
  indexes, measured again after the stored fields of an index are resolved once when its
  layout is compiled (S14, part 2, acceptance (a), as the owner accepted it; entry F6). A refused unique key is a withdrawal after the reservation, which gives
  the standing reserve of each index back (S14, part 2), so here a withdrawal may stop
  being rare: this sprint measures it before anyone changes that rule (the planner,
  20 November 2026).
- **Out of scope.** Swaps inside a batch (S17). Activation of a new unique index over
  existing data (S38).

**Added by the planner on 21 November 2026, from the architect's review after S15, part 2.**
Two things the sprint carries beside its own work. The start of the machine has a line of
`index-search` with a text key, for J1 of `refactoring.md`, beside the line of
`write-callers` for H1. And the author of the tests makes one helper at the root of
`Moltaro.Lattice.Tests/Indexes` for "this call is refused as an argument", used by
`BindingSearch.ExpectRefusedAsync` and `IndexPartTypeTests.ExpectRefused`, that holds the
exception to be an `ArgumentException` itself and not a type derived from it: every such
refusal is one today.

#### S17 · Atomic write batch

**Carried from the architect's review after S15, part 1 (21 November 2026), for the study
of this sprint:** the position of an answer, the larger of the sequence of a step and the
stamp of the entity, is written at three places of `EntityTable`, and the end at an
unchanged sequence at three more; `TakeEntry` is the one home of the stamp. What "exposed
whole or not at all" changes for a read has these places to change, all in one file.

**Carried from the review of S11:** a child applies a transaction of several effects in
three steps (every insert installed and every replace or delete marked; the position
published once; the stamps), built in S11 and reasoned, not tested, because no master
sent such a transaction. The test of this sprint: a read on a child that sees the
position of a batch sees all of it, for a batch that inserts, replaces and deletes.

**Track:** full · **Needs:** S16 · **Decisions:** D4 · **Owners:** E03, E08

- **Goal.** Several writes of different entity types take effect together at one
  position, or not at all, on every node.
- **Contract.** Exists. No option is added: a batch has no limit of its own (decision
  O18), and `LatticeLimitEnum` gets no member for a batch.
- **To read.** [Q01 atomic write batch](../decisions/document-model.md#atomic-writebatch-within-one-database);
  [batch save with Upsert, repeated roots](../decisions/mutation-commands.md);
  [write coordinator](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
  [arena protocol, write batch extension](../design/arena-protocol.md#bounded-writebatch-extension);
  evidence: [write batch and recovery](../design/prototype-evidence.md#bounded-writebatch-postgresql-and-application-recovery).
- **Acceptance.** Every test of `WriteBatchTests` passes with its assertions unchanged
  and the sample runs to the end. In addition: a reader running during publication sees
  all of a batch or none of it, on the master and on a child, by key and by index. All
  identities of a batch are claimed together; two batches with overlapping entities do
  not deadlock. Two entities exchange unique values in one batch; a delete frees a value
  another member takes. A failing member refuses the whole batch and reports its number.
  A batch has no limit of its own: one of 10,000 small entities is executed as one, and
  so is one that is larger than an internal
  group. `CommitAsync` delivers its failures by the rule of every asynchronous member
  (S14, part 2, acceptance (c)): a batch committed twice is the caller's mistake, thrown
  at the call; what the database decides comes through the task. A batch that the memory budget cannot hold is refused with `WriteRejected` and
  `Memory` before anything is written, and the database is as it was. Two entities changed by one batch
  share its position and have revisions that do not satisfy a condition on each other. An internal group never splits
  a batch. A child never acknowledges a part. A transaction with several effects is exposed
  whole or not at all, by key, by a search and by an enumeration, on the master that
  publishes it and on a child that applies it, and no answer carries its position before
  all of its effects are visible (the owner, 21 November 2026).
- **Measured.** Reported: how long ordinary reads are held by the publication of a batch
  of 1, 32, 1,000 and 10,000 entities.
- **Out of scope.** Partial updates as batch members (S20). The batch's token (S26).

**Found by the breaker of S15, part 1 on 21 November 2026.** On a child, a transaction of
several effects at one position is exposed a part at a time: the publication
(`ChangePublisher.PublishBeforePosition`) installs the effects one after another, each fully
visible, and moves the clock afterwards, while a read reports the larger of the clock and
the stamp of what it read. A probe that merges 300 insert transactions of a master into
one through a relay found, in 10 of 10 runs, an answer that held the first member but not
the last at the position of the transaction, by key, by a search and by an enumeration.
That breaks "A child must not expose a prefix or advance applied progress past a partial
batch" ([document model](../decisions/document-model.md)). No master on `main` sends such a
transaction and committing a batch is refused, so it is not reachable today; the same loop
publishes a batch on a master.
- The sentence of the Acceptance above states it. The breaker's `BatchPrefixProbeTests`,
  kept outside the repository with its outputs, is the starting point of its test, written
  first.
- A question for the study of this sprint: by what means the effects become visible
  together. The study names how established systems make a group of changes visible at
  one moment to readers that take no lock, and what it costs a read.
- A narrower case, an open question with this sprint as its limit (Q53).
- Several tests of the engine already feed a child a transaction of several effects
  through a relay, among them the refused change of a child and the null upkeep on a
  child. They state other things and stay; when this sprint defines that form, they are
  checked against it.

**Carried from the fixes after S13.** `ReceivedChanges` checks "a transaction names each
key once" by scanning the changes it has prepared and the keys it has skipped for every
effect, which is quadratic in the effects of one transaction, and both lists keep the
capacity of the largest transaction. One effect for a transaction today; a batch needs a
set.

**Decided by the planner on 18 November 2026, on a question of the architect's review.** A
batch is one member of a commit group: one pending write, one position, several changes.
"Consecutive positions from the first sequence" of the storage step stays true, and a
position never falls inside a batch. The master does not get a loop of its own for the
three steps of a publication: it uses the shared type of entry D1 of the
[refactoring backlog](refactoring.md), which takes the changes of one publication as a
span for this reason. A batch is one publication for the drain of retired memory, and it
retired something if any of its changes did. The envelopes of the effects of a
transaction move beside the code that reads them in this sprint (entry D5).

**Required of this sprint, carried from S14.** The memory reserved for index upkeep grows
at most linearly with the number of changes prepared before a publication. As S14, part
2 leaves it, the need grows with the square of the changes prepared and not yet
published; [the reservation](../design/ordered-index.md#the-reservation) gives its
formula and a figure. Candidates include a linear bound, with its proof first. A
reservation that is refused leaves no managed room behind either: the managed room of the
pool is taken in step with what the arena gives, and a pool that grew for writes that are
gone gives it back (the planner, 20 November 2026; entry F3 of the
[refactoring backlog](refactoring.md)). A batch reserves for each index once, for the
counts of the whole batch; today `ChangePublisher.TryReserve` takes one change and
`PreparedWrite` holds one change inline, so a batch would top up each index once for each
change. The linear reserve and this are one design.

### Stage 5. The full type model and access

#### S18 · Owned values, Lists and references

**Added by the planner on 18 November 2026: Epic 1 does not build this, and does not
close the way to it** ([Epic 3](epic-3-after-the-first-milestone.md)). One line is added to the Acceptance: an index
declared on a reference field finds the entities whose reference holds a given key; an
entity whose reference is null is found by a search for null. It uses only S14 and the
reference of this sprint, and is the first half of R2 of Epic 3.

**Carried from S10:** what travels to a child holds no address and no block number; the
child stores received bytes through its own allocation.

**Track:** full · **Needs:** S17, S03 · **Decisions:** P9 · **Owner:** E01

- **Goal.** An entity with nested owned values, ordered Lists and references is stored,
  copied out, replaced and followed by children.
- **Contract.** The declarations of S03 take effect for owned types, `List` and
  `Reference`.
- **To read.** [Collections and strings](../design/milestone-1-engine-contract.md#collections-and-strings);
  [large payloads](../decisions/physical-storage.md); [Q02, Q02.1, Q02.2](../decisions/document-model.md);
  [collection input contract](../decisions/schema-and-types.md).
- **Acceptance.** A List keeps the supplied order and duplicates across the chunk
  boundary; fixed-width elements are packed, complex elements own their values. A
  nullable owned parent may be absent. A reference stores the key of its target and is
  never checked: a missing target is not an error. A complete write replaces the whole
  tree and frees everything the old one owned, shown by the counters. One protection
  covers an entity and all it owns: a reader never sees a tree half replaced. A child
  loads such entities and applies their replacement. An index over a field inside
  an owned value finds the entity by that field; when the owned value is absent the
  component is null, the entity is still found by a search for null, and a unique index
  does not compare it. An ownership audit after churn
  finds every block owned by exactly one live entity. Nothing a caller changes after a
  write returned — in the record or the object, in its collections, its owned values or
  its byte arrays — changes what was written, and two reads of one entity share nothing
  at any depth.
- **Measured.** Reported: bytes retained for the fixture's user and group with their Lists.
- **Out of scope.** Map and Set (S19). Element commands (S20).

**Carried from the architect's review after S13 (18 November 2026).**
`ValueBlocks.LiesInChunks` decides by the length of a descriptor alone, for the figure of
a read; for a collection the length of a descriptor is the number of its elements. This
sprint keeps the descriptors of collections away from that test, or brings the test of
the kind back with its measured cost. The rule of counts is ready for a descriptor in
any block; the walks over the descriptors of a root and the validation of a received
root are tied to the root layout, and entry A4 of the
[refactoring backlog](refactoring.md) prepares them.

**Added by the planner on 21 November 2026, from Q55 of `open-questions.md`.** The sprint
also gives a value written by name the rule of a search: `FieldValues.Normalize` accepts
what `IndexValue.Bind` accepts, by the same code and not a second list, for the value of a
field of a record and of an owned value. The elements, keys and values of a collection
stay as declared, and a primary key keeps its rule. The remarks of the indexer of
`LatticeRecord` and of `LatticeOwnedValue` state the one rule and point to `IndexKey`.
Tests, beside the tests of record values: each pair of the allowlist written by name and
read back as the value of the field's type; each refused pair (a Decimal for a
floating-point field and back, a floating-point value or a Decimal for an integer field,
42.0 included, an integer a Float32 does not hold exactly, a Float64 that does not convert
back) refused with the message of J2; an Enum field refuses an integer its enum type does
not declare, as today. If this needs a sentence of the Acceptance of S18, the coordinator
of S18 asks the owner for it.

#### S19 · Maps and Sets

**Carried from S10:** what travels to a child holds no address and no block number; the
child stores received bytes through its own allocation.

**Track:** full · **Needs:** S18 · **Owners:** E01, E04

- **Goal.** Maps and Sets are stored compactly when small and hashed when large, with
  the accepted input rules, on every node.
- **Contract.** The declarations of S03 take effect for `Map` and `Set`.
- **To read.** [Collections and strings](../design/milestone-1-engine-contract.md#collections-and-strings);
  [Map and Set lookup layout](../decisions/physical-storage.md);
  [Q03.3, collection input contract](../decisions/schema-and-types.md);
  [initial loading, derived state](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading).
- **Acceptance.** Lookup is by the comparison service of the element type, the same that
  indexes use, for every permitted key type. A collection crosses the threshold of 8
  entries upward and of 4 downward and stays equal. A duplicate key in bulk Map input
  refuses the whole write; a Set keeps the first representation of equal members; a
  Map key is never null. A child rebuilds the hash directories itself and never uses a
  hash computed in another process. Malformed transferred entries are refused before
  use. The binary round trip of the arena and of the wire encoding now covers every
  type of the schema.
- **Measured.** Reported: bytes retained for the fixture's role and setting; lookup in a Map of
  8, 9 and 1,000 entries.
- **Out of scope.** Element commands (S20). Shrinking after removals (S23).

**Added by the planner on 21 November 2026.** Where this sprint compares or binds a value
given by a caller, it uses `IndexValue` of `Access/Values`, the holder of a value without
a box that the keys of an index use, and its `Bind`; it does not write a second holder.

#### S20 · Partial updates

**Carried from the study of S11:** how a partial update travels to a child is decided in
the study of this sprint; if as a partial effect, a partial effect for a key not held
during catch-up is recorded and must be resolved by a delete before R.

**Track:** full · **Needs:** S19 · **Owners:** E08, E03 · **Closes:** Q07.3.6

- **Goal.** An application changes named fields and collection elements of one entity
  without submitting the whole entity.
- **Contract.** One explicit update of one entity, by class and by name, alone or as a
  member of a batch: assignments of scalar fields, replacement of a whole collection field,
  and the accepted element commands — List
  append, insert, replace and remove at a position; Map set and remove by key; Set add
  and remove — with an optional expected revision and a session. Its result says
  whether anything changed. The planner writes its types and members before the stage
  begins; the shape needs no reflection per call.
- **To read.** [Q13: field assignments, collection mutations, outcomes, target
  requirements, repeated scalar targets, numeric assignments](../decisions/mutation-commands.md);
  [revision semantics](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
  [Q08.1.1 live payload](../decisions/replication-and-hosting.md).
- **Acceptance.** Commands run in the submitted order against the state at execution;
  a later position sees earlier edits. All of an update is published at once with its
  index changes; a failing command leaves nothing changed. Unmentioned fields keep
  their values. A scalar field named twice refuses the update before commit. An absent
  entity or an absent parent is an error, and so is an element command on a null
  collection; replacing the whole collection initializes a nullable collection whose
  parent exists, and an earlier replacement in the same update is the target of a later
  element command; removing an absent
  Map key or Set member and adding a present Set member are ordinary unchanged results;
  an invalid List position is an error. An assignment is a change even when the value
  is equal; a confirmed no-op keeps the revision and still has a position. A unique
  index is enforced. A child applies the same commands and ends equal to the master.
  The encoded effect of a small update is smaller than the encoded entity. Setting a
  Map value by a key that equals a stored key in another spelling keeps the stored
  spelling; adding a Set member that equals a stored one changes nothing, its spelling
  included. The values of commands bind by the
  same allowlist as index keys: the agreement suite of S15 is extended to Map keys, Set
  members and command values, and passes.
- **Measured.** Reported: encoded size of a one-field update of the fixture's user against its
  complete replacement.
- **Out of scope.** Arithmetic commands, change tracking, edits inside an element, key
  rename: all excluded by decision.

**Added by the planner on 21 November 2026.** Where this sprint compares or binds a value
given by a caller, it uses `IndexValue` of `Access/Values`, the holder of a value without
a box that the keys of an index use, and its `Bind`; it does not write a second holder.

#### S21 · Protected views by name

**Track:** full · **Needs:** S20 · **Decisions:** O6 · **Owners:** E08, E05

- **Goal.** Short synchronous code reads fields and collections of one entity directly
  from the arena, by the names of the schema, without a copy and without a way to misuse
  the memory.
- **Contract.** A read that calls the application with a read-only view of one entity and
  returns the application's result, the fields addressed through handles bound once from
  the schema. A form that passes state, so that no closure is allocated. An asynchronous
  form waits for the session first and then runs the same synchronous code. Text and
  Binary are exposed as read-only spans that cannot leave the call.
- **To read.** [Q06.2, Q06.3](../decisions/memory-and-access.md);
  [reader protection, borrowed reads](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction).
- **Acceptance.** Scalars, nested values, Lists, Maps, Sets and references are readable
  through a view and equal the owned copy. A database call from inside the callback is
  refused. Code that stores a view, returns it or awaits inside the callback does not
  compile, shown by compiler tests. An exception in the callback releases the
  protection. A writer of the same entity waits for the callback and then proceeds;
  writers of other entities do not wait for it. Closing or faulting the database waits
  for open views before memory is freed. A handle bound to another schema is refused.
- **Measured.** Asserted: a view read of scalar fields allocates nothing. Reported: sum
  over a List of 1,000 numbers through a view and through a copy; writer delay caused by
  a callback of a known duration.
- **Out of scope.** Typed views (S22). Writable views. Views that span entities or follow
  references.

#### S22 · Typed protected views

**Track:** full · **Needs:** S21 · **Decisions:** O6 · **Owners:** E08, E05

- **Goal.** The same protected read is available through the application's entity class:
  a view with a property for each field, written for the class when the application is
  built.
- **Contract.** A typed view for an entity class and the read that passes it to the
  application, with the same forms as the read by name. A new analyzer project with a
  source generator that writes the view; it owns the Roslyn dependency at build time
  only. Typed copies keep the accessors compiled once when a class is bound.
- **To read.** [Q06.2, Q06.3](../decisions/memory-and-access.md);
  [reader protection, borrowed reads](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction);
  [schema identity](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration).
- **Acceptance.** The typed view and the view by name return the same values for every
  field type, including nested values and collections. The misuse that does not compile
  for a view by name does not compile for a typed view. The generator reports a class
  that does not match its entity type when the application is built, with the field
  named. A class that inherits its properties from a class in another assembly gets its
  view. No shipped assembly references Roslyn, checked by a test of the built
  references. An application that does not use typed views builds and runs without the
  analyzer.
- **Measured.** Asserted: a typed view read of scalar fields allocates nothing.
  Reported: read time through a typed view and through a view by name.
- **Out of scope.** Generating typed copies or the binding of a class. Leaving the
  generator out is a decision of the owner about the scope, not of this sprint.

#### S23 · Compaction and shrink

**Carried from S13, the first question of the study of this sprint:** a root names a
shared text entry by its address, so the roots that name an entry cannot be found from
the entry and it cannot be moved cheaply. Choose between leaving shared entries in place
(Valkey never moves an object with more than one reference), regions of their own for
entries, or an address table then — with the figure of what entries that are not moved
cost G1 after churn.

**Carried from S09, part 2:** compaction never moves a prepared block and takes its turn
under the lock of the writes or by the rule this sprint states.

**Carried from S14, part 2, deferred here by the owner on 20 November 2026, as the planner
relays:** before its design, the references of a shared text for each entry are decided
with the figures of 20 November 2026 (F8 of the [refactoring backlog](refactoring.md)):
compaction revisits the shared entries anyway.

**Track:** full · **Needs:** S22 · **Owners:** E01, E03 · **Closes:** Q17

- **Goal.** A database that changes for a long time with a constant amount of live data
  keeps a bounded amount of memory.
- **Contract.** None public. Internal: bounded moves of one block per step, collection
  shrink, release of empty segments, movement of shared text through its stable entry.
  The measurement program gains the churn trace of the fixture.
- **To read.** [Reader protection, publication and compaction](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction);
  [Q17, all four parts](../decisions/allocation-and-compaction.md);
  [arena protocol, reclamation and relocation](../design/arena-protocol.md#reclamation-and-relocation);
  [memory accounting](../design/storage-and-maintenance.md#memory-accounting-and-evidence).
- **Acceptance.** Under churn with a constant live set — replace with other sizes,
  delete and reinsert, collection growth and removal — retained memory stops growing and
  the counters equal an independent tally. A reader of an entity being moved, by copy or
  through a view, always sees a coherent value; index entries and text handles stay
  valid across a move. An entity reserved by a write is not moved. Compaction starts on
  a region below half occupancy and moves one bounded block per step. A collection whose
  capacity stays above four times its need for 5 seconds shrinks to at most twice. Empty
  segments beyond the reserve of one are returned.
- **Measured.** Early figure for G5: peak against steady memory of one MemoryOnly node
  during the churn trace. Reported: compaction step time against its 1 ms budget; reader
  p99 with compaction on and off.
- **Out of scope.** Refusing writes at the memory budget (S24). Manual maintenance
  commands, which are outside the milestone.

**Carried from the architect's review after S13 (18 November 2026).** Block kinds of
their own for the directory and the chunks of a text entry are decided here, where
something first has to tell them apart: today all three are of the kind of shared text
and only the length in the slot of the text table says which form an entry has. The
referrers of an entry cannot be found from it, and from S14 an index may be among them.
The slot array and the bucket table of the text table never shrink.

#### S24 · The memory budget

**Carried from the outside review, 18 November 2026:** a child sizes its primary index
from the count its master announces after checking only that this many entities, each at
its least size, fit its memory limit; the bytes of the index itself are not in that
check. When the index is charged to the budget, the check becomes "the index for this
count plus the least root bytes fit", and the refusal stays `Unavailable` at the open.

**Carried from the review of S11:** decide whether the budget report shows the memory
the delivery tail retains (whole blocks, the spare included) as its own figure, next to
`PendingDeliveryBytes`.

**Carried from the review of S10:** a child sizes its index from the counts its master
announces, bounded only by its memory limit, and each accepted connection holds about
200 KiB of managed buffers before its Hello; neither is in the budget.

**Track:** full · **Needs:** S23 · **Owners:** E03, E08

- **Goal.** An instance never takes more memory than its budget: a write that would
  exceed it is refused before it has any effect, and a child that reaches it stops
  applying and keeps its reads.
- **Contract.** The memory budget declared in S09 is enforced for the whole instance.
  `WriteRejected` names memory as the limit.
- **To read.** [Initial bounded settings](../design/milestone-1-engine-contract.md#initial-bounded-settings);
  [Q14 master memory pressure, child memory pressure](../decisions/write-admission.md);
  [index preparation, capacity reservation](../design/milestone-1-engine-contract.md#index-preparation-and-traversal).
- **Acceptance.** A write that cannot reserve its memory is refused before commit and
  does not wait for compaction; reads continue, and after memory is freed writes are
  accepted again. The budget counts the arena with its reserve, both tables of a growing
  primary index, secondary index nodes, the text table, the native scratch of reads if any exists, and the pages of root entries
  (prepared capacity is counted in the arena since S09, and a waiting write holds nothing in the engine); S26 adds the retained outcomes of a MemoryOnly master. A reservation that fails releases everything it took,
  shown by the counters. A child over its budget pauses application, keeps its reads and
  resumes when memory is free again; reloading after a long pause is S34. Nothing queued
  runs after its cancellation.
- **Measured.** Reported: peak against steady memory of one MemoryOnly node held at its
  budget under the churn trace; the share of the budget each counted part takes on the
  reference fixture. Also a write that alternates with indexed writes at the limit: the
  top-up of the standing reserve of S14, part 2 takes back what the give-back before a
  refusal freed, so such a write may go through the whole resolution every time; this is
  read, not measured, and it is measured here before anyone changes that rule (the
  planner, 20 November 2026).
- **Out of scope.** Budgets of the journal and of delivery history (S31, S33).

**Carried from the architect's review after S13 (18 November 2026).** Whether a text
entry is allocated live from the start, instead of prepared and then published, is
decided here with the charge of the text table to the budget: it changes what
`PreparedBytes` counts for a write that is later withdrawn, which is accounting and not
structure, so the refactoring sprint after S13 did not take it.

#### S25 · Reference fixture, managed reference and the memory gate

**Track:** full, verification · **Needs:** S24 · **Decisions:** O11, D6 · **Closes:** G1

- **Goal.** The reason the project exists is measured: the engine keeps the reference
  data in at most 75% of the memory of an optimized managed representation.
- **Contract.** None. The measurement program of S05 gains the read mix and the write
  trace, the optimized managed reference, the manifest of the single-node runs, the
  check of the machine before a run, the start of a node as a container with limits, and
  the report.
- **To read.** [Reproducible first fixture](../design/milestone-1-engine-contract.md#reproducible-first-fixture);
  [retained data memory target, ordinary workload](../design/performance-requirements.md);
  [accepted measurement conditions](../design/performance-requirements.md#accepted-measurement-conditions).
- **Acceptance.** The fixture is the one S05 generates. The managed reference holds the same records, keys, text,
  collections and declared indexes with the same comparers, shares dictionary text,
  uses the selected Arc containers where they fit, and is reviewed as an honest
  optimized baseline before any number is read. Both sides count everything retained:
  data, indexes, text tables, control and gate metadata, unused capacity, the reserve.
  The comparison is made after load and after the churn trace, at identical logical
  states. The manifest of the single-node runs fixes the values of decision O11 that
  apply to one node and the limits of its container. The check of the machine refuses a
  run on a busy processor or without enough free memory. The owner approves the manifest
  before the gate run.
- **Measured.** **G1.** Early figure for G2: p50, p95, p99 and maximum of the read mix
  on one MemoryOnly node at 10,000 reads/s with the write trace at 200 changes/s,
  identifier and exact-index reads, range reads and view reads reported separately. The
  scaling of reads with threads is reported with the read mix; if it bounds a gate or the
  reads of the reference deployment, it is reported to the owner with what in the
  synchronization bounds it, before any change (Q42 of the open questions): it is now the
  check of the read mix against what S08 reached.
- **Out of scope.** Children, PostgreSQL, the burst. No number from a prototype.

### Stage 6. Durability

**The word for the durable store**, a note of the planner of 21 November 2026 until the
contracts of the stage are written: the engine says "persistence" for what makes a write
durable and "storage" for the form of the entities in memory, while the public contract
says "storage" for the durable store of a provider: `LatticeDatabaseOptions` with its
`StorageConnectionString`, and the documentation of `ILatticeSet`,
`LatticeDatabaseStateEnum`, `LatticeErrorEnum` and `LatticePersistenceModeEnum`. The
contract of the stage, before S26, changes it and lists it, or keeps it with its reason.

#### S26 · Operation identity and safe retry

**Track:** full · **Needs:** S25 · **Decisions:** O12 · **Owners:** E08, E06

- **Goal.** A caller that lost the answer to a write can learn its outcome or submit it
  again within the window, and the write never takes effect twice.
- **Contract.** An opaque operation identity created by the database before submission
  and kept by the caller; a way to submit any write, including a batch, with it; a
  status query that answers pending, committed with the original result and position,
  rejected with the original error, expired or unknown; errors for a changed request
  under a used identity and for an expired one; the option for the window, 15 minutes
  by default. By the owner's decision O20 there is no option for the memory of retained
  outcomes. The session and the
  identity travel in one value, so that the write members do not multiply; the planner
  writes the types and members before the stage begins.
- **To read.** [Operation retry protocol](../design/operation-retry-protocol.md);
  [operation identity and retained results](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1);
  [session consistency, three identities](../decisions/session-consistency.md);
  evidence: [operation admission and retry](../design/prototype-evidence.md#bounded-operation-admission-and-retry).
- **Acceptance.** In MemoryOnly, on a master and through a child: a repeat of a committed
  write returns the original result and position and changes nothing; a repeat of a
  rejected write returns the stored rejection without evaluating it again; a repeated
  delete of an absent key does not delete an entity created later; two simultaneous
  submissions of one identity run once; a changed request under a used identity is
  refused; an expired identity is refused and never becomes a new write; a repeat does
  not extend the window; an identity of another history is refused. A repeat through a
  child still waits for that child to apply the original position. A session never moves
  backward on a replayed result. The fingerprint covers schema version, operations,
  identities, ordered commands, values and conditions. The retained outcomes have no
  bound of their own: no unexpired outcome is dropped, they are counted in the memory
  budget, and a write is refused only when the budget cannot hold it. The retained outcomes of a MemoryOnly master are counted in the memory budget of
  S24. A change of the configured window applies to operations admitted after
  it and never shortens the window of one admitted before. A clock that moves backward
  does not revive an expired identity. An identity that names a generation closed for
  admission is refused as a new operation.
- **Measured.** Reported: memory per retained outcome; write time with and without an
  identity.
- **Out of scope.** Durable receipts (S28) and their retirement (S31). Automatic retry by
  the library, which is excluded by decision.

#### S27 · PostgreSQL provider and Durable open

**Carried from S12:** with Durable, say whether a master can be behind a position a
session holds, and what a forwarded write that names such a position then does.

**Carried from S09, part 2:** the durable storage step is a log writer behind
`CommitAsync`: the call hands the group over and returns; the commit to PostgreSQL runs
behind its task. `CommitAsync` for the next group may be called on the thread that
completes the task of the previous one, from inside that completion. While a storage
step is busy, admitting a write walks the list of the writes that wait
(`WriteCoordinator.FindLastOfEntity`), and withdrawing one walks it too (`Unlink`):
measured by two reviewers at 0.6 to 0.8 ms an admission with 40,000 writes waiting, and
19 s to cancel 40,000. This sprint replaces the walk by a lookup by entity, paid only
when writes wait, and measures admission with a backlog. It also gives the input of Q52
its durable figures.

**Carried from S09:** what a failed storage step means is stated by this sprint: a
rejection the storage knows to be definite releases the group and leaves the instance
Ready; only an outcome it cannot know is the case of S09, `IndeterminateOutcome` for the
group and a faulted instance. The storage step has a timeout, so that a close, which
waits for a step in progress, is finite. The figures of the write path of S09 — writes
a second of several callers below those of one — are judged again here, as writes a
second per storage commit.

**Track:** full · **Needs:** S26 · **Decisions:** O1, O4, O9 · **Owner:** E06

- **Goal.** A master opens in Durable mode against PostgreSQL: it creates the database
  the first time and finds the same identity, history and schema every time after.
- **Contract.** New assembly `Moltaro.Lattice.PostgreSql` that owns `Npgsql`. The options
  take the storage as an object from that package in place of `StorageConnectionString`,
  which is removed with its tests and documentation. The provider boundary between the
  engine and the package is internal. The PostgreSQL lane and its CI job are added, and
  the job is added to the required checks in `.github/rulesets/main.json`. New project
  `Moltaro.Lattice.TestNode`, a console program that hosts one database instance so that
  a test can start it and kill it.
- **To read.** [Independent base, journal and recovery](../design/milestone-1-engine-contract.md#independent-base-journal-and-recovery);
  [Q09.5](../decisions/persistence-modes.md), [Q09.6](../decisions/persistence-backends.md);
  [persistent metadata](../design/operation-retry-protocol.md#persistent-metadata);
  [schema identity](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration).
- **Acceptance.** The tests of Durable opening written in this sprint pass in the
  PostgreSQL lane, and the test that asserted the refusal of Durable mode is removed.
  Until S28 every write on a Durable master is refused with `NotSupportedException` and
  its tests are skipped: a Durable master never acknowledges a write it has not made
  durable. An empty storage is initialized with the given schema, a database identity and a
  history. A reopen keeps identity and history and needs no schema; a schema that
  differs from the stored catalog is refused and nothing is migrated. A child given a
  storage is refused. A MemoryOnly instance loads no PostgreSQL code, checked by its
  loaded assemblies. An unreachable server at open fails within its deadline with
  `Unavailable`. The provider's tables live in a schema of their own and hold nothing
  but what the specification lists. A master takes the leadership of its store when it
  opens. A second master that opens a store whose master is alive fails at once with an
  error that names the reason, and the first master is not disturbed. After the first
  master closes, or after its process is killed, another master opens the same store. The
  product documentation states what remains for the deployment: the leadership
  connection is a direct session, not one through a proxy that pools per transaction;
  and how fast PostgreSQL notices a dead session is a setting of the server. It also
  states the consequence for a rollout: a new master is refused while the old one lives,
  so a master is stopped before its replacement starts.
- **Measured.** Reported: time to open an empty and an initialized storage.
- **Out of scope.** Writing data durably (S28–S29). The saved base (S30–S31).

**Carried from S14.** A field that is part of an index stores a value longer than 64
bytes as a text entry, so adding or dropping an index changes the layout of the fields it
names. From the sprint that stores roots beyond the life of a process on, that is a
change of the stored form and belongs to what a migration converts.

**Also carried from S14:** the memory reserved for index upkeep grows with the changes
prepared and not yet published; its bound is a requirement of S17.

#### S28 · Durable commit

**Carried from S14, a question and not a decision:** whether a search may see inside a
group of writes being published.

**Track:** full · **Needs:** S27 · **Decisions:** O9 · **Owners:** E06, E03

- **Goal.** A write on a Durable master succeeds only after its admission and its
  effects are committed in PostgreSQL and published in memory, and after a reopen the
  database is what was acknowledged.
- **Contract.** No new members.
- **To read.** [Two durable transitions, repeat and status](../design/operation-retry-protocol.md);
  [independent base, journal and recovery](../design/milestone-1-engine-contract.md#independent-base-journal-and-recovery);
  [Q09.1](../decisions/write-acknowledgment.md);
  [Q09.2, write batch recovery boundaries](../decisions/checkpoint-and-journal.md).
- **Acceptance.** Admission is committed before any effect is dispatched. Effects,
  position and the committed receipt are one transaction; positions come from the
  provider's control row. Independent writes share a commit and keep their own positions
  and outcomes; a batch is one journal transaction. Memory is published after the
  commit, in order. A reopen replays the journal and equals the acknowledged state,
  including outcomes: a repeat of a committed write after reopen returns the original
  result. A semantic rejection is stored and returned on repeat without a data change.
  No lock of the arena is held during SQL. Every commit checks the leadership number. Until S29
  any failure of the storage during a commit faults the instance: nothing is acknowledged
  and nothing is published.
- **Measured.** Early figure for G3 on the master alone, local PostgreSQL: p50, p95, p99
  of single small writes at 200 changes/s with 16 callers, admission included; SQL
  commits per second and operations per commit.
- **Out of scope.** An unreachable server, an uncertain commit and the loss of
  leadership (S29). The saved base: here recovery reads the journal from its start,
  which is acceptable only until S30. Kill tests at every boundary (S32).

#### S29 · Storage failures and the loss of leadership

**Track:** full · **Needs:** S28 · **Decisions:** O9 · **Owners:** E06, E03 · **Closes:** R25

- **Goal.** When PostgreSQL is slow, unreachable or has given the store to another
  master, a write is never lost, never acknowledged early and never committed by a
  master that no longer holds the store.
- **Contract.** The storage recovery wait (30 seconds), the deadline of one attempt
  (5 seconds) and the interval of the leadership check (5 seconds) are options of the
  storage. `IndeterminateOutcome` is reported for a commit whose result is not known.
- **To read.** [Diagnostics, backup and deployment boundary, one store one master](../design/milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary);
  [Q14 pending writes during storage unavailability](../decisions/write-admission.md);
  [failure outcomes](../decisions/write-acknowledgment.md);
  [capacity and failure invariants](../design/operation-retry-protocol.md#capacity-and-failure-invariants);
  [Q09.2 instance recovery after committed publication failure](../decisions/checkpoint-and-journal.md).
- **Acceptance.** While the server is unreachable, writes wait within the bound and are
  then refused or reported uncertain, never lost and never acknowledged early; reads
  continue. A failed commit publishes nothing. A commit whose result is unknown is
  reported as `IndeterminateOutcome` and is resolved by its identity after the server
  returns. A master whose leadership session is ended from outside, while its process
  lives, commits no write after another master took the store; with nobody writing to
  it, it notices within the check interval. When the store is free again and the leadership number is
  still its own, it takes the leadership back within the storage recovery wait and goes
  on. When another master holds the store, or took it and gave it up in the meantime so
  that the number is no longer its own, it faults and stops answering: a master never
  continues over writes it has not seen. Nothing queued runs after its
  cancellation.
- **Measured.** Reported: time from the return of the server to the first completed
  write.
- **Out of scope.** Killing processes at every boundary and restarting the server under
  load (S32). The background transactions (S30, S31).

#### S30 · Saved base and background application

**Track:** full · **Needs:** S29 · **Decisions:** O9 · **Owner:** E06

- **Goal.** A background worker folds the journal into an independent saved base, and a
  reopen loads the base and replays only the tail.
- **Contract.** None public. Internal: the encoded root in its first version, saved
  units of exactly 256 bytes, the root directory, the base cursor in its own row.
- **To read.** [Independent base, journal and recovery](../design/milestone-1-engine-contract.md#independent-base-journal-and-recovery);
  [background application, cadence, saved-unit baseline](../decisions/checkpoint-and-journal.md);
  [independent persisted base](../decisions/physical-storage.md);
  [unique indexes, initial load and recovery](../decisions/unique-indexes.md#master-validation-and-publication);
  evidence: [write batch with saved units](../design/prototype-evidence.md#writebatch-with-256-byte-saved-units-and-background-application).
- **Acceptance.** Changed units and the cursor commit in one transaction; a rollback
  leaves both as they were. The applier reads only complete transactions up to the
  published boundary, applies commands in order and never reduces collection commands
  to a last assignment. Only changed units are written; obsolete ones are removed. The
  applier's working set stays within 32 MiB with a batch of the largest entities. A
  reopen from base and tail equals the acknowledged state for every type of the schema,
  with indexes and text rebuilt. Unique indexes are validated while the indexes are
  rebuilt; a violation found in recovered data fails the open, and nothing is repaired
  or dropped. The encoded root round-trips every type and carries no native address. A
  saved root with a wrong digest, length, version or schema is refused before it is
  decoded, and the open fails as a whole. The applier checks the leadership number in
  the transaction that makes its change: after another master took the store, it commits
  nothing.
- **Measured.** Reported: unapplied changes and their oldest age during the write trace
  at 200 changes/s; master reopen time from base and tail for the reference fixture.
- **Out of scope.** Removing applied journal, retiring receipts and the limits of the
  journal (S31): until then the store only grows, and `README.md` lists it among what is not implemented
  yet. Killing processes (S32). Restore (S36).
- **The version of the case table**, a note of the planner of 15 November 2026 until the
  contracts of the stage are written: the saved base records the case table version it
  was written under; a base of another version is not opened as it is — whether it is
  refused or its case-insensitive keys and indexes are validated and rebuilt is decided
  with the contract of S30 and of S37 (upgrade).

#### S31 · Journal limits, cleanup and receipt retirement

**Track:** full · **Needs:** S30 · **Decisions:** D2, O9 · **Owner:** E06 · **Closes:** R26

- **Goal.** What the store keeps shrinks back for as long as the database
  runs: applied journal is removed, outcomes are retired when their time has passed, and
  an application that sets a hard limit has its writes stopped at it.
- **Contract.** Options of the storage for the journal trigger (64 MiB or 5 seconds) and,
  unset by default, for a hard limit of the journal and for a budget of the receipts
  (the owner's decision O20).
- **To read.** [Independent base, journal and recovery](../design/milestone-1-engine-contract.md#independent-base-journal-and-recovery);
  [transient background application failure, triggers and hard limit](../decisions/checkpoint-and-journal.md);
  [expiry, cleanup and crash ordering](../design/operation-retry-protocol.md#expiry-cleanup-and-crash-ordering).
- **Acceptance.** Cleanup removes only what the committed base cursor covers. Receipts
  are retained on their own schedule, independent of the journal, and retired in
  generation order; the retired boundary is durable before or with the deletion. The
  retirement of receipts never turns an old identity into a new write: an identity of a
  retired generation is refused. While the applier fails, foreground writes continue;
  with a hard limit set they continue until it and are then refused before commit; when
  the applier recovers it catches up and writes resume. A receipt budget, when one is
  set and full, refuses new admissions and drops no
  unexpired receipt. Cleanup and retirement check the leadership number in the
  transaction that makes their change. After a run of writes that stops, the journal and
  the receipts shrink back without a restart.
- **Measured.** Reported: size of the retained journal and of the receipts over the write
  trace at 200 changes/s and after it stops.
- **Out of scope.** Killing processes (S32). Shrinking the files of PostgreSQL, which is
  maintenance of the server.

#### S32 · Recovery under failure

**Track:** full, verification · **Needs:** S31 · **Closes:** Q09.2, R27

- **Goal.** No interruption, at any boundary of the durable path, loses an acknowledged
  write, applies one twice or serves a partial state.
- **Contract.** None. Fault points in the engine that only the tests can arm,
  also inside the node program of S27, under exception X3.
- **To read.** [Verification handoff](../design/milestone-1-engine-contract.md#verification-handoff-and-closure);
  [recovery and failure boundaries](../design/engine-foundation.md#recovery-and-failure-boundaries);
  [failure outcomes](../decisions/write-acknowledgment.md);
  evidence and its limits: [write batch and recovery](../design/prototype-evidence.md#bounded-writebatch-postgresql-and-application-recovery).
- **Acceptance.** A master process is killed, in turn, before and after admission
  commits, before and after the effects commit, during publication, in the middle of a
  batch, during an applier transaction, during cleanup and during receipt retirement. It is also killed during its own reopen — while it loads the base,
  replays the tail and rebuilds the indexes, after it took the leadership — and the next
  reopen reaches the same state.
  After each kill a reopen equals exactly the acknowledged writes plus those whose
  callers got no answer and can learn the outcome by their identity. A batch is present
  whole or absent. A PostgreSQL container owned by the test is restarted under load; the
  master waits, recovers or faults as specified, and nothing acknowledged is missing.
  Truncated, altered and schema-incompatible journal and base data are refused. Storage
  budgets — journal hard limit, receipt budget, recovery wait — are each exhausted in a
  test. An ownership and reference-count audit passes after every recovery.
- **Measured.** Reported: reopen time after a kill at each boundary, for the reference
  fixture.
- **Out of scope.** Loss of the storage medium, which backup and restore cover (S36).
  Faults of the replication network (S35).

### Stage 7. Replication to release level

#### S33 · Bounded delivery and slow children

**Added by the planner on 18 November 2026: Epic 1 does not build this, and does not
close the way to it** ([Epic 3](epic-3-after-the-first-milestone.md)). On every node there stays one place where a
transaction becomes visible, at which its effects are known as (entity type, key, written
or deleted, sequence): on a master the publishing hold, on a child its application; a
child that reloads knows the moment after which it cannot say what changed. Nothing is
called from there in Epic 1 and no code of an application ever runs under the lock of the
writes; the note only forbids a design that loses that list or that moment.

**Carried from S12:** a forwarded write of 32 MiB delays small forwarded writes of the
same child by up to 214 to 349 ms and an Applied by up to 98 ms, because a child has one
connection; judged here with the hold of 30 ms of S11 and Q52. The constant of 5 s of a
master's close (X6) becomes the option that bounds a send to a child that does not read.
`ConnectionEndTests` asserts that wait as a range of 4.5 s to 9 s around the constant and
so depends on the load of the machine; with the option it is given a short one and becomes
deterministic (planner, 19 November 2026).
A call withdrawn by its token stays in the queue of the child's sender, with its encoded
request, until the sender takes it out; judged here with the allocation of a forwarded
write. Not tested, because nothing can fault the publication of a child: a faulted child ends a
forwarded write that holds its reply in `SynchronizationTimeout` with its committed
position; the test comes with the fault point of a child's publication.

**Carried from S11:** the bound of the tail is decided here, with the figures of S11 in
"Measured results". Not built in S11 and judged here: a pool of tail memory and encoding
outside the lock of the writes (it needs the published root kept from being freed until
it is copied). A write of 32 MiB holds the lock about 30 ms with a child following,
against 2.5 ms with none, and small writes wait behind it; the planner brings that to the
owner with Q52. X6 now names the waits of a stream: the sends end here, the waits for a
frame in S34. The acknowledgment of a child under a stream that never pauses has no
deterministic test; a `RootTable.Add` that throws while a child applies has none either
(the root table has no fault point).

**Carried from the review of S10:** the two waits of X6 end here: the master's send to a
child that does not read, and its wait for the end of a loaded child's connection (a
child lost without a reset is not noticed). The milestone row "concurrent captures, up
to 10" is not built before this sprint.

**Carried from S10:** `OpenTimeout` bounds the time without progress of a load; a child
that waits for a free capture is sent a frame that counts as progress, or its wait is
stated otherwise here.

**Track:** full · **Needs:** S32 · **Decisions:** P3 · **Owner:** E07

- **Goal.** A child that is slow or gone costs the master a bounded amount of memory,
  and many children that start together are served within a limit.
- **Contract.** Options for the pending delivery memory (the smaller of 32 MiB and 20%
  of the budget), the deadline of a slow peer (30 seconds) and the number of captures
  that run at once (10). The default of the pending delivery memory is a share of the
  instance budget and is taken from the limit in force at open, the given `MemoryLimit`
  or the computed default.
- **To read.** [Replication transport and initial loading](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading);
  [Q08.1.3 pending delivery in memory](../decisions/replication-and-hosting.md);
  [Q14 child memory pressure](../decisions/write-admission.md).
- **Acceptance.** One delivery history serves all children, and a transaction leaves it
  when every participating child applied it. A child that holds it beyond its bytes or
  its deadline loses its claim and must reload; the other children are not disturbed.
  The master's memory for delivery stays within the limit whatever a child does. With
  the default, the pending delivery memory follows the limit in force of the instance.
  Children that start together load in parallel; with the limit of captures set below
  their number, a child beyond the limit waits before its capture is registered, becomes
  ready after a turn is free, and fails with `Unavailable` when its open timeout passes
  first. A capture that cannot reserve its memory within the budget waits in the same
  way. Until S34 a
  child that lost its claim keeps serving its local reads and does not come back by
  itself.
- **Measured.** Reported: master memory held for a stalled child up to its eviction;
  the master's read p99 while 1, 5 and 10 children load together.
- **Out of scope.** What the evicted or reconnecting child does next (S34). PostgreSQL
  as a source for a child's catch-up, excluded by decision.

**Carried from the failures seen under load (20 November 2026).** `ChildCloseTests`, its
first test, depends on the receive buffer of the engine's own child, with a margin of two
over the largest window of Windows (16 MiB). S33 gives a way to state it without resting
on that buffer: the bound of the tail built here, or the option of the close.

#### S34 · Reconnect, reload and a restarted master

**Added by the planner on 18 November 2026: Epic 1 does not build this, and does not
close the way to it** ([Epic 3](epic-3-after-the-first-milestone.md)). On every node there stays one place where a
transaction becomes visible, at which its effects are known as (entity type, key, written
or deleted, sequence): on a master the publishing hold, on a child its application; a
child that reloads knows the moment after which it cannot say what changed. Nothing is
called from there in Epic 1 and no code of an application ever runs under the lock of the
writes; the note only forbids a design that loses that list or that moment.

**Carried from S12:** a forwarded write that waits for its reply waits without an end
when its master vanishes without closing the connection (X6); the detection of a lost
master ends it. Decide whether a wait for a position, and a forwarded write that holds
its reply, continue across a reconnection instead of ending at once; the sentence of the
contract "On a child that has lost its master or has faulted the wait ends at once" and
the remark "Not implemented yet" on `Unavailable` (no reconnection) are rewritten here.

**Carried from S12:** a receive deadline that fires while bytes wait in the socket reports
that nothing arrived, which is false: they arrived and the process had not handled them
(found at S12 on a starved thread pool: deadline armed at 3916 ms, 128 KiB in the socket
at 4237 ms, callback at 8231 ms). Decide with the lost-peer rule whether a deadline that
finds bytes waiting keeps reading, and how far a starved process may then run past its
timeout. Until then the message of that refusal is not evidence about the master or the
network.

**Carried from S11:** a child that stopped following — a malformed transaction, a change
its arena cannot hold, the end of its connection — stays Ready at its position and does
not follow again; a master that vanishes without closing the connection leaves the child
waiting for a frame without a deadline (X6). Both end here.

**Carried from the review of S10:** the pauses between a child's attempts and a master's accepts (100 ms,
doubling, at most 5 s) are constants (X6) and become options here. The listener resolves
a host name synchronously, binds its first address only and reports a wildcard address
in `ListenUri` as it was given; decide each here.

**Track:** full · **Needs:** S33 · **Decisions:** D7 · **Owner:** E07

- **Goal.** A child that was disconnected, evicted or restarted, or whose master
  restarted, comes back correct: resumed from history when possible, reloaded otherwise.
- **Contract.** Options for the readiness wait during a reload (30 seconds), the number
  and the retained memory of the requests that may wait during a reload, and the
  shortest and longest pause between reconnection attempts (100 ms and 5 seconds).
- **To read.** [Replication transport and initial loading](../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading);
  [Q08.1.2, Q08.1.3 with one arena and bounded read waiting, MemoryOnly children](../decisions/replication-and-hosting.md);
  [explicit local position wait](../decisions/session-consistency.md#explicit-local-position-wait).
- **Acceptance.** A reconnecting child resumes when the history still has what it lacks,
  and otherwise reloads; a restarted child starts empty. A reload closes readiness, lets
  protected reads finish, discards the old arena and loads one new arena — never two.
  Requests during a reload wait within the bound and then fail; a request beyond the
  allowed number or memory of waiting requests fails at once. A disconnected child keeps
  answering reads without a session; a read with a position it has not reached waits and
  fails, and never answers stale. A wait that succeeded does not excuse a read after a
  reload: the read waits again or fails. After a restart of the master with the same
  history children resume or reload; a master with a new history is refused until they
  reload. Reconnection backs off between its two pauses.
- **Measured.** Reported: time to resume and time to reload on the reference fixture,
  against the 30-second wait.
- **Out of scope.** Election or promotion, which are not part of the design.

**Carried from the fixes after S13.** One receive deadline covers a whole frame and is
not renewed by partial reads, so a load that keeps arriving below about 17 kbit/s at the
default, or under a small `OpenTimeout`, is ended. The deadline becomes a time without
bytes, decided here together with the case of bytes that wait. Until then `OpenTimeout`
says that a part is a frame of at most 64 KiB and its header and has to arrive whole
within the timeout.

#### S35 · Cluster verification

**Track:** full, verification · **Needs:** S34 · **Decisions:** P3 · **Closes:** Q08.1.3, Q13, R30

- **Goal.** The guarantees hold between real processes, with real connection faults, in
  the deployment profile.
- **Contract.** None. New project `Moltaro.Lattice.Cluster.Tests`, which starts the node
  program of S27, and a way to cut, delay, duplicate and truncate the connection of one
  child, under exception X3. The cluster lane and its CI job are added, and the job is
  added to the required checks in `.github/rulesets/main.json`.
- **To read.** [Verification handoff](../design/milestone-1-engine-contract.md#verification-handoff-and-closure);
  [session consistency, costs and implementation boundary](../decisions/session-consistency.md#costs-and-implementation-boundary);
  [repeat and status behavior](../design/operation-retry-protocol.md#repeat-and-status-behavior).
- **Acceptance.** With one Durable master and 3 children on every run, and with 5
  and 10 children run by the coordinator on the reference machine before the sprint
  closes: a child joins while single writes, partial updates and batches continue, and
  equals the master at its readiness position. Ten children that start together, under
  the same writes, all become ready within their open timeout, each equal to the master
  at its own readiness position, and the master's memory stays within its budget. Dropped, duplicated, delayed and
  truncated frames never produce a gap, a repeat or a partial batch on a child. A
  connection cut after a forwarded write was dispatched leaves the caller with an
  unknown outcome that its identity resolves; a repeat through another child applies
  nothing twice. Typed, by-name and forwarded submission of the same write produce the
  same journal effect and the same result. A request that carries a position from one
  child is honored by another. A child killed and restarted reloads. The master killed
  and reopened keeps its history. A master that comes back at the
  same address with a new history — an empty store opened in its place — is refused by
  the children until they reload, and then they follow it. No buffer exceeds its bound, shown by the counters of
  every process.
- **Measured.** Early figure for G4: readiness of one new child with the reference
  fixture and 3 children reading. Correctness of G7 with 5 and with 10 children.
  Reported: time until all are ready when 5 and when 10 children start together.
- **Out of scope.** Load gates (S40, S41). Faults of the physical server.

#### S36 · Restore and a new history

**Track:** full · **Needs:** S35 · **Decisions:** O9, P6 · **Owners:** E06, E07 · **Closes:** Q09.4, R31

- **Goal.** A database restored by infrastructure from a PostgreSQL backup is validated
  offline, activated deliberately with a new history and then serves; nothing from
  before the restore is honored by accident.
- **Contract.** An explicit activation of restored storage in the provider package,
  separate from an ordinary open. The product documentation gets the restore procedure,
  which says that a restored copy must be activated before it is opened; see P6.
- **To read.** [Backup and restore](../decisions/backup-and-restore.md);
  [diagnostics, backup and deployment boundary](../design/milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary);
  [accepted backup and restore control](../design/storage-and-maintenance.md#accepted-backup-and-restore-control).
- **Acceptance.** A backup taken with PostgreSQL's own tools while writes run is restored
  into a separate target; activation validates it, gives it a new history, loads one
  arena and makes it ready with the data of the backup's boundary. A damaged or
  incompatible target fails validation and is not activated; the original stays
  untouched. Positions, revisions and operation identities of the old history are
  refused on the new one. Children of the old history reload completely. A different
  connection string alone never causes a new history, and an ordinary restart never
  resets one. An activation interrupted at any step and repeated ends in one new history;
  it never exposes a mix of the old and the new identity and never sends children into
  endless reloads. Activation takes the leadership of the target store as an open does, and is
  refused while a master holds that store.
- **Measured.** Reported: activation time for the reference fixture.
- **Out of scope.** Starting or scheduling backups, retention, recovery objectives: all
  infrastructure. Commands that launch a restore.

### Stage 8. Operation

#### S37 · Schema migration on the master

**Track:** full · **Needs:** S36 · **Decisions:** D1, P7, O9 · **Owners:** E05, E06 · **Closes:** Q16, R35

- **Goal.** An application moves a Durable master to a new schema in one explicit step:
  the saved data of the changed entity types is converted, and the new schema is
  activated atomically or not at all.
- **Contract.** An explicit migration call that takes the target schema and the
  application's converters for the changed entity types and reports progress and
  outcome; a rejection that says a schema change is in progress; the rule that an
  identity is never reused. On a MemoryOnly master the call is refused with an error
  that says there is nothing saved to convert. The contract of this stage also says how a
  migration treats a changed comparison — of a Map key, of a Set element, of a String
  primary key, of a part of a unique index: it changes what the database may hold
  without changing a layout fingerprint. By the owner's answer to Q10 the catalog version
  of the target schema is greater than that of the schema in force: a migration to a
  schema with the same or a lower version is refused, with an error the contract of this
  stage names. A changed comparison of a String primary key also changes what a Map or a
  Set of references to that entity type may hold, and what a unique index over a
  reference to it admits.
- **To read.** [Schema identity, migration and facade integration](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration);
  [Q03, Q03.9](../decisions/schema-and-types.md).
- **Acceptance.** All writes pause, also those of unaffected entity types, and the
  applier drains to a fixed position; affected entity types are unavailable; reads of
  unaffected types continue. Affected roots are converted in bounded batches into a
  shadow generation on disk; no second arena exists at any time. The source is untouched
  until activation, which is atomic. A kill before activation leaves a database that
  reopens on the old schema and can resume or discard the shadow; a kill after it
  reopens on the new one. A rename keeps identities; a reused identity is refused. A
  converter runs outside every gate and never inside publication; a converter that
  throws leaves the old schema in force. The migration transactions check the leadership
  number. Operations admitted before the change and not executed are rejected with the
  stable result before activation; committed outcomes of the same history stay
  resolvable after it. The roots of the new generation round-trip in the encoded format
  under the new catalog, and a damaged root of the shadow generation is refused. Until
  S38 the call is refused with `NotSupportedException` when a child is connected to the
  master, or when the target schema declares a unique index the current one does not
  have.
- **Measured.** Reported: migration time and peak memory for adding a field to the
  fixture's users.
- **Out of scope.** Children and a new unique index over existing data (S38). Mixed layouts and conversion on access,
  deferred by decision. Migration of a MemoryOnly master, excluded by the owner's
  decision P7.

#### S38 · Migration across the database

**Track:** full · **Needs:** S37 · **Decisions:** D1 · **Owners:** E05, E06, E07 · **Closes:** R32

- **Goal.** A migration leaves the whole database consistent: its children, its unique
  indexes and every input that was bound to the old schema.
- **Contract.** A rejection that says a schema change has made an input stale.
- **To read.** [Schema identity, migration and facade integration](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration);
  [unique indexes, initial load and migration](../decisions/unique-indexes.md#master-validation-and-publication);
  [repeat and status behavior](../design/operation-retry-protocol.md#repeat-and-status-behavior).
- **Acceptance.** Children see the boundary of the schema change and reload completely
  on the new schema; during it their reads wait within the reload bound. A child with
  the old schema never applies a change of the new one. A new unique index over existing
  duplicates prevents activation and keeps all data; nothing is chosen as the winner.
  An input bound to the old schema — a record, a field handle, a typed view —
  is refused after activation. Positions and sessions continue across a migration: the
  history does not change.
- **Measured.** Reported: time during which writes are paused and time until every child
  is ready again, for the reference fixture with three children.
- **Out of scope.** Converting several schema versions in one step. Lazy conversion.

#### S39 · Diagnostics

**Track:** full · **Needs:** S38 · **Owner:** E08 · **Closes:** R33, Q14

- **Goal.** An operator sees the state of a node and, through a child, of its master,
  from the application's own host, without affecting reads.
- **Contract.** A diagnostic service of the database with one status value: role and
  lifecycle, history and schema, published, applied and base positions, known progress
  of the master with the age of the observation, pending delivery, retained journal and
  receipts, queue reservations, the breakdown of native and managed storage, compaction,
  longest open view, the last failure. Registration in the dependency injection package.
  A new thin assembly for ASP.NET Core that maps the route group under a path the
  application gives. The wait for the master's status is an option.
- **To read.** [Control and maintenance tools](../design/storage-and-maintenance.md#control-and-maintenance-tools-q18);
  [diagnostics boundary](../design/milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary);
  [on-demand comparison with master progress](../decisions/replication-and-hosting.md).
- **Acceptance.** Local status answers while the node is loading, reloading,
  disconnected or faulted, and contacts nobody. A value that is not known is reported
  as unknown, never as zero. The master's status asked on a child is collected by the
  master and returned over the replication connection within a bound; an unreachable
  master gives an explicit failure, not an old observation as current. No lock is held
  while a forwarded answer is awaited, and ordinary reads do not slow down while status
  is requested in a loop. The HTTP endpoints serialize the same value; access policy is
  the application's. There is no endpoint that changes anything. The wait for the
  master's status is exhausted in a test. With it the last bounded resource has its
  check: every row of the
  [initial settings](../design/milestone-1-engine-contract.md#initial-bounded-settings)
  that an application can exhaust has a test that exhausts it; the table is gone through
  row by row in this sprint, and a row without a test keeps the sprint open.
- **Measured.** Asserted: a status request allocates a bounded amount. Reported: read p99
  while status is requested in a loop.
- **Out of scope.** Manual maintenance commands, backup or restore commands, a CLI, a
  GUI: all excluded by decision.

### Stage 9. Release gates

**Carried from the planning of observability (Epic 3, R4), 19 November 2026.** A
measurement must not put a shared write on the read path: what a read contributes is
either read from state the engine keeps anyway when a collector asks, or kept per thread
and summed on request. An instrument that needs a counter incremented by every read on
one cache line is not built.

#### S40 · Ordinary-load gates

**Track:** full, verification · **Needs:** S39 · **Decisions:** O11, D6 · **Closes:** G2, G3, G4, G5

- **Goal.** The accepted targets of the ordinary profile are met by the whole system on
  the reference machine, with 5 and with 10 children.
- **Contract.** None. The measurement program gains the topology runs and the manifest
  of the topology: the limits of the containers of the master, PostgreSQL and the
  children, shown to fit into the memory of the reference machine together with ten
  children and the load driver. The owner approves it before the first gate run.
- **To read.** [Performance requirements, accepted targets](../design/performance-requirements.md);
  [fixture](../design/milestone-1-engine-contract.md#reproducible-first-fixture);
  [accepted measurement conditions](../design/performance-requirements.md#accepted-measurement-conditions).
- **Acceptance.** With a Durable master, PostgreSQL and the children as separate
  containers under the limits of the manifest, after the check of the machine passed,
  driven at a paced offered rate: G2 on every child for reads by identifier and by exact
  index value, G3 for single-entity writes through children, G4 for one child started
  under load in each of 5 runs, G5 for the
  master and each child with `Msteady` fixed before the run, each with 5 and with 10
  children. Queues and replication lag do not grow over the run. Range reads, batches,
  position waits and end-to-end constrained reads are reported as their own classes; a
  range read above 1 ms is reported to the owner as its own line. Every gate that fails
  is reported with its component measurements; the implementer fixes the cause, and a
  contract or a target is never changed to pass.
- **Out of scope.** The burst (S41). A network-separated run, which the owner does not
  require for this epic.

#### S41 · Burst and recovery profile

**Track:** full, verification · **Needs:** S40 · **Decisions:** O11, D6 · **Closes:** G6, G7, Q10.1; confirms G1

- **Goal.** The system takes the accepted burst without losing or duplicating a write
  and returns to the ordinary profile within a minute.
- **Contract.** None. The manifest of the burst: the limits of the containers, the
  resource budget the burst may use, the warm-up and the baseline window. The owner
  approves it before the run; the manifest of S40 is not changed.
- **To read.** [Accepted burst and recovery profile](../design/performance-requirements.md#accepted-burst-and-recovery-profile);
  [accepted measurement conditions](../design/performance-requirements.md#accepted-measurement-conditions).
- **Acceptance.** With 5 children and with 10: baseline at 200 changes/s, 30 seconds at
  10,000 changes/s offered,
  then 200 changes/s, with 10,000 reads/s per child offered throughout. Every submitted
  burst operation is accounted for as completed, refused or uncertain, and the
  uncertain ones are resolved by their identities. No accepted write is missing on the
  master, in the saved base after it caught up, or on any child. Within 60 seconds of
  the end of the input every connected child and the applier cover the highest burst
  position and latency, queues and lag are back at baseline. A child that reloaded is
  reported, not dropped from the result. Limits stay in force; refused work is reported
  as a capacity shortfall. G1 is measured again on this final build under the manifest of
  S25 and still holds.
- **Out of scope.** Repeated or longer bursts; sustained 10,000 changes/s.

#### S42 · Close

**Track:** full · **Needs:** S41

- **Goal.** What the epic delivered is described where a user looks for it, and the
  formats are fixed.
- **Contract.** None. The versions that the saved, the wire and the compiled-schema
  formats have at this point are declared final for the release: from here a change of
  a format needs a new version and a reader of the old one.
- **To read.** [Development process, close of an epic](process.md#a-sprint-on-the-full-track);
  `AGENTS.md`, Documentation.
- **Acceptance.** Every row of the release checks and every gate has its evidence named
  in this document. `README.md`, the [architecture](../design/architecture.md) and the
  documents they point to describe what exists; every disagreement between the code and
  an accepted decision is reported to the owner, not edited away. The delivered behavior
  is in the product documentation, including what
  remains for the deployment: a replication port reachable only by the nodes; a
  leadership connection that is a direct session; the server's setting for noticing a dead session; backup by infrastructure; and the
  consequence of one master per store for a rollout. `docs/design/index.html`
  is rebuilt. The table of measured results is in the product documentation. Epic 2's
  document says which of its requirements the core now meets. This
  working document is removed.
- **Out of scope.** Publishing a package or a release, which is the owner's action.
