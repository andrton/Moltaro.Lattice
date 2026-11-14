# Replica startup and hosting

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md)
now specifies the mechanisms and starting values described as open, partially accepted or
illustrative below, and the [decision register](../design/questions.md) owns the current
status. The earlier notes are kept as the decision's history; they do not reopen what was
specified. "v1" below is wider than the first production milestone, whose scope is in
the [planning handoff](../design/planning-handoff.md).

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

- [Q08](#q08-can-a-child-load-everything-before-accepting-reads)
- [Q08.1.1](#q08.1.1-how-are-live-changes-delivered)
- [Q08.1.2](#q08.1.2-what-may-a-lagging-or-disconnected-replica-read)
- [Q08.1.3](#q08.1.3-how-does-a-replica-recover-after-falling-behind)
- [Q11](#q11-can-the-master-run-inside-the-application-process)
- [Q15](#q15-configured-master-and-child-connection)

## Q08: Can a child load everything before accepting reads?

**Accepted on 12 November 2026:** the first version fully replicates
the database state from the master before accepting reads. Do not implement partial
availability or request-time page fetching. Continuous updates after readiness
remain required; excluding demand loading does not mean a one-time stale copy.
The later [Q08.1.1 decision](#q08.1.1-how-are-live-changes-delivered) selects active
push of committed binary logical changes without an invalidation mode in v1.

Queries over the database would wait for all relevant data anyway. Full startup time is a workload-specific measurement, not a consequence of stream
bandwidth alone. Readiness includes data installation, index construction and catch-up.
The first local prototype has a separate numerical load gate; it does not establish
a network-replica startup guarantee.

**Clarified on 13 November 2026:** the master is the child's only source of initial
state and subsequent changes. A v1 child is MemoryOnly, has no PostgreSQL dependency
or connection, and does not read the master's storage. It loads the full database,
builds its local indexes and applies changes through a recorded published master
position before becoming Ready. It then continues applying confirmed changes in the
background through the [shared arena application mechanism](#shared-arena-application-on-masters-and-replicas).

Initial transfer and the live stream must join without a gap. Arrange bounded
retention/delivery of changes during loading, not only after Ready. The complete
database at the readiness boundary is required; zero lag for future writes is not.
The [initial capture and readiness handoff](#initial-capture-and-readiness-handoff)
is accepted below. Framing and production integration remain E07 work without a
child storage provider.
Both initial state and subsequent delivery come from the master's memory. PostgreSQL
serves Durable master recovery, not initial loading or catch-up for children. See
[pending delivery in memory](#pending-delivery-in-memory).

### A. Load the selected replica completely, then accept reads — accepted for v1

- **Reads:** no startup page faults or missing-page waits after readiness. Before
  readiness the new child takes no application traffic; existing replicas carry
  it. Long loading therefore still delays scale-out.
- **Writes:** the master continues accepting writes. Each captured root is coherent;
  different roots can reflect different published positions. Per-root watermarks
  and the ordered change tail reconstruct one coherent state before readiness.
  The copied base alone is not a database-wide snapshot.
- **Memory:** each ready child holds the full database. Fewer per-page availability
  structures and waiters are required, but the base image, retained journal,
  receive buffers and indexes still consume memory. Streaming directly into the
  final arena can avoid a complete staging copy on the child.
- **Implementation:** one startup state machine: Connecting → Loading → CatchingUp
  → Ready. A single read gate replaces faulting individual reads during startup.
  The master still owns bounded transfer/checkpoint lifetimes and reconnect logic.
- **Limit:** an empty child cannot become ready while the master is unavailable.
  Already-ready children continue local reads under the accepted Q08.1.2 policy.
  The later Q08.1.3 decision requires MemoryOnly children in v1; a child process
  restart always requires full loading from the master.

### B. Serve partially loaded data and fetch missing pages — excluded from v1

- **Reads:** some requests can succeed sooner, but cold reads wait on the master.
  Complete searches may need many missing segments, not merely the requested key.
- **Writes:** publication must coordinate base pages with ordered changes, including
  updates to data not present yet. Per-page installation rules become necessary.
- **Memory/network:** initial loading still fetches the full database. There is
  additional availability metadata and bounded waiter/change state. Random demand
  fetches can interfere with sequential transfer; no measured overhead is known.
- **Implementation:** loading state becomes part of record, index and query access.
  Shared fetch ownership, cancellation, failure and full-result semantics must be
  designed. This is substantially more work than a startup gate.
- **Benefit:** useful if full initialization misses the startup budget and early
  traffic has enough locality to benefit. It does not create more master bandwidth.

### What must actually be streamed?

Use engine-owned binary arena-block transfer with schema/layout identifiers and enough
metadata to install the complete state in the child's arena. Exact initial-transfer
encoding remains E07 work. A raw CLR heap dump or absolute native pointers cannot be
interpreted as the child's live arena at a different address. Representation must
be compatible with the receiver; allocated native backing is accepted
separately in Q16. [Q19](index-lifecycle.md) accepts transferring index definitions
with the schema and rebuilding physical indexes locally on every node. V1 does not
persist or transfer physical Lattice index images. Complete readiness measurements
must include local index construction and catch-up.

While the master mutates data, copying its current memory sequentially can mix
different committed states. Initial transfer must therefore coordinate its base
and ordered changes so they reconstruct a coherent published boundary. The accepted
capture/handoff mechanism below also applies to a MemoryOnly master; its production
implementation remains E07 work.
No native reader lock may be held throughout a multi-second socket transfer. A pause
of master writes for the whole transfer is not implicitly selected by this contract.

After base installation, catch up through a recorded barrier R, then transition
to continuous replication without a gap. Ready means coherent and caught up to
that barrier; it does not mean future master commits have zero network latency.
Let master changes arrive at rate lambda and child application run at rate mu in
the same units. Catch-up cannot converge under sustained load unless mu exceeds
lambda. Retain journal history only within a budget; an expired base requires a
new base rather than permanent versions.

### Initial replica-state representation

**Clarified on 13 November 2026:** initial replication prioritizes startup speed
through bulk transfer of binary native-arena data blocks with the metadata needed
to install them on the child. This carries forward the memory-transfer requirement;
it is not an open choice between block transfer and per-record re-encoding. The
independent record-stream proposal is withdrawn as the default initial-load path.

The master supplies schema/layout identification and block/placement metadata along
with the arena payload. The child installs it directly into its one native arena,
preserving or translating relative references as required. Process-local pointers,
locks and reader state do not become live child state by copying their bytes.
Reconstruct local runtime ownership and control structures, rebuild physical indexes
under Q19, then apply the ordered change tail through the readiness boundary.
Required metadata inspection and index building may visit records; they do not
require serializing every record field into a second logical wire representation
or constructing a full intermediate CLR object graph.

Use bounded transfer/install buffers and account for required metadata. The baseline
does not require a second full arena. Exact block framing, treatment of unused ranges,
layout compatibility, reference translation and string/dictionary reconstruction are
E07 implementation design and measurement tasks. Do not turn them into a repeated
approval choice about transferring memory versus records.

This initial image is distinct from the accepted independent PostgreSQL base format.
Durable and MemoryOnly masters both supply initial data from the master to the child;
the child remains MemoryOnly and never connects to PostgreSQL. The accepted subsequent
binary logical mutations are unchanged. The child's own later physical compaction
does not need to mirror the master's physical placement.

Bulk copying alone does not produce a coherent image while writes continue. Capture
must coordinate schema/data/block lifetimes and the retained change tail, preserve
WriteBatch boundaries and join live delivery without gaps before Ready. No arena/index
gate spans socket I/O, no pause for the entire network transfer is implicitly selected,
and retained changes/buffers remain bounded. The capture/handoff mechanism below is
accepted; production block/lifetime integration and concurrency/failure verification
remain required. No production replication engine is implemented.

The existing ReplicaStreamComparison is a raw loopback baseline, not proof of full
startup performance. Further bounded checks must include coherent capture, transfer,
installation, local indexes, catch-up and master read/write impact. The objective is
minimum end-to-end readiness time under the accepted memory bounds, not a claim of
zero-copy networking or an achieved startup-time guarantee. No new benchmark ran
for this clarification.

### Initial capture and readiness handoff

**Accepted on 13 November 2026 for v1:** copy coherent current roots from a fenced
control-entry range while retaining the required logical changes; use per-root
capture watermarks and temporary deletion metadata to reconstruct the complete
database at a fixed published readiness position. This extends Q08/E07 without
requiring a historical database-wide snapshot, permanent object versions or a
second full arena.

1. At published position **S**, register the initialization's claim on pending
   delivery and record the current control-catalog range in the same short
   coordination step. Scan that range once. New roots and roots recreated under
   the same type/key are represented by complete Upserts in the retained tail.
2. Do not reuse fenced control slots while the capture still needs them. A deleted
   fenced entry retains its type/key and deletion revision, not its old object
   payload. Release payloads when ordinary protected-access rules permit it.
   Capture emits the deletion watermark so earlier partial effects are recognized
   as already covered. Recreation cannot reset the ordering information and make
   old effects appear newer than the new lifetime.
3. Copy each root's blocks and capture revision coherently under the appropriate
   root/shared-data lifetime protection. Release protection before network I/O.
   Copied roots may reflect different published positions; keep the provisional
   child arena unreadable. Transfer binary blocks with installation metadata;
   this does not select per-field re-encoding as the default path.
4. After the complete fenced scan, record published position **R**. Install every
   base frame before replaying complete transactions in **(S, R]** in order.
   For each effect, compare its order with that root's captured/current watermark
   and skip only effects already covered. One WriteBatch member can be covered
   while another still needs application; never skip a whole batch using one
   member's revision. Account for the complete batch before advancing replay.
5. Open reads and expose the applied position **R** only after all base frames,
   complete transactions through R and required local indexes are ready. No applied
   acknowledgment before that boundary may certify the provisional mixed state.
   An internal processing/transport cursor is not an applied replica position.
   Changes after R remain pending and follow through the ordinary live path;
   readiness does not wait for a continuously moving latest-master position.
6. Release acknowledged delivery after all participating recipients and active
   sends release it, without an extra history window. Release this initialization's
   catalog-retention claim and deletion watermarks when no longer needed; another
   active capture can still own a claim. Missing base/tail ranges or exhausted
   capture/delivery retention invalidate initialization and require full reload,
   never partial readiness or a PostgreSQL fallback.

Catalog/deletion metadata, pending changes and detached copy/install buffers must
all fit finite budgets. A slow initialization cannot retain them indefinitely.
The fixed range prevents repeated scanning of newly appended identities, but its
temporary unreusable slots still have a cost. No numerical defaults are selected.

The [bounded correctness experiment](../design/prototype-evidence.md#bounded-replica-bootstrap-correctness)
passed 127 checks and reproduced three expected counterexamples for weaker rules.
It supports this ordering mechanism on small blocks, not full-engine correctness
or startup performance. Exact capture/block/reference handling, shared strings,
chunks, multiple simultaneous children, index-building integration and metadata
budgets remain E07 engineering work. The model's single publication gate and its
transaction-position revision encoding are not production implementation choices.
A correct published transaction order is required; its concurrent master scheduler
now follows the separately accepted [concurrent preparation and ordered
commit/publication stage](write-acknowledgment.md#ordered-commit-and-publication).
That pipeline's production integration and failure handling remain engineering work;
the bootstrap model does not verify them.

### Throughput budget

Network rates below are decimal gigabits/second. Image sizes use GiB (2^30 bytes)
to remove ambiguity from the stated approximate GB target. These are arithmetic
lower bounds for one transfer with the entire link available to payload:

| Link rate | 5 GiB | 10 GiB |
| --- | --- | --- |
| 1 Gbit/s | 42.95 s | 85.90 s |
| 10 Gbit/s | 4.29 s | 8.59 s |
| 25 Gbit/s | 1.72 s | 3.44 s |

Framing, other traffic, container CPU limits, source reads, allocation, validation,
indexes and catch-up add cost. Some phases can overlap, so measure the critical
path instead of simply adding all component times. For N simultaneous children
receiving an uncompressed image S from one master, its aggregate egress is about
N × S; one fast child does not demonstrate fast horizontal scale-out.

### Experiment and acceptance boundary

[ReplicaStreamComparison](../../src/Prototypes/ReplicaStreamComparison/README.md) is a
bounded first transport experiment: one local TCP connection, an existing source
arena and a fully retained destination arena. It measures receive allocation and
first touch, then verifies every received byte separately. It is not a distributed
database startup benchmark.

Two local 5 GiB runs took 8.94–10.79 seconds for transfer and destination allocation,
plus 1.29–1.30 seconds for full byte verification. Source preparation was separately
1.03–1.10 seconds. All bytes verified correctly. These results neither measure
the production network nor establish a complete database-readiness time.

The decision needs representative cross-node measurements of:

1. Process start to readable database state, with separate timings for finding or
   capturing the base, transfer, installation/index preparation and change catch-up.
2. The same path with continuous writes at representative update rates, checking
   final content and atomic visibility rather than transfer byte counts alone.
3. Multiple children starting together, including effects on existing readers,
   master write throughput, memory and outbound bandwidth.
4. Container start and readiness under actual CPU/memory/network limits, with
   scheduling and image-pull time reported separately from engine initialization.

The roughly 5–15 second window is the stated working expectation. Dataset byte size,
concurrent child count, update rate and acceptable startup percentile must be stated
before it becomes an acceptance test. A socket-only local result cannot select
the production startup architecture by itself.

**Decision:** A is accepted for v1. Load the full selected replica, prepare its
indexes and apply changes through the readiness barrier before opening reads.
No partial-availability or request-time page loading path is planned for v1.
Measure and optimize startup without treating an unverified timing expectation
as an achieved guarantee.

<a id="q08.1.1-how-are-live-changes-delivered"></a>

## Q08.1.1: How are live changes delivered?

**Accepted on 13 November 2026:** option A for both delivery and payload.
The master actively pushes committed binary schema-level mutations to subscribed
children. Complete Save/Insert/Replace/resolved Upsert sends the complete resulting
document; explicit partial updates send their committed field/collection effects.
V1 has no invalidation mode or automatic diff of a complete Save. Updates continue
after readiness even when the child has no application queries.

Each root mutation becomes visible coherently with local indexes and revision metadata.
This does not add a child-acknowledgment barrier to master write success or promise
zero lag. The payload contract below closes Q08.1.1; exact byte encoding, headers,
framing and batching belong to E07 engineering work. Initial state capture/transfer
remains separate; [Q19](index-lifecycle.md) now requires local index reconstruction
before readiness, without physical index images. The later
[Q08.1.2 decision](#q08.1.2-what-may-a-lagging-or-disconnected-replica-read) accepts local
reads during lag/disconnection and explicit master-progress checks. Q08.1.3 now
accepts [bounded resume or full reload](#q08.1.3-how-does-a-replica-recover-after-falling-behind);
single-arena reload with bounded read waiting is also accepted below; numerical
budgets remain open; MemoryOnly children fully reload after process restart.
This is an accepted design, not an implemented or benchmarked replication engine.

### Existing boundaries

A child fully installs its database state and required indexes and catches up before
becoming Ready. Continuous updates remain required after startup, even without local
queries. Applications write through the master. Local caller-owned copies remain
independent of subsequent arena updates; protected borrowed views remain read-only.

Durable master success requires committed storage and coherent master publication,
without a selected child-acknowledgment barrier. MemoryOnly has no PostgreSQL wait.
Neither delivery alternative below makes replication instantaneous. Q08.1.2 now
accepts local reads during lag/disconnection with separately observable progress.
Q08.1.3 accepts bounded resume or full reload, with remaining reload/retention details
open. Q15 excludes engine-managed failover and child promotion in v1.

### Delivery option A: push committed update data — accepted

After the initial load, maintain an ordered stream from the master to each subscribed
child. Send enough committed update data for that child to apply the change without
an additional fetch for the affected content. A child applies each complete root
mutation coherently with its local indexes and revision metadata. Local queries do
not trigger delivery; updates continue while the child has no application traffic.

For example, after a partial update changes a numeric field, its committed update can
carry the target identity and final value, with the ordering/schema/revision metadata
required by the eventual protocol. Delete must remove the local root and index entries;
a full Save sends the complete resulting owned document. The accepted binary logical
payload contract below excludes automatic diffing of full Save in v1. Explicit partial
updates carry committed effects instead of unrelated unchanged document content.

Transmit committed effects, not arbitrary caller property assignments or application
callbacks. A child must not independently reinterpret a caller's expected-revision
condition against a different local state. Ordered collection effects must remain
correct. Schema versions, string dictionaries, local handles and payload validation
need a deterministic protocol; process-specific native addresses are not portable.

Use bounded send/receive work and preserve mutation boundaries. Q08.1.3 accepts
bounded retained-tail replay or full reload; a lagging child cannot force unlimited
RAM queues or retained history. Full reload uses one arena with bounded readiness
waiting for reads. MemoryOnly children fully reload after process restart. Exact
budgets remain Q08.1.3/Q14 work. The stream can use a persistent direct connection
without an external broker. Exact socket protocol, framing and batching remain open.

Advantages: fits a full local replica, avoids a per-change invalidate/fetch exchange,
and keeps background progress independent of reads. Once applied, changes are usable
by both ID lookup and searches through updated indexes. Ordinary reads stay local.

Costs: every subscribed child receives the database changes even if it never reads
those records. Master outbound bandwidth and child apply work grow with update volume
and subscriber count. Serialization, index maintenance and bounded buffers still cost
CPU/RAM. Atomic root publication may briefly contend with local readers; no general
read-latency or throughput figure is established by this design.

### Delivery option B: invalidate and refresh — excluded from v1

Send a notification identifying outdated content or a changed generation. The child
then requests current replacement data, individually or in bounded batches. The
refresh can run in the background so a child still tries to become current without
application queries; demand-only refresh would not meet that requirement.

Advantages: repeated invalidations can sometimes be combined before fetching the
latest state. This may avoid transferring intermediate versions that will never be
read, when the freshness and ordering contract permits it. Notification payloads can
be small, but they are not the whole cost: changed content must still be fetched.

Costs: an additional stale/refreshing state machine, request scheduling and an extra
exchange before replacement data arrives. Notifications, fetch responses and later
updates must be ordered so an old response cannot overwrite newer installed data.
Deletes and revision identities must remain coherent. Missing connectivity can leave
an invalidated record unable to refresh; it must not be reported as confirmed absent.

Searches also need attention: a stale secondary index can omit newly matching records
or return outdated matches. Marking only a document as stale does not make a complete
query correct. Refresh must coordinate data/index publication; waiting, temporary
unavailability or serving explicitly stale data needs the separate freshness contract.
This alternative cannot silently reintroduce the excluded missing-page startup path.

If all selected records are refreshed promptly anyway, transfer savings may be small
while scheduling and round trips remain. Batch invalidation/fetch can amortize those
costs but needs its own budgets and correctness rules. No measured advantage is known.

### Delivery comparison and rationale

| Aspect | A: push update data | B: invalidate and refresh |
| --- | --- | --- |
| Ordinary ready-node reads | Local data and indexes; background updates continue | Local once refreshed; stale records/indexes need an explicit policy |
| Delivery latency | Transfer and apply the update | Notify, schedule a fetch, transfer and apply replacement |
| Master write acknowledgment | No additional child wait selected | No additional child wait selected |
| Network | Sends database changes to each subscribed child | Small notifications plus fetched content; possible coalescing savings |
| RAM | Full selected replica and bounded transfer/apply buffers | Full database plus stale/refresh state and bounded fetch buffers |
| Complexity | Ordered transfer and coherent application | Those correctness needs plus invalidation/fetch races and query freshness |

The decision selects **A** for v1, without an invalidation mode or a policy switch.
It supports full local replicas and background convergence without turning a normal
read into a refresh operation. It does not promise zero lag or lower bandwidth in
every workload. Q08.1.2 settles local reads/status, and Q08.1.3 settles bounded
reconnect replay or full reload, with one arena and bounded read waiting. Numerical
budgets stay open; child restart uses full loading with MemoryOnly.

This is an architectural comparison, not a new benchmark. The following payload
contract selects the logical transfer unit. Compression, batching, schema/dictionary
encoding and transport details remain engineering work. Q08.1.2 supplies the separate
read/progress contract. Q08.1.3 accepts bounded replay or full reload using one arena
and bounded read waiting. Child restart is MemoryOnly/full load; numerical limits
remain open.

### Live payload contract — accepted for v1

On 13 November 2026 the decision selects binary logical changes: full documents for
complete writes and explicit changes for partial updates, without automatic diffing
of full Save. Full-image-only and physical-mirroring modes are not selected for v1.
This governs live updates after initialization; initial full-state capture/transfer
is separate. [Q19](index-lifecycle.md) requires local physical index construction
before readiness, without persisted/transferred index images.

### Shared arena application on masters and replicas

**Clarified on 13 November 2026:** use one engine mechanism to prepare and publish
logical changes into the local arena, including owned payloads, string references,
indexes and revision state. Master writes and child replay share this mechanism;
the orchestration around it differs by authority and persistence mode.

| Path | Input and preparation | Before publication | Local application |
| --- | --- | --- | --- |
| Durable master | Validate an application command, resolve its effect and reserve required resources | Commit effects and operation result in PostgreSQL | Publish prepared arena/index changes, then expose confirmed changes to replication |
| MemoryOnly master | Validate and prepare the same kind of application command | No durable-storage step | Publish prepared arena/index changes, then expose confirmed changes to replication |
| MemoryOnly child | Receive confirmed binary effects from the master; verify history/order and prepare local resources | No local SQL commit or application-write admission | Publish through the same arena/index mechanism and advance applied progress coherently |

The child applies the effect already resolved by the master. It does not rerun
application callbacks or decide an expected-revision condition as a new application
write. It still checks protocol/schema compatibility, ordering and local resource
availability. Master revisions are installed, not replaced by independently generated
child revisions. Already-applied effects and gaps follow the existing replay contract.
Child replay does not send the same mutation back as a new write to the master.

Shared application means shared state-change and publication logic, not identical
entry points, queues, native addresses or mandatory journal/wire byte encoding.
Each node resolves objects and allocates in its own arena. Read protection, bounded
memory and explicit WriteBatch atomicity remain the same. Durable master preparation
reserves the required publication resources before commit; no native gate spans
PostgreSQL or transport I/O. This does not select a single global execution thread.

The PostgreSQL worker that coalesces the journal into the saved base is a separate
persistence responsibility inside the master. It updates provider-owned saved units,
not a child's arena, and is not part of the child application path.

### Explicit WriteBatch boundaries

The accepted [Q01 WriteBatch](document-model.md#atomic-writebatch-within-one-database)
adds a logical group boundary around its member mutations. Preserve that boundary
through transfer, buffering, replay and child publication. A subscribed child prepares
all members before closing the database for group publication; it must not expose
an installed prefix or advance applied progress past a partial batch. Existing
bounded memory/backpressure and one-arena rules remain in force.

This does not make unrelated internally grouped SQL writes an application transaction.
Partial network receipt is not permission to apply a prefix of an explicit WriteBatch.
Master success still does not wait for child application/freshness, and separate reads
or scans can cross the child's publication boundary. Exact frame layout and recovery
mechanics remain E07 work.

### Shared requirements

Every alternative carries enough identity, history/order, schema and revision
information to install a coherent committed root state. Exact headers, identifiers,
framing and compression are not selected here. The stream preserves per-root mutation
boundaries; receiver application maintains related indexes and safe string/owned-memory
lifetimes. It does not require sending physical index changes.
It sends no speculative application edits and does not rerun application callbacks.
The master has already resolved existence and optional expected-revision conditions.

A child's local reads and caller-owned copies keep their accepted contracts. Network
receipt is not publication; a partial message cannot become a partially updated root.
Duplicate or out-of-order delivery must not repeat collection edits or restore older
values. Detect missing/incompatible history and follow an explicitly defined recovery
policy under Q08.1.3; no silent skipping or unbounded catch-up history is implied.
All alternatives need bounded transport/preparation resources and atomic publication.

### Payload option A: binary schema-level mutations — accepted

Use one logical protocol with different payload kinds matching the committed effect:

| Committed change | Accepted payload |
| --- | --- |
| Insert or complete Save/Replace | Complete resulting owned document with its key and new revision |
| Upsert | The actual create/replace effect resolved by the master, with the complete resulting document |
| Explicit scalar partial update | Target fields and their final assigned values |
| Whole-collection replacement | Target collection field and its complete resulting value |
| Explicit collection element edits | Ordered committed List/Map/Set effects sufficient to reproduce the same result |
| Delete | Root identity and ordered removal metadata |

Encode typed values using the schema in a binary representation. This is not JSON,
a CLR heap image or a requirement to materialize an intermediate POCO on the receiver.
Field identifiers, value encodings, schema compatibility and precise command forms
remain engineering work within the selected model.

Full Save deliberately sends a complete logical value in this v1 baseline. There is
no automatic old/new document comparison, client dirty tracking or adaptive choice
of payload format. When the caller already knows a small change, the separate explicit
partial API produces the smaller committed update. Local physical copying and actual
PostgreSQL bytes written remain independent of this public/network distinction.

The receiver maps identities to its own arena, applies the committed effect and
maintains affected local indexes. Positional List edits require the correct prior
root state and submitted operation order; they cannot be applied to an arbitrary
newer/older state. The protocol must suppress already-applied mutations and detect
gaps using its history/order contract. The caller's optimistic condition is not a
new application write to evaluate again on the child.

String values must be meaningful independently of sender-local StringIds and pointers.
The receiver can intern supplied text into its own accepted database dictionary.
A transport string dictionary would need an explicit lifecycle/protocol; it is not
selected here. Logical values permit different local arena locations and compaction
schedules. A supported logical/schema protocol is still required; this is not a
promise of arbitrary mixed software/schema versions in v1.

Advantages: sparse explicit updates send little unchanged content. Replica allocation
and physical compaction need not mirror the master. The committed mutation machinery
can share semantics with the local writer and recovery pipeline, although an identical
journal/wire encoding is not assumed.

Costs: typed decoding, local lookup, string interning, collection application and index
maintenance. Some element edits can move or allocate many local bytes even when the
network message is small. Full Save remains proportional to document size. More payload
kinds and ordered replay cases are required than for full-image replacement alone.
No zero-copy, constant-time apply or measured throughput improvement is claimed.

### Payload option B: complete document image after every change — not selected

Send the resulting full owned document after any successful mutation, including a
one-field partial update or a single collection append. Delete still needs an ordered
removal marker. The image can be schema-aware binary data; it need not be JSON or a
raw native allocation dump. Receiving it installs the new value with its indexes and
revision coherently. Duplicates and older images still need order checks.

Advantages: fewer live payload kinds and no positional collection-operation replay
on the child. A replacement supplies the full state of its root. This can be adequate
for small documents or infrequent updates, and bulk image decoding may be efficient.

Costs: a small mutation of a large document transfers unchanged fields, strings and
collections to every subscribed child. Serialization, receive buffers and local
replacement/index work can outweigh the simpler protocol. Peak memory depends on
publication strategy; a full second database image is not required, but preparation
for an affected large document can still be costly. Per-root images do not make a
gap in the global selected update stream safe to ignore.

### Payload option C: physical block or byte-range changes — not selected

Replicate changes addressed by logical block identifiers or relative offsets, with
portable metadata; never send absolute process addresses as usable child pointers.
The child needs a matching physical representation or a defined translation layer.
Changes to allocation metadata, string dictionaries and index structures must either
be represented consistently or handled by an explicit separate local-rebuild strategy.

Advantages: compatible layouts may allow efficient bulk copying with less logical
field decoding. A small change can transfer a small affected range when the physical
format and change-tracking mechanism expose one. The exact benefit depends on layout,
alignment, update units and copying/application costs.

Costs: allocator, format, dictionary-ID and layout compatibility become part of the
replication protocol. Compaction may move unchanged logical content and create extra
traffic or require remapping. One root may span many blocks, and a block can contain
parts of multiple roots; dirty-block delivery alone does not preserve root atomicity
or exclude uncommitted neighboring bytes. A consistent committed change set and
publication protocol are still needed.

Sending an entire dirty block may amplify a tiny update; variable byte ranges reduce
that amplification but require their own tracking and framing. The accepted 256-byte
PostgreSQL saved-unit baseline does not select native allocation units or wire blocks.
This option cannot be evaluated honestly as a memcpy-only benchmark while excluding
allocation, metadata, indexes, dictionary lifetime and compaction behavior.

### Illustrative size comparison

Assume an encoded root is 64 KiB and an explicit update changes one 8-byte numeric
field. B sends roughly 64 KiB of document content to each child. A sends the 8-byte
value plus key, field, ordering, revision and protocol overhead. C sends the affected
range or block plus its metadata; its size cannot be stated until the physical format
and change unit are selected. These are structural examples, not measured packet sizes
or compression/throughput results. A complete Save still sends the full value under A.

### Payload comparison and decision

| Aspect | A: logical committed mutations | B: full document each time | C: physical changes |
| --- | --- | --- | --- |
| Sparse explicit updates | Small logical input; local application work remains | Full document traffic | Range/block size and shared metadata determine amplification |
| Complete Save | Full resulting document; no automatic diff | Full resulting document | Physical change detection determines the payload |
| Child CPU | Decode and apply typed effects; maintain indexes | Decode/install image; maintain indexes | Potential bulk-copy benefit, plus metadata/publication work |
| RAM | Bounded messages and preparation; full Save can still be large | Bounded full-document preparation | Bounded block staging and compatibility/mapping metadata |
| Compaction | Local physical moves need not be replicated | Same for portable logical images | Physical movement can affect protocol and traffic |
| Complexity | Several mutation kinds and ordered replay | Simpler payload set, greater write amplification | Strong coupling to an as-yet-unselected physical format |

The decision selects **A** for v1: full values for complete writes and explicit committed deltas
for partial updates, encoded in binary. It matches the accepted Save/partial split,
serves frequent small changes and leaves the arena layout independently optimizable.
It does not select automatic diffing for full Save, a physical mirroring mode or a
configuration switch among these alternatives.

The decision is based on contracts and expected data movement, not a measured
speed ranking. A focused prototype should compare equivalent full and sparse writes,
including collection edits and repeated/unique strings, across representative document
sizes. Measure encoded bytes, preparation/apply CPU, peak buffers, read contention and
replica catch-up, and verify recovered state/order. A physical-copy comparison needs
a concrete compatible layout first; the existing socket-stream and fixed-field storage
prototypes do not measure any complete candidate protocol.

<a id="q08.1.2-what-may-a-lagging-or-disconnected-replica-read"></a>

## Q08.1.2: What may a lagging or disconnected replica read?

**Accepted on 13 November 2026:** option A. An initialized child continues
reading its intact, coherently installed local state while lagging or disconnected,
with replication status available separately and no immediate-freshness guarantee.
There is no automatic stale-age cutoff or ordinary per-read master coordination.
The contract also requires an on-demand comparison with master progress, at
least to determine whether relevant changes remain unapplied. Its meaning is defined
below; this does not turn local reads into synchronized reads.

Q08.1.3 now accepts [bounded resume or full reload](#q08.1.3-how-does-a-replica-recover-after-falling-behind).
Single-arena reload with bounded read waiting is accepted below. See [question register](../design/questions.md#current-discussion).
This is an accepted design, not an implemented status API or replication engine.

### Read consistency boundaries

A child fully loads its database, prepares indexes and catches up before opening
reads. This decision does not make a partially initialized child readable. After
startup the master pushes committed binary logical mutations; the child publishes
each root, its affected indexes and revision coherently. No application write is
made authoritative by a child; its SDK may forward application writes to the master.
Caller-owned copies remain unchanged by later replication.

Master write success does not wait for child acknowledgment. A successful Save on
the master may therefore precede visibility of that value on a child. A full query,
graph walk or multiple calls have no common snapshot: only each root read is coherent.
The policy must apply to ID lookups, searches and traversal, not just cached keys.
The later [Q14 child pressure decision](write-admission.md#child-replica-memory-pressure)
accepts pausing incremental application on controlled pre-publication memory shortage
with intact local reads, bounded buffers and observable lag. Memory pressure alone
does not trigger full reload; Q08.1.3 still requires it for missing/incompatible history.

### Completion of a write forwarded through a child

**Required on 14 November 2026:** a successful child-facing Save/WriteBatch includes
coherent local application on the calling child, after durable/master publication
completion. Returning only the master's receipt while that child remains behind
does not satisfy this contract. Direct master completion remains unchanged.

The child must reach the complete operation boundary in the matching database
history, with roots and indexes applied. An RPC response must not install a later
object out of order, skip intervening batches or bypass the ordinary replica
application mechanism. Subsequent local reads may observe this write or later
committed changes; they are not a permanent snapshot of the returned value.

Waiting is bounded. A timeout after master commit is not a rollback and must preserve
the operation identity/result-resolution path. Exact SDK signatures, cancellation
results and transport integration remain implementation work. This requirement adds
no PostgreSQL connection, optimistic authoritative write or second arena to a child.

[Session consistency](session-consistency.md) is accepted: related requests propagate
a minimum history/position and wait for it with a timeout on any serving pod. An
all-serving-replica completion barrier and mandatory sticky routing are excluded.
The linked contract owns facade input/output context and its distinction from object
revisions. Ordinary reads without a constraint keep their local-state semantics.

### Read option A: local reads continue, with explicit replication status — accepted

Once initialized, the child serves the latest state it has completely applied.
Ordinary reads do not contact the master or wait for a newer replication position.
They can still wait briefly for the existing local document gate or fail under the
accepted query/resource limits; local availability is not a wait-free promise.

If the connection is lost, continue reading the intact installed database. Pending
messages are not visible until coherently applied. Records may be older than the
master indefinitely, including locally present records deleted on the master or
locally absent records created there. A missing key means absent in the installed
local state, not confirmed absent on the current master. Searches retain their live
traversal contract over local data/indexes; their results may differ from the master.
An unavailable database, failed query or damaged state must still fail explicitly,
not masquerade as absence or an empty successful result.

Expose status independently of individual document reads, at database scope: whether initial loading is complete, connection state, applied
history/position and last observed master progress when available. Exact API names,
position representation and observation frequency remain engineering work. Connection
state is observed, not an instantaneous failure detector. Connected does not prove
caught up; caught up to a last observed position does not prove current now.

When disconnected, current master progress and exact lag are unknown. Time since
the last applied mutation is not a freshness measurement: an idle database may have
no new changes. Position comparisons must use the same database history.
Status observations are not atomic freshness certificates for subsequent queries.

There is no maximum stale-read age or automatic expiry for ordinary reads.
The calling-child write-completion requirement above supplies a local read-after-write
boundary, not automatic synchronization of unrelated reads on other children.
An application can choose to stop using a disconnected replica,
but ordinary engine reads do not silently gain a freshness guarantee. Local readability
and replication connectivity are separate concepts; Kubernetes probe policy is a
later hosting decision. Q08.1.3 now accepts bounded retained-tail replay or full reload;
single-arena replacement permits bounded read waiting. MemoryOnly children fully
load again after process restart; numerical limits remain unresolved. This option does not
make invalid or partially reloaded state readable, add failover, or retain unbounded
history for offline readers.

Advantages: reads stay local through a master restart, with no network round trip or
freshness-wait queue per read. Master write acknowledgment is unchanged. The selected
arena and indexes remain the main RAM cost; small status metadata does not require
historical document versions or a second full replica.

Costs: unqualified reads may return stale values and results. Constrained related
requests wait if the serving replica has not reached their required position;
a forwarded Save must meet the calling-child requirement above. Observation and reporting still cost
some work, and normal background application competes with local reads. This design
specifies behavior; it is not a measured latency or availability claim.

### On-demand comparison with master progress — accepted clarification

The [Q18 diagnostic API](../design/storage-and-maintenance.md#control-and-maintenance-tools-q18)
provides this access through a DI service and library-owned HTTP routes mounted by
the application. A child forwards an explicit master-status request through the
internal protocol; the master collects the response without needing a public HTTP
endpoint. Local status does not make that network request. The progress semantics
below also apply to this forwarded diagnostic response.

The application can explicitly request a replica status check against the master.
The master reports a committed and published progress boundary for its database;
compare it with the child's coherently applied progress in the same database history. This is replication progress, not an individual document revision,
a native address, or a PostgreSQL WAL position. Exact identifiers and wire/API shapes
remain engineering work. Each child receives the complete database stream; there
is no filtering by a partition or selected subset.

Expose the observed master progress, local applied progress, history/scope compatibility
and whether the master observation succeeded, with its observation time. A check can
report:

| Comparison | Meaning |
| --- | --- |
| Caught up through the observed master position | All relevant changes through that boundary have been coherently applied; the child may already have applied later changes |
| Behind the observed master position | At least one relevant change through that boundary has not been applied |
| Unknown or incompatible | No current master observation, incomplete initialization, or database history/schema cannot be compared safely |

The status covers the whole database. A contiguous applied position must not advance
merely because a message arrived or a later change finished out of order. Separate
Lattice database instances have independent histories and status checks.

At minimum the caller can distinguish caught-up, behind and unknown/incompatible.
Expose a count of pending changes only if the chosen positions define an exact count
for the same relevant stream; otherwise it is unknown. Subtracting arbitrary revision
numbers, global positions from differently filtered streams, or positions from different
histories does not give a valid lag count. A count is not elapsed seconds or bytes.
Master unavailability returns an explicit unavailable/unknown observation, never a
fabricated zero lag; the last successful observation may remain visible as historical.

The comparison is about an observed point, not permanent equality: the master may
commit again immediately afterward. A child sampled later can legitimately be ahead
of the observed master boundary in the same history. This checks replication progress,
not byte-for-byte equality or corruption; it does not scan/hash the entire arena.
A restored or reset history cannot be compared by numeric positions alone.

The later [Q09.4 restore-history decision](backup-and-restore.md#replica-history-after-restore)
requires a new Lattice history on backup restoration and full reload of affected
child replicas when detected. Ordinary restart against the same continuous
Durable history preserves identity and remains eligible for bounded resume. This
policy adds no instantaneous detection for disconnected children and no permanent
per-mutation document versions; existing local-read/reload contracts remain in force.

Only the explicit status check needs master contact; ordinary reads remain local.
It does not wait for the replica to catch up, add a master-write acknowledgment barrier,
or hold document gates during network I/O. Use a bounded/cancellable status request;
exact timeout and SDK failure shape remain engineering work. No mandatory polling rate,
background heartbeat schedule, freshness expiry or administrative UI is selected here.

### Read option B: require a bounded freshness proof — not selected

Serve a local read only while the replica can establish the chosen freshness bound.
Otherwise wait within a budget or return an explicit freshness/unavailability error.
A connection alone, or the age of the last user-data update, cannot establish that
bound. The design needs comparable master progress observations, a defined barrier
or heartbeat protocol covering this database history.
If proof expires or is unavailable, stop accepting reads under this policy, even if
all local data remains intact.

Advantages: applications receive a defined stale-data bound instead of indefinite
staleness. Ordinary admitted reads can still be local while the proof is valid.

Costs: master restarts or a slow network can make readable local data unavailable.
Freshness observation, timers, expiration races and bounded waiting add complexity.
Waiting requests consume memory unless rejected immediately or admitted under explicit
limits. Write acknowledgment can remain unchanged; this policy restricts reads rather
than automatically making replication synchronous. The exact bound, proof and failure
semantics would need further decisions before implementation.

### Read option C: synchronize each read with master progress — not selected

Before a read, obtain a committed progress barrier from the master and wait until the
child has applied the relevant database through that barrier. Then execute the local
read. Do not hold document gates during network I/O or catch-up waits. Missing master
connectivity or an unreachably slow replica causes bounded waiting and then failure.

Advantages: the read starts from state that includes the master's committed changes
through the obtained barrier. This can support a stronger read-after-write contract
when the barrier covers that write in the same database history.

Costs: each read adds coordination traffic and a network exchange, plus any catch-up
wait. The master now participates in read load; bounded waiters consume resources.
The barrier still does not freeze subsequent updates or create a consistent snapshot
for a multi-record query, and it must not be advertised as such. Keeping a full local
replica does not remove the coordination latency. Write acknowledgment need not change.

### Read-policy comparison and decision

| Aspect | A: local installed state | B: bounded freshness proof | C: per-read master barrier |
| --- | --- | --- | --- |
| Ordinary reads | Local; no freshness wait | Local while proof valid; otherwise wait/fail | Master exchange, catch-up if needed, then local read |
| Master outage | Intact initialized local data stays readable | Reads stop when freshness cannot be proved | Coordinated reads cannot proceed |
| Staleness guarantee | No age bound or automatic read-your-write | Selected proven bound | Includes changes through the obtained barrier |
| Master write acknowledgment | Unchanged | Unchanged | Unchanged; extra master work for reads |
| Extra RAM | Status metadata and existing bounded replication work | Observation state and any bounded waiters | Barrier state and bounded waiters |
| Complexity | Lowest of these choices; status and failure distinctions still required | Freshness proof, expiry and admission rules | Barrier protocol, scope/history tracking and waiting |

The decision selects **A** for v1. It matches the requirement to continue using a full local
replica during master restart and keeps ordinary reads independent of network latency.
The tradeoff is explicit: record integrity is guaranteed, current master freshness is
not. Do not introduce a configurable family of all three modes as part of this choice.
A dedicated wait for a known write or another stronger read contract is not implicitly
selected; it would need its own explicit scope before implementation.

A later focused verification should delay delivery, disconnect the master, apply a
partial collection update and a deletion, and verify coherent local reads plus honest
status. Include idle-master observations, write-success-before-child-apply, partial
initialization and failure-not-empty behavior. No new prototype or production engine
has been implemented by this documentation change.

<a id="q08.1.3-how-does-a-replica-recover-after-falling-behind"></a>

## Q08.1.3: How does a replica recover after falling behind?

**Partially accepted on 13 November 2026:** option A. Resume ordered
binary logical changes from coherently applied progress while the complete required
tail is retained and compatible. Otherwise require full reload of the affected
database. History and per-child work are bounded; a slow or disconnected
child cannot pin replication history indefinitely. Numerical limits will be selected
from measurements, not assigned defaults by this decision.

Single-arena reload with bounded read waiting is accepted below. See [question register](../design/questions.md#current-discussion).
MemoryOnly children fully load after process restart, as accepted below. Numerical
history/wait budgets keep Q08.1.3 partially open. This is an accepted recovery design, not an implemented protocol.

### Reconnect boundaries and remaining scope

The child retains a full selected replica and serves coherent local reads while
lagging/disconnected. The explicit status check compares applied progress with an
observed master boundary in a compatible history and database. Writes go only to
the master. Live updates use binary logical mutations; no invalidation or automatic
full-Save diff exists in v1. Initial loading is complete before reads, with indexes
and catch-up included. No automatic writer failover is implied.

This decision selects bounded incremental resume or full reload. The later accepted
reload policy below uses one arena and bounded read waiting, without a second full
arena. MemoryOnly children fully reload after their process restarts. Numerical
history/wait budgets remain Q08.1.3/Q14 work; protocol/scheduling details are E07 work.

### Reconnect option A: bounded resume history, then full reload — accepted

The reconnect handshake supplies the child's coherently applied position and its
database history/schema identity. The master checks compatibility and availability
of the complete required tail before resuming ordered delivery. A surviving socket
is not required; a valid local base and a retained replayable tail are required.
Acknowledged receive position alone is insufficient: local data, indexes and applied
progress must agree. Retransmission must not repeat already applied collection edits.

If the complete tail is available, send the missing committed logical changes, then
continue live push without a gap. The protocol must coordinate retained-range access
with trimming: history cannot disappear after an availability check and before replay
without an explicit reload-required outcome. Any pin or transfer work stays bounded.

If needed history is gone, the local state is missing, or database history/schema
is incompatible, do not skip the gap or apply a new history to the old arena. Report
that reload is required and use the full-state initialization path for the affected
database, including index preparation and catch-up. An ordinary Durable master restart
preserves database history identity, but does not preserve the RAM delivery tail. If
the required tail has been lost, reload from the recovered master's memory; do not
read PostgreSQL to reconstruct a delivery tail for that child.

Keep pending delivery bounded by encoded bytes and age, with bounded send/receive
work per child. These are upper bounds, not a minimum retention window: release a
change when all participating recipients have acknowledged it. RAM storage is settled;
exact limits and buffer/cursor implementation remain engineering work.
When a child can no longer be supported inside those limits, remove its claim on old
replication history and require reload. Do not queue every future update for that child
in RAM or retain a separate unlimited copy of the journal per subscriber.

A valid initialized local database remains readable while incremental catch-up runs,
under Q08.1.2: roots and their indexes publish coherently, without a database-wide
snapshot. A partial full reload is not readable. The accepted replacement policy uses
one arena; new reads may wait within time/count/memory limits for full readiness. It
does not promise uninterrupted reads or retain a second full arena.
Failed capture, missing prerequisites or inadequate capacity must not expose a partial
state or turn a required reload into reported catch-up success.

Advantages: short interruptions usually transfer only the missed changes. The existing
arena and indexes are reused during incremental catch-up. Bounded retention prevents
an arbitrarily old subscriber from determining master memory/disk growth. Normal writes
still need no child acknowledgment.

Costs: the master needs a replayable history window and the protocol needs compatibility,
ordered replay, duplicate suppression and reload-required handling. Full reload costs
network traffic, decoding, allocation/index work and capture/catch-up resources. Many
simultaneous reloads can contend with normal master writes and local reads; admission
and budgets need measurements. Applying a very large tail is not always faster than a
full reload; a measured adaptive threshold is not selected as part of this baseline.

### Pending delivery in memory

**Clarified for v1 on 13 November 2026:** both Durable and MemoryOnly masters send
replication data from memory. Retain confirmed binary logical changes only while
participating children still need them. Once all those recipients acknowledge a
change, release its delivery payload after outstanding send users finish with it.
There is no extra time window, archival RAM history or retention for hypothetical
future children. With no recipients or active initialization requiring a tail,
replication does not accumulate past changes.

Use one shared ordered delivery buffer with per-child progress, rather than a full
payload copy per child. Prepare immutable delivery content within the existing
resource admission budget and expose it after coherent master publication; Durable
mode still requires PostgreSQL commit first. Sender buffers must not depend on mutable
caller objects or unprotected arena addresses. Root/index gates do not span socket I/O.
Preserve ordered collection effects and atomic WriteBatch boundaries.

Delivery confirmation follows the existing coherently applied-progress contract.
Completing a socket write is not confirmation that the child can resume from that
position. Acknowledgments govern buffer reclamation, not successful application-write
completion: master writes still do not wait for all children. Exact acknowledgment
framing and buffer layout remain engineering work.

A new child receives the complete current state from the master's arena. Its capture
session retains only the subsequent changes required for a gap-free handoff to live
delivery. A reconnecting child with intact local state may resume only if the complete
required tail is still in RAM; otherwise it loads the full state. Already delivered
changes are not kept solely to improve a possible later reconnect.

Existing byte/age and per-child work limits still bound outstanding delivery. A slow
or disconnected recipient may retain pending changes only within those limits; when
its claim is revoked, it requires full reload and no longer blocks reclamation for
other recipients. Limits are ceilings, not reasons to keep acknowledged changes.
Initialization retention is also bounded. Numerical limits remain to be measured.

### Persistence journal and replication history are different obligations

The PostgreSQL journal is used for master recovery and background saved-base
application. Neither live delivery, reconnect catch-up nor initial replica loading
reads it. Reusing that journal as a replica catch-up source is excluded from v1;
there is no SQL fallback when a RAM delivery range is missing. A Durable master's
own recovery from PostgreSQL before serving children remains required.

Releasing a RAM delivery payload does not delete the corresponding durable recovery
input. PostgreSQL cleanup independently preserves everything needed by the saved base,
recovery and any accepted storage consumers. Replica acknowledgments do not pin SQL
journal retention, and SQL background application does not free still-needed RAM
delivery payloads. The existing hard journal budget remains a storage-admission rule.

A MemoryOnly master has the same bounded RAM delivery mechanism, without hidden
spool files or a durable recovery source. Its process loss can destroy state and the
delivery tail; surviving children do not automatically become writers. Delivery
buffers do not retain old arena versions. Ordinary local read copies do not pin them.

### Reconnect option B: full reload after every disconnection — not selected

Always run full initialization on reconnect, even when the child has intact data and
missed only a few changes. Live ordered delivery and consistent initial capture still
need correctness and bounded resources, but there is no incremental reconnect promise.

Advantages: fewer reconnect cases and no history retained specifically to support old
child positions. It can be adequate for small replicas and infrequent reconnects.

Costs: a brief network interruption can force transfer and installation of the entire
database. Large replicas and simultaneous reconnects increase master bandwidth, CPU
and capture work, affecting reads/writes through contention. Temporary memory and read
availability follow the accepted single-arena/bounded-wait reload policy; removing
resume history does not remove all replication buffers or make full loading free.

### Reconnect option C: retain changes until every registered child catches up — not selected

Promise incremental recovery for an arbitrarily long absence by keeping every needed
change until the slowest subscriber applies it. This requires authoritative subscriber
membership and applied acknowledgments, and transfers storage pressure to the master.

Advantages: compatible surviving local state can resume without a full reload even
after long outages, provided enough history storage was actually retained.

Costs: offline or abandoned subscribers can make history grow without bound. A finite
hard budget eventually requires master write refusal, explicit subscriber eviction or
abandoning the promise. MemoryOnly pays in RAM; Durable storage still needs finite
capacity and cleanup. This conflicts with the bounded-retention goal and is not
recommended. Applying a huge retained tail can itself be slower than loading current
state; keeping history is not a recovery-time guarantee.

### Reconnect comparison and decision

| Aspect | A: bounded resume, else reload | B: always reload | C: retain for every child |
| --- | --- | --- | --- |
| Brief disconnect | Replay the compatible retained tail | Full database state transfer | Replay retained tail |
| Long absence | Reload once the required tail expires | Full database state transfer | Potentially very large replay |
| Master RAM/disk | Bounded history and per-child work | No reconnect window; capture/live buffers remain | Growth controlled by slowest registered child |
| Read impact | Local reads during incremental catch-up; bounded readiness wait during full reload | Every reconnect invokes single-arena reload/read waiting | Local reads while lagging; replay can compete for resources |
| Write impact | No child acknowledgment barrier; retention/capture work still costs resources | More full-load contention on reconnect | Retention pressure may block writes at the hard limit |
| Complexity | Replay eligibility, trimming, duplicate suppression and reload | Simpler reconnect policy; consistent loading still required | Subscriber lifecycle plus long retention/recovery management |

The decision selects **A**. It preserves useful local state across short interruptions while
accepting full reload as the bounded way to recover from expired/incompatible history.
No numeric pending-delivery limit, guaranteed reload duration or adaptive replay-versus-
reload selector is accepted here. The later decisions below exclude a second full
arena, permit bounded read waiting and select MemoryOnly children for v1.

Before implementation, verify interruptions before/after apply acknowledgment, a lost
range, trimming during catch-up, same-history master restart, a history reset, positional
collection replay and simultaneous slow children. Check that replica-only eviction never
removes required master recovery input and that the hard journal limit still behaves
as accepted. These are future focused checks, not results of a new benchmark.

### One arena and bounded read waiting

**Accepted on 13 November 2026:** replace the affected replica storage
using one arena, with no simultaneously resident old and replacement full arenas.
A read arriving during
full reload can wait for readiness for a bounded time, then execute normally if ready.
**Thirty seconds is an illustrative configurable wait**, not an accepted default,
a measured reload duration or a guarantee that reload finishes within that time.
Numerical budgets remain to be measured.

The second-arena approach is explicitly excluded, not a selectable reload mode or a
fallback. Waiting consumes bounded request state; it does not retain an old full arena.
This accepted correction supersedes the earlier proposal to reject every new read
immediately without any readiness wait.

#### Scope and ownership during replacement

An ordinary disconnection alone does not clear the arena or close local reads.
Compatible incremental catch-up also keeps reads open under Q08.1.2. The following
steps apply to actual full replacement when incremental recovery is unavailable:

1. Establish a reload source/session under the initialization protocol before destructive
   replacement. This cannot guarantee that transfer will finish. Until replacement
   begins, intact old state can remain readable.
2. Mark the affected scope as reloading and stop starting new reads against that arena.
   Admit eligible new requests into bounded readiness waiting; do not execute them
   against partial replacement data. Replication status remains inspectable.
3. Let operations already using the old arena finish under existing resource/time
   limits. Drain old apply work and all other arena users before release/reuse. A
   waiting request must hold no document gate, borrowed view or old-arena pointer.
4. Release or reuse old storage and install the replacement with bounded decode,
   allocation, index and catch-up workspace. No document gate spans network/storage I/O.
5. Publish readiness only when database state, schema, indexes and applied history/progress
   are coherent and the catch-up barrier is reached. Admit still-live waiting reads
   through the normal read path, using the replacement schema/data/indexes.

Previously returned caller-owned copies remain usable and unchanged. They do not pin
the old engine arena. New lookups/reference resolution can wait while reloading; a
multi-call graph computation still has no global snapshot. Queries covering unavailable
scope cannot skip it and claim a complete or empty successful result. Exact reload
scope covers the database under the full-loading contract; no fine-grained per-table
replacement is introduced. The master and other replicas continue independently.

#### Readiness waiting, deadlines and failures

Bound waiting by a finite configured duration, pending-request count and retained
request-memory budget, including retained query/parameter state. Exact values and API
names remain engineering/measurement work. This is readiness admission for reads,
not the separate pending-write queue used during PostgreSQL outages.

- If readiness arrives before expiration/cancellation, begin the ordinary read. Its
  normal validation, schema binding, resource limits and errors still apply. A query
  prepared against discarded schema/index state must not execute a stale plan.
- A shorter caller deadline, cancellation or existing overall request budget takes
  precedence. Repeated reload attempts must not reset a request's waiting deadline.
  Readiness waiting does not promise that query execution itself fits the reload-wait
  allowance or extend the caller's total deadline.
- If the waiting allowance expires, report an explicit timeout/temporary-unavailable
  failure. If count/byte capacity is exhausted, reject the new waiter explicitly;
  cancellation reports cancellation. Never substitute an absent key or empty result.
- A request that expires, is cancelled or is rejected before execution is removed and
  must not start later when readiness arrives. Resolve the readiness-versus-expiration
  race before transferring ownership to normal execution. Already started reads follow
  the existing execution/cancellation contract; they are not automatically replayed.
- A terminal reload failure can fail waiting requests early. If recovery is still in
  progress, requests remain subject to their original deadlines and capacity bounds.
  Requests admitted after earlier timeouts are independent; there is no implicit retry
  of completed failures or unbounded accumulation across reload attempts.

Async entry points use cancellable asynchronous waiting rather than an engine worker
thread per waiting read. Any synchronous facade can block its caller, but must not
hold arena/document locks or prevent reload progress. Concrete SDK entry points remain
engineering work. Once ready, release eligible work under normal execution/resource
admission; waking waiters does not authorize an unlimited burst of query workspaces.

If loading fails after old data was discarded, the affected scope stays unavailable
until valid initialization completes. Bounded waiting can hide a short interruption,
not restore discarded state or guarantee availability. Loading status must not advertise
the old generation's progress as a ready replacement. There is no second-arena fallback.

#### Costs and alternatives

| Aspect | Accepted: one arena, bounded readiness wait | Earlier: immediate read error | Rejected: retain old arena beside replacement |
| --- | --- | --- | --- |
| Short reload | A waiting call may complete with extra latency | New calls fail even if reload ends soon | Reads use old intact data during preparation |
| Long or failed reload | Explicit deadline/capacity/failure outcome | Prompt error | Can retain valid old state, if capacity permits |
| RAM | One affected arena, load workspace and bounded waiting state | One arena and load workspace | Old/new data, dictionaries and indexes plus workspace |
| Coordination | Safe drain, readiness wait admission, deadlines/cancellation | Safe drain and reload readiness | Generation selection, cutover, iterators and deferred old-memory release |
| Master writes | No child-acknowledgment barrier; reload still costs resources | Same acknowledgment boundary | Same acknowledgment boundary |

One arena is not a promise that the entire process stays at exactly one dataset size.
Caller-owned copies, managed control state, allocator fragmentation, index builders and
waiting request state still consume memory. The earlier illustrative 4 GiB database
would require roughly 8 GiB for two similar resident generations before other process
costs; this is why the second full arena is excluded, not a measured memory peak.

Measure wait-count/byte headroom, expired/cancelled requests, drain time, transfer,
index preparation, catch-up and post-readiness admission separately. Verify safe
reclamation with active readers, no late execution after timeout/cancellation, failure
not reported as empty data, and successful reads after readiness. These are future
focused checks; no runtime implementation or new benchmark is supplied here.

### MemoryOnly children and process restart

**Accepted on 13 November 2026:** client replicas operate in MemoryOnly
and fully load from the master after their process restarts. Persistence of the Durable
master remains in PostgreSQL. A surviving child can keep reading through a connection
outage; a restarted empty child cannot serve data until full initialization completes.

V1 has no child-owned persistent data/schema/index image or recovery spool. This
settles the child restart-storage part of Q08.1.3. Numerical history/wait budgets remain
open for measurements; protocol/scheduling details remain E07 work within these contracts.
See [question register](../design/questions.md#current-discussion).

#### Restart boundaries

A connection loss while the child process survives is different from process loss.
An intact running child keeps reading local state and can resume from compatible
bounded history. If required history is missing, full reload uses one arena with
bounded readiness waiting; a second full arena is excluded. Persistence is selected
explicitly at instance open time: Durable is the default, MemoryOnly is explicit,
and role does not silently select or switch the mode.

All initial readiness still requires complete database state, schema/index preparation
and catch-up. A restarted process cannot inherit the previous process's live native
pointers, protected views or waiting requests. Caller-owned objects outside the lost
process and storage belonging to the master are separate lifetime domains.

#### Restart option A: MemoryOnly children — accepted

V1 child instances explicitly select MemoryOnly. They keep no engine-owned saved
schema, arena, index image or catch-up spool on disk or in another SQL store. After
process loss, start a new child and obtain its complete database from a live master,
prepare indexes and catch up before opening reads. A normal reconnect without process
loss retains the accepted incremental resume path and does not force full loading.

The existing Durable default is not silently changed based on role. The accepted
v1 child hosting configuration requires explicit MemoryOnly. A request for a Durable
child is unsupported in v1 and must fail configuration validation rather than silently
run as volatile. The exact configuration validation/API is engineering work.
Durable masters and standalone/embedded hosting
retain their accepted contracts; this is the accepted v1 child capability boundary.
The Durable master persists in PostgreSQL; the already accepted explicit MemoryOnly
master mode remains available for volatile use cases.

Advantages: no per-child storage backend, flush/checkpoint pipeline or crash-consistent
local restore protocol. Application reads remain in native memory. Replication apply
performs no child persistence I/O, and waiting/buffering has fewer competing storage
consumers. The child does not require a persistent database volume or a separate SQL
copy just to host its replica.

Costs: each child process restart requires full network transfer, installation/index
work and catch-up. Multiple simultaneous starts can load the master heavily. If the
master is unavailable, an empty restarted child cannot become readable; other surviving
initialized children can still read their own intact local state. Bounded waits can
hide a short initialization interruption where supported by the startup API, but do
not recreate missing data or promise an offline restart. No startup latency figure is
established by this policy; network speed alone does not determine readiness time.

RAM still includes the complete selected replica, indexes, managed control state and
bounded working/wait buffers. Avoiding child persistence does not shrink the resident
logical dataset or guarantee lower read latency; it removes its persistence work.

#### Restart option B: durable child recovery image — not selected for v1

A child can explicitly retain recoverable local data/schema and a coherent persisted
applied position. On restart, recover that local base, prepare/restore indexes, verify
database history/schema against the master, and catch up from the durable position
when the complete required tail remains available. Expired or incompatible history
still requires the accepted full reload. An image alone does not guarantee resume.

Define live-applied versus durably recoverable progress separately. A child may have
served newer committed master data before its own recovery image reached that position;
after a crash only the safely persisted position can seed recovery. The mechanism must
not pair newer metadata with older bytes or claim durable state after an incomplete
checkpoint. Persistence timing, replay/cleanup and local failure behavior require their
own contract; this option does not assume a second full in-memory arena.

Advantages: a compatible local base can reduce network transfer and master capture work
on restart. If local restore plus catch-up is cheaper than a full transfer, startup can
be faster. The benefit depends on storage performance, image age, index work and the
remaining tail, and needs measurements.

Costs: child disk/SQL writes and persistence buffers compete with replication apply and
reads. Crash recovery, metadata consistency, retention, upgrades and failed local storage
add implementation and operational work. A retained source must actually survive the
relevant restart: an ephemeral container path is not a durability contract. Storage
placement/ownership would need a separate deployment design if B were adopted;
v1 Q15 does not add child persistence or fault-tolerance orchestration.

PostgreSQL is the accepted first Durable backend. B does not automatically approve a
new file provider or direct mmap-backed working arena. Reusing the accepted backend
or introducing a child-specific recovery image needs an explicit format/ownership
choice; children must not open master storage as independent writers. Local restored
state still serves reads from native memory after initialization.

A saved image does not by itself authorize serving reads after a child restart while
the master is unreachable. That would change the initial readiness/history-validation
contract and needs a separate decision. Continued offline reads in a surviving process
are already accepted, and are not the same guarantee. B does not add master write
acknowledgment waits, automatic failover, or authority to promote a child to writer.

#### Restart comparison and decision

| Aspect | A: MemoryOnly children | B: recoverable child state |
| --- | --- | --- |
| Ordinary reads | Local native arena/indexes | Same; background persistence may contend |
| Replica apply | No child persistence I/O | Additional persistence work and buffers |
| Master write acknowledgment | Unchanged; no child wait | Unchanged unless a separate guarantee is selected |
| Child process restart | Full database transfer and initialization | Local recovery plus retained-tail catch-up, or full reload |
| Master unavailable after child restart | Missing state prevents readiness | Offline restart is not automatically authorized by a saved image |
| Resident RAM | One full database plus bounded workspace | Same live database plus persistence/recovery workspace; no second full arena |
| Storage/deployment | No child persistence backend required | Recoverable storage and its ownership/lifecycle required |
| Complexity | Reuse full initialization and existing reconnect | Add child persistence, crash recovery and durable progress semantics |

The decision selects **A** for v1: MemoryOnly children fully reload from the master after
process restart. The Durable master retains PostgreSQL persistence. This keeps scaled
readers simple; a durable child recovery pipeline is not part of v1. It does not change
the general mode default or permit silently downgrading a Durable request.

Verify child process restart, loss of all child-local state, full readiness before
reads and an unavailable master during initialization. Measure index work, master
interference and peak child memory during simultaneous starts. A later B comparison
would require a concrete recoverable format; no B speedup or new benchmark is claimed.

## Q11: Can the master run inside the application process?

**Accepted on 12 November 2026: option B.** One reusable engine supports an embedded
.NET library and a standalone service.
An application using an embedded master reads and writes the same local engine;
it does not need a child replica or a socket connection to itself.

### Role and hosting are separate choices

Master/child describes authority: the master accepts application writes, while a
child applies replicated changes and serves local reads. Embedded/standalone
describes which process owns the engine. Embedding does not make a child writable
or introduce a second authoritative writer for the same logical database.

The embedded master consists conceptually of:

```text
Application process
  Application code
    -> local Moltaro.Lattice API
      -> one engine: arena, schema, queries, indexes, mutation coordinator
      -> persistence owned by that engine
         -> foreground journal append and internal background applier
         -> SQL connections to PostgreSQL in Durable mode
      -> optional transport when remote replicas are configured
```

The PostgreSQL background applier runs within this same master process; its
connections are SQL sessions to storage, not a socket between the master and a
separate worker. This boundary also applies to standalone hosting. See the
[persistence failure and lifecycle contract](checkpoint-and-journal.md#transient-background-application-failure-continue-within-budget-accepted-for-v1).

There is one stored arena for this instance. Caller-owned result copies and query
workspace still consume memory under the existing contracts. GetById/ReadInto,
protected reads and mutation ordering remain the same; application code cannot
bypass the writer by mutating native storage directly.

The standalone service uses the same engine behind a process lifecycle
and network adapter. It can serve the already-planned replica topology. The
choice does not introduce a new remote query API or a shared-memory protocol.
The application with a ready local child still reads locally; a standalone
master does not imply a network round trip for every read.

An embedded master can serve configured remote children through the
transport module. With no remote users it need not start a network listener.
The core storage/query engine has no dependency on transport, and persistence
does not become optional merely because hosting is embedded. WAL, checkpoints,
recovery and acknowledgment semantics remain Q09 work shared by both hosts.
The later [Q09.5 decision](persistence-modes.md#q09.5-how-is-persistence-mode-selected)
accepts Durable by default or explicit MemoryOnly at instance open time. Hosting
alone does not choose that mode.

### Background worker and horizontally scaled readers

A supported design scenario is: a background worker
embeds the authoritative master, performs application computations and submits
most writes. Independently scaled application instances embed the client library
and a local child for the master's database. Each child loads the full
state and catches up before accepting reads under Q08, then receives ongoing
changes from that same master.

The worker can read its own master directly. It needs no redundant child for
those reads. Business calculations remain application code, not work inserted
into the database's mutation queue as one long-running algorithm. Long calculations
should use independently owned data and release record protection before doing
that work; Q06.2's direct-view operations remain short and synchronous. Multiple
worker tasks can submit mutations concurrently to the master's mutation coordinator. There is no
whole-computation or whole-graph snapshot implied by this deployment.

Scale the reader application instances independently of the single master owner.
Each child retains its database state and indexes, so scaling readers adds replica
memory and startup traffic. Application writes originating in a reader instance
must still reach the master; the child does not become a writer. Heavy worker
computation shares CPU and process lifetime with the master and can affect apply
or publication throughput. Hosting does not remove that contention or supply
automatic failover; Q08.1.2 permits intact local reads while disconnected.

### Alternatives considered and costs

| Effect | A: require a standalone master service — not selected | B: support embedded and standalone hosting — accepted |
| --- | --- | --- |
| Local reads | A ready application-side child already supports local reads; otherwise the access path needs its own contract | The application's embedded master directly serves the accepted local read APIs |
| Writes | Application requests cross a process/network boundary before reaching the master coordinator | Embedded calls avoid that boundary but still use the master coordinator and persistence contract |
| Memory | A master plus a local full child holds two stored copies of database state across the processes | A single-process embedded master needs one stored arena; owned results, indexes and persistence buffers remain |
| CPU and lifetime | Master has a separate process, can receive an independent resource budget and can remain running when the application restarts | Embedded engine shares CPU, memory limits, failure and restart with application code |
| Operation | Deploy and manage a database process even for a one-process application | One-process applications need no separate database service; distributed deployment remains available |
| Implementation | One supported host composition | One engine with two lifecycle/composition paths and validation of equivalent behavior |

The avoided call boundary is a structural difference, not a benchmarked throughput
claim. In a replicated deployment local reads may have similar engine costs in
both options. Storage flush latency, contention and materialization remain.
The one-versus-two arena comparison assumes a separate master plus a local child
with the same database state; it is not a universal memory multiplier.

### Lifecycle and ownership requirements

The application owns opening, readiness and closing its engine. After restart,
the master recovers durable state before serving reads/writes; embedding does
not imply empty startup or immediate readiness. Shutdown must coordinate active
operations and native-memory lifetime. It cannot free a protected view still in
use or promise graceful completion after an abrupt process failure.

A standalone service owns those same responsibilities. A .NET Generic Host
adapter can integrate startup/shutdown with an existing application host; see
[the .NET Generic Host lifecycle](https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host).
That adapter is a composition option, not a requirement for the engine core or
another host instance that every embedding application must create.

One engine owner controls the master storage for a logical database. Starting
multiple application copies must not open the same durable files as independent
writers. They are either separate databases or participants in the explicit
master/child topology. Embedding does not supply automatic leader election,
shared writes or transparent failover. Exact file ownership enforcement and
deployment wiring remain implementation work.

These boundaries fit the accepted separation of local arena and distribution.
The added work is a clean lifecycle/API and two host compositions, not a second
storage engine. Focused integration checks must exercise durable reopen, startup
readiness, shutdown with active readers and identical write/read behavior through
each host. A one-process local run must work without network configuration.
Host-process or container failure still determines availability for that engine;
the choice does not alter the accepted Q08.1.2 local-read and progress-check contract.

**Decision recorded:** embedded library and standalone service on one engine,
with no mandatory replication for an embedded master. The worker-hosted master
and horizontally scaled local-reader topology is an intended deployment scenario.
Physical hosting/lifecycle implementation, replication protocol and durability
details remain to be designed and verified. Configurable query resource limits,
runtime schema access and scan fallback follow their separate accepted contracts.

## Q15: Configured master and child connection

### Master authority and concurrent write callers

**Clarified on 13 November 2026:** the restriction is one authoritative master node,
not one calling thread. Many threads and clients may submit writes concurrently.
Application mutations become authoritative only through the master; a child cannot
independently change its replica state. Applying confirmed replication updates is an
engine operation, not an application write on that child.

A client-facing Save API may forward a request to the configured master. This is a
possible API shape, not a second authority or permission to optimistically mutate the
local replica. Forwarding signatures and transport are not selected by this clarification.
A successful Durable write means PostgreSQL commit and coherent master publication;
the later [calling-child requirement](#completion-of-a-write-forwarded-through-a-child)
also makes that child-facing call wait for local application before success.
Related requests use the accepted [minimum-position context](session-consistency.md)
for cross-child visibility, without an all-replica completion barrier.

Caller concurrency, internal preparation parallelism and commit/publication ordering
are distinct. Earlier references to a single writer describe the authority/order
boundary, not a one-thread public API restriction. The master must coordinate conflicting
mutations and recovery/replication order. Its thread count and scheduling strategy are
physical-engine decisions; this clarification does not promise parallel execution of
all writes or require serial execution of all preparation work.

**Scope clarified on 13 November 2026:** keep the v1 topology simple.
A child has a configured master URL and connects to that endpoint. Lattice does not
provide a fault-tolerance system, master discovery/election, child promotion,
standby takeover or orchestration of process restart and replacement.

The application/deployment supplies one authoritative master and manages its lifetime.
One writable master per logical durable store is a deployment precondition;
concurrent writable masters sharing it are unsupported. This does not introduce a
Lattice fencing service, ownership-transfer protocol or automatic recovery controller.
Master/PostgreSQL placement and any external restart policy are environment choices,
not additional v1 engine features or pending architecture choices.

### Connection and recovery contract

| Situation | Child behavior |
| --- | --- |
| Configured URL is unavailable | An initialized child keeps coherent local reads and observable disconnected/unknown-freshness status; it attempts to reconnect within bounded transport resources. An empty child cannot serve data. |
| Same continuous history returns | Resume from the applied position when the complete compatible tail is available; otherwise fully reload. |
| New or restored database history appears at that URL | Fully reload the database through the accepted single-arena path, with bounded waiting for reads during reload. |
| Child process restarts | MemoryOnly child loads its database state completely from the master before readiness. |

A replacement process that opens the same continuous Durable storage is still the
same history. A newly initialized database or restoration from backup is a new
history, even when the URL is unchanged. Endpoint identity is not history identity.
The child never searches for or selects another master on its own.

Master unavailability prevents new authoritative writes from completing; no unbounded
offline write queue, promotion or automatic resubmission of uncertain writes is added.
Existing write-result and resource-limit contracts remain. Starting/restarting the
master, changing its configured endpoint and stopping the previous process are host/
operator responsibilities. Backup restoration retains its accepted separate-storage
and one-arena procedure; a failed connection does not initiate restoration.

### Costs and implementation boundary

Ordinary reads remain local. Reconnection/history checks occur in replication work,
not as a mandatory network round trip for each read. Same-history resume transfers
retained changes; a new history requires full transfer, local index construction and
catch-up. Buffers remain bounded, and no standby-master arena or second reload arena
is introduced. Master downtime lasts until the environment starts a usable master;
Lattice promises no automatic takeover or fixed recovery time.

E07 implements the configured connection, reconnect scheduling, compatibility check
and existing resume/reload paths. Validate endpoint outage, same-history return with
and without retained tail, new history at the same URL and child restart. Exact URL/
configuration syntax, protocol framing and numerical limits remain implementation and
measurement work. No service, failover mechanism or new benchmark is implemented here.
