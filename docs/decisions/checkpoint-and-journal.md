# Persistence recovery and retained history

**Milestone 1 implementation baseline:** [concrete mechanisms and initial settings](../design/milestone-1-engine-contract.md)
now resolve the engineering selections described as open below. Product contracts
remain in force; measured production validation is still required.

Accepted Moltaro.Lattice decisions, rationale and alternatives. The storage
strategy, background application with coalescing, a 256-byte baseline saved unit,
short bounded batches with immediate catch-up, and automatic trigger/hard-limit
policy are accepted. Transient applier failures permit continued healthy journal
appends within budget and paced retries. A committed publication failure stops the
affected Lattice instance and requires recovery through close/reopen without mandatory
host-process termination. Operation tokens and bounded durable result retention are
accepted under Q13. Numerical budgets, detailed failure/expiry protocols and
retained-consumer coordination remain open.

[Decision index](README.md) · [Question register](../design/questions.md) · [Remaining discussion](../design/questions/checkpoint-and-journal.md)

<a id="q09.2-postgresql-base-and-compact-journal"></a>

## Q09.2: PostgreSQL base and compact journal

**Partially accepted on 12 November 2026:** a compact application
journal in PostgreSQL and a recoverable base, maintained in v1 by **background
journal application with coalescing**. This latest choice replaces the earlier
open choice of how to construct periodic bases. Ordinary maintenance does not
require freezing or copying the complete live arena.

PostgreSQL remains the first Durable backend. Q09.1 requires reliable storage
acknowledgment and coherent native publication before write success. MemoryOnly
retains its accepted behavior. No production provider is implemented by this
record. The 256-byte saved-unit baseline is now accepted; other prototype SQL
layout details and numerical settings are not accepted defaults.

The base is the complete saved state at its applied-through position A. It can
consist of many addressable binary units and metadata. It is advanced incrementally,
not rewritten in full after every maintenance trigger. On recovery, load this
state and replay committed changes after A, then prepare indexes before Ready.
This is internal recovery state, not permanent versions or a public snapshot API.

### Explicit WriteBatch recovery boundaries

The accepted [Q01 WriteBatch](document-model.md#atomic-writebatch-within-one-database)
is a logical atomic unit. Journal commit/replay, coalesced saved-state publication,
applied positions, retention and state capture must preserve its complete boundary
or use an explicitly proven equivalent recovery protocol. Coalescing cannot erase
the information needed to recover a complete committed batch before serving data.

This adds boundary-aware recovery/application requirements, not permanent versions
or a full-base copy on every write. Concrete framing, applier transaction limits and
capture rules remain E06 work. Committed publication-failure recovery is accepted below.

### Instance recovery after committed publication failure

**Accepted on 13 November 2026:** if a committed mutation or WriteBatch cannot be
coherently published, stop serving the affected Lattice instance. Refuse new local
reads and writes, prevent partial state from escaping and recover through controlled
close/reopen. This does not require terminating the embedding application process,
other Lattice instances or PostgreSQL.

Enter the recovery-required state before publication protection can admit access to
partial data. Every access path, including operations already waiting for gates,
must respect that state. Do not acknowledge successful publication or report a
definite rollback for a mutation whose durable commit is already confirmed.

Quiesce instance-owned work and account for in-flight storage transactions before
recovery becomes readable. Drain protected users before releasing native segments;
caller-owned independent copies remain valid. Release the old arena safely, load
the durable base and committed tail, rebuild indexes and become Ready only after
coherent recovery. No second full arena or unbounded automatic recovery loop is selected.
The host may reopen the engine or use its normal service-restart lifecycle.

Ordinary same-store Durable reopen preserves history. Initialized children keep their
accepted last-coherent-state/disconnected-read behavior and later resume or reload
according to history availability. MemoryOnly gains no PostgreSQL recovery guarantee;
its existing empty-master/full-child-reload restart contracts remain unchanged.

Temporary PostgreSQL unavailability,
ordinary pre-commit validation/memory refusal and transient background-applier failures
retain their separate accepted policies. Exact failure states, safe drain/reopen APIs,
budgets and injected-failure verification remain engineering work.
[Rationale and alternatives](../design/questions/publication-failure.md).

The separately accepted [operation-result contract](mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1)
requires compact receipts in the mutation commit and bounded same-token resubmission.
Receipt retention can outlive the mutation journal; cleanup must preserve the promised
window and reject retired tokens. Exact expiry/admission mechanics remain engineering work.

## What PostgreSQL already owns

| Responsibility | Owner and boundary |
| --- | --- |
| Durable SQL transactions and storage crash recovery | PostgreSQL WAL/recovery; Lattice waits for the accepted durable commit |
| Physical database checkpoints | PostgreSQL; Lattice does not implement another mechanism merely to duplicate SQL durability |
| Binary format, application replay and saved-unit updates | Lattice's PostgreSQL provider, preserving engine schema/ownership/string invariants |
| Restoring the useful in-memory state | Lattice loads saved state and its required tail, then prepares indexes before Ready |
| Ordered delivery to children | Lattice distribution; its retention needs are distinct from provider recovery |

PostgreSQL [WAL](https://www.postgresql.org/docs/current/wal-intro.html) restores
committed SQL rows. It does not interpret Lattice's custom changes. Lattice needs
application replay because its chosen SQL representation deliberately permits the
saved base to lag behind committed journal entries. This design adds no separate
local WAL file and imposes no format on MemoryOnly or a future file provider.

## A. Recoverable base plus a bounded change journal — accepted

<a id="background-application-with-coalescing-accepted-for-v1"></a>

### Background application with coalescing — accepted for v1

1. The ordered foreground writer durably appends encoded changes to PostgreSQL,
   coherently publishes them in the native arena, and acknowledges success.
   It does not wait for their later application to saved units. No root gate spans I/O.
2. One background worker reads the next committed journal prefix using bounded
   command and changed-unit workspace. It starts from the saved state at A,
   applies operations in their original order, and writes each touched unit once
   per application batch. Foreground journal appends continue.
3. Updated units, required format/schema/dictionary metadata and the new
   applied-through position commit atomically in **one PostgreSQL transaction**.
   The position advances only past a fully applied committed prefix.
4. On failure before commit, the old saved state and position remain authoritative.
   On successful commit, both describe the new boundary. Reopening resumes from
   that durable position; a failed attempt cannot grant cleanup permission.
5. Covered journal ranges become eligible for logical removal only when no required
   reader or retained consumer still needs them. Application, logical removal and
   physical SQL space reclamation are separate operations.

A single prefix position is sufficient; updating an Applied flag on each journal
row is unnecessary. Sequence values can contain rejected/aborted gaps, so operation
count and byte lag must not be inferred by subtracting sequence numbers.

Coalescing must preserve the result of **all** operations. Repeated assignments to
one field can collapse to their last value. Collection edits, creation/deletion and
changes to dictionaries or schemas require equivalent ordered reconstruction;
keeping only the last arbitrary operation is incorrect. Dedicated arithmetic commands
are deferred beyond v1 by [Q13](mutation-commands.md#numeric-assignments-without-arithmetic-commands-accepted-for-v1).
If introduced later, their ordered effects would also need preservation. The current
prototype tests fixed-size field assignments, not those general cases.

The worker operates on persisted state and the committed journal, so it requires
neither a full live-arena freeze nor a second full dataset in RAM. Bounded buffers,
lookup structures and temporary driver/SQL work still consume memory. Persistent
units use the accepted 256-byte baseline below, independently of native allocation
segments. [Q13](mutation-commands.md#q13-how-can-a-document-be-updated) now accepts
document/scalar updates and collection replacement/element commands. Binary command
encoding and remaining mutation semantics remain unresolved. Only successfully
committed mutations reach background replay. Invalid submitted commands must fail
atomically in the foreground; coalescing preserves the ordered result of committed
changes and cannot substitute for that validation.

### Background application cadence — short bounded batches accepted for v1

**Accepted on 12 November 2026:** option A, short bounded application
batches with continuation without an intentional wait while catching up. Durable
acknowledgment, native read visibility and the 256-byte unit baseline keep their
accepted contracts. Numerical interval, batch and workspace defaults remain open.

#### Option A: short bounded windows with immediate catch-up — accepted

When caught up or receiving a small trickle, let changes accumulate for a short
configured interval and apply a bounded prefix. When a batch reaches its work or
workspace limit, continue with the next prefix without an intentional accumulation
wait. An overdue cycle does not add another full interval after its work finishes.
When idle, wait without rewriting saved state. All work and temporary memory remain
bounded; waiting does not load the entire unapplied journal into RAM.

This is fixed scheduling with explicit budgets, not an adaptive controller based
on estimated future traffic. The tested 250 ms interval, 128 journal batches and
32 MiB reusable payload cap remain evidence inputs, not accepted defaults.
This choice does not replace the accepted size/age maintenance triggers or hard
retention limit. Integration of those thresholds with scheduling remains E06 work.

For example, if one field changes repeatedly within a window, the journal still
records every acknowledged operation and native reads see normal publication.
The worker can reconstruct the final equivalent state and replace its affected
saved unit once. The data/position transaction still covers an ordered prefix.
Uniform changes may share no units; batching can still reduce application transaction
frequency without promising less data traffic or faster execution for every workload.

Advantages: fewer background SQL commits/round trips, more opportunity to combine
repeated changes, and a controlled workspace. This is intended to reduce interference
with foreground durable writes and resident reads. Actual latency remains dependent
on batch size and resource contention; this is design reasoning, not a new benchmark.

Costs: saved state normally trails the journal by more than with immediate small
batches; recovery may replay more changes. A larger bounded workspace is usually
needed. Large batches can cause longer SQL/CPU bursts, so bounded work matters as
well as the interval. A short interval is not a maximum-lag guarantee during overload.
The policy adds a timer and batch-full/idle continuation rules to the existing worker.

#### Option B: apply each available foreground journal batch immediately — not selected

Start application as soon as committed work is available, without a deliberate
coalescing interval, using one foreground journal batch per application transaction.
That batch may already contain multiple changes; B does not mean one SQL commit
per scalar update. Use the same atomic data/position commit and retention guarantees.

Advantages: less intentionally accumulated replay work, smaller per-transaction
workspace and simpler scheduling. This can suit very low write traffic or tighter
recovery-tail requirements when storage has sufficient capacity.

Costs: more frequent background SQL transactions and repeated replacement of a unit
when it changes across adjacent batches. This can increase CPU/I/O work and compete
with foreground writes and resident reads. Immediate scheduling does not ensure the
worker keeps up; if it is slower than arrivals, its durable tail can still grow.
Neither option adds a direct foreground wait for saved-unit application.

| Aspect | A: bounded windows | B: immediate foreground batches |
| --- | --- | --- |
| Foreground writes | Same durable journal wait; typically fewer competing application commits, but larger work bursts | Same durable journal wait; potentially more competing application commits |
| Resident reads | Same native path; interference depends on background CPU/allocations | Same native path; frequent small application work can interfere |
| RAM | More reusable unit/command buffers within an explicit bound | Usually smaller per-batch workspace; backlog still stays on disk |
| Saved-state writes | More opportunities to combine repeated changes; uniform traffic may not coalesce | Fewer opportunities across batches; fresher base if the worker keeps up |
| Recovery | Some intentional tail plus actual processing lag | Less intentional tail; processing lag can still dominate |
| Complexity | Timer, bounded batch and immediate continuation rules | Simpler cadence; the same consistency and failure machinery remains necessary |

The decision selects A because the journal already supplies durability, reads use
the live arena, and small temporary application lag during peaks is acceptable.
Acceptance selects the scheduling policy; it does not accept the prototype's numbers
or guarantee that background capacity exceeds every offered peak. Failure recovery
and retry delays are separate from ordinary catch-up scheduling.

<a id="transient-background-application-failure-continue-within-budget-accepted-for-v1"></a>

### Transient background application failure — continue within budget accepted for v1

**Accepted on 13 November 2026:** option A. For a temporary failure
of background saved-state application, continue durable journal writes up to the
accepted retention budget and retry application with pauses. This preserves
[Q09.1](write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed) and the
[hard retained-journal limit](#what-happens-at-the-hard-limit).

The applier runs **inside the master process**, as part of the owning engine in
both embedded and standalone hosting. There is no master-to-applier socket, separate
worker service or extra pod. Separate connections used for journal append and saved
state application are connections from that same process to PostgreSQL. The exact
thread/task scheduling and connection pooling remain implementation details.
The applier consumes committed mutations; it is not a second application writer.

The relevant failure is a transient error in the applier's SQL transaction or its
PostgreSQL connection while saved state, journal and native publication remain
trustworthy. It is distinct from losing PostgreSQL entirely, corrupt recovery input,
an uncertain foreground commit or a master-process crash. Because the applier shares
the master's process lifetime, a process crash stops both; restart uses the durable
base/position and journal. Continuing append is possible only while that path works.

#### Option A: continue durable writes within the journal budget — accepted

For a recognized transient applier failure, keep accepting writes only while the
foreground can durably append them and coherently publish them. Retain all required
journal entries and keep the last committed applied position authoritative. The
unapplied tail grows on disk; do not mirror it in an unbounded RAM queue. Expose the
applier's failure/lag through the eventual health/maintenance surface so it is not
mistaken for normal progress; this contract does not implement a control UI.

Retry background application with a bounded frequency and bounded workspace.
Retries may wait between attempts; the accepted immediate catch-up policy applies
to useful progress, not a loop continuously repeating the same error. After recovery,
resume from the durable applied position and catch up using the accepted batches.
Exact retry classification, delays and operational escalation remain engineering
and numerical follow-up; no backoff formula or retry-count default is selected here.

If a background commit outcome is uncertain, resolve the old attempt before another
application attempt may change the same stream. Then reread the committed position;
never assume rollback or blindly repeat already-applied operations. Preserve the
single-applier invariant even across connection recovery. This is separate from
retrying a caller's mutation, whose ambiguity is already covered by Q09.1.

Keep the accepted hard-limit behavior: explicitly refuse an append that would exceed
the logically retained-journal budget. Successful application creates cleanup
eligibility only when required consumers release the covered history. Failure cannot
reset accounting or permit deleting needed replay input. Numerical limits remain
unconfirmed. Automatic admission recovery must honor all remaining fault/budget
conditions; freeing bytes alone does not resolve a damaged recovery stream.

Advantages: a brief maintenance failure need not interrupt useful application writes;
resident reads retain their local path. Successful writes still have durable journal
records. Ordinary reads do not wait for saved-state updates merely because the
applier has failed. The RAM workspace stays bounded.

Costs: more retained disk journal and potentially longer restart replay; retries and
later catch-up can compete with foreground writes and reads. Availability lasts only
while the foreground is healthy and budget remains. Implementation must distinguish
transient failure, uncertain outcomes and integrity failure, and report degraded
maintenance rather than silently stop doing it.

#### Option B: suspend new write admission on any applier failure — not selected

Stop admitting new mutations when background application fails, even if journal
appends are still possible. Preserve and serve valid committed native data under the
existing read contract. Resume admission only after the failure is resolved and the
worker can safely continue; the exact resume threshold would need a follow-up.
Already admitted or in-flight foreground operations still require correct completion
or failure reporting under Q09.1; do not relabel committed writes as rolled back.

Advantages: limits additional accepted tail growth during the outage and gives a
simpler admission rule. It is easier to keep new application traffic from extending
recovery work while maintenance is broken.

Costs: a temporary maintenance-only failure immediately becomes an application write
outage. This does not materially improve the normal read path, ordinary write speed
or RAM efficiency; A already keeps its backlog on disk with bounded workspace.
Atomic recovery and uncertain-commit handling are still required under B.

#### Boundaries of this choice

Both options preserve durable acknowledgment. If the foreground cannot confirm a
durable journal commit, it cannot report successful mutation; Q09.1's indeterminate
outcome rules still apply. [Q14](write-admission.md#q14-write-admission-and-resource-pressure)
accepts bounded recovery waiting and explicit failure after expiration; numerical
budgets remain open. A corrupt record, unsupported replay format,
inconsistent position or failure to publish coherent native state is not treated as
a recoverable background timeout. Do not use this policy to continue mutations when
the recovery stream or native publication cannot be trusted; require diagnosis and
recovery, never skip journal entries. Reads may continue only where their committed
state remains trustworthy.
Detailed integrity-fault/read-availability handling still belongs to Q15/E03/E06.

| Aspect | A: continue within budget | B: suspend new admission |
| --- | --- | --- |
| Writes during a transient applier failure | Continue if durable append/publication remain healthy and budget permits | New mutations are refused even while append could work |
| Ordinary reads | Same local path; retry/catch-up activity can contend for resources | Same local path while committed state remains valid |
| RAM | Bounded workspace/admission state; durable tail remains on disk | Similar bounded state; no major saving from stopping admission alone |
| Disk and restart | Tail can grow to the retained-history budget, adding replay work | Less new tail growth after the failure is detected |
| Complexity | Conditional continued admission, paced recovery and visible failure state | Simpler admission rule, but the same atomicity/uncertainty handling |

A is accepted for transient background failures because the journal already protects
successful writes. The failure/admission policy is accepted, not a measured failure
recovery guarantee. The existing prototype stops its experiment on a worker failure;
rollback/reopen checks do not implement or validate continued admission and automatic
retries. Production failure handling and numerical retry limits remain unimplemented.

### PostgreSQL saved-unit baseline — 256 bytes accepted for v1

**Accepted on 12 November 2026:** use **256 bytes** as the baseline
payload size of a saved data unit in the PostgreSQL provider for v1. Background
application uses this granularity when reconstructing and replacing affected units.
Several units can be processed in one SQL batch and committed together with their
applied position under the already accepted transaction rules.

This is provider data granularity, not a document-size limit, journal-command size,
SQL transaction size, native allocation segment or PostgreSQL physical page size.
A larger document can span multiple units. Encoding, packing, addressing, headers,
final-unit padding and mapping onto SQL rows remain E06/Q16 format work. This decision
does not add adaptive unit sizing or per-field/table size configuration to v1.
It leaves document atomicity, string sharing and native-memory layout contracts intact.

Q16 accepts an [independent binary base](physical-storage.md#independent-persisted-base-and-arena-reconstruction)
with provider-local placement and arena reconstruction on load. Pure native
compaction does not relocate the saved units. Their precise encoding remains E06 work.

The [sustained comparison](../design/prototype-evidence.md#sustained-comparison-of-compact-saved-units)
provides the rationale: 256 B accepted all 1.8 million changes during a three-minute
10,000/s peak, with a small replay tail, 7.92 ms overall write p99 and 3.86 MiB of
retained worker workspace. The 1 KiB and 4 KiB variants showed more foreground
interference on that setup. These remain local synthetic observations, not SLOs.

Smaller units reduce the payload replaced for a scattered tiny edit and limit
per-unit scratch space. For a fixed dataset they also increase the number of unit
identities and potential SQL rows/index entries relative to larger units. Loading
must batch/stream units rather than issue one request per 256 bytes. Resident native
reads do not acquire a per-read SQL operation from this decision. Whole-document
updates, variable-size data, startup cost and metadata overhead still need full-engine
measurements. The provider baseline is settled; those implementation details do not
reopen the accepted size without an explicit later revision.

### Recovery loading and retention

Recovery uses a consistent saved state at A plus its required later committed
changes. During ordinary master recovery, keep application service unavailable and
leave the saved-base applier and journal cleanup stopped. Load base data, compatible
metadata and A, replay the complete required committed tail, construct indexes and
only then enable normal service and maintenance. This path does not need a
concurrent PostgreSQL base snapshot. No native access gate is held over storage I/O.

Child initialization is owned by [replication](replication-and-hosting.md#q08-can-a-child-load-everything-before-accepting-reads).
Children receive initial state and confirmed logical changes only from the master;
they run in MemoryOnly and never connect to PostgreSQL. The master-to-child state
transfer and readiness handoff are separate from restoring the master's storage.

Measure the **unapplied tail** separately from **applied but retained journal**.
Only changes after A need replay on recovery; a large already-applied journal does
not automatically mean a large replay. Keeping required history on disk does not
require a RAM copy. Applying a tail to SQL before startup is not automatically faster
than replaying it into RAM: it still processes that tail and adds storage writes.

Initial replica transfer and backup capture remain separate contracts. Replication
uses [pending delivery in master RAM](replication-and-hosting.md#pending-delivery-in-memory),
not reads from this journal. Child acknowledgments do not retain SQL journal ranges;
RAM delivery reclamation does not remove required durable recovery input. Storage
consumer limits and reload behavior must agree with Q08.1.3/Q09.4/Q14.

### Costs and evidence

Foreground writes retain their compact journal path. Background application adds
SQL reads, updates and WAL traffic and can contend with foreground commits and native
reads. Coalescing helps when changes reuse records/units within a batch; it cannot
make widely distributed changes free. Larger replaced units can amplify tiny edits.
If application persistently falls behind, recovery work grows despite fast appends.

The [first background measurements](../design/prototype-evidence.md#background-journal-application-with-coalescing)
verified bounded workspace and base-plus-tail recovery. Hot-key updates with 4 KiB
units kept a small tail at 10,000 offered changes/s; uniform updates and 64 KiB units
fell behind. The accepted mechanism is therefore not a universal throughput promise.
The [compact-unit follow-up](../design/prototype-evidence.md#sustained-comparison-of-compact-saved-units) compares 256 B, 1 KiB and 4 KiB with uniform
input for three minutes per variant and a journal-only control. 256 B accepted all
arrivals with a small tail; 1 KiB caught up after a foreground stall, while 4 KiB
accumulated lag. The 10,000/s rate represents peak demand, not a required normal
rate on every machine; temporary durable lag is evaluated against peak duration,
post-peak catch-up capacity and retention/recovery budgets. The subsequent decision selects 256 bytes as the PostgreSQL v1 baseline; this does not claim it is optimal
for every workload.

## B. Persist updated current-state units directly — not selected for v1

This alternative updates the saved units in the **foreground commit** before
acknowledgment, avoiding a separate application recovery tail. PostgreSQL still owns
SQL durability. It can be attractive for small units: the earlier direct 256-byte
comparison sustained the offered rate. Larger unit replacement was much slower.

The distinction from accepted A is when saved-state updates happen and what success
waits for. A also uses SQL UPDATE in its background worker. Choosing A does not
prohibit UPDATE, batching or a normalized provider metadata table.

Direct persistence reduces replay responsibility but still needs coherent metadata,
consistent initial loads and separate child-delivery history where required. Neither
approach automatically supplies a public snapshot API or constant disk use.

## Comparison

| Aspect | A: journal plus background application | B: directly persisted current state |
| --- | --- | --- |
| Ordinary writes | Await journal commit and native publication | Await affected saved-unit updates and native publication |
| Ordinary reads | Local arena; background SQL/CPU can interfere | Local arena; foreground persistence can interfere |
| Recovery | Load saved state, replay required tail, prepare indexes | Load current saved state and prepare indexes |
| RAM | Bounded command/unit workspace; no obligatory second arena | Bounded update/serialization buffers |
| Disk | Saved units, retained journal, SQL update versions and WAL | Current units, SQL update versions and WAL; optional separate delivery history |
| Complexity | Ordered application, atomic data/position commit, replay and retention | Coherent current-state updates; layout-dependent write amplification |

Paused full-image capture and copy-before-write were earlier alternatives, described
in the [discussion history](../design/questions/checkpoint-and-journal.md). Their measured
seconds-long pauses are not pauses imposed by the selected incremental method.
The earlier eight-second full-base rewrite interval was only an experiment setting.

## Two journals and two space measurements

The Lattice journal is ordinary durable PostgreSQL table data. PostgreSQL's own WAL
serves physical durability/recovery and is a different log. Advancing A, deleting
covered application rows and returning physical disk space are different events.

[Ordinary VACUUM](https://www.postgresql.org/docs/current/routine-vacuuming.html#VACUUM-FOR-SPACE-RECOVERY)
primarily makes obsolete SQL row space reusable; deleting rows need not immediately
shrink files. Test sustained cleanup, SQL versions, retained readers and WAL separately.
RAM efficiency takes priority over tightly packed disk files; the requirements permit
periodic offline disk packing. This does not relax live-arena bounds or recovery
budgets. The SQL table layout and cleanup implementation remain engineering work.

## Compaction triggers and journal budget

**Accepted policy:** automatic maintenance by uncompacted size **OR** oldest pending
age, plus a separate hard logically retained-journal budget with explicit write
refusal. Background application may run continuously before either threshold;
thresholds request/expedite catch-up and safe reclamation, not a full-image rewrite.
Only one application worker advances this recovery stream. An empty tail needs no
application work. Short bounded batches and immediate continuation while catching
up are accepted; numerical batch/workspace limits and the interval remain unconfirmed.

### A. Automatic size/age triggers plus a hard limit — accepted for v1

| Setting | Trial value, not accepted | Accepted policy meaning |
| --- | --- | --- |
| Unapplied encoded journal size trigger | 256 MiB | Request maintenance when the required replay tail reaches this size |
| Oldest unapplied change age trigger | 30 minutes | Request maintenance when a committed change remains unapplied this long |
| Maximum retained application journal | 1 GiB | Refuse an append before its encoded bytes exceed the logical retention budget |

These are configurable trial inputs, not defaults, capacity promises or recovery
SLOs. Scope is one Durable instance's application journal, not an allowance multiplied
by every table. Age means the oldest pending committed change, not process uptime;
restart must not reset it. No adaptive replay-time estimator is selected for v1.

### Why both size and age?

Size responds to bursts; age prevents small tails from remaining indefinitely in
rarely updated databases. Incremental application writes affected units, not the
entire base for a tiny edit. Neither threshold alone guarantees recovery time for
arbitrary commands, indexes or storage. With the prototype's 32-byte commands,
10,000/s produces about 0.305 MiB/s of payload: approximately 14 minutes to 256 MiB
or 56 minutes to 1 GiB without logical removal. These are arithmetic examples,
excluding framing; real commands vary. Master replay and child full-load time are
different budgets.

### What is counted?

The soft size trigger counts encoded changes after A. The hard limit counts all
logically retained application journal bytes, including applied ranges still needed
by a consumer. Count command/batch framing before PostgreSQL compression and reserve
in-flight/admitted bytes before append. Logical removal releases this budget even
if SQL files have not shrunk. Saved data, SQL indexes/versions, backups and PostgreSQL
WAL need separate disk headroom. No server settings are changed by this contract.

### What happens at the hard limit?

Refuse a new write explicitly before append/publication if it would exceed the budget.
Previously committed data remains readable. Never discard required input or create
an unbounded RAM queue. Successful application alone does not grant capacity while
required consumers still retain the covered journal. Failed application leaves the
last committed data/position and recovery input intact. Resume admission after safe
logical reclamation or a deliberate budget change, once other fault conditions
permit. Paced retry after transient applier failure is accepted. The separate
[RAM delivery buffer](replication-and-hosting.md#pending-delivery-in-memory) follows
its own bounded retention and full-reload contract; a slow child does not pin this
SQL journal. Required recovery input and other valid storage retention claims remain
protected. Numerical retry/queue limits,
remaining consumer coordination and final API errors stay open.

### Effects on speed, memory and complexity

| Aspect | Effect |
| --- | --- |
| Foreground writes | Same durable append/publication; accounting adds work, background SQL can increase latency |
| Native reads | No per-read SQL; CPU, allocation and storage activity can still interfere |
| RAM | Bounded application workspace and admission state; on-disk tail need not be mirrored |
| Recovery | Keeping A near the committed end reduces replay; no time guarantee follows from bytes alone |
| Disk/I/O | Incremental saved-unit writes plus journal cleanup and SQL maintenance |
| Complexity | Explicit application, retention accounting, admission and failure boundaries |

### B. Scheduled/manual-only baseline — not selected for v1

Maintenance only in operator-selected windows is simpler to schedule but leaves a
longer recovery tail and can exhaust the same finite journal budget between windows.
It does not guarantee progress under continuous input. Future manual commands under
Q18 can coexist with automatic maintenance; unlimited retention is not selected.

## Remaining Q09.2 subtopics

The method, 256-byte baseline, bounded-batch scheduling and transient applier-failure
policy are accepted. [Q09.2 remaining work](../design/questions/checkpoint-and-journal.md#next-discussion-and-remaining-persistence-work)
includes numerical retry and memory/time budgets, detailed failure classification,
consistent loading/retained consumers and recovery limits. The separate
[Q14 decision](write-admission.md#q14-write-admission-and-resource-pressure)
accepts bounded pending-write recovery waiting; it does not reopen the accepted
hard-limit refusal or durable-success contract. See [question register](../design/questions.md#current-discussion).
The trial 256 MiB / 30 minute / 1 GiB values remain unapproved. These subtopics keep
Q09.2 partially accepted and leave the decision-register totals unchanged.

Prototype correctness and throughput do not establish production readiness. General
document encoding, insert/delete churn, schema/dictionary changes, simultaneous
loads, actual crash interruption and sustained reclamation still require validation.

The [14 November saved-unit extension](../../src/Prototypes/WriteBatch/UNITS.md) provides
bounded integration evidence for multi-type full Upsert/Delete, grow/shrink/recreation,
simultaneous recovery snapshots, atomic base/cursor updates, cleanup, retained
receipts and nine application-process crash boundaries. General formats,
schema/dictionary metadata, long-running reclamation and server/network failures
remain unverified. The fixture SQL layout and numeric budgets are not selected defaults.
