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

**In the contract and working in the placeholder.** Opening and closing; master and child;
MemoryOnly. The compiled schema: the closed JSON descriptor with identities of entity
types, fields, indexes, enum types and owned types, scalar fields, enum and Flags types,
text policies and the string encoding, owned values, Lists, Maps, Sets and references,
index parts that are paths into owned values; binary save and load with a digest; both
fingerprints; the optional .NET class, bound by one table for every position, with a .NET
enum that has to agree with its enum type. Records and owned values that hold nested
values and copy what is set into them. Sets by class and by name, for
entity types of scalar fields with the default text policies: read, enumerate, insert,
replace, upsert, delete, expected revisions. Positions and sessions; a write through a
child completes when that child has applied it; waiting for a position with a timeout.

**In the engine, not behind the contract yet.** Mechanisms of
`Moltaro.Lattice.Engine`, reached only by its tests until S12 moves the entry point: the
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
reads and writes run on it beside the placeholder, through an internal entry (exception
X4), until S12 (S06). By the owner's decisions of 15 November 2026 an entity, a write
batch and the queue of writes have no limit of their own, and the validation of the core
refuses text that is not well-formed on both implementations. From S07 the engine
stores every key type of the catalog — the eight integers, String with either
comparison, Guid —, a String field compared without case, Enum fields and a schema in
UTF-16, through one comparison and hash service of the schema; the validation of the
core checks enum values, and a write batch takes one operation per entity as the schema
compares keys. The public entry keeps the refusals of the placeholder until S12.

**In the contract, not implemented.** Entry points refuse with `NotSupportedException`;
tests are written and skipped; samples are written.

| Feature | Contract | Tests waiting | Sprint |
| --- | --- | --- | --- |
| Search through a declared index | `set.Index(name).Find` / `FindAsync` | `IndexTests` | S14–S15 |
| Unique indexes | `"Unique": true`, `LatticeIndex.IsUnique`, `UniqueConstraintViolation` | `UniqueIndexTests` | S16 |
| Atomic write batch across entity types | `ILatticeWriteBatch` | `WriteBatchTests` | S17 |
| Durable mode | `LatticePersistenceModeEnum.Durable`, the default | None; one test asserts the refusal | S27–S32 |
| Storing what the schema declares beyond the defaults: a comparison that ignores case, Dictionary storage, an enum field, UTF-16 | The declarations of S02; the schema compiles, a write of such an entity type is refused | `TextPolicyAndEnumWriteTests` asserts the refusal | S05–S07 in the engine, S13 for Dictionary; behind the public entry point from S12 |
| Storing owned values, Lists, Maps, Sets and references, and reading them into a class | The declarations of S03; the schema compiles, records and classes hold the values, a write of such an entity type is validated and then refused | `ValueWriteTests` and `ValidationBeforeRefusalTests` assert the refusal | S18, S19 |

**Accepted for the milestone, not in the contract yet.** Each gets its contract, sample
and tests in the sprint named.

| Feature | Sprint |
| --- | --- |
| The memory budget as an option, the budgets of a read as options that are not set by default; resource errors. An entity, a batch and the queue of writes have no limit of their own (decisions O18, O19 and O20) | S08, S09, S17, S24 |
| A network transport between master and children | S10–S11 |
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
| R2 | The public library keeps no package dependency. Arc.Collections and the PostgreSQL driver each have one owning assembly, and dependencies point one way | [Handoff](../design/planning-handoff.md#planning-obligations) | Must | Holds; no engine yet | S01, S27 |
| R3 | The schema catalog owns stable positive identities of entity types, fields and indexes, a catalog version and deterministic layout fingerprints; one compiler serves every input | [Engine contract](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration), [Q03.9](../decisions/schema-and-types.md) | Must | Partly: indexes have no identity, no fingerprint | S02 |
| R4 | The accepted scalar catalog and its rules: eight integers, Float32, Float64, Decimal, Boolean, String, Guid, the temporal types, Binary; enum and Flags; explicit nullability; String comparison Ordinal or OrdinalIgnoreCase per field | [Q03.1–Q03.8](../decisions/schema-and-types.md), [Q04](../decisions/strings.md) | Must | Field types work; enums and text policies absent | S02, S05, S06–S08 |
| R5 | Owned entity trees, ordered Lists, Maps, Sets and references by key without integrity enforcement, with the accepted input, null and comparer rules | [Q02, Q02.1, Q02.2](../decisions/document-model.md), [Q03.3, Q03.4.2](../decisions/schema-and-types.md) | Must | Absent | S03, S18, S19 |
| R6 | Every data operation exists for entity classes and for records by name, with the same rules | [Q06.3](../decisions/memory-and-access.md) | Must | Works for scalar fields | Every sprint that adds an operation |
| R7 | Ordinary reads return owned copies, coherent for one entity | [Q06, Q06.1](../decisions/memory-and-access.md) | Must | Works | S06–S08 |
| R8 | Short synchronous protected read-only views: no copy, no escape, no nested database call | [Q06.2, Q06.3](../decisions/memory-and-access.md) | Must | Absent | S21–S22 |
| R9 | Native segments, size classes, relative addressing, stable managed root entries, large values in bounded chunks | [Q05](../decisions/memory-and-access.md), [Q16](../decisions/physical-storage.md) | Must | Absent | S04, S05 |
| R10 | Text: Plain and Dictionary storage, the database encoding, stable text entries, safe sharing and reclamation, well-formed Unicode only | [Q04.1–Q04.5](../decisions/strings.md) | Must | Absent | S05, S13 |
| R11 | Space is reused for the life of the database: size-class reuse, bounded incremental compaction, collection shrink, release of empty segments | [Q17](../decisions/allocation-and-compaction.md) | Must | Absent | S04, S23–S24 |
| R12 | Memory is bounded and its exhaustion is explicit: reservations before commit, refusal instead of unbounded waiting, a child pauses application and keeps its reads; an entity has no cap of its own (decision O19) | [Q14](../decisions/write-admission.md) | Must | Absent | S09, S23–S24, S33–S34 |
| R13 | Arc UnorderedMapSlim for the primary index and Arc OrderedMultiMap for secondary indexes, behind engine-owned protection, rebuilt locally on every node | [Index structures](../decisions/index-structures.md), [Q19](../decisions/index-lifecycle.md) | Must | Absent | S06–S08, S14–S15 |
| R14 | One comparison and binding service for indexes, collections and lookups: the numeric allowlist, NaN and zero, null order, enum order, both text encodings | [Q07.3.6](../decisions/queries.md), [engine contract](../design/milestone-1-engine-contract.md#index-preparation-and-traversal) | Must | Absent | S06–S08, S14–S15, S19 |
| R15 | Search through a named index: full-key equality, leading fields with a range, both directions, Limit and Offset, budgets; never a silent scan | [Index access](../decisions/index-access.md), [Q07.1.1, Q07.3.x](../decisions/queries.md) | Must | Contract; `IndexTests` skipped | S14–S15 |
| R16 | Enumeration and index traversal over live data: coherent entities, no identity twice in one pass, unchanged matches included, a budget on time and memory | [Q07.1, Q07.3.5](../decisions/queries.md) | Must | Works without a budget | S06–S08, S14–S15 |
| R17 | Unique indexes on one field or several, validated against the final state on the master; keys with a null are exempt; swaps and delete-and-reclaim inside a batch | [Q07.4](../decisions/unique-indexes.md) | Must | Contract; `UniqueIndexTests` skipped | S16, S17 |
| R18 | Complete writes: Insert, Replace, Upsert, Delete; expected revisions; immutable keys; a complete write is never turned into a diff; inputs stay stable until completion | [Q13](../decisions/mutation-commands.md) | Must | Works | S06–S08 |
| R19 | Explicit partial updates: scalar assignments, replacement of a whole collection field and the accepted List, Map and Set element commands, in order, atomic for one entity | [Q13](../decisions/mutation-commands.md) | Must | Absent | S20 |
| R20 | The atomic write batch: several entity types, one operation per type and key, one position, bounded, refused as a whole | [Q01](../decisions/document-model.md), [Q13](../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch) | Must | Contract; `WriteBatchTests` skipped | S17 |
| R21 | Write coordination: parallel preparation, order per entity, ordered bounded commit and publication, resources prepared before effects commit, cancellation only before dispatch, a failed publication stops the instance | [Q09.1](../decisions/write-acknowledgment.md), [Q13](../decisions/mutation-commands.md#cancellation-at-the-commit-boundary) | Must | Absent | S09 |
| R22 | Workload limits are options, validated together when the instance opens | [Engine contract](../design/milestone-1-engine-contract.md#initial-bounded-settings) | Must | Only the synchronization timeout | S06–S08, S09, S10–S11, S17, S23–S24, S26–S34, S39 |
| R23 | Operation identities: a token kept before submission, a retained outcome, safe repetition for 15 minutes by default, never a second execution | [Q13](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1), [retry protocol](../design/operation-retry-protocol.md) | Must | Absent | S26, S28–S29, S31 |
| R24 | Durable is the default mode and MemoryOnly is explicit; the mode is chosen when the instance opens; children are MemoryOnly and never connect to PostgreSQL | [Q09.5](../decisions/persistence-modes.md), [Q09.6](../decisions/persistence-backends.md) | Must | Options exist; Durable refuses | S27 |
| R25 | A Durable write succeeds after its durable admission, its PostgreSQL commit and its coherent publication; an uncertain commit is reported as uncertain | [Q09.1](../decisions/write-acknowledgment.md), [Q09.2](../decisions/checkpoint-and-journal.md) | Release | Absent | S28–S29 |
| R26 | An independent saved base in 256-byte units advanced in the background with coalescing; base and cursor commit together; journal and receipts are bounded when the application sets a limit, and shrink back after load in any case | [Q09.2](../decisions/checkpoint-and-journal.md) | Release | Absent | S30–S31 |
| R27 | Recovery: an offline reopen rebuilds one arena and its indexes from the base and the committed tail; nothing partial is replayed; damaged or incompatible data is refused | [Q09.2](../decisions/checkpoint-and-journal.md), [Q16](../decisions/physical-storage.md) | Release | Absent | S30–S31, S32 |
| R28 | A child loads the whole database from the master's memory, rebuilds its indexes and becomes ready at a fixed position; changes then arrive in order as complete transactions. Children that start together load in parallel, up to a configurable limit | [Q08, Q08.1.1](../decisions/replication-and-hosting.md) | Must | Works inside one process | S10–S11, S33 |
| R29 | A write through a child succeeds after the master completed it and that child applied it. Related requests carry a minimum position and wait for it with a timeout on any node | [Session consistency](../decisions/session-consistency.md) | Must | Works inside one process | S12 |
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

| S07 | Read by a String key of one of 10,000 settings of the scalar form, by name, one thread; 1,000,000 reads, three runs | p50 300 to 400 ns, p99 1,400 to 1,900 ns, alike for a key compared with Ordinal, without case in the stored spelling, and without case in upper case | development-2 | 15 November 2026 |

The figure of S07 comes from the scenario `read-by-text-key`; its keys are 13 ASCII
characters, and no key outside ASCII was measured.

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
it owns `Arc.Collections`, pinned to exactly 2.2.0, the version the evidence was measured
on. `Moltaro.Lattice.PostgreSql` is the Durable provider and owns `Npgsql`; a child never
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
engine.

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
- **A read budget is one value, `LatticeReadLimits`**, passed to an enumeration and later
  to a search, with one error code and the exceeded limit named in `Limit`.
- **The number of preparing workers is between 1 and 64.**
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

- The [development process](process.md) applies. On the full track: contract, then tests
  by another agent, then implementation, each its own commit.
- A sprint on the full track has its own branch, `epic-1/sprint-NN-name`, cut from
  `main` and merged back when the sprint closes, as [branches and releases](branches.md)
  says; the light sprint S01 uses a light-track branch. One sprint runs at a time. `main` stays green after every merge: what is not implemented refuses and
  its tests are skipped.
- Every document under **To read** is read at its baseline of 14 November 2026. If one
  changes, the coordinator stops and has this plan corrected.
- The briefs of S02 to S12 name their types and members exactly, under "Types and
  members"; the owner approves them with the plan, and the contract author puts them into
  code as written. A later brief says what its contract must be able to do, and the
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
- A verification sprint is on the full track: the test author writes the scenarios and
  the harness, the implementer fixes what they expose.
- A test that only asserts that a feature is not implemented, such as
  `Search_is_not_implemented_yet`, is removed by the test author in the tests step of the
  sprint that implements the feature. "Assertions unchanged" speaks of the tests of the
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
| S07 | Every key type and declared value | Full | Not started |
| S08 | Enumeration under change and the growth of the index | Full | Not started |
| S09 | Write coordinator and write limits | Full | Not started |
| S10 | A child loads the database | Full | Not started |
| S11 | A child loads under writes and follows the master | Full | Not started |
| S12 | Writes through a child, sessions; the placeholder is removed | Full | Not started |
| S13 | Text entries and Dictionary strings | Full | Not started |
| S14 | Secondary indexes and exact search | Full | Not started |
| S15 | Ranges, order, paging and the binding rules | Full | Not started |
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

A String key compared without case follows `StringComparison.OrdinalIgnoreCase` of .NET,
the semantic reference Q04 names, with the same result in a UTF-8 and in a UTF-16
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
and are not edited in S07.

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

**The scaling of reads, by the owner's decision on Q42 of 15 November 2026**, given
through the planner's session: reads by key have to scale with threads, and the work is
done in this sprint, not judged at S25. The owner's reason: that more threads read less
in total is a very bad signal, and open-source databases that are very fast are there to
be studied: how they do it is to be learned from their source and, in essence, repeated.

- **Goal, added.** Reads by key scale with the number of threads that read.
- **To read, added.** The review of S06 on the scaling of reads (Q42), and "Reader
  protection, publication and compaction" of the milestone contract, which calls
  `ReaderWriterLockSlim` the initial synchronization.
- **Types and members, added.** The protection of a read is reworked in this sprint. Today
  a read by key makes four writes to memory that all readers share — the counter of the
  close barrier, the catalog gate twice (for the lookup and when the claim is dropped), the
  stripe of its root —
  which is why more threads read less in total. What the sprint
  has to reach: a read by key writes to no memory that other readers write to on its
  ordinary path, or as near to that as the guarantees allow, and takes the gate of the
  index once. The implementer first studies how open-source databases that read fast from
  memory on many threads protect a read — their source, not a summary of it — and writes
  down in the pull request which of them it looked at, what each does for this problem,
  and which it follows. The aim is to repeat a solution that is proven there, not to
  invent one. Among the candidates are reader counts kept apart per thread or per
  processor, optimistic reads checked by a version, and protection of memory by epochs;
  how many protections there are and how fine they are is decided by what those systems
  do and by the figures the implementer measures, not by a preference stated here. The
  guarantees do not move: a read is coherent, a deleted entry is reused only after the
  readers that could see it are gone, a close waits for the reads in progress, no
  application code runs inside a protection, and the storms of the reviews of S06 pass unchanged. When the choice
  replaces what the milestone contract names as the initial synchronization, the
  coordinator corrects that section with the sprint and reports it to the owner.
- **Acceptance, added.** Reads by key on 16 threads complete more in total than on one
  thread, on the machine of the review; the figure for 1, 2, 4, 8 and 16 threads is
  reported, with a writer and without, and the owner judges it against the aim of growth
  close to the number of processors. The model-checked storms and the close cycles of S06
  pass on the new protection. The scaling is a figure of a measurement and not a test of
  the default lane; a miss of the first sentence is reported to the owner in the sprint,
  as an early figure that misses its gate is.
- **Measured.** The line on reads by key becomes: reads by key per second on 1, 2, 4, 8
  and 16 threads, without a writer and with one, before and after the change.
- If the coordinator judges that this makes the sprint too large for one implementer and
  one review, it splits it and tells the planner: the protection of reads first, the
  enumeration and the growth of the index after it.

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
text table, the pages of root entries, prepared capacity and the scratch of reads; until then those are not
counted, and the documentation of `MemoryLimit` says so. Internal, in the engine
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
    int WireFormat, int LayoutFormat, LatticeRoleEnum Role);

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

#### S11 · A child loads under writes and follows the master

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

#### S12 · Writes through a child, sessions; the placeholder is removed

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

### Stage 4. Indexes and batches

#### S13 · Text entries and Dictionary strings

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

#### S14 · Secondary indexes and exact search

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

#### S15 · Ranges, order, paging and the binding rules

**Track:** full · **Needs:** S14 · **Owners:** E04, E08

- **Goal.** An application searches by the leading fields of an index and a range, in
  either direction and in pages, within a budget, and gets the same answer whichever way
  it supplies a value.
- **Contract.** Exists. Added: overrides of the read budget in `LatticeFindOptions`.
- **To read.** [Index access](../decisions/index-access.md);
  [index preparation and traversal](../design/milestone-1-engine-contract.md#index-preparation-and-traversal);
  [Q07.1.1, Q07.3.1–Q07.3.6](../decisions/queries.md).
- **Acceptance.** Every test of `IndexTests` passes with its assertions unchanged, and
  the index sample runs to the end. In addition: for every scalar and key family the
  same value is found by key, by index, through a class and by name; the numeric
  allowlist binds or refuses exactly as specified, including overflow, 42.0 for an
  integer field, NaN and signed zero; nulls are last in both directions; enum order is
  numeric; text compares by its field policy in UTF-8 and in UTF-16 databases. A
  traversal under concurrent change returns no identity twice and every unchanged match,
  also inside a group of equal keys. Offset skips matches and Limit bounds them without
  reading all matches first. A search over its budget fails explicitly. An unsupported
  key or bound shape is refused.
- **Measured.** Reported: search time for exact and range lookups on the fixture's user
  indexes, with and without a writer.
- **Out of scope.** Uniqueness (S16). A query language, automatic choice of an index,
  continuation by key.

#### S16 · Unique indexes

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
  unique index.
- **Out of scope.** Swaps inside a batch (S17). Activation of a new unique index over
  existing data (S38).

#### S17 · Atomic write batch

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
  group. A batch that the memory budget cannot hold is refused with `WriteRejected` and
  `Memory` before anything is written, and the database is as it was. Two entities changed by one batch
  share its position and have revisions that do not satisfy a condition on each other. An internal group never splits
  a batch. A child never acknowledges a part.
- **Measured.** Reported: how long ordinary reads are held by the publication of a batch
  of 1, 32, 1,000 and 10,000 entities.
- **Out of scope.** Partial updates as batch members (S20). The batch's token (S26).

### Stage 5. The full type model and access

#### S18 · Owned values, Lists and references

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

#### S19 · Maps and Sets

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

#### S20 · Partial updates

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

#### S24 · The memory budget

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
  primary index, secondary index nodes, the text table, prepared capacity of writes in flight, the scratch of reads, the pages of root entries
  and the queued write input; S26 adds the retained outcomes of a MemoryOnly master. A reservation that fails releases everything it took,
  shown by the counters. A child over its budget pauses application, keeps its reads and
  resumes when memory is free again; reloading after a long pause is S34. Nothing queued
  runs after its cancellation.
- **Measured.** Reported: peak against steady memory of one MemoryOnly node held at its
  budget under the churn trace; the share of the budget each counted part takes on the
  reference fixture.
- **Out of scope.** Budgets of the journal and of delivery history (S31, S33).

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

#### S28 · Durable commit

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

#### S34 · Reconnect, reload and a restarted master

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
