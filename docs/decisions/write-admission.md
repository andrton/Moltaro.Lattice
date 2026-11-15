# Write admission and resource pressure

**Milestone 1 implementation baseline:** [concrete mechanisms and initial settings](../design/milestone-1-engine-contract.md)
now resolve the engineering selections described as open below. Product contracts
remain in force; measured production validation is still required.

Accepted design contracts, alternatives and remaining work. No production write
admission implementation is supplied by this record.

## Q14: Write admission and resource pressure

**Partially accepted:** bounded waiting for PostgreSQL recovery, explicit master
write refusal when required memory cannot be reserved within the instance budget,
pausing child application with intact local reads under controlled memory pressure,
and a configurable root-object size limit. Memory pressure alone does not trigger
child full reload. Numerical limits, remaining protected-work limits and
startup/recovery pressure details stay open. The master memory policy applies to
both Durable and MemoryOnly instances.

[Question register](../design/questions.md) · [Durable acknowledgment](write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed) · [Persistence decisions](checkpoint-and-journal.md)

### Pending writes during temporary journal-storage unavailability

**Accepted on 13 November 2026:** option A for Durable v1.
Wait for PostgreSQL recovery within configured time and memory limits. If storage
remains unavailable until the waiting deadline, fail the waiting call with an
explicit exception. This settles outage admission, not all of Q14. The root-size
policy is accepted below; its numerical threshold, other operation limits and
numerical budgets remain open. The accepted
[background-failure policy](checkpoint-and-journal.md#transient-background-application-failure-continue-within-budget-accepted-for-v1)
only covers an applier failure while foreground journal appends still work.

#### Two different backlogs

The background tail contains **already durably committed** changes and resides in
PostgreSQL. The pending-write queue holds **not yet committed** requests, consumes
memory and provides no successful-write guarantee. A caller whose request is queued
still waits for completion. It must not be published in the arena or acknowledged
merely because the engine accepted it into that queue.

The master remains the sole application writer. Both alternatives retain normal
writer serialization; B does not remove that ordinary queue. The accepted choice
allows new calls to wait through a known temporary inability to append the journal,
within the limits below.

#### Option A: bounded waiting before execution — accepted

Allow a request to wait for storage recovery within an explicitly bounded queue and
a finite deadline. Bound both the number of requests and engine-owned retained bytes;
a count alone cannot bound RAM for variable-size documents. The exact values and
API/configuration shape remain open. No caller-held root gate spans this wait or SQL I/O.

When journal append becomes possible, execute queued requests through the ordered
writer and complete each only after durable commit and coherent native publication.
If the queue has no capacity, reject the new request explicitly. If its deadline or
cancellation occurs while it is definitely still queued, remove it and report that
it was not executed. An expired request that never started must not execute later
when storage returns. The waiting deadline is not reset by unsuccessful recovery
attempts. Resolving the dequeue-versus-cancel race is implementation work.

The accepted [cancellation boundary](mutation-commands.md#cancellation-at-the-commit-boundary)
also covers preparation before dispatch. Once work is handed off for storage,
caller cancellation does not abort execution; engine-owned finite failure deadlines
and actual outcome reporting apply. This does not turn a lost commit response into
confirmed non-execution or allow cancelled queued work to execute later.

**Thirty seconds is an illustrative configurable waiting timeout**,
not a default or measured recommendation. For example, if that value were configured
and a request remained queued throughout the interruption, it would fail when that
wait expires. Capacity exhaustion can reject it earlier. Exception type names and
the precise configuration API remain engineering work. No numerical timeout, request
count or byte budget is selected here.

A shorter interruption may instead end with normal durable completion. The policy
absorbs some temporary interruptions; it cannot increase steady-state storage
capacity or retain an unlimited burst.

Advantages: a brief interruption need not become an immediate error in application
code; the master coordinator can resume pending work when storage returns. Existing
committed local reads keep their ordinary path. It offers explicit bounded backpressure
while preserving durable completion.

Costs: increased write response time during the interruption, bounded extra RAM for
pending payloads, and queue/deadline/cancellation bookkeeping. Draining pending work
may compete with reads and background saved-state application. Waiting too long can
be worse for a latency-sensitive caller, so a finite deadline is part of the
accepted policy.

#### Option B: reject new calls while journal storage is unavailable — not selected

When the foreground is known unable to append, reject a new write promptly instead
of holding it until storage returns. The normal ordered queue and handling of
previously admitted/in-flight requests still exist. This is not a promise of detecting
a storage failure before every attempted SQL call; an in-flight command can still fail.

Advantages: less pending payload memory during a known outage, fast explicit feedback
and simpler outage admission. Applications that prefer to stop or schedule their own
retry can do so without first waiting inside the engine.

Costs: short interruptions immediately reach application code as failed requests.
Resubmission and any ordering requirements move partly to callers. This does not make
a successful SQL commit faster or improve ordinary native reads. It still requires
correct queue and transaction-outcome handling for work already in progress.

#### Guarantees preserved by both alternatives

A request that has begun SQL execution is different from one still waiting. Timeout
or connection loss around COMMIT does not prove rollback; follow the accepted Q09.1
indeterminate-outcome rules rather than return a definite not-executed result or blindly
retry a potentially committed mutation. The new policy does not select automatic
resubmission, operation-ID storage or a status-query API.

The accepted hard retained-journal limit still explicitly refuses an append that
would exceed its budget; selecting A does not silently turn that rule into indefinite
waiting. Existing admission/in-flight byte reservations must remain accounted for.
Integrity failure or incoherent publication is not a temporary availability wait.
MemoryOnly has no PostgreSQL outage. Its master memory-pressure admission is
accepted below; remaining queue-budget details stay Q14 work. Numerical wait/queue defaults are not selected by either option here.

| Aspect | A: bounded waiting | B: prompt refusal during known unavailability |
| --- | --- | --- |
| Write response | May complete after recovery, with extra waiting latency | Fast error; a later attempt belongs to the caller |
| Sustainable write speed | Storage-limited; a queue only absorbs a bounded interruption | Same storage limit; less internal backlog |
| Native reads | Existing committed data remains on the same local path; drain work can contend | Same local path; no intrinsic normal-read speedup |
| RAM | Bounded queued payloads plus request state | Less additional outage backlog, but normal queues still need bounds |
| Complexity | Capacity, deadlines and safe cancellation before execution | Simpler outage admission; callers handle more failures/resubmissions |

The decision selects **A** to absorb temporary journal-storage interruptions without
weakening durability. Keep the queue and waiting finite, return explicit exceptions
for overload or expired storage waiting, and preserve the distinction between not
started, definitively rolled back and possibly committed work. A generic storage
unavailable exception must not imply that an in-flight transaction was rolled back.

This policy is accepted, not implemented or fault-tested. The storage prototype has
a bounded queue and a pre-execution expiration check in its paused-capture workload,
but no timer-driven completion at the waiting deadline or PostgreSQL outage-recovery
and cancellation contract. Those experiments do not validate this policy. Their
count, byte and waiting limits are not product defaults.

### Master writes under memory pressure

**Accepted on 13 November 2026:** option A for v1. Enforce an instance
memory budget. Reject a new master mutation before submitting it to the Durable
journal or publishing it in MemoryOnly when required preparation/publication capacity
cannot be reserved. Release partial preparation and leave stored data and revisions
unchanged. Do not add an internal wait for future reclamation on this path.

Account for operation peaks, not just final net growth; reuse eligible free capacity
and reserve segments within budget. Existing PostgreSQL-outage waiting, root atomicity,
no-lock-during-I/O and post-commit uncertainty/recovery contracts remain unchanged.
This is controlled admission failure, not a claim that every process-wide allocation
failure can be safely caught. No numeric defaults or performance guarantee are selected.

#### Scope and budget

This question concerns admitting new application mutations on the master, in Durable
and MemoryOnly modes. It does not reconsider waiting for PostgreSQL recovery or the
existing query time/workspace limits. The accepted empty-segment reserve is reusable
capacity within the budget, not additional memory outside it.

Account for engine-owned segment capacity, indexes/dictionaries/control structures,
pending/prepared work, query workspace and maintenance headroom with clear ownership.
Do not count a reserved slot inside an already charged segment as another whole
allocation. Future capacity reservations must prevent concurrent consumers from
spending the same remaining budget. Exact managed-overhead accounting/estimates and
headroom remain engineering work; this is not an exact cap on process resident memory.

Caller-retained copies, the runtime, other embedded application code and allocator
overhead also need deployment headroom. The .NET
[GC heap hard limit](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#heap-hard-limit)
covers the GC heap and its bookkeeping; it is not a budget for the native arena or
the whole embedded process. No runtime setting or numerical memory default is chosen.

**Added by the owner on 15 November 2026:** the default of the instance budget is half of
the memory available to the process — the limit of its container, or the physical memory
of the machine — computed when the instance opens. The budget is enforced as accepted
above. An application that prefers to run into swap rather than have writes refused sets
the budget above physical memory; nothing else changes for it.

A mutation needs capacity for its preparation/publication peak, not merely its final
net growth. A replacement with a smaller final document can still need temporary
memory. A deletion is not automatically allocation-free: journal, index and command
work may need resources. Reusable slots and reserve segments should be used before
requesting new capacity, subject to type/size compatibility and existing lifetimes.

#### A. Reject when required capacity cannot be reserved — accepted for v1

Use current safe reusable space and available budget. If preparation cannot acquire
the resources needed for coherent execution within those bounds, fail explicitly
before submitting the mutation to the Durable journal or publishing it in MemoryOnly.
Release partial preparation/reservations; leave stored data and revisions unchanged.
Do not add a wait for future reader completion or future compaction on this path.
Existing background reclamation continues under its own bounded schedule.

Reserve/prepare the state needed for publication before durable commit. No root gate
spans journal I/O. A reservation does not guarantee that the OS will satisfy every
allocation; preparation failures are different from failures after a durable commit.
The accepted post-commit failure/recovery contract still applies and cannot be
reported as a clean rejection. Detailed reservation/publication mechanics remain E03/E06.

- **Reads:** existing committed data keeps its normal access path; memory-consuming
  queries/copies still obey their own limits. This is not a guarantee that every read
  succeeds during process-wide pressure.
- **Writes:** healthy admission adds accounting/reservation work. At exhaustion,
  the request receives an explicit failure without waiting for hypothetical future
  space. Operations whose full preparation fits may continue; this is not a blanket
  ban on all writes or all replacements.
- **Memory:** no extra backlog specifically waiting for memory. Ordinary queued and
  in-flight work remains bounded and counted. The limit does not create missing RAM.
- **Complexity:** budgeting and safe preparation are required in both alternatives;
  A avoids an additional reclamation-wait queue and its wakeup/cancellation rules.
- **Tradeoff:** a request can fail shortly before a reader finishes or maintenance
  creates useful space. The caller chooses whether to retry; automatic replay is not
  selected, especially for uncertain commit outcomes.

#### B. Wait for memory within bounded time and retained bytes — not selected for v1

If current capacity is insufficient, retain the pending request only within finite
time, count and byte limits. Resume when compatible safe capacity becomes available;
otherwise fail before execution. No lock is held while waiting, and a timed-out call
that never started must not execute later. A request that cannot fit even by itself
must not wait as though a short reclamation delay could solve it.

- **Reads:** same ordinary data access; background work and retained pending state
  can still compete for resources.
- **Writes:** brief reader/reclamation delays can be absorbed, but response time rises
  and steady live-data growth cannot be solved by waiting. Under ordered writing,
  a blocked request may also delay later deletions that could release memory.
- **Memory:** waiting payloads and request state consume some of the scarce budget.
  They must not consume the headroom required for reclamation or completion.
- **Complexity:** wakeup, fairness, cancellation and deadline races, plus writer-order
  coordination. Do not silently reorder mutations or let a waiting foreground writer
  prevent the local maintenance work it depends on from running.

#### Selection rationale and remaining boundaries

The decision selects A for v1: explicit bounded admission and a clear pre-execution failure are
simpler than waiting for space that may never appear. B is useful for genuinely short
retention spikes, with added queueing cost. Neither policy evicts valid documents,
exceeds the budget as a fallback or frees storage still used by a protected reader.

Child replica application and recovery are separate admission situations: their
changes may already be committed on the master. They cannot skip an unapplied change
or advance the applied position merely to satisfy a limit. The later child policy
below accepts a controlled application pause with intact local reads and bounded
buffers. Numerical limits and remaining startup/recovery mechanics stay open under
Q14/Q08.1.3; the master-write policy itself does not alter local stale-read guarantees.

Measure accounting overhead, preparation peaks, active-reader retention, query versus
writer contention, replacement/delete workloads and maintenance progress. Exercise
failures before journal submission and after commit separately. Existing copy/storage
prototypes do not establish these guarantees or latency results. This acceptance
adds no runtime implementation, retry mode or benchmark run.

### Child replica memory pressure

**Accepted on 13 November 2026:** option A for v1. When an initialized
child cannot prepare the next change within its memory budget, pause incremental
application before publication, release partial preparation and preserve intact local
data. Existing local reads continue under ordinary limits; replication status exposes
pressure and lag. Bound receive/prepare work and resume in order when capacity permits.
Memory pressure alone does not trigger full reload; missing or incompatible history
still requires it under the accepted reconnect contract. No second arena, persistent
child spool, skipped change or master acknowledgment dependency is introduced.

#### What is already settled

A master write succeeds without waiting for children. The child cannot turn an
already committed master write into a rejected application command. V1 children
are MemoryOnly, have bounded receive/history work, and do not keep a durable spool.
Initialized intact local data remains readable while lagging or disconnected, with
observable applied versus observed master progress. That read contract remains unchanged; the accepted policy below specifies the
response to controlled application memory pressure.

This acceptance concerns controlled budget/preparation failure before publishing the
affected local mutation. It does not treat arbitrary process-wide out-of-memory,
corruption or partial publication as a safe stale-read condition. Completed earlier
mutations remain applied; partially prepared data must not leak into the arena or
indexes. The installed state retains per-root coherence, not a graph/query snapshot.

#### A. Pause application, retain intact local state — accepted for v1

Stop at the next mutation that cannot be prepared within budget. Release its partial
preparation and preserve the last coherently applied progress. Allow maintenance
and safe reclamation to continue; pausing the applier must not also prevent the
local maintenance work that can restore capacity.

Expose resource pressure and the applied/observed progress through replication
status. Do not report caught-up while a required change remains unapplied. Existing
reads continue against intact installed data under their ordinary resource limits;
new copies and query workspace still need capacity. No additional freshness promise,
Kubernetes readiness policy or status API signature is selected here.

Bound per-child receive/prepare memory. Stop admitting additional work before those
budgets are exhausted; use flow control or disconnect/resume as required by the
eventual protocol. Do not grow an unlimited message queue, block master commits on
this child or pin history indefinitely. No disk spill or second arena is introduced.
Transport receipt must not be confused with coherently applied progress.

After reclamation or an explicit capacity/configuration change permits progress,
retry from the missing position in order. Rechecks must be paced or driven by useful
capacity changes rather than busy-looping; timings remain to be measured. Do not
skip a large mutation to reach a later deletion that might free space, and do not
reorder commands to disguise the gap.

If the complete compatible tail is no longer available, the accepted full-reload
contract still applies. Retaining the old arena cannot turn a missing tail into
successful catch-up. Report reload-required/resource-blocked state as applicable;
capacity-related reload failure must not trigger an endless discard/reload loop
under unchanged conditions. Startup and partial full reload never expose incomplete
data as ready. Detailed admission/retry mechanics remain engineering work.

- **Reads:** preserve the existing local-read path and useful data while incremental
  application is paused. Values may become increasingly stale; pressure can still
  prevent operations that require additional workspace.
- **Master writes:** no child acknowledgment is added. Normal replication work still
  has costs, but this child cannot require an unbounded send backlog or history pin.
- **Memory:** keep one installed arena and bounded receive/preparation state. Pausing
  does not make a larger live dataset fit; more capacity or a smaller configured
  selection may ultimately be needed.
- **Complexity:** pressure state, bounded flow control and resumption coordination;
  largely uses accepted lag/reconnect behavior. No claim of measured overhead.

#### B. Attempt automatic full reload under memory pressure — not selected for v1

When incremental preparation cannot fit, deliberately enter the existing full-reload
path: drain protected users, release the affected old state and rebuild the selection
and indexes from the master using one arena. Ordinary reads use the accepted bounded
readiness waiting and then succeed or fail explicitly. Never expose a partial rebuild.

- **Reads:** lose access to the old installed selection during reload. Requests may
  time out if loading/index construction/catch-up does not finish within their budget.
- **Master writes:** acknowledgments still do not wait for this child, but capture,
  transfer and catch-up consume master CPU, memory and network capacity.
- **Memory:** rebuilding can remove fragmentation or avoid some incremental growth
  peaks, but a full load and index builder also need workspace. One arena does not
  guarantee that the latest selection fits or that peak process memory decreases.
- **Complexity:** reuses the full-load path but adds pressure-triggered scheduling,
  failure handling and retry limits. Repeated retries under unchanged insufficient
  capacity cannot be the recovery strategy.

B is useful when rebuilding is known to reduce the required footprint enough.
Without such evidence, discarding useful data may turn stale reads into unavailable
reads without resolving the memory shortage.

#### Selection rationale and evidence boundary

The decision selects A for v1. It preserves the accepted local-read behavior while memory can
be reclaimed and avoids a full reload solely because incremental preparation failed.
Missing/incompatible history remains an independent reason requiring full reload.
No live-data eviction, master rollback or automatic writer failover is selected.

Before choosing budgets, measure transient retained-reader pressure, fragmentation,
permanent live-set growth, a large change followed by a deletion, tail expiration
while paused and restart/reload with insufficient capacity. Verify bounded buffers,
unchanged local data/revisions on pre-publication failure, exact applied progress,
ordered resume and no partially visible indexes. Compare full rebuild cost separately.
Existing storage/copy prototypes establish none of these outcomes. This acceptance
adds no production implementation or new benchmark run.

### Configurable root-object size limit

**Accepted for v1:** option A. Enforce a configurable maximum size for each root
object, even when the instance still has spare memory. Choose the numerical limit
from measurements; no default or fixed number is accepted yet.

The limit covers the root's own data: fixed fields, strings, Binary values, owned
nested objects and collections. A reference contributes its stored key, not the
contents of the independently owned object it identifies. A graph spread across
roots is therefore not one object for this limit. Splitting an owned value into
native chunks does not remove it from the owning root's size.

Validate the resulting root before submitting its mutation to the Durable journal
or publishing it in MemoryOnly mode. This applies to complete writes and explicit
partial updates. Exceeding the limit produces an explicit error without changing
stored data or revision; never truncate data to make it fit. A rejected member of
an explicit WriteBatch prevents that batch from committing, preserving its accepted
atomicity. Exact byte accounting, including shared string storage, is engineering
work; it must account for the owned contents rather than only their small handles.

The root-size limit is separate from the instance memory budget, peak preparation
reservation and bounded WriteBatch. Passing it does not establish that an operation
fits those other budgets. It also does not bound how long caller code can run inside
a protected-read callback or how much memory a caller retains in independent copies.

**Costs and rationale:** limiting one root bounds the data volume of whole-root
copies and full Save replacement. It does not promise a measured latency ceiling.
Checking size during preparation adds accounting work; targeted partial updates
should use maintained size information or equivalent accounting without copying
the whole root merely to validate the limit. The concrete mechanism remains open.
The alternative of relying only on total instance capacity would permit one root
to dominate a protected copy or replacement. No runtime implementation or new
benchmark is claimed by this decision.

### Remaining Q14 work

Choose numerical deadlines, instance/queue/workspace budgets and the root-size
threshold using measurements; resolve remaining protected-operation limits. Define
exact accounting and reservation mechanics, including managed overhead and headroom.
Read admission and remaining startup/recovery
pressure details remain distinct from admitting a new master mutation or pausing an
initialized child. The accepted child policy does not make an incomplete load readable.

Q17 now accepts [automatic bounded collection shrink](allocation-and-compaction.md#automatic-shrinking-of-unused-collection-capacity),
with concrete algorithms and numerical budgets still open. Q09.4 also accepts
[PostgreSQL-native Durable backup responsibility](backup-and-restore.md#durable-backup-responsibility)
and [isolated replacement restore with one arena](backup-and-restore.md#restore-destination).
[New history/full child reload after restore](backup-and-restore.md#replica-history-after-restore)
is accepted as well. [Q15](replication-and-hosting.md#q15-configured-master-and-child-connection) accepts
the configured URL and environment-owned lifecycle, with no fault-tolerance orchestration. See the
[remaining resource questions](../design/questions/write-admission.md) for Q14; neither accepted
master policy introduces automatic foreground resubmission.
