# Durable write acknowledgment

Accepted Moltaro.Lattice completion guarantee, rationale and remaining engineering.
The storage guarantee is settled; no production write pipeline is implemented.

[Decision index](README.md) · [Question register](../design/questions.md) · [Measured evidence](../design/prototype-evidence.md#postgresql-storage-and-streaming) · [Persistence modes](persistence-modes.md)

<a id="q09.1-when-does-a-durable-write-succeed"></a>

## Q09.1: When does a Durable write succeed?

**Accepted on 12 November 2026:** the first Durable storage implementation
must wait for reliable storage acknowledgment before reporting write success.
PostgreSQL is the first backend selected in
[Q09.6](persistence-backends.md#q09.6-durable-storage-backends). Early RAM-only
success followed by background persistence is outside Durable v1.

This selects the durable-success requirement of A. Batch size/delay, latency SLOs,
checkpoint frequency, operation-status APIs and child freshness remain separate.

### Accepted storage guarantee and local publication

For Durable v1, return success only after the change is durably committed in
PostgreSQL and coherently published in the master's arena and affected indexes.
Do not return success merely for queue admission. This confirmation does not select
an all-child acknowledgment barrier. A [write forwarded through a child](replication-and-hosting.md#completion-of-a-write-forwarded-through-a-child)
must additionally wait for that calling child's coherent local application before
its own success response. [Related requests propagate a minimum applied position](session-consistency.md)
with bounded waiting on any serving node; there is no all-replica barrier.

Master authority permits concurrent callers; it does not restrict the API to one
calling thread. Internal scheduling is covered by the [engine foundation](../design/engine-foundation.md).

The accepted [preparation policy](physical-storage.md#targeted-mutation-preparation-and-publication)
uses targeted changes for explicit partial updates and full replacement for Save
without changing this completion guarantee. Internal storage batching is accepted
below. The later [ordered commit/publication decision](#ordered-commit-and-publication)
accepts one ordered stage per instance; since its amendment of 17 November 2026 changes
are also prepared in it, one write at a time. Admission budgets and batch sizes remain
engineering work.

### Per-object coordination and internal group commit

**Accepted on 13 November 2026:** protect and coordinate conflicting writes per
object. Prepared independent changes may share one PostgreSQL transaction. This
internal grouping does not create public multi-object read atomicity or a common
query snapshot. Each ordinary write completes only after its durable commit and
coherent local publication.

While a write awaits storage, conflicting writers for that object must not invalidate
its prepared state. Readers may continue using the previous published state; no
root/index gate is held over I/O. This requires write scheduling/ownership distinct
from the short reader-excluding publication gate.

This per-object decision permits many concurrent callers and storage batching.
The ordered stage selected below adds the scheduling baseline. Batch limits and concrete same-root dependency
handling remain engineering work. Commit amortization is not an end-to-end
throughput guarantee.

An explicit application-requested all-or-nothing batch is a separate accepted
[Q01 contract](document-model.md#atomic-writebatch-within-one-database). It requires
whole-group validation, commit, publication and recovery/replica boundaries; internal
group commit alone does not satisfy it.

Accepted write ordering, with detailed publication engineering still owned by E03/E06:

1. Accept concurrent callers, order conflicting mutations at the master and prepare the state
   needed for safe publication. Keep queue and preparation memory bounded.
2. Persist the ordered mutations and receive a durable PostgreSQL commit. The
   subsequently accepted Q09.2 strategy appends compact changes to its PostgreSQL
   application journal; Q09.1 itself defines the acknowledgment guarantee.
3. Publish each root and its affected indexes coherently under the required short
   gates. No root gate is held across storage/network I/O.
4. Complete the mutation after durable storage and coherent local publication.
   Child delivery uses committed, locally published changes; ordinary write success
   does not wait for child application under the accepted replication contract.

Ordinary master readers must not see an uncommitted mutation from this pipeline.
They can read the previous committed state while storage is in progress. After
success the mutation has been applied locally; a later writer can of course replace
it. Multiple roots in one persistence batch do not gain a public atomic transaction
or a shared query snapshot. Root-level consistency remains the accepted contract.

The guarantee relies on PostgreSQL WAL durability rather than forcing an entire
engine checkpoint for each change. PostgreSQL documents local WAL flush before
success for non-off synchronous commit modes. With synchronous PostgreSQL standbys
configured, `synchronous_commit=on` also waits for their durable receipt. See
[PostgreSQL commit settings](https://www.postgresql.org/docs/current/runtime-config-wal.html#GUC-SYNCHRONOUS-COMMIT).
Keep `fsync` and the required durable-storage guarantees enabled; do not weaken
the selected service's HA guarantee to reproduce local benchmark latency.

PostgreSQL standby replication and Lattice child replication are separate systems.
Not waiting for Lattice children does not instruct PostgreSQL to skip its configured
durable standby acknowledgment. Local durability alone does not promise survival
of total storage loss or promotion of an asynchronous PostgreSQL standby.

### Ordered commit and publication

**Accepted on 13 November 2026 for v1:** prepare independent changes concurrently,
then save and publish short bounded groups sequentially through one ordered stage
per database instance. Complete the current group's storage outcome and local
publication before advancing the next group through that stage. Concurrent API
callers and independent preparation remain supported within admission budgets.

**Amended on 17 November 2026 by the owner.** Changes are prepared one write at a time,
under the one lock of the writes that also publishes them; they are not prepared
concurrently. Epic 1, sprint S09, built concurrent preparation in front of the ordered
stage and measured it: the coordination it needs cost more than the overlap gained (one
caller replaced 1.21–1.29 million entities a second and 16 callers 0.57–0.64 million in
total, against 1.59–1.63 and 0.91–1.03 million for the path that replaced it, in memory
only), and the write path was rebuilt after established stores that execute writes under
one lock and group them only at the storage commit. What stands unchanged: any number of
callers may write concurrently; no lock is held across the storage wait; while a group is
in storage, other writes are prepared and form the next group; one storage commit serves
a group; groups are published in order, the current one before the next. Where this
record or a document that cites it says "concurrent independent preparation", read
"preparation of other writes while a group is in storage". Whether preparation runs
concurrently is not a guarantee to applications; it is open again as
[Q52](../work/open-questions.md#q52-does-the-write-path-need-the-parallel-form) for the
stage that builds durable storage.

- Preserve conflict ownership for the same root and explicit WriteBatch so that
  a prepared mutation's predecessor cannot be invalidated before commit. Work on
  independent roots may prepare while the current group waits for PostgreSQL.
- Combine prepared independent operations in a bounded PostgreSQL transaction.
  Only one such foreground commit/publication group occupies the ordered stage
  at a time. No arena, root or index gate spans database or network I/O; readers
  continue using the previously published state during the storage wait.
- After durable commit, publish the logical operations in order and advance the
  published position only over completely published transactions. Prepare the
  required publication/delivery resources before commit under the existing budget.
  An explicit WriteBatch preserves its whole-batch publication barrier and is not
  split into separately visible or separately committed pieces.
- Ordinary operations combined for storage retain their individual publication
  boundaries. A storage group does not acquire public multi-object read atomicity.
  Existing per-operation completion after durable commit and local publication
  remains sufficient; child acknowledgment is not added to write completion.
- Rejected or rolled-back work cannot be reported as published. Resolve an
  uncertain commit through the accepted operation-result mechanism before
  advancing the ordered stage past it. Failure after a known commit follows the
  accepted instance stop/reopen policy; no later publication bypasses the failure.
- MemoryOnly uses the same ordered publication boundary without the PostgreSQL
  step. The saved-base background applier is separate from this foreground stage.
  This decision does not prohibit its independent PostgreSQL maintenance work.

An asynchronous coordinator does not require a dedicated OS thread. No fixed
worker count, artificial batching delay, queue size or maximum group size is
selected here. The group must be bounded by admitted work and memory; concrete
limits and fairness need measurement and implementation. An expensive group can
delay later groups, so accepting this baseline does not establish a throughput
or tail-latency guarantee.

[Caller cancellation](mutation-commands.md#cancellation-at-the-commit-boundary)
is honored before the synchronized storage-dispatch boundary. After dispatch, finish
the engine path within its own finite failure deadlines and report the actual
outcome; one caller's token cannot interrupt the shared commit or publication.
MemoryOnly uses the corresponding handoff to publication. A timeout that leaves
commit uncertain remains Unknown, not confirmed non-execution.

The [initial replica-loading protocol](replication-and-hosting.md#initial-capture-and-readiness-handoff)
uses the resulting published order. A higher assigned operation number alone is
not a completion certificate. Concrete position assignment, abort/uncertain-result
handling and capture registration must preserve that invariant. ReplicaBootstrap
checks bootstrap interleavings, not this concurrent storage/preparation pipeline.

Multiple independently committing foreground groups are not the v1 baseline.
The [comparison](../design/questions/master-write-order.md) records their potential I/O
parallelism and extra ordering/memory costs. Detailed scheduler, reservation,
index-lifetime and failure integration remain E03/E06/E07 work.

### Rejected for Durable v1: acknowledge in RAM, persist afterward

Publish the mutation in the master's arena and return success, then persist its
ordered change asynchronously. Caller latency can be lower because it excludes
storage completion. This creates a window in which a successful write can disappear
if the master fails. Publishing or replicating these early values also exposes
state that durable recovery might not retain.

If PostgreSQL slows down, acknowledged-but-unpersisted changes occupy a growing
queue until a bound forces waiting or rejection. Background persistence must still
keep up with input; earlier answers do not increase the storage engine's sustainable
throughput. The loss window cannot be described as a fixed number of milliseconds
when storage is unavailable.

PostgreSQL's own asynchronous commit is another way to acknowledge before durable
flush, with a different loss window. It is not a substitute for a documented
application guarantee; [PostgreSQL describes the lost-commit risk](https://www.postgresql.org/docs/current/wal-async-commit.html).
Neither early-success mechanism belongs to Durable v1. Explicit MemoryOnly
already exists for applications that require no durable recovery, but B is distinct:
it would still persist data eventually and introduce its own recovery semantics.

### Costs of the two options

| Aspect | A: durable commit, then publication/success | B: RAM publication/success, then persistence |
| --- | --- | --- |
| Write response | Includes batch wait, PostgreSQL commit and local publication | Can complete before the storage round trip/flush |
| Sustained write rate | Bounded batching amortizes commits; storage limits remain | Same eventual storage work; a queue only postpones backpressure |
| Ordinary reads | Local arena; previous committed values remain readable during I/O | Same local access, but returned values may disappear after recovery |
| Temporary memory | Bounded pending/prepared work, reclaimed after completion and reader safety | Also retains already-acknowledged work until persistence catches up |
| Failure semantics | Successful writes recover when the durable store survives under its declared guarantee | Some successful writes may be lost; define and operate a loss window |
| Complexity | Commit/publication coordination and ambiguous outcomes still need implementation | Adds early-visible state, loss-window semantics and acknowledged-backlog handling |

The prototype's 10,000/s batch runs measured approximately 2.75–3.34 ms end-to-end
p99 on local PostgreSQL with durable commits. This makes A a credible starting point,
not an accepted production latency budget. The full engine adds indexes, schema
work and other costs; cloud/network/HA conditions differ. The measured 2 ms batch
collection setting remains an experiment input.

### Failure outcomes that the implementation must distinguish

- A rejected or definitively rolled-back mutation is not published or successful.
- Losing a connection or timing out around commit does not prove rollback. The
  durable change may exist even if its caller did not receive success. Report an
  indeterminate outcome. The accepted [operation-result contract](mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1)
  provides authoritative lookup and same-token resubmission within a bounded window.
  Do not silently retry a non-idempotent command as a new operation.
- A crash after commit but before publication/response requires restoring committed
  state through the provider; application replay is needed only for formats that
  use an application recovery journal. If local publication fails after commit,
  it cannot be reported as a clean rollback. The accepted
  [instance-recovery policy](checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure)
  stops new local reads/writes and restores coherent state through close/reopen,
  without mandatory host-process termination. E03/E06 must implement and test it.
- When PostgreSQL is unavailable, A cannot acknowledge new durable writes.
  [Q14](write-admission.md#q14-write-admission-and-resource-pressure)
  now accepts bounded recovery waiting and explicit failure on expiration or queue
  exhaustion. Numerical limits remain open. A request that never started must not
  execute after its expiration; an uncertain in-flight commit remains indeterminate.
  Existing local reads follow the applicable availability/freshness contract.

The no-I/O-under-root-lock rule applies in every case. A method can return a Task
without changing A: awaiting successful completion still waits for durability and
publication. An asynchronous API is not the same as early acknowledgment.

The accepted [operation-outcome contract](mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1)
adds compact durable receipts and bounded safe resubmission. Commit and publication
remain distinct; a saved receipt does not permit successful completion while the
master arena still requires publication or recovery.

### Boundaries left for later questions

MemoryOnly completion remains atomic local publication with no durability promise.
Batch count/byte/delay limits and load targets remain Q10.1/Q14. Checkpoint and
change-tail retention are Q09.2; backups are Q09.4. PostgreSQL hosting is an
environment concern under Q15, not a Lattice failover subsystem. Child freshness is Q08.1.2. Coordinating an unrelated application SQL
transaction with a Lattice write is excluded from v1 by [Q09.3](external-transactions.md); PostgreSQL-backed storage does not
automatically provide that integration.

The successful-storage acknowledgment requirement is settled. Detailed publication,
recovery and uncertainty-handling algorithms remain E03/E06 work; their implementation
must preserve this guarantee, ordinary per-root consistency and explicit database
WriteBatch atomicity.
