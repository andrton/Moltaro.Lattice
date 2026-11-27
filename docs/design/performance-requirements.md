# Performance requirements

## Production milestone and focused experiments

The first production milestone includes PostgreSQL durability and one database per instance.
Its physical baseline is specified in the [milestone contract](milestone-1-engine-contract.md)
for milestone/sprint planning. Durability, concurrent write submission, recovery and
bounded memory are release concerns, not optional follow-up experiments.

## Accepted primary workload and first-milestone access scope

**Accepted on 14 November 2026:** users, groups, roles and application settings are
the primary workload for the first production milestone, with **5–10 child replicas**.
The reference workload is read-dominated. Production validation must also cover
frequent small-object writes submitted one object per API call. Evaluate
**10,000 logical changes/s as a separate peak-load scenario**, not the mandatory
ordinary sustained rate of every deployment. This identifies a validation workload;
it does not turn Lattice into a user-management product or prescribe a domain schema.

**OQL is outside this first production milestone. Secondary indexes are required.**
Applications must be able to search declared single-field and composite indexes
through a structured .NET facade. Primary-key lookup and explicit .NET enumeration
remain available. [Explicit declared-index access](../decisions/index-access.md) is
accepted: callers select the schema index without automatic index selection or a
silent scan fallback. Exact SDK signatures remain implementation work. A textual
query language/parser is not a prerequisite for index lookup.

Previously recorded OQL semantics remain the broader design, not a first-milestone
delivery commitment. OQL-only operator questions and parser/planner implementation
do not block this milestone. Shared field comparison, null ordering, key encoding,
atomic index maintenance and resource limits still apply to direct indexed access.
No other accepted engine guarantee is removed by deferring the language layer.

PostgreSQL durability, multi-type atomic WriteBatch, coherent replica application,
calling-child completion and propagated minimum-position consistency remain required.
Measure index construction at startup and index maintenance under writes on the
master and children; a small primary dataset does not waive crash/recovery checks.

The first reference root counts, ordinary read/write rates, small-operation p99,
single-child readiness, retained-data savings, ordinary node peak-memory targets and
the burst/recovery timeline are accepted below. The milestone contract now specifies
the deterministic record shapes, indexes and operation mix. The benchmark owner still
records execution hardware, concurrent callers, absolute node budgets, burst-specific
resource limits and additional recovery/startup scenarios before a run.
Five to ten replicas is a deployment profile, not a hard
engine limit or a performance guarantee. This scope record is not the milestone's
implementation plan or authorization for a new load test.

### Accepted reference dataset volume

**Accepted on 14 November 2026 under Q10.1.** The first reference point is a
reproducible synthetic dataset of **121,000 root objects**:

| Entity type | Root count |
| --- | ---: |
| Users | 100,000 |
| Groups | 10,000 |
| Roles | 1,000 |
| Settings | 10,000 |
| **Total** | **121,000** |

This defines the first test volume, not measured capacity or a limit on database size.
Owned lists and nested values do not count as additional roots; their sizes, string
lengths/repetition and index cardinality must be specified and measured separately.

This baseline gives the accepted workload a concrete scale for latency and readiness
goals and the remaining memory targets. A smaller fixture remains useful for correctness,
but cannot establish behavior at this volume. Starting with millions of roots raises
test memory/time costs without yet establishing the primary baseline. Larger-scale
checks can be selected after this baseline, independently of the separate 10,000
changes/s peak scenario. No benchmark is started by recording this decision; local
checks must remain bounded and must not launch 5–10 full replicas on the workstation
as an implicit consequence of the deployment profile.

Index structures and consistency are fixed contracts. Data volume changes the amount
of data/index memory, rebuild/transfer work and potentially cache locality, but does
not select another engine design. Use the milestone contract's fixture; run durations,
hardware and scenario budgets are explicit measurement inputs, not permission to
replace the accepted dataset or weaken its service objectives.

### Accepted ordinary workload rates

**Accepted on 14 November 2026 under Q10.1.** Use a steady test profile of
**10,000 local read operations/s per ready child** and **200 logical root changes/s
in total through the master**. With five or ten
ready children, the aggregate read input would be 50,000 or 100,000 operations/s;
all children apply the same master changes. The write rate is not multiplied by the
number of replicas. These are accepted offered-load targets for the first reference
profile, not measured throughput or a ceiling on supported production load. The
accepted small-operation p99 targets below apply to this ordinary profile. The
separate 10,000 changes/s peak profile stays separate.

Include a named small-object path with **one root mutation per independently submitted
API write call** at the 200 changes/s rate. Do not require callers to assemble large
WriteBatch transactions to reach the reference rate. Multiple concurrent callers and
the accepted engine-owned bounded group commit remain allowed; one API call does not
require a separate PostgreSQL commit. Report single-object results separately from
explicit multi-object WriteBatch results. Exact small-object payload sizes, changed
fields and operation mix must be fixed in the fixture. The users/groups/roles/settings
dataset is a reference scenario, not the only supported production usage.

**Clarified by the owner on 14 November 2026:** the fixture of the milestone contract is
that profile. It offers 192 single-entity calls and 4 two-entity batches a second, 200
changes in all; no separate run of 200 single-entity calls is required. The small-write
target is judged on the single-entity calls, and the batches are reported as their own
class, as [accepted measurement conditions](#accepted-measurement-conditions) says.

Reads exercise primary-key and explicitly selected single-field/composite indexes
with bounded results, without OQL or hidden scan fallback. Final operation weights,
result sizes, record/string/list distributions, index selectivity, owned versus
protected access and consistency-wait phases must be explicit in the fixture. Do not
report these unlike operations as one unexplained average. Count logical root changes,
API write calls, WriteBatch transactions and storage commits separately.

Measure successful completions and latency alongside offered rate, rejections,
timeouts, replication lag and queue growth. A write succeeds only at its accepted
completion boundary, including local application when called through a child; queue
admission is not completion. Concurrency, duration, hardware and the remaining
pass/fail budgets remain open. The rates do not authorize running a distributed load
test on the workstation; execution must have an explicit bounded resource profile.

For unchanged dataset/index definitions, these rates do not inherently enlarge the
stored dataset. Additional CPU work, in-flight requests, pending delivery and writes
can increase memory and latency. A higher ordinary write target, such as 1,000
changes/s, would emphasize durable commits, index updates and replication much more
strongly; it remains an optional later profile rather than the accepted first
steady profile. No engine-policy change follows from the accepted test rate.

### Accepted ordinary latency targets

**Accepted on 14 November 2026 under Q10.1.** Use these p99 acceptance targets under
the accepted ordinary load, after readiness and warm-up:

| Operation | Accepted p99 target | Completion boundary |
| --- | ---: | --- |
| Local ID or exact-index read with a small bounded result | At most 1 ms | Facade invocation through the returned result, including lookup, synchronization and any selected result materialization; required position already locally applied |
| Single-small-object write through a child | At most 50 ms | Caller invocation through durable master commit, coherent master publication and application on the calling child |

p99 means at least 99% of measured operations complete within the stated duration.
It is not a maximum-latency promise or a cancellation timeout. Report p50/p95/p99,
maximum, offered/completed rates and all errors/timeouts, with client/admission queues
included. A result is not successful merely because writes were accepted into a queue.
Do not satisfy a latency target by discarding slow or failed operations.

The local-read target applies after its dependency is satisfied. For a lagging
replica, separately report minimum-position waiting and full end-to-end constrained
read latency; do not hide that wait from the application-visible measurements.
No numerical catch-up-wait target is selected here. Large ranges, full enumeration,
large roots, explicit multi-object WriteBatch, recovery and the separate peak profile
need their own measurements rather than inheriting these small-operation thresholds.

These are accepted engineering targets, not evidence of achieved capacity
or deployment-independent guarantees. Fix hardware, network/storage conditions,
payload/result sizes, index selectivity, concurrency and run duration before using
them as pass/fail gates. Count sustained queue/lag growth even when short-run latency
passes. Choosing a target does not relax durability, consistency or memory limits.

Tighter initial budgets, such as 0.1 ms and 10 ms, were not selected.
That leaves less room for runtime scheduling, materialization, storage and network
variation and may require more optimization or stronger deployment resources. It
does not inherently reduce data memory or authorize additional caching. Start with
the accepted budgets and optimize against measured causes; faster results remain
desirable even when the acceptance threshold is met.

### Accepted child readiness time

**Accepted on 14 November 2026 under Q10.1:** **at most 10 seconds from
invoking the child's engine-open operation to Ready** for the accepted 121,000-root
reference dataset, with a reachable ready master under the ordinary 200 changes/s
load. Start one new child while the existing children continue their ordinary read
workload. This sets a time budget for the accepted full-load protocol; it does
not reopen partial availability, demand paging or physical index transfer.

Include connection/handshake, schema and full-state transfer from master memory,
arena installation, local primary/secondary index construction and coherent replay
through the fixed complete-transaction readiness position. Ready means local reads
can actually run against a complete database and its indexes. Receipt of the last
state-transfer byte alone does not stop the clock. A moving master head must not
turn catch-up into an unbounded wait; retain the accepted fixed-boundary protocol.

This is engine readiness on a specified deployment, not a Kubernetes scheduling or
container-image download promise. Record process/runtime startup and complete pod
readiness separately so the full scale-out delay remains visible. Fix hardware,
network, record/string/collection sizes and index definitions before using the target
as an acceptance gate. The root count does not specify a gigabyte volume. Report
every measured initialization duration and failure; a numerical readiness percentile
and repetition count have not been selected.

Measure CPU, transfer bytes, peak master/child memory, pending delivery and ongoing
read/write latency during loading. The 10-second target grants no unbounded
temporary memory or second arena and does not waive existing admission/consistency
rules. Simultaneous multi-child startup and master recovery from PostgreSQL require
separate scenarios; this decision does not assign them the same deadline.

A tighter five-second target was not selected. It could make scale-out more responsive
but leaves less margin for transfer and local index building. Meeting it may require more optimization
or deployment resources; additional parallelism/buffering must still fit measured
memory budgets. Start with the ten-second readiness gate, measure its stages and
optimize the bottleneck. Neither value is a measured capability or a new API timeout.
Recording this decision does not start a load test.

### Accepted retained data memory target

**Accepted on 14 November 2026 under Q10.1.** Lattice's retained data footprint must
be **at most 75% of a reasonably optimized managed .NET representation**
of the accepted 121,000-root reference dataset: at least 25% less memory. This is
an accepted target to test, not a measured saving or a claim about every
schema and payload distribution.

The comparison covers one resident database with the same logical records, key
types, strings, collection contents, primary/secondary index definitions and read
consistency. The managed reference uses suitable collections/compact value storage
and safe text sharing where appropriate; do not manufacture a win by duplicating
its payloads or omitting its optimizations. It is a data-representation reference,
not a requirement to build a second durable distributed database.

Count all retained data-owned managed storage, allocated native arena capacity,
primary/secondary indexes, string tables, root/control/gate metadata, unused allocated
capacity, retained blocks and the bounded free reserve. Do not compare only native
payload bytes with the complete managed representation. Fix both implementations'
capacity policy and measure identical post-load and post-churn logical states after
in-flight work and safe reclamation have settled. Release shared fixture staging
equally; count each shared allocation once.

Report the retained-footprint components and absolute bytes alongside total process
private/resident memory and managed/native allocation measures. The percentage is
for the resident data representation; it does not claim 25% lower total pod memory.
Master/child transport and persistence workspaces, outstanding writes, operation
receipts, startup staging and runtime/GC overhead remain in full-node memory and peak
reports. No component may disappear from the report by falling outside this ratio.
An external PostgreSQL process is reported separately, not added to just one in-process
representation in the comparison.

Fix field sizes, index mix, text cardinality and collection lengths before applying
the target. Report high-repetition and high-cardinality text cases explicitly. The
historical MemoryOnly experiment's similar ratio applies to a different fixture and
is not evidence for this production reference. Absolute node/container limits and
burst/churn budgets remain separate work; the ordinary node peak ratio is accepted
below. Do not guess a MiB limit from root count.

A stricter target of at most 50% of the managed reference was not selected. It would
save more memory per replica but may require additional packing, encoding or index work and
could trade CPU time against memory. The accepted first target preserves the
accepted read/write latency and readiness goals: a memory win cannot be obtained by
silently relaxing them. Neither target changes the accepted arena/index choices or
authorizes speculative optimization or a load-test run.

### Accepted peak node memory target

**Accepted on 14 November 2026 under Q10.1:** **peak memory must be at most
1.5 times steady ready-state memory for each isolated Lattice test node** on the
reference dataset during ordinary-load mutations/compaction and one-child startup.
This permits a 50% temporary overhead. Evaluate the master and each child separately;
cluster averages must not hide a single node's excess.

This uses total process/container memory, including managed/native data, indexes,
runtime/GC memory, allocated capacity, transfer/index-building workspaces, pending
writes and replication buffers. Declare one OS/container measurement scope and metric
per test platform and use it consistently for the numerator and denominator. Include
component measurements for attribution; managed live bytes or arena payload alone
cannot establish this node-level target. Keep the generator/load driver and external
PostgreSQL outside the isolated node and report their memory separately.

Define `Msteady` for a ready, warmed node with the same logical dataset, index schema,
topology and ordinary workload after queues have caught up. Fix this reference before
the mutation run; do not repeatedly rebase it upward as memory grows. For a newly
loading child, use its corresponding settled ready-state reference rather than the
empty process. Report the absolute reference, the highest observed memory and end
state, together with sampling coverage; do not inflate reserves to improve the ratio.

Measure startup peaks through transfer, arena installation, local index construction
and catch-up, on both sender and receiver. During mutation/compaction checks, keep
logical live data volume/distributions comparable, including deletion/recreation and
variable-value replacement. Persistent upward drift after work drains remains a
problem even if a short run stays below 1.5 times its reference. Exact sustained-run
duration and reclamation tolerances remain measurement work.

This is an accepted test gate, not a dynamic memory limit or a production pod
size. A production host also needs its application/runtime headroom. The existing
one-arena rule and pre-commit memory admission remain unchanged. Ordinary read/write
latency and readiness targets still apply; exceeding memory cannot be hidden by
silently throttling the test workload. The separate 10,000 changes/s peak, simultaneous
child loading and master recovery require their own resource scenarios.

A tighter alternative of 1.25 times ready-state memory was not selected. It reserves
less temporary space but can constrain buffering, index construction and preparation, potentially
increasing load/write latency or requiring more optimization. Use the accepted 1.5-times
gate and tune from component measurements. Neither value establishes measured
capacity, permits a second arena or starts a load test.

### Accepted burst and recovery profile

**Accepted on 14 November 2026 under Q10.1.** Use the following first peak profile
on the accepted reference dataset, with ready nodes and no simultaneous new-child load:

| Phase | Accepted offered write rate and duration |
| --- | --- |
| Establish the ordinary baseline | 200 logical root changes/s; warm-up and baseline window fixed in the run manifest |
| Burst | 10,000 logical root changes/s in total through the master for 30 seconds: 300,000 submitted changes |
| Recovery under continued work | Return to 200 changes/s; restore the ordinary operating profile within 60 seconds from the end of the burst |

Keep the ordinary 10,000 reads/s per ready child as offered read demand throughout
and report completed reads and latency by phase. The burst rate is total input, not
an extra 10,000 changes/s per replica. Repeated changes may target the same root;
300,000 mutations do not mean 300,000 new objects. Use bounded small-object calls and
record the operation/key distribution. Do not coalesce incoming API writes to make
the offered rate appear sustainable; accepted background saved-base coalescing remains.

For recovery, resolve every submitted burst operation as completed, explicitly
rejected, or still uncertain under the existing operation-ID contract. Once committed
burst outcomes are known, record their highest complete transaction position. Within
the accepted recovery window, all connected test replicas and the saved-base applier
must cover that fixed boundary, burst foreground work must be resolved, and
ordinary latency/queue/lag behavior must return to baseline while new work continues.
The 60-second clock starts when burst input ends, not when the last burst write commits.
Do not wait for a moving latest position or require queues to be exactly empty while
ordinary traffic continues. Unexpected disconnection/reload is a separately reported
outcome, not permission to silently remove a lagging replica from the result.

This defines an accepted peak characterization scenario. It does not claim measured
10,000/s completion, establish sustained production capacity or silently impose a
no-refusal guarantee for the separate peak. Report offered/admitted/completed/rejected
and uncertain counts, per-phase percentiles, pending bytes, memory peaks and time to
the fixed boundary. Refused work is a capacity shortfall even when accepted work
drains quickly; a generator that cannot offer the target has not exercised the profile.
No accepted write may be lost, and uncertain outcomes must not be reported as rejection.

Ordinary p99 gates do not automatically become burst p99 promises. Keep configured
memory/admission limits and finite operation deadlines in force, and choose the
burst's absolute resource budget before execution. No unbounded queue, second arena,
early durable acknowledgment or automatic retry flood is authorized by this profile.
The one-node ordinary 1.5-times gate remains scoped to its accepted scenarios; burst
resource findings are reported separately rather than hidden in that ratio.

A three-minute whole-engine burst was not selected for this first profile: 1,800,000
offered changes, six times the accepted burst input. It exercises longer overload/maintenance cycles
but requires more run time and can accumulate more pending work. Historical
three-minute storage-only checks do not substitute for this engine/replica scenario.
Start with the bounded 30-second burst and 60-second recovery observation. Exact
payloads, hardware, concurrency and safety budgets belong in the run manifest; this
decision starts no benchmark.

The [historical MemoryOnly gates](first-prototype.md#proposed-numerical-gono-go-targets)
remain hypotheses for isolated memory tests. They do not prove end-to-end Durable
latency, recovery or production readiness. Include multiple concurrent write callers
when defining the production load profiles; a single-producer microbenchmark cannot
establish that behavior.

### Accepted measurement conditions

**Accepted by the owner on 14 November 2026, with the plan of [Epic 1](../work/epic-1/README.md).**
The targets above left the conditions of a run to the benchmark owner. These are the
conditions under which a target is judged as passed or failed.

- **Where.** One reference machine, the owner's workstation. This is enough to close the
  first milestone; a network-separated run is not required for it. A result on this
  machine says that the engine meets a target here, not that every deployment does.
- **Isolation.** The master, every child and PostgreSQL run as separate containers with
  declared limits of CPU and memory; the load driver stays outside them. The memory of a
  container is the measurement scope of its node. The manifest records that the
  containers run under a virtual machine of the workstation, so these results are not
  compared with the prototype timings taken on the host.
- **State of the machine.** Before a gate run the measurement program checks that free
  memory covers the sum of the limits with a margin, that the processor has been quiet
  for a stated time and that no foreign container runs. When the check fails, the run
  does not start; a result taken on a busy machine is neither a pass nor a failure.
- **Concurrency and duration.** Sixteen concurrent write callers. For the ordinary
  profile 2 minutes of warm-up and 10 minutes of measurement.
- **Readiness.** Five starts of a new child; every one within the target.
- **PostgreSQL.** Version 18, the only version tested and stated for the milestone.
- **What a target is judged on.** The small-read target on reads by identifier and by
  exact index value; range reads are reported as their own class against the same
  figure, and a miss there is reported, not hidden in a common percentile. The
  small-write target on single-entity calls; batches are reported as their own class
  and inherit no threshold.

The thresholds of the machine check and the limits of every container are written into
the run manifest, which the owner approves before the first gate run. The manifest must
also show that the master, PostgreSQL, ten children and the load driver fit into the
memory of the machine together.

## Durable and distributed workload context

**Historical context from here to the end of the document.** The sections below record
the measurements and open points that led to the storage decisions. The accepted targets
above, the [milestone 1 contract](milestone-1-engine-contract.md) and the
[decision register](questions.md) are current; where a trial value or an "open" note
below differs from them, they prevail.

**Workload requirement recorded on 12 November 2026:** small changes can arrive at
thousands of logical mutations per second. Measurements must establish
their effect on reads and whether SQL streaming can improve state loading. These
requirements guide Q10.1 and the Q09.6 backend comparison. The later accepted
ordinary-rate and p99 targets above are test requirements; they are not measured
full-engine performance or deployment-independent guarantees.

**Historical experiment target:** local PostgreSQL with Npgsql, measured
on 12 November 2026. [PostgresStorageComparison](../../src/Prototypes/PostgresStorageComparison/README.md)
implements a bounded baseline. The subsequent decision selects PostgreSQL as the
first Durable backend in Q09.6. Production version/hosting and full-engine
performance requirements remain open.

[Question register](questions.md) · [Backend decision](../decisions/persistence-backends.md) · [Prototype evidence](prototype-evidence.md)

## Three paths to measure separately

| Path | Work and performance question |
| --- | --- |
| Resident application reads | GetById, direct indexed lookup, protected views and explicit .NET enumeration use local state. Measure their latency under updates, base maintenance and replica loading; no SQL call per ordinary read is introduced. OQL is outside the first milestone. |
| Initial load and recovery | Read binary state, allocate/install it, prepare indexes and apply the change tail. SQL streaming can improve this path; bytes received alone do not mean Ready. |
| Mutation completion | Validate and order a logical mutation, maintain affected indexes, publish coherently and satisfy the selected acknowledgment policy. Measure completion, not just queue admission. |

Persistence need not add a direct SQL round trip to resident reads. It can still
affect them through CPU and memory-bandwidth contention, allocation, root/index
synchronization and retained checkpoint/transfer state. Root locks must not be held
across disk or network I/O. Include both reads of updated roots and unrelated roots.

## Peak demand and deployment capacity

**Workload scope:** 10,000 logical changes/s represents peak demand, not the
mandatory ordinary sustained rate on every deployment. The three-minute comparison
holds that peak deliberately as a stress test. Sustained demand at this level calls
for adequate machine/storage capacity, including vertical scaling where appropriate;
no scaling curve has been measured by the local prototype.

A temporary unapplied tail is acceptable when admitted writes are durably committed,
required history stays within budgets, and spare application capacity after the peak
can catch up. The accepted first profile uses a 30-second burst and at most 60 seconds
of recovery under continued ordinary writes. Evaluate burst frequency, foreground
latency/admission and recovery-tail budget separately. Rejecting before commit is
not the same as accumulating already-durable changes. Remaining numerical resource
budgets and repeated-burst profiles remain Q10.1/Q14 work. Q14 separately accepts bounded waiting during temporary
PostgreSQL unavailability; it does not set numerical budgets or general overload rules.

For a simple capacity estimate, tail growth during overload is approximately
`(committed changes/s - applied changes/s) * peak seconds`; catch-up requires the
post-peak application rate to exceed new committed input. Real capacity varies with
locality, batch size and I/O competition. This estimate is not an accepted SLO.

## Small-update workload

Count one submitted logical document mutation as one operation; report changed
field bytes, encoded command bytes, database commands, durable commit batches and
checkpoint bytes separately. An eight-byte value change does not imply an
eight-byte persisted operation: identity, ordering and encoding also take space.
[Q13](../decisions/mutation-commands.md#q13-how-can-a-document-be-updated) accepts complete
document replacement, explicit scalar assignments and collection replacement/element
commands. Benchmark labels must identify which operation was actually exercised.
The fixed-field prototypes do not measure a general document or collection mutation API.

Proposed exploration points are 1,000, 5,000 and 10,000 logical changes/second,
followed by a bounded search for saturation. These are test inputs, not accepted
SLOs or a claim that any backend supports them. The accepted 121,000-root reference
volume, 200 changes/s ordinary rate and small-operation p99 targets define the first
steady profile. The first burst/recovery timeline is also accepted. Repeated-burst
frequency, payload/index distributions and remaining latency budgets stay Q10.1 work.

Cover these dimensions with small, named scenarios rather than one unbounded run:

- Fixed-width field changes, indexed versus unindexed fields, and variable-sized
  string/collection changes; measure allocation and physical write amplification.
- Uniform keys and a hot subset, including repeated changes to the same root.
  Count every submitted mutation; do not silently discard intermediate changes
  or coalesce commands to improve the rate without a separately agreed contract.
- No secondary indexes, then explicitly declared single-field and composite
  indexes. Preserve schema comparison rules and verify final results.
- Quiet reads, then concurrent ID reads and representative single-field/composite
  indexed requests; add saved-base application and full child initialization as
  distinct interference phases. OQL workloads belong to a later language milestone.
- MemoryOnly as a local-engine baseline, file-backed Durable and the chosen SQL
  Durable candidate. Compare durable candidates at equivalent guarantees; do not
  present a memory-only or enqueue-only result as durable write throughput.

## Bounded batching is a candidate, not early acknowledgment

A single ordered writer does not require one storage commit per logical mutation.
Investigate compact change batches with bounds on mutation count, encoded bytes
and maximum collection delay. A strict durable experiment must acknowledge each
mutation only after the storage commit covering it succeeds. Grouping persistence
work does not add a public multi-document atomic-read or transaction guarantee.
Commit batching can group UPDATE/UPSERT operations on current state; it does not
require an append-only recovery journal. The original prototype measured only the
append representation. The subsequent unit comparison measures UPDATE/UPSERT
separately; its unit sizes and maintenance costs must accompany any capacity claim.
Q09.1 now accepts reliable storage acknowledgment before success. The detailed
publication/failure protocol remains E03/E06 work.

For a strictly serial, unpipelined commit loop, an illustrative five-millisecond
cycle limits it to 200 commits/second. If a cycle could cover 50 mutations at the
same duration, the arithmetic ceiling would be 10,000 mutations/second. Real batch
collection, payload growth, validation, indexing and publication add work; neither
duration nor resulting capacity is measured here. Record batch-wait latency as
well as commit duration. Larger batches cannot be treated as a free speedup.

Distinguish grouping work before a durable acknowledgment from acknowledging
before durability. PostgreSQL documents
[group-commit flush amortization and latency tradeoffs](https://www.postgresql.org/docs/current/wal-configuration.html).
Its [asynchronous commit documentation](https://www.postgresql.org/docs/current/wal-async-commit.html)
explains that acknowledging before WAL flush can lose recent acknowledged changes
after a crash. Use equivalent durability settings when comparing backends.
This test proposal does not select delayed durability or a database tuning setting.
SQL's internal group commit does not guarantee that one sequential
client command per mutation will be combined; test engine-side batching explicitly.

## PostgreSQL streaming and exclusive application ownership

Q09.2 accepts a compact PostgreSQL journal and a base advanced by background
coalescing in short bounded batches with immediate catch-up. Numerical interval
and batch/workspace limits remain open;
`bytea` remains a candidate payload type. Direct current-state updates are the
measured alternative, not the selected ordinary-write path. The decision chooses
neither a unit size nor one SQL row per document. These paths supply evidence:

| Path | Candidate API and purpose | Cost to verify |
| --- | --- | --- |
| Load committed state | Npgsql reader with `SequentialAccess` and `GetStream` | Coherent read boundary, bounded buffering, copying into native segments and complete readiness |
| Bulk state transfer | Binary `COPY FROM STDIN` / `COPY TO STDOUT` through Npgsql | Row/field framing and encoding, batch sizes and throughput under writes |
| Frequent small mutations | Batched current-state UPDATE/UPSERT versus the measured append-only change batches | Affected-unit size, write amplification, commit latency and cleanup under equivalent durability |

[Npgsql sequential access](https://www.npgsql.org/doc/performance.html) avoids
buffering a complete result row; fields must be consumed in order.
[GetStream](https://www.npgsql.org/doc/api/Npgsql.NpgsqlDataReader.html)
exposes a field as a stream. Keep the reader and connection alive during transfer
and consume each field before moving on. Batching can reduce command round trips;
it does not remove commit or storage work.

[Binary COPY](https://www.npgsql.org/doc/copy.html) is a bulk-transfer alternative
to measure, not an assumed winner for tiny commits. Npgsql's raw binary COPY stream
contains PostgreSQL COPY framing, not just arena bytes. It cannot be copied directly
into the arena as its memory image. Use the row/field API or account explicitly for
format handling. COPY completion inside an explicit transaction is not that
transaction's commit. The initial prototype measures streamed reads, COPY export
through a per-chunk byte array, and one prepared INSERT per encoded change batch.
The additional unit modes measure set-based UPDATE/UPSERT with array parameters
and paused base replacement through COPY. None is an implemented engine API.

The unit comparison includes repeated updates, reopened final-state validation,
paused journal compaction and ordinary vacuum. Insert/delete churn, simultaneous
SQL snapshots and sustained space/cleanup observations remain further evidence.
Changing a large binary value is not assumed to update only the changed field's
bytes physically. Separate payload size, SQL compression policy and storage work
instead of selecting an application recovery journal from the append test alone.

Validate numerical settings for the accepted [compaction trigger/hard-limit policy](../decisions/checkpoint-and-journal.md#compaction-triggers-and-journal-budget)
with realistic replay/index costs, sustained background application and cleanup. Measure encoded
retained bytes separately from physical SQL/WAL space. The 256 MiB / 30 minute /
1 GiB trial values are not approved defaults or measured guarantees. Compare the
[earlier capture alternatives](questions/checkpoint-and-journal.md) using the whole
write pause, bounded backlog/drain, read latency, peak RAM and interrupted recovery.

The requested [larger pause experiment](prototype-evidence.md#larger-paused-captures-and-pending-write-memory)
now covers 128/512/1,024 MiB, 10,000 offered requests/s, 32-byte/1 KiB commands,
bounded queue retention, continued arrivals after resume, full readback/replay,
partial generations and rolled-back publication. Its observed pause range grows
from 3.74–5.28 s at 128 MiB to 30.36–38.54 s at 1 GiB. The larger cases expose both
admission overflow and pre-execution expiration. The report explains which queue
figures are measured and which estimate accepting every arrival. Production
serialization, earlier-writer drain, repeated cleanup cycles and crash/remote
storage behavior remain unmeasured. These are historical paused-capture results,
not the selected background maintenance method; no numerical budget is selected.

The design adopts [background journal application with coalescing](../decisions/checkpoint-and-journal.md#background-application-with-coalescing-accepted-for-v1) for v1.
Measure foreground acknowledgment latency and background progress independently:
10,000 durable appends/s do not prove 10,000 applied changes/s. Record unapplied
count/bytes and oldest commit age while arrivals continue, before separately
measuring post-load catch-up. Compare uniform and repeated-key workloads against
the same journal-only foreground baseline. Separate persisted unit size, logical
mutation size and total dataset size. Bound the worker's changed-unit workspace;
an on-disk recovery tail must not imply a RAM copy of the complete journal.
Verify saved units and their applied prefix atomically, rollback/reopen behavior,
and reconstruction from saved state plus an outstanding tail. This is a different
algorithm from paused full-image capture; neither its performance nor its memory
cost can be inferred from that earlier experiment. Physical cleanup and long-run
storage equilibrium remain separate evidence; test settings are not product defaults.
The [background-application evidence](prototype-evidence.md#background-journal-application-with-coalescing)
now records 20 main 30-second scenarios and eight focused 20-second reader
comparisons. Hot-key coalescing with 4 KiB saved units kept a small replay tail;
uniform writes and 64 KiB saved units did not. Successful short hot-key runs coexist
with observed foreground refusals/latency spikes, so sustained production capacity
remains unproven. Reader affinity reduced a measured source of comparison noise;
the probe remains a tiny native read, not a full-engine query measurement.

The [completed compact-unit comparison](prototype-evidence.md#sustained-comparison-of-compact-saved-units) uses identical 128 MiB data at 256 B, 1 KiB and 4 KiB
saved-unit sizes, with uniform 10,000 changes/s for three minutes each and a matching
journal-only control. Keep change size and application budgets fixed. Report lag
throughout the run, per-minute acknowledgment latency/refusals, reader interference,
workspace/process memory and recovered state. This is a sustained application test,
not proof of indefinite space equilibrium. The subsequent decision selects
256 bytes as the PostgreSQL v1 baseline; other test budgets are not defaults.
256 B accepted the full offered peak and kept a small tail. 1 KiB caught up but
refused requests during a transient stall; 4 KiB accumulated a growing tail and
also refused requests. Interpret these against peak duration and deployment
capacity, not a new requirement to sustain the peak indefinitely.

RAM efficiency has priority over tightly packed disk files. The requirements permit
periodic disk compaction with Lattice stopped; this does not authorize unbounded
RAM growth or settle backup/replica capture pauses. Short pauses are acceptable
in principle, subject to measurement; no numerical duration is accepted. Compare buffer capacity,
allocation traffic and extra capture images separately. A paused checkpoint can
stream an existing stable arena without retaining a second complete image, but
its pause and disk costs still need validation on the intended engine and hosting.

Compare bounded-buffer streaming into the arena with the necessary metadata
decoding, validation and copying included. Do not claim end-to-end zero-copy or
zero allocation: the driver/network and installation can still buffer or copy.
Retrieve an ordered set of chunks in a bounded number of commands instead of one
round trip per document. Stream a coherent base, not arbitrary changing bytes.
Compatible schema/dictionary state, retained base generations and the ordered
change tail remain necessary while the writer runs.

One application owning the storage tables can simplify layout and avoid competing
business access. It does not eliminate SQL logging, background maintenance, network
costs or consistency obligations. One logical writer also does not require using
one SQL connection for every operation. Compare a dedicated load-reader connection
with the write/commit connection so a long stream does not serialize commits behind
its reader lifetime. Do not hold an arena lock for the duration of that stream.

Measure storage/server read work, network throughput, client buffering/copy cost,
native installation, index construction and catch-up separately and end-to-end.
Report warm/cold storage cache, network placement, driver/service configuration
and concurrent streams. Features that bypass normal SQL access require validation
on the chosen managed service; streaming alone does not establish their availability.

## Measurements and completion criteria

Use a bounded offered-rate driver that records intended arrival time, actual
submission, completion, failures and outstanding work. A client that simply waits
for each response before generating the next request can hide queue growth under
the intended arrival rate. Include time waiting before admission in latency;
report rejected or unsent work rather than omitting it from the denominator.

Report completed mutations/second, per-mutation p50/p95/p99 and maximum latency,
batch collection time, writer queue age/size, read latency, CPU, managed allocation,
peak RAM, data/log bytes written, checkpoint duration and replica lag. Record the
post-input drain time. A temporarily high admission rate with an increasing queue
is not sustainable throughput. Backend tests alone do not prove complete engine
performance; incremental test stages must state which costs are absent.

Verify observed document states and index results, then crash/reopen durable test
instances and account for acknowledged writes under their declared guarantee.
Include stream/checkpoint activity during writes and bounded-memory behavior when
commit or catch-up falls behind. Functional fixtures remain small; larger runs need
explicit resource/time limits and owned resources. No shared service is a load target
merely because it is available as a source of reference data.

There is no full-engine write-capacity verdict yet. The initial local PostgreSQL/file
prototype used explicit durable completion as an experiment condition; the subsequent decision selects that storage-acknowledgment requirement in Q09.1. Its bounded
workloads and measured environment are documented with the
[prototype](../../src/Prototypes/PostgresStorageComparison/README.md). Packed-memory loops,
raw TCP transfer and this storage baseline omit essential engine work; none alone
establishes a production rate or readiness guarantee. Later experiments need the
selected service, realistic workload budgets and the agreed acknowledgment policy.
