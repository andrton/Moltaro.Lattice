# Physical storage and maintenance

**Current implementation baseline:** the [milestone 1 contract](milestone-1-engine-contract.md)
now specifies the allocation, compaction, reconnect and backup mechanisms listed as open
choices below. This document still owns the diagnostics and maintenance scope (Q18);
its other open-choice notes are kept as history and do not reopen what was specified.

**Requirements recorded on 12 November 2026.** The design requires an explicit
physical-storage design, bounded memory growth over a long process lifetime,
a fragmentation/reclamation policy, and useful administration/maintenance facilities.
Q18 now selects DI-registered diagnostics and a library-owned HTTP module mounted
in the application for the first release, without a CLI or GUI. A polished visual
interface remains a later product goal. Backup and restore must be covered by the
architecture; exact algorithms and remaining operation scope are separate work.

[Architecture](index.html) · [Question register](questions.md) · [Accepted arena backing](../decisions/physical-storage.md#q16-physical-storage-and-layout)

## Accepted foundation and open choices

The accepted main arena uses explicitly allocated native segments with managed
.NET control structures and separately defined persistence. This is a design
contract; no complete storage engine is implemented yet.
Root-document gates, short protected views, owned result copies, key references
and string reference counts with delayed safe reclamation are accepted. They are
constraints on the physical design, not a completed allocator or compaction policy.

| Owner | Decision scope | Existing engineering owner |
| --- | --- | --- |
| Q16 | Native backing, record-oriented fixed fields with separate variable payloads, relative segment/offset addressing, chunked large values and database-owned active segments with a common bounded empty reserve and stable root control entries in managed arrays of structs, targeted partial preparation/post-commit publication and full Save replacement without diff accepted; compact entry layout, sizes/thresholds, exact encoding, collection internals and detailed copy boundaries remain open | E01; E04/E05 for indexes/schema layout |
| Q17 | Size-class reuse, bounded compaction, bounded empty-segment reserve/release and automatic collection shrink accepted; numerical sizes/reserve/work/headroom budgets, concrete algorithms and remaining pressure details stay open | E01/E02/E03; Q14 supplies broader pressure rules |
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
Numerical block sizes, compaction/reserve budgets, release timing and broader pressure rules remain open.
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
Numerical thresholds/work budgets and implementation details remain unresolved.
Large strings/collections now use bounded chunks, with small payloads contiguous.
A bounded empty-segment reserve and background release of excess are accepted.
Only safely detached empty segments without retained users or reservations qualify.
Availability metadata avoids scanning all segments on each allocation. Automatic
bounded shrink of substantial persistent unused collection capacity is now accepted;
this changes capacity within live allocations, unlike relocating them unchanged.
Preserve logical data, List order and revisions, and avoid repeated resizing under
small size oscillations. Remaining Q17 questions:

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
Size-class reuse, automatic incremental maintenance, chunked large-payload layout
and bounded empty-segment retention/release are accepted, as is automatic collection
shrink. These state boundaries do not select concrete free lists, chunk directories,
epoch/shrink algorithms or numerical scheduling budgets.

## Memory accounting and evidence

The eventual control surface should distinguish live payload, index/dictionary
metadata, immediately reusable free space, allocation slack, retired/pinned data,
segment capacity and temporary operation buffers. OS resident memory and virtual
address reservations are additional observations, not synonyms for live payload.
Categories must be defined without double-counting; thresholds remain Q14/Q17 work.
The accepted master-admission policy requires peak preparation/publication capacity
within an instance budget before journal submission or MemoryOnly publication. If
reservation fails, release preparation and reject without changing stored data or
revisions. Empty-segment reserves stay inside the budget. Initialized children also
have an accepted controlled-pressure policy: pause application, retain intact local
reads/status, bound buffers and resume in order. Pressure alone triggers no full reload.
These policies do not settle every query, startup/recovery or process-memory boundary.

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
