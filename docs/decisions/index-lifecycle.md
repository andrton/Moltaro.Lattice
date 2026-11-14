# Physical index lifecycle

Accepted Moltaro.Lattice decision, rationale and alternatives. This is an architectural
contract; no production index builder or storage engine is implemented by this record.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

## Q19: Should physical indexes be persisted or rebuilt?

**Accepted on 13 November 2026:** option A. Store index definitions in the
schema. Each node reconstructs its own physical indexes during loading/recovery, before
readiness. V1 does not persist or transfer physical Lattice index images. Both the primary
key index and declared secondary indexes follow this policy; live changes maintain them
coherently with the affected document. This does not disable PostgreSQL's provider indexes.

The Durable master persists schema definitions and authoritative data/recovery history
in PostgreSQL. MemoryOnly children receive definitions with the schema and build indexes
from their loaded data; they retain no local persistent image. A MemoryOnly master gets
its definitions from schema input without disk persistence. No second full arena is added.

The policy closes Q19. The [primary hash-table structure](index-structures.md) is
accepted separately under Q07/E04; its implementation, secondary structures, bounded
construction/replay scheduling and performance verification belong to E04/E06/E07.
Existing filtering/ordering and durable
acknowledgment contracts remain unchanged. See [question register](../design/questions.md#current-discussion).

### Definitions, live structures and durable data

An index definition identifies fields, their order and comparison rules. It is
part of the schema and must survive recovery/restore. A physical index is a derived
lookup structure containing search keys and record locators. Reconstructing that
structure does not require retaining historical document versions.

The primary hash index resolves a document key to a stable root control entry, which
provides its protected current location. Secondary
indexes accelerate the declared filters and ordering. Both need a startup policy;
secondary indexes being optional in the schema does not make declared indexes
optional during recovery. Concrete primary layout and secondary structures remain
E04 work under the [physical index contract](index-structures.md).

Building once is insufficient. After loading, the master maintains affected
indexes on inserts, updates and deletes; children maintain theirs while applying
replication changes. Data and related index changes must obey the existing atomic
publication contract. An update to an unindexed field need not change search keys,
but relocation may still require locator maintenance.

### A. Rebuild physical indexes from data — accepted for v1

Persist data, schema/index definitions and the recovery information required by
Q09. Construct indexes as records load, or use a bulk build where measurements
justify it. Apply journal/replication changes with consistent index maintenance.
The exact scheduling of recovery and bulk construction remains engineering work.

- **Startup:** trades reading/installing index bytes for CPU work extracting keys,
  comparing/hashing values and allocating structures. Some work may overlap data
  loading, but startup is not guaranteed to remain limited by disk/network speed.
- **Reads after readiness:** for the same final structure and layout, query behavior
  has no inherent penalty because the index was rebuilt rather than loaded.
- **Writes:** normal in-memory maintenance remains necessary. A separate durable
  representation of index entries is unnecessary if replaying durable data changes
  reconstructs them correctly; data durability itself is unchanged.
- **Memory:** ready indexes still consume RAM. Construction buffers, temporary
  locators and retained catch-up data add to the startup peak.
- **Disk and complexity:** no physical index image to checkpoint, validate or migrate.
  The engine instead needs a correct, bounded builder and an index-independent way
  to enumerate recoverable records. Slow recovery cannot be hidden by early Ready.

#### What avoiding index flush actually saves

With A, the engine does not flush physical index contents to disk during initial
construction, ordinary maintenance or checkpoints. Index-entry changes need no
separate durable log if replaying authoritative data changes reconstructs them.
The schema/index definitions and durable document changes still need persistence;
the accepted Durable acknowledgment policy still requires reliable data-journal commit
and coherent publication. Removing index persistence does not remove that durability wait.

Against B's checkpoint-only image, the direct saving is the image's serialization,
disk space and checkpoint/backup write traffic. Both alternatives can keep ordinary
index mutations in RAM between checkpoints, so A is not automatically faster on
every application write. Lower checkpoint I/O contention may help write latency,
but that effect must be measured under the same durability policy and workload.
A continuously persisted mutable index would have a different I/O cost profile;
do not attribute that cost to B's checkpoint-only design.

The corresponding cost is full construction on each startup that rebuilds indexes,
using CPU, memory bandwidth and temporary workspace. Later mutations normally
maintain affected entries incrementally rather than rebuild the whole index.
Construction may overlap data loading; bulk construction may instead need a later
phase. Measure actual wall-clock readiness rather than assuming that load and
build times always add, or that all building can be hidden behind I/O.

### B. Save a rebuildable index image with a consistent checkpoint — not selected

Save physical indexes at the same committed boundary as the corresponding data
and metadata. Load a compatible image, restore valid locators and apply the later
journal changes to both data and indexes before readiness.

- **Startup:** may avoid expensive comparison/allocation work, but adds index I/O,
  validation and possible relocation. Reading a large image can cost more than a
  local rebuild; neither option is universally faster.
- **Reads and memory:** comparable once equivalent indexes are ready. Installation
  buffers and address translation have their own peak cost; no automatic RAM saving.
- **Writes and disk:** live index maintenance remains. Checkpoints/backups containing
  images are larger and capture more state. A checkpoint-only image does not require
  a second disk write or a separate index log record on every document mutation.
- **Complexity:** define format compatibility, data/index boundary matching, locator
  relocation and behavior for an absent, outdated or unusable image. Such an image
  must never be mistaken for authoritative document data or used while inconsistent.

Persisting a continuously mutable on-disk index is another design, coupled to Q16
and Q09. B here specifically means a checkpoint image, not an implicit commitment
to that more tightly coupled persistence mechanism or to multiple v1 modes.

### A child also needs local indexes

Master construction alone cannot accelerate a child's local searches. The decision selects local rebuilding; the transfer-image alternative below records its tradeoffs:

| Child startup path | Benefit | Cost and constraints |
| --- | --- | --- |
| Receive data and definitions; build locally — accepted | Reuses the recovery builder; sends fewer bytes; local locators are natural | Each new child spends CPU and temporary memory; simultaneous starts multiply that work |
| Receive data plus a compatible index image — not selected | Can reuse master construction across children and reduce child build CPU | Sends more bytes; needs a consistent capture boundary, compatible layout and valid local locators; master capture/egress costs must be measured |

The accepted v1 decision selects local reconstruction on both master and children,
with no physical images persisted or transferred. Master persistence and child transfer
are technically separable: a master could transmit an image without persisting it, or
persist one while children rebuild. Those hybrid policies are not selected for v1. Raw absolute
native pointers cannot simply be reused in another process; relocation/handles and
schema/comparison compatibility remain necessary.

### Decision rationale and practical limits

The decision selects A for v1: preserve definitions, reconstruct
both primary and declared secondary indexes on each node, and maintain them in memory
after loading. V1 excludes physical index images and therefore does not need their
persisted format, consistent capture or network relocation. This concerns Lattice indexes;
PostgreSQL still owns any indexes required by its journal/saved-state tables.

A does not save steady-state index RAM: equivalent ready structures use comparable
memory. It adds key extraction, hashing/comparison, allocation and possibly bulk-build
workspace to every full initialization. B may reduce construction CPU, but increases
persisted/transferred bytes and needs validation/relocation. Neither is universally
faster. The accepted 256-byte PostgreSQL saved unit does not determine index nodes or
network image granularity. Index building must remain within bounded workspace and
must not introduce a second full data arena.

For example, restarting ten MemoryOnly children means ten local builds under A. B can
reuse a compatible captured index image across those starts, but must deliver its bytes
and install valid local references on each child. Normal queries and affected-index
updates still occur locally in both designs. No time, throughput or RAM multiplier is
claimed before a representative implementation is measured.

### Correctness and readiness requirements

- The persistent/base format must identify recoverable live root records without
  relying on a discarded index. Allocation holes and superseded/deleted records
  must not become live documents during reconstruction.
- Primary-key lookup needed during replay must exist or have a bounded temporary
  mechanism whose cost is included. Bulk builders cannot race changes and publish
  an index for an older state over newer data.
- All selected data and declared indexes must describe the same recovered/applied
  boundary before Ready. Startup failure must remain visible; no incomplete index
  may produce a successful but incomplete search. Existing no-demand-loading v1
  behavior is unchanged.
- Preserve key equality, null placement, composite ordering and string comparison
  rules. Rebuilding must use the active schema and comparison semantics, not merely
  matching field names. V1 always rebuilds; no image/rebuild mode switch or fallback
  policy is selected.
- Backup/restore Q09.4 must retain the schema and enough authoritative data/recovery
  information to rebuild indexes. Physical Lattice index images are excluded in v1;
  PostgreSQL's own storage indexes are a separate provider concern.

### Planned verification and evidence

The policy is accepted; native index structures, formats of authoritative data and
builders remain engineering work. A CLR Dictionary timing alone does not establish
native reconstruction or full-readiness performance. No new benchmark is reported here.

Verify rebuilding primary and declared secondary indexes from authoritative data on
master recovery, child startup, full reload and restore. Cover single/composite keys,
plain/dictionary strings, nulls, duplicate secondary values, deletes and updates during
loading. Check that no query opens against incomplete/stale indexes, no physical index
image is persisted or sent, and rebuilding uses bounded workspace without a second
full data arena. Verify identical query results before and after reconstruction.

Measure data transfer/read, journal replay, index build, catch-up, peak memory and
end-to-end readiness separately, including simultaneous child starts. After readiness,
measure ordinary reads and affected-index updates. Existing storage/socket/layout
prototypes do not certify this complete pipeline. A future image comparison needs a
concrete compatible format and explicit scope; implementing the rejected image path
is not a prerequisite for the accepted v1 policy.
