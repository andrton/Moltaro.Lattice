# Physical storage and maintenance

**Current implementation baseline.** The [milestone 1 contract](milestone-1-engine-contract.md)
specifies allocation, compaction, reconnect and backup. The
[arena protocol](arena-protocol.md) describes automatic maintenance on the
MemoryOnly engine. This document owns the diagnostics and maintenance scope (Q18)
and records earlier design considerations.

**Requirements recorded on 12 November 2026.** The design requires an explicit
physical-storage design, bounded memory growth over a long process lifetime,
a fragmentation/reclamation policy, and useful administration/maintenance facilities.
Q18 now selects DI-registered diagnostics and a library-owned HTTP module mounted
in the application for the first release, without a CLI or GUI. A polished visual
interface remains a later product goal. Backup and restore must be covered by the
architecture; exact algorithms and remaining operation scope are separate work.

[Architecture](index.html) · [Question register](questions.md) · [Accepted arena backing](../decisions/physical-storage.md#q16-physical-storage-and-layout)

## Accepted foundation and open choices

The MemoryOnly engine uses explicitly allocated native segments with managed
.NET control structures. It supports protected views, owned result copies, key
references and shared text references with delayed safe reclamation. Durable
persistence remains a separate implementation.

| Owner | Decision scope | Existing engineering owner |
| --- | --- | --- |
| Q16 | Native backing, record-oriented fixed fields with separate variable payloads, relative segment/offset addressing, chunked large values and database-owned active segments with a common bounded empty reserve and stable root control entries in managed arrays of structs, targeted partial preparation/post-commit publication and full Save replacement without diff accepted; compact entry layout, sizes/thresholds, exact encoding, collection internals and detailed copy boundaries remain open | E01; E04/E05 for indexes/schema layout |
| Q17 | Size-class reuse, bounded compaction, empty-segment reserve and release, and automatic collection shrink are implemented in MemoryOnly | E01/E02/E03. Q14 covers instance memory pressure. |
| Q09.4 | PostgreSQL-native Durable backup and infrastructure-owned execution, without Lattice API launch commands; isolated replacement restore with former storage preserved until verification and one arena, plus new history/full child reload accepted. Same-history Durable restart preserves identity. Deployment procedure/objectives and activation/validation details remain work | E06/E07; Q09.1/Q09.2 supply durability and journal contracts |
| Q09.5 | Accepted v1 selection at instance open time: Durable default or explicit MemoryOnly | E06/E07; Q08.1.3 supplies resume/reload rules and Q15 the configured endpoint/lifecycle contract |
| Q09.6 | PostgreSQL accepted as the first Durable backend; production format/provider implementation remains engineering work | E06; Q09.1/Q09.2 commit/recovery rules and Q09.4 restore validation |
| Q18 | First-release scope accepted: diagnostic DI services and library-owned HTTP endpoints under a host-selected prefix; local/master status through a child. Automatic maintenance remains; manual maintenance commands, CLI/GUI and backup/restore launch commands are outside v1 | Host integration and E07 internal request/reply transport; engine-owned status collection |
| Q19 | Accepted: schema index definitions and local construction on every node; no physical index images persisted/transferred in v1 | E04/E06/E07: bounded builders and startup measurements; Q10.1 supplies full-readiness targets |

Q16 accepts [explicit allocation with separate persistence](../decisions/physical-storage.md#q16-physical-storage-and-layout)
and [record-oriented fixed fields with separate variable payloads](../decisions/physical-storage.md#record-oriented-layout-with-separate-variable-payloads).
[Relative segment/offset addressing](../decisions/physical-storage.md#relative-block-addressing)
is now accepted, with safe owner/index locator updates on movement.
[Automatic incremental compaction](../decisions/allocation-and-compaction.md#automatic-incremental-compaction)
and [chunked large-payload storage](../decisions/physical-storage.md#large-payloads-in-bounded-chunks)
are accepted, as is [empty-segment retention and release](../decisions/allocation-and-compaction.md#empty-segment-retention-and-release).
[Master memory admission](../decisions/write-admission.md#master-writes-under-memory-pressure),
[child memory-pressure handling](../decisions/write-admission.md#child-replica-memory-pressure)
and [automatic collection shrink](../decisions/allocation-and-compaction.md#automatic-shrinking-of-unused-collection-capacity)
are accepted, as are [Durable backup responsibility](../decisions/backup-and-restore.md#durable-backup-responsibility)
and [isolated replacement restore](../decisions/backup-and-restore.md#restore-destination).
[New history and full child reload after restore](../decisions/backup-and-restore.md#replica-history-after-restore)
are also accepted. [Q15](../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection)
accepts a configured master URL and environment-owned lifecycle, without fault-tolerance orchestration.
[Size classes and reusable slots](../decisions/allocation-and-compaction.md#q17-allocation-and-fragmentation-lifecycle)
are accepted for small/medium allocations, with a separate large-allocation path.
The [arena protocol](arena-protocol.md#reclamation-and-relocation) records current
selection and reclamation bounds. Broader instance memory accounting and admission
follow Q14.
MemoryCopy is a copy operation, not a competing allocator or persistence model.
Both allocated and mapped arenas may need copying, handles, allocation metadata,
index maintenance and reclamation. Native storage does not mean one allocation
per object; a mapped file does not defragment stored documents automatically.

[Q19](../decisions/index-lifecycle.md) accepts schema index definitions and local
physical index construction on every node before readiness. V1 excludes saved or
transferred physical Lattice index images. Continuous index maintenance remains
required after startup; builder memory and complete readiness time need measurement.

[Q09.5](../decisions/persistence-modes.md#q09.5-how-is-persistence-mode-selected) accepts
Durable by default or explicit MemoryOnly, selected when opening a local instance
and fixed for its lifetime in v1. MemoryOnly omits engine-managed persistence,
including schema/index files and disk WAL/checkpoints, while retaining the same
arena and record semantics. Allocator reclamation and bounded replication/query
memory remain necessary. Detailed restart/reconnect policy remains open;
[Q09.1](../decisions/write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed)
now requires reliable storage acknowledgment before Durable write success.

[Q09.6](../decisions/persistence-backends.md#q09.6-durable-storage-backends) accepts
PostgreSQL as the first Durable backend. [Q09.2](../decisions/checkpoint-and-journal.md#q09.2-postgresql-base-and-compact-journal)
accepts a compact PostgreSQL journal and recoverable saved state, maintained by
background application with coalescing and atomic saved-data/position commits. This is
a provider representation choice, not a journal mandate for MemoryOnly or every
future backend. Automatic size/age triggers and a hard retained-journal limit with
write refusal, a 256-byte baseline saved unit and short bounded application batches
with immediate catch-up are accepted. Transient applier failures permit healthy
journal appends within budget and paced retries inside the master process. Detailed
failure classification, numerical limits and retained consumers remain open; exact encoding, SQL layout and provider implementation are E06 work. Managed backup/restore and
deployment must still be validated alongside
latency, startup and resource use. The engine owns coherent capture, format
compatibility and recovery from a restored store. A later file backend has no
selected delivery date; MemoryOnly remains independent of SQL persistence.

## Allocation and fragmentation (Q17)

**Resource priority as of 12 November 2026:** RAM efficiency and bounded memory
growth matter more than minimizing allocated disk space. Periodic disk compaction
may run with Lattice stopped or in a maintenance pause. Online disk shrinking is
not a v1 requirement. This permits maintenance downtime for disk packing; it does
not select a backup/replica capture algorithm or pause duration. Normal saved-state
maintenance now uses the accepted background application method.

Space reclamation/packing and byte compression are different operations; no new
compression codec is selected. For PostgreSQL, ordinary vacuum keeps obsolete SQL
space reusable. Returning that space to the OS can require a heavier rewrite;
VACUUM FULL requires extra disk and an exclusive table lock while PostgreSQL itself
is running. Lattice can be offline for that operation. See the
[PostgreSQL VACUUM reference](https://www.postgresql.org/docs/current/sql-vacuum.html).
Relaxed disk packing does not remove normal PostgreSQL maintenance or the need
to bound required journal retention and recovery time.

A growing string or collection can outgrow its current block; deletion leaves
free space; rounded allocation sizes create slack; a few live blocks can keep a
large segment occupied. Retired blocks awaiting readers/checkpoints are different
from immediately reusable holes. Dictionary entries, owned children, indexes and
allocator metadata all need explicit lifetime owners.

The required outcome is bounded engine-owned growth for bounded live data and
bounded concurrent work. Repeated updates must not require an operator to restart
the process periodically merely to recover abandoned space. Exact steady-state
headroom, transient peak limits and pressure behavior still need decisions.
Legitimate increases in live data, declared indexes or retained caller copies are
not fragmentation and must not be hidden by a misleading memory ratio.

The [accepted allocation policy](../decisions/allocation-and-compaction.md#q17-allocation-and-fragmentation-lifecycle)
uses size-class slots for small/medium blocks and a separate large-allocation path.
[Automatic incremental compaction](../decisions/allocation-and-compaction.md#automatic-incremental-compaction)
now uses bounded steps coordinated with each node's local writer/applier, without a
second full arena. Region/class counters track live, retained-retired, reserved and
reusable slots; expected useful reclamation, compatible destinations and workspace
trigger work with separate start/stop conditions. Relocation preserves readers,
physical locators and enumeration. CLR GC does not compact native database records.
[The arena protocol](arena-protocol.md#reclamation-and-relocation) describes the
implemented work limits, the start threshold, the relocation workspace and
publication. Large strings and collections use bounded chunks, with small payloads
contiguous. The default arena keeps one completely empty segment for reuse. Excess
segments with no retained users or reservations return to the native allocator.
Availability metadata avoids a scan of all segments on ordinary allocation.
Automatic collection shrink returns writer-only lookup spares outside the active
probe range while preserving values, List order and revisions. Foreground memory
admission, additional sparse-segment triggers and persistent storage retain their
separate design decisions.

The following questions recorded the original allocation study. Implementation
bounds and mechanisms are in the arena protocol:

- Which numerical class sizes, region/segment sizes and large-allocation threshold
  meet the workload? Which chunk/directory sizes and structures fit large payloads?
  Which numerical reserve/timing budgets fit empty-segment reuse and release?
  How are live allocation slack, retired child/collection blocks and dictionary
  entries reclaimed within their lifetime rules?
- Which numerical start/stop thresholds and minimum reclamation benefit justify
  a step? Define cadence, fairness and bounded deferral when busy roots or retainers
  prevent progress. The incremental mode is accepted; numerical defaults are not.
- What temporary headroom, CPU work and maximum read/write interruption may one
  compaction step consume? A second full-size arena is not a free default.
- Which handles/offsets/index entries must change during relocation, and how is the
  new location published without invalidating an active protected view?
- How do checkpoint capture, replica transfer and delayed readers retain old data,
  and what happens when those users prevent reclaiming within the memory budget?
  Never free referenced storage to make a pressure metric look healthy.
- Define exact safe-detachment and address-reuse mechanics for the accepted
  empty-segment reserve/release policy. Reusing capacity, releasing native allocations,
  reducing resident memory and shrinking a persistent file are separate outcomes
  and need separate measurements.
- How are interruption, shutdown and recovery handled during movement? Recovery
  and replication must not interpret a half-published relocation as valid data.

A candidate lifecycle to investigate is allocate → publish → retire → reclaim →
reuse, with relocation/empty-segment release when fragmentation justifies it.
The current engine uses size-class reuse, bounded incremental maintenance, chunked
payloads, a limited empty-segment reserve and automatic collection shrink.

## Memory accounting and evidence

The measurement program runs the same fixed-volume workload on a master and a
TCP child with either maintenance setting. From `src` use `storage-churn
development` or `storage-churn-off development` with the Release
`Moltaro.Lattice.Measurements` project. The output under `.local/measurements`
includes the authored trace and complete turn records. It reports public read
latency, native capacity and admitted step time alongside all-attempt timing.
Initial, peak and final native grants come from the allocator. The logical
volume is checked using the public values, separately from native allocation
classes and managed metadata.

The eventual control surface should distinguish live payload, index/dictionary
metadata, immediately reusable free space, allocation slack, retired/pinned data,
segment capacity and temporary operation buffers. OS resident memory and virtual
address reservations are additional observations, not synonyms for live payload.
Categories must be defined without double-counting; thresholds remain Q14/Q17 work.
MemoryOnly enforces peak preparation and publication capacity in one instance budget,
which [the memory budget](#the-memory-budget) below describes. Public pressure
diagnostics, retention limits for slow children and reconnect or reload remain separate
work. The [engine contract](milestone-1-engine-contract.md#initial-bounded-settings)
defines the accounting boundary.

By the owner's decision of 15 November 2026 an entity has no size limit of its own, and the
[configurable root-size cap](../decisions/write-admission.md#configurable-root-object-size-limit)
accepted before it is superseded. What bounds an entity is the instance-wide peak-memory
reservation: account for it before commit.

Evidence must include long-lived insert/update/delete churn with a bounded live
set, varying string/collection sizes, hot/cold records and mixed allocation sizes.
After warm-up, capacity should stabilize within the agreed bounds or report the
defined pressure condition. Include delete-heavy phases and verify both reuse and,
where required, release of empty segments; a fast allocation loop proves neither.

Measure peak engine bytes, retained versus reusable memory, bytes moved, read/write
tail latency and reclamation delay. Include active readers and checkpoint/transfer
retention. Compare correctness and steady-state memory, not only operations/second.
These are required evidence dimensions, not a load test being started now or
numerical acceptance thresholds already chosen.

## The memory budget

A MemoryOnly instance admits its work against one budget, its memory limit (by default
half of the memory available to the process; see the
[engine contract](milestone-1-engine-contract.md#initial-bounded-settings)). The budget
guarantees two things: a write that does not fit is refused before it has any effect,
and a Ready child at the limit pauses and keeps its reads. It is not an exact cap of the
memory of the process: it charges memory by structure, exactly where memory grows with
the data and by fixed estimates elsewhere.

The budget is one counter of charged bytes (`Storage/Budgeting/MemoryBudget` in the
engine). A reservation is a compare-and-swap that never passes the limit, a release an
interlocked subtraction; neither takes a lock, except a child that waits for memory and a
release that has such a waiter to wake. Every instance has one; a structure made alone in
a test has an unlimited one.

**What is charged, and when.**

- *Native segments*, at their grant, with their managed metadata — the segment object and
  its arrays of regions and slot bitmaps — and the empty reserve; released when a segment
  is returned to the allocator. A block inside a charged segment, a root or a node of a
  secondary index, is not charged again.
- *The managed structures that grow with the data*: the root pages, the tables and
  overflow buckets of the primary indexes, the arrays of the text table, the collection
  tails, the retirement queue, the publication scratch, a child's key set, and the buffers
  of connections and of a load in transfer. Each is charged by the bytes
  of its backing array — a header of 24 bytes and the elements, rounded up to 8 bytes —
  when it grows: the new array is reserved before it is allocated, and the old one is
  released once it is dropped, or, for a generation a reader may still hold, such as the
  replaced table of a primary index or a directory replaced by a larger copy — the segments
  of the arena, the pages of the root table, the overflow pages of a primary index —, when
  its retirement is freed. A growth therefore holds both arrays for a moment, and a
  structure keeps the capacity it reached.
- *The root image of an ordinary write*: one workspace of the arena, charged at its size
  and grown under the writes lock when a wider root is written. A write whose image fits
  allocates none.
- *The input of a batch*, when a member is added, after every check of the addition and
  after the snapshot it builds: its encoded length and `BatchInputCharge.PerMemberBytes`
  (1,280 bytes), and with the first member `PerBuilderBytes` (640 bytes). One finalizable
  owner per batch returns the whole charge when the execution of the batch ends — on a
  child, at the reply of its master, before the child waits to apply the batch —, or when an
  abandoned batch is collected. A snapshot longer than one piece of a transfer (64 KiB) is
  held in pieces, which keep it off the large object heap and charge nothing of their own:
  its encoded length is charged here.
- *A write in progress*: an ordinary write, a child transaction, a write a child forwards
  and the request it arrives as on the master reserve one estimate when they start —
  `PerMemberBytes` for each member, a child transaction counting them by the number of
  effects its header states, and the encoded commands of a partial update — and
  release it when they end; the child's write ends with the reply of its master. Their
  values are charged where they land: in the root image, in the native blocks, and, for a
  transaction or a forwarded request, in the connection buffers that carry it.
- *The headroom*, once, when the parts of the instance are made: at open on a master, when
  the schema arrives with the load on a child.

Not charged: the owner objects of the structures (their arrays are), the working objects
of a write under its estimate — edits, claims, decoded commands and keys, the key set of a
batch, replies —
application memory and garbage awaiting collection, the workspace of an enumeration,
which the read budget bounds, and the delivery tail of a master, which is reported as its
own figure until its retention policy is built.

**The headroom.** The small fixed bookkeeping that the budget does not charge object by
object — the owner objects of the structures, weak references, fixed directories, the
headers of each entity type and each index — is covered by a headroom computed from the
schema and the options (`Engine/Headroom`):

| Part | Bytes |
| --- | ---: |
| Every instance | 7,168 |
| The slots of the protection of reads | 24 + 128 × the slots |
| Each entity type | 1,024 |
| Each index: the primary index of each entity type and each secondary index | 1,344 |
| A child | 15,360 |
| A master that listens for children | 3,584 |
| What reads hold of the text table, when the schema has texts the text table holds | 1,176 × the slots |

The slots of the protection of reads are an array of 128 bytes a slot, as many slots as
twice the processors, at least 128, rounded up to a power of two: their bytes depend on the
host, so they are computed with the function the protection makes its slots with, not
measured — 16,408 bytes with 128 slots. A leaf of the locator of the text table is replaced
by a copy when its text IDs outgrow it or leave it half empty, or dropped when they are all
gone, and a branch of its pages is dropped when its pages are gone; both are released at
once. A read holds at most one replaced leaf and one dropped branch at a time, so the
headroom holds one of each for every slot, 1,176 bytes a slot from their layouts
(`TextLocator.HeldByOneRead`), when the schema has a String with Dictionary storage or a
String in a secondary index, whose texts the text table holds. The other figures were
measured on Windows x64 with .NET 10 as what the heap retains once an instance is open,
beside the arrays its structures report, its segment objects and the slots, as the median
of five opens between two still heaps, on the schemas of the headroom tests: one entity
type, 32 entity types, one entity type with 32 secondary indexes, and one of four entity
types with five secondary indexes and texts. Each figure is the most found in any of those
cases, the parts measured before it at their constants; each constant is 10 to 25 % above
its figure, the margin for a host that was not measured, and the constants in the code
state each figure. A child retains more for each type and index than a master, and the
parts of a type and of an index follow the child. For the schema of the reference fixture,
four entity types and six secondary indexes, the headroom with 128 slots is 191,640 bytes
on a master, 195,224 on a master that listens and 207,000 on a child.

**The balance.** At a point of rest — no write, no child transaction, no forwarded request
and no batch alive — the charge equals the native grants with their metadata, the arrays the
structures hold and the headroom; while batches or writes are alive it exceeds that by at
most their estimates. The tests of the engine check it through an inspection of what the
instance holds (`EngineDatabase.InspectHeld`). After close the charge is zero and every
native grant is returned.

**What a refusal returns.** A write whose reservation does not fit is refused with
`WriteRejected` and `Memory` before commit and before any effect; a new entity that finds
no addressable root slot is refused with `RootSlots`. Before it refuses, the write
coordinator frees what is retired and tries once more, then waits outside every lock for
the reads that hold retired memory, frees it and tries again. The refused attempt returns
the reservations it did not use and its estimate, its native blocks and root slots, its
root handles, the texts it added, its index reservations, its retirement places and its
loans of collection tails; installed data, revisions and position stay unchanged. The
arrays it grew stay with their structures, charged and reusable, so the next write that
needs the same growth takes no new charge for it. A segment it left empty goes to the
reserve or is returned, as any empty segment. A batch addition that does not fit reports
`WriteRejected` with `Memory` at once, keeps the earlier members and can be retried.

**The wait of a child.** A Ready child whose budget refuses a frame or a transaction keeps
it on the live connection and pauses there; reads continue at its applied position, and no
later frame or acknowledgement passes it. After the refused attempt has given back what it
prepared, the child marks itself as the waiter of the budget, which hands it a ticket, the
count of frees so far, and tries once more. Refused again, it waits for the first free
counted after its ticket — a release of bytes, or a native block or a root slot made
reusable — or at most the retry pause, and tries again, until the change applies, once and
in order. The two marks answer two races: memory freed before the mark is seen by the
attempt that follows it, and memory freed after it moves the count past the ticket. The
attempt's own rollback is not counted as a free, so the child does not wake itself. Close
and cancellation end the wait. Before Ready, a child that cannot hold its initial state
ends its open with `Unavailable`.

<a id="backup-and-restore-q09.4"></a>

## Backup and restore (Q09.4)

Backup is an engine correctness contract before it is a button in a console.
Define a recoverable boundary covering schema/layout metadata, data, dictionary
state and any required journal tail. Index definitions must be retained; accepted
Q19 requires rebuilding physical Lattice indexes on restore, without saved index
images. PostgreSQL's own indexes remain a separate provider concern.
A raw copy of files being mutated is not automatically a consistent database backup.

[Durable backup responsibility](../decisions/backup-and-restore.md#durable-backup-responsibility)
is accepted: PostgreSQL/deployment-native mechanisms capture/restore Durable storage;
Lattice validates and opens it, replays its required journal and builds physical
indexes before readiness. V1 introduces no Lattice-owned Durable backup archive.
The concrete PostgreSQL technique belongs to the deployment procedure. The
[accepted control boundary](#accepted-backup-and-restore-control) assigns capture and
restore execution to infrastructure, with no launch commands in the Lattice v1 API.
[Restore destination](../decisions/backup-and-restore.md#restore-destination) is accepted:
use an isolated replacement PostgreSQL target and retain former storage until the
result is verified. Stop the former writer and drain protected users, release its
arena, then load/validate restored data and rebuild indexes before readiness. This
requires a master availability gap, not a second full Lattice arena. Provider-level
preparation does not prove engine readiness; rollback after new writes needs a separate
data/recovery decision. The [accepted history policy](../decisions/backup-and-restore.md#replica-history-after-restore)
activates a fresh Lattice history before restored service readiness and fully reloads
affected child subscriptions when they detect it. Ordinary restart of the same
continuous Durable history preserves identity and remains eligible for bounded resume.
Disconnected children keep the accepted local-read contract until they detect the
new history. Exact activation mechanics remain open; the environment owns master start/stop.

The deployment procedure must define online versus paused capture, full versus
incremental capture, restore scope, retention and disk budgets. A MemoryOnly child
is not an independent Durable backup source. A checkpoint used internally for recovery
is not by itself an independently retained backup with a verified restore path.
Explicit export from a memory-only instance is a separate possible backup scope,
not automatic persistence or an already accepted feature of Q09.5.

Define artifact completeness/validation, target database/schema compatibility,
switch/history-activation mechanics and detailed failure/interruption handling within
the accepted replacement-target, new-history/full-reload and readiness contracts. Backup creation must not
claim success for a partial artifact. Verify restoration independently of the
original live process and include the time, memory and disk requirements.

No external service becomes mandatory. The delivery stage and destination adapters
are open. Online capture may need temporary retention, but that does not introduce
permanent per-update history or a public whole-query snapshot feature in v1.

## Control and maintenance tools (Q18)

**First-release scope settled on 14 November 2026:** deliver a diagnostic API through dependency
injection and ready-made HTTP endpoints, with no Lattice diagnostic CLI or GUI in
the first release. The application supplies the mounting path; Lattice owns the
endpoints, request handling and response generation. Local node status and an
on-demand master-status request through a child are required.

### DI diagnostics and a host-mounted HTTP module

Register the diagnostic service against the intended Lattice instance in the
application's DI container. Supply an HTTP adapter that maps the complete Lattice
diagnostic route group under a configurable base path in the application's existing
web host, for example `/internal/lattice`. The application registers/maps the module
and applies its existing access policy; it does not write diagnostic controllers,
response DTO assembly or the child-to-master forwarding logic.

The module owns its status subroutes and serializes consistent responses/errors.
Exact extension-method names, suffixes and DTO schemas remain implementation work;
the base path example is not a finalized SDK signature. The host retains its normal
ingress/listening configuration. Lattice does not start a mandatory separate web
server or require a public endpoint on the master. The core diagnostic service is
independent of HTTP and can also be called through DI in an embedded application.

| Operation | Source of the answer | Network work |
| --- | --- | --- |
| Local node status | The Lattice instance serving the request | No master request; a child reports its own readiness, memory and replication observations |
| Master status requested on a child | The configured master collects its own current diagnostic state | Lattice forwards the request and reply over the internal child/master protocol |
| Master status requested on the master | That master instance | Local collection; no self-forwarding |

The typical deployment is an application API hosting a child behind ingress, with
the master embedded in an internally reachable worker. A caller reaches both status
operations through the child-hosting API. The master answers the internal protocol
request even when no HTTP diagnostic module is mapped on its worker host. The child
does not access the master's PostgreSQL storage or infer its current state from the
replica's local memory.

### Status observations and failures

Use one engine-owned status collector for local DI/HTTP calls and incoming internal
status requests. Report the responding node/role and observation context; database
history and sample times must be available where needed to interpret progress.
Metadata for readiness, memory/fragmentation, journal/background application and
replication follows the owning contracts. Exact fields, units and serialization are
engineering work, not a promise to inspect every arena object for each status call.

Local status remains inspectable while loading, reloading or disconnected when the
process and diagnostic service are running. It does not wait for full data readiness
or contact the master. Unavailable measurements must be identified rather than
reported as zero. A live master-status call has bounded waiting and cancellation;
if the master cannot be reached, return explicit unavailability/timeout rather than
presenting an old observation as a current response. Last-observed master progress
may still appear in local replica status with its historical observation context.

Reuse the [accepted progress-comparison contract](../decisions/replication-and-hosting.md):
compare only compatible histories, distinguish unknown from zero lag and do not
turn a status observation into a freshness guarantee for later data reads. Ordinary
local reads and replication must not require a successful diagnostic request.
No root/index/arena lock may be held while waiting for a forwarded response. Request
framing, correlation, scheduling and numerical limits belong to internal protocol
engineering, with bounded metadata and buffers rather than a second arena.

### Accepted backup and restore control

**Accepted on 14 November 2026 under Q09.4/Q18: option A.** PostgreSQL-native backup,
isolated replacement restore, one-arena activation, new history after restore and
full child reload remain the accepted recovery contract. Infrastructure launches
backup and restore; Lattice v1 has no API commands for launching these jobs.

**Selected: deployment-owned execution.** The operator or deployment's existing
PostgreSQL tooling performs capture and restore, stops the old master and configures
the restored target. Lattice supplies a documented, verified activation/reopen path:
validate restored inputs, deliberately activate a new history for restore, rebuild
the single arena/indexes and expose readiness or a truthful failure through its
existing diagnostics. Ordinary same-history reopen remains distinct. The exact
restore-activation signal belongs to the engine/host opening contract; do not infer
restore merely from a different connection string or silently reset every restart.

This decision adds no first-release HTTP/DI service that starts PostgreSQL backup
jobs, manages their artifacts/credentials or orchestrates replacement of the running
master. It does not remove backup/restore verification from production acceptance.
A restore exercise must recover confirmed data to the backup's boundary and verify
new-history child loading before the procedure is considered ready. Retention,
schedule and recovery objectives belong to the selected deployment procedure.

**Not selected for v1: Lattice-owned administrative orchestration.** This would provide operations
that initiate native PostgreSQL/deployment jobs and track them through completion,
failure and safe activation. This still uses the accepted PostgreSQL backup format;
it adds adapters for the deployment's backup mechanism, job lifetime/progress,
required operational access, interruption behavior and coordination with the master
host. Those capabilities are additional first-release implementation and validation.

Both choices preserve resident read paths and durable write acknowledgment. Actual
backup I/O can compete with PostgreSQL commits; neither choice promises zero impact.
A needs no Lattice backup-job queue or artifact buffers. B can use bounded control
state, but adds such coordination without an inherent read/write speed or storage
memory advantage. Both retain one Lattice arena, the availability gap during restore
activation, and separate memory/storage accounting for the external backup tools.

The decision concerns who controls the work. It does not select a PostgreSQL
backup command, cloud provider, schedule, retention count, recovery-time promise or
mandatory new service. The already accepted local/master diagnostic API remains.
Other maintenance commands are outside this particular choice.

### Accepted administrative scope

**Accepted on 14 November 2026 under Q18: option A.** Automatic bounded arena compaction,
saved-base application and cleanup, index construction during loading and controlled
replica catch-up/reload are already accepted. This choice concerns explicit operator
commands exposed through Lattice's administrative module, not whether those engine
mechanisms exist. The v1 administrative API provides diagnostics; manual maintenance
commands are deferred. This settles Q18's first-release product scope, not its implementation.

**Selected: diagnostics with automatic maintenance in the first release.** Keep
local/master status, progress and resource observations. Do not add first-release
administrative commands to force compaction/trim, rebuild indexes online, pause/resume
maintenance workers or force a replica reload. The engine follows its accepted bounded
policies; the host retains the accepted close/reopen and process lifecycle for recovery.
Backup/restore launch already belongs to infrastructure. Normal data mutations,
WriteBatch and explicit position waiting remain part of the database facade.

**Not selected for v1: manual maintenance controls.** Operator-triggered work can
be useful during diagnosis or a planned quiet period. It must reuse the existing
engine mechanisms and budgets, with explicit eligible states, coordination with
ongoing automatic work, completion/failure results and defined cancellation where
supported. A command must not bypass memory admission, safe reclamation or the
single-arena constraint. Exact operations would need a bounded scope before implementation.

Both choices perform the same necessary automatic maintenance and preserve the
accepted read/write contracts. A avoids adding operator-request scheduling and its
extra state transitions; it does not remove maintenance CPU or buffer costs. B adds
bounded control/job state and validation, with potential read/write interference when
operators request additional work; no inherent speed or storage-memory gain follows.
The tradeoff is first-release implementation complexity versus manual intervention
convenience. The first-release choice is settled; SDK/HTTP implementation and validation
remain engineering work.

### Remaining maintenance and presentation scope

A usable, attractive visual interface remains a later goal, outside the first
release. Its layout, progress reporting and contextual errors must use the same
engine-owned operations. There is no CLI deliverable selected for the first release.

The broader workflow inventory is:

| Workflow | What an operator needs to understand/control |
| --- | --- |
| Inspect storage | Live data, reusable space, fragmentation, retained data and memory/disk pressure |
| Inspect node/database state | Which data is loaded, role/readiness and replication state relevant to an operation |
| Maintain storage | Whether reclamation/compaction is needed, its expected headroom and impact, and the actual outcome |
| Create/inspect backups | Consistency boundary, progress, artifact completeness, location and validation result |
| Restore | Target, compatibility, affected-data availability, progress and successful reopen |

The engine owns operations and their allowed states; future interfaces call that
contract instead of editing native memory or database files independently. This
acceptance covers diagnostic status routing, not a generic relay for arbitrary
administrative mutations. Backup/restore launch is assigned to infrastructure.
Manual engine-maintenance controls and later visual delivery remain outside the first
release. UI code must not
become a second writer or a required dependency of embedded reads/writes.

For long operations, progress, completed/failed states and interruption semantics
must be truthful. Cancellation is exposed only with a defined safe behavior, not
as a cosmetic button. UI implementation can follow later, but the operation
boundaries and necessary state information must be considered in the engine design.
The first-release diagnostic delivery and routing are settled. Exact SDK/HTTP
contracts, protocol integration and verification remain engineering work; no runtime
diagnostic module is implemented by this documentation.
