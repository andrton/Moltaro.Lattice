# Q09.2: Base maintenance and background journal application

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Method accepted for v1:** background application of the committed journal with
coalescing. The [decision record](../../decisions/checkpoint-and-journal.md#q09.2-postgresql-base-and-compact-journal)
owns this contract, the compact journal/base representation, automatic size/age
triggers, hard retained-journal limit and the accepted **256-byte baseline saved
unit** for PostgreSQL v1. Short bounded application batches with immediate catch-up
are also accepted. On 13 November 2026 the decision selects continuing healthy journal
appends during transient applier failure, with paced retries and the existing hard
limit. Numerical budgets and consumer coordination remain open. No public
whole-database snapshot is added.

**Completed experiment:** [256 B, 1 KiB and 4 KiB saved units](../prototype-evidence.md#sustained-comparison-of-compact-saved-units) on the same
128 MiB dataset, with uniform 10,000 changes/s for three minutes per variant and
a journal-only control. It measures sustained lag, write/read interference, memory
and recovery. 10,000/s represents peak demand; this deliberately long
peak is a stress test, not a normal-load requirement. The 256-byte baseline is now
accepted, together with bounded-window scheduling. Numerical timing, batch limits
and other test settings remain experiment inputs.

Short pauses were considered acceptable in principle, subject to measurements.
The historical capture options A–D below preserve that earlier comparison; none is the normal v1
maintenance mechanism selected now. Backup and replica capture remain separate.

[Question register](../questions.md) · [Larger pause and queue evidence](../prototype-evidence.md#larger-paused-captures-and-pending-write-memory) · [Storage lifecycle](../storage-and-maintenance.md)

## Next discussion and remaining persistence work

The [transient applier-failure policy](../../decisions/checkpoint-and-journal.md#transient-background-application-failure-continue-within-budget-accepted-for-v1)
is now accepted, including the clarification that the worker lives inside the master
process. Its SQL connections go to PostgreSQL, not to a separate worker service.

[Q14 bounded recovery waiting](../../decisions/write-admission.md#q14-write-admission-and-resource-pressure)
is now accepted for requests **not yet durably accepted**, unlike the already-durable
journal tail held while an applier recovers. Its numerical budgets remain open.
Q13 accepts document/scalar updates and collection replacement/element commands.
Unchanged collection outcomes and invalid List position failures are now accepted.
Partial updates require existing targets and explicit initialization. Complete-document
Insert/Replace/Upsert, ordinary deleted/already-absent results and optional revision
checks are accepted. Primary keys are immutable in v1, without a rename command.
Numeric writes assign ready values, with arithmetic commands deferred beyond v1.
Full-document Save and a separate explicit partial-update API are accepted, without
automatic change tracking. Mutable write inputs remain unchanged until asynchronous
completion, including error/cancellation completion. Active push of binary logical
changes is accepted, with full documents for full writes and explicit partial effects,
without invalidation or automatic Save diff. Q08.1.2 now accepts intact local reads
during lag/disconnection and explicit master-progress comparison. Q08.1.3 accepts
bounded resume/full reload and one arena with bounded readiness waiting for reads.
[MemoryOnly child restart](replica-restart.md) is accepted. See [question register](../questions.md#current-discussion).
[Repeated scalar targets in one explicit partial mutation](../../decisions/mutation-commands.md#repeated-scalar-targets-in-an-explicit-partial-update)
reject that mutation or its containing WriteBatch before commit. Duplicate roots in an
explicit WriteBatch are also rejected. Local setters are not database writes.
Q09.2 still has numerical retry/resource/recovery budgets, consistent loading and
retained-consumer coordination to resolve.

## A. Pause mutations and stream the stable arena — historical alternative

The initial recommendation used one checkpoint barrier in the single writer's ordered work stream.
Complete the work before that barrier, temporarily stop applying new mutations,
then stream a complete base from the stable arena using bounded buffers. Ordinary
resident reads continue under their existing access contracts. The pause covers
the Durable database instance's mutation stream. This historical whole-arena capture
alternative does not add selective data scopes or reopen the settled Q12 boundary.

This trades write availability during capture for low additional engine RAM and
simpler consistency. RAM efficiency has priority and short pauses are acceptable in principle,
making this a candidate to measure. It remains a proposal; the actual duration and
queue cost must inform the choice.

### Proposed capture sequence

1. Insert the checkpoint barrier into writer order. Finish earlier admitted work,
   including any durable commit, coherent native/index publication and ambiguous
   outcome resolution needed to establish one known committed boundary C.
2. Keep the captured data and required metadata stable at C. Pause user mutations,
   schema changes and physical relocation/reclamation that could invalidate the
   capture. Transient reader bookkeeping is not serialized as database state.
3. Keep serving native reads. Root gates retain their ordinary short lifetimes;
   do not hold a global reader lock or any root gate over storage/network I/O.
   The writer lifecycle barrier, not a long-held memory lock, prevents mutation.
4. Serialize the complete required state in bounded chunks: documents/ownership,
   schema/layout metadata, dictionaries and the selected index policy. Runtime
   pointers, gates and managed object references are not a portable base format.
5. Write a new base generation without replacing the currently recoverable one.
   Chunks may be committed in bounded SQL transactions. A partial generation is
   never eligible for recovery merely because some of its rows are durable.
6. Once the new generation is complete and validated, atomically and durably
   publish its identity and boundary C in PostgreSQL. Preserve the old base and
   required journal until this publication is known to have succeeded.
7. Resume mutations in order. Changes after C remain in the journal. Reclaim the
   covered journal/old generation only when recovery and retained consumers no
   longer need them; reclamation need not extend the main capture pause.

The generation/publication sequence is an architectural candidate, not approved
production SQL DDL. The earlier unit prototype uses single-transaction TRUNCATE/COPY;
the larger pause prototype streams separate generations and publishes a manifest.
Neither experimental layout is a production requirement.

The proposed read-availability guarantee applies to the already loaded native
read path, including ordinary master/child reads. A new child's initial transfer,
backups and SQL readers still need their own coherent boundary and retention
coordination; this proposal does not solve their lifecycle implicitly.

### Waiting writes and bounded memory

Recommend allowing later writes to wait in the existing ordered writer queue,
with explicit limits on queued bytes/count and waiting time. No waiting write is
acknowledged before its durable commit and native publication. A full queue or
expired pre-execution wait produces an explicit failure, not silent loss or an
unbounded backlog. An expired/cancelled operation that has not started must not
be applied later as an unnoticed mutation. Once storage execution has begun,
the existing uncertain-commit contract still applies; a timeout is not proof
that the operation rolled back.

The already accepted hard retained-journal limit and reservation accounting take
precedence over queue admission. This capture proposal does not replace that limit
with unlimited waiting. Queue sizes and wait durations remain unaccepted settings
under Q14/E03. Read-only requests do not enter the paused writer queue.

Illustration: at 10,000 incoming writes/s, a five-second pause produces 50,000
waiting requests if all are admitted. Their RAM includes commands, strings,
completions and caller state, not only compact journal bytes. A full arena copy
may be avoided while an unbounded request queue still exhausts memory; both
capture buffers and pending work must have owners and finite budgets.

### Pause duration and failure boundaries

The pause includes earlier-work drain, serialization, transfer/storage and durable
base publication. It is not merely a memory-copy pause and is not promised to be
milliseconds. The earlier 16 MiB prototype took roughly 0.6–1.6 s. The requested
larger local experiment observed 3.74–5.28 s at 128 MiB, 15.31–22.89 s at 512 MiB,
and 30.36–38.54 s at 1 GiB. Those intervals include about 81–139 ms of instrumentation;
they exclude earlier-writer drain and real schema/index work. See the
[measurement explanation](../prototype-evidence.md#what-the-timing-means).

At 10,000 requests/s, the tested queues reached their limits in every large case.
Their calibrated peak retained memory was 6.48 MiB for 32-byte commands and
18.86 MiB for 1 KiB commands, with explicit refusals; the 1 GiB cases also expired
requests under the test's 30-second pre-execution budget. Accepting every arrival
would require a different queue/deadline budget. The report separates measured
bounded-queue memory from estimates for that alternative. These results do not
justify assuming A provides a short pause for frequent writes on large datasets.

Recommend a configurable capture deadline/cancellation boundary, with the numeric
budget left for measurement. Before publication, a failed or cancelled build leaves
the old complete base and its required journal authoritative. Do not advance the
base boundary or release required history. After restoring a known coherent state,
resume writes if storage and admitted budgets permit; a failure must not claim
that compaction freed space. Automatic retry/backoff remains a separate open topic.

If the final publication commit has an uncertain outcome, resolve the active base
before cleanup or other actions that depend on that result. Cancellation during
SQL/network I/O is not an exact wall-clock pause guarantee. No implementation is
allowed to dispose memory still used by an unfinished capture operation.

A serial writer also has to drain the accumulated bounded backlog afterward. A
pause shorter than the interval between triggers does not alone prove sustainable
operation: normal write throughput must have spare capacity for catch-up. If the
needed pause or backlog is unacceptable, choose another capture method rather
than increasing the queue without bound.

### Costs

| Aspect | Expected effect |
| --- | --- |
| Ordinary reads | Continue from the stable local arena; CPU, cache and memory-bandwidth contention can still reduce throughput |
| Writes during capture | Application pauses; requests wait only within the proposed bounded queue/deadline policy if selected |
| Extra engine RAM | Bounded serialization/driver buffers and bounded pending requests; no second full capture image required |
| Disk | Old and replacement base plus journal/SQL overhead coexist until safe publication and reclamation |
| Complexity | Simplest proposed coherent capture; still needs writer barrier, complete-generation publication and failure handling |
| High sustained update rates | Most sensitive to pauses and backlog drain; measure with the actual workload before accepting a numerical availability target |

## B. Copy the whole state, then persist the copy

Pause mutations long enough to build a coherent independent copy of all required
data and metadata. Resume writes and persist that copy afterward. The write pause
can be shorter because it excludes most storage time, but includes copying and
possibly serialization. A raw memcpy of pointer-rich segments is not automatically
a complete independent snapshot.

Extra RAM can approach the size of another dataset, in addition to buffers and
queues; an arena of 4 GiB can therefore need roughly another 4 GiB just for captured
state. Reads compete with the copy for memory bandwidth. Ordinary writes avoid
per-update snapshot tracking after the copy. Complexity is lower than general COW,
but this RAM tradeoff conflicts with the stated priority, so it is not recommended
as the default v1 method.

## C. Copy-on-write while updates continue

Establish a capture boundary and preserve the old contents of a block before its
first destructive modification while the snapshot still needs it. Serialize the
old logical state while the writer proceeds on current data. Correct handling also
covers allocation, deletion, dictionaries, schema/ownership metadata and reclamation.
A short initial barrier does not make those obligations disappear.

Extra RAM follows the amount of still-needed data overwritten during capture, not
just the number of updates. Repeated writes to one small block need not create a
permanent version per write, but broad churn can retain an amount comparable to
another dataset; coarse blocks amplify small changes. Reclamation and an explicit
memory-exhaustion response are mandatory. More CPU/copy work enters the write path,
and reads contend with that work. This is more complex than A/B and does not offer
an unconditional small-memory or zero-pause guarantee.

[Redis persistence](https://redis.io/docs/latest/management/persistence/) is an
industry example of COW-backed background snapshotting. Its process-fork mechanism
is not a selected .NET implementation or a cross-platform guarantee for Lattice.
The memory/correctness tradeoffs above are design analysis for this proposed engine.

## D. Rebuild a base from persisted state in a separate worker

Choose and retain an existing base B and committed changes through boundary C.
A worker reconstructs state at C from those inputs and writes a new base while
the live writer continues with changes after C. Publication must not remove later
changes, regress the writer's committed position or release the worker's input
prematurely. SQL read isolation can stabilize stored rows; it does not interpret
Lattice's changes or assemble the arena. See
[PostgreSQL repeatable-read snapshots](https://www.postgresql.org/docs/current/transaction-iso.html).

An implementation that reconstructs the complete target arena in RAM needs a
second dataset there. Putting it in another process/host moves that requirement.
Bounded-memory reconstruction is possible only with a suitable persisted layout,
change routing/indexing and/or disk workspace; it needs a separate algorithm and
measurements. A second full RAM image is not a requirement of every background
approach; option E updates addressable saved units with a bounded workspace.

Reads and normal mutations can continue, with extra SQL I/O/CPU and retained-journal
pressure. If the worker cannot keep up or reaches the accepted hard limit, writes
still need the accepted pressure response. This is attractive if pauses are
unacceptable, but involves more layout/recovery work than A and is not the proposed
v1 baseline. No additional production service is made mandatory here.

## E. Continuously apply the journal to saved units — accepted for v1

The accepted method advances saved state from ordered journal prefixes using a
bounded workspace and atomically commits affected units together with their applied
position. The foreground still waits for durable journal commit and coherent native
publication. See the [contract and recovery conditions](../../decisions/checkpoint-and-journal.md#background-application-with-coalescing-accepted-for-v1).

This avoids a mandatory complete live-arena freeze or second arena. It still adds
SQL reads, updates and resource competition. Coalescing reconstructs equivalent
final state; its benefit depends on change locality and unit size. Application,
logical journal deletion and physical disk reclamation are distinct workloads.

| Aspect | Expected effect to verify |
| --- | --- |
| Foreground writes | Await journal commit and native publication; background SQL competes for storage/CPU and can increase acknowledgment latency |
| Native reads | Continue without per-read SQL; contention must be measured |
| RAM | Bounded changed-unit buffers and command/lookup structures; unapplied history remains on disk |
| Saved-state writes | One update per touched unit per worker transaction; savings depend on repeat frequency, window size and persisted unit size |
| Recovery | Saved units plus unapplied tail; sustained worker lag lengthens replay |
| Disk | Journal, saved units, SQL update versions and PostgreSQL WAL coexist; cleanup is an independent workload |
| Complexity | Bounded coalescing, atomic data/cursor updates, restart, consistent loading and retention; no whole-arena freeze required |

The [runnable experiment](../../../src/Prototypes/PostgresStorageComparison/README.md#background-journal-application-with-coalescing)
compares journal-only, immediate application and larger coalescing windows on
uniform and hot-key workloads. It measures acknowledgment/read latency, lag during
arrivals, bounded memory and residual-tail recovery. Results from paused full-base
capture cannot establish the throughput or pause behavior of this different path.

The [measured results](../prototype-evidence.md#background-journal-application-with-coalescing)
show bounded workspace and correct saved-state-plus-tail recovery. At the tested
10,000/s input, hot-key coalescing with 4 KiB units maintained a small tail; uniform
updates and 64 KiB units fell behind. Short successful runs do not override observed
latency/refusal variability. The subsequent decision selects E as the v1 method.
The 256-byte saved-unit baseline is now accepted. Application-window budgets and
sustained resource behavior remain to be established.

## Remaining implementation choices

The method, 256-byte baseline, bounded-batch scheduling and transient applier-failure
policy are accepted. Remaining numerical budgets and format details are still open.
The latest evidence compares small persisted units under a sustained uniform peak.
Keep capacity while arrivals continue separate from catch-up after they stop,
and retained worker workspace separate from allocation traffic and total RAM.

General document encoding, inserts/deletes, concurrent loads, retention consumers,
production storage, sustained cleanup and actual crash interruption still need
validation. Preserve the accepted durability and hard-limit admission guarantees.
Detailed failure classification, numerical retry/batch limits and recovery/resource budgets remain open
within Q09.2; this acceptance does not close that entire decision item.
