# Allocation and compaction

**Milestone 1 implementation baseline:** [concrete mechanisms and initial settings](../design/milestone-1-engine-contract.md)
now resolve the engineering selections described as open below. Product contracts
remain in force; measured production validation is still required.
The [reader protection and compaction section](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction)
specifies the current sparse-region and nearly empty segment rules.

Accepted Moltaro.Lattice decisions, costs and alternatives. This record owns Q17's
accepted subtopics; unresolved maintenance policies and numerical budgets remain open.

[Decision index](README.md) · [Question register](../design/questions.md) · [Storage requirements](../design/storage-and-maintenance.md)

## Q17: Allocation and fragmentation lifecycle

**Partially accepted:** size-class reuse for small/medium allocations, a separate
large-allocation path, and automatic bounded incremental compaction are settled.
Region counters, useful-reclaimable-capacity triggers and background scheduling
coordinated with the local writer/applier are accepted. Q16 now selects bounded
chunks for large payloads. A bounded empty-segment reserve with background release
of excess eligible segments is also accepted. Substantial persistent unused collection
capacity is automatically reduced through bounded maintenance. Exact sizes, thresholds,
reserve/work/headroom budgets, collection shrink algorithms and remaining pressure
behavior stay open. No allocator or production compactor is implemented by this record.

### Size classes and reusable slots

**Accepted on 13 November 2026:** option A for v1. Common small/medium
record and payload allocations use a bounded set of capacity classes and reuse safe
free slots within native segments. Large allocations use a separate path whose
threshold remains to be measured; the later Q16 decision selects bounded chunks. Prefer compatible reusable space
before acquiring more capacity within the engine's memory budget.

A block's payload length differs from its capacity. Reuse is allowed only after
readers and other retainers can no longer access retired data. Existing root gates,
relative addresses and owner/index updates on relocation remain in force. Rounding
is per allocated block, not per scalar field or numeric-array element. No concrete
class sequence, power-of-two rule or segment size is accepted by this allocation
subtopic. The later collection-shrink decision below selects its maintenance policy.

The later [compaction acceptance](#automatic-incremental-compaction) is recorded below.
Q17 remains open in the register; this acceptance changes neither the total decision
count nor the number fully accepted.

#### Allocation boundary

The accepted layout keeps fixed fields together and variable payloads separately.
Accepted addressing uses segment/offset locations; movement must update owning
descriptors and relevant index locators safely. This decision selects how freed
space becomes available for common record, string and collection allocations.

An allocation is a record region or payload block, not each scalar field or each
element of a numeric array. Narrow numeric types remain packed according to their
eventual record/collection layout. Allocation rounding applies to a block as a
whole, not to every stored byte value.

#### A. Size classes and reusable slots — accepted for v1

Round a small/medium allocation request to a suitable supported capacity. An
allocation region contains slots of one class; a free-slot list or bitmap tracks
availability. Multiple regions can occupy a larger native segment, so supporting
several classes need not reserve a whole native segment per class. Prefer suitable
reusable slots before acquiring more capacity within the engine's memory budget.

The visible payload length remains separate from its allocated capacity. A change
that fits can reuse the allocation under the accepted publication/lifetime rules.
Growth beyond capacity needs a larger block, copying the affected payload and
publishing its new descriptor before retiring the old block. Retirement does not
make bytes reusable until all protected users and other retainers are finished.

For illustration only: a 100-byte payload in a 128-byte capacity leaves 28 bytes of
unused capacity. One million such payloads would have 28,000,000 bytes, about
26.7 MiB, of this slack alone. Headers, alignment, indexes and free/retired capacity
are additional. Neither 128 bytes nor a powers-of-two class sequence is selected.

- **Reads:** the reader still follows the accepted relative address. Allocation
  does not add a per-field lookup, but slack and region placement can affect cache
  locality and total working-set size.
- **Writes:** taking/returning a known-class slot is a simple common path. A fixed
  field update that needs no allocation does not benefit directly. Growth/copying,
  index maintenance and journal acknowledgment retain their own costs.
- **Memory:** rounding adds internal slack. Free slots of one class may not satisfy
  another class, and a few live slots can keep a region occupied. Empty regions can
  be reused/reassigned or released under a policy still to be selected; partially
  occupied regions may eventually need compaction.
- **Complexity:** relatively simple small-block reuse; still requires region
  metadata, safe reclamation, large-allocation handling and bounded retained
  capacity. This is not a claim of fragmentation-free or constant-memory operation.

Large payloads should use a separate allocation path rather than an ever-growing
catalog of small-slot classes. The later [Q16 decision](physical-storage.md#large-payloads-in-bounded-chunks)
selects bounded chunks for large strings/collections. Thresholds and chunk sizes
remain Q16/E01 work. This decision does not force one huge list
into a single block. The later shrink acceptance below selects automatic reclamation
of substantial unused capacity; exact growth/shrink algorithms remain open.

#### B. Variable-size free regions with splitting and merging — not selected for small/medium blocks

Track free ranges, choose a suitable range for the requested size after alignment,
split it where worthwhile, and merge adjacent freed ranges. Headers and minimum
useful remainder sizes still exist; this is not allocation with zero rounding.

- **Reads:** the same relative address can reach payload bytes. A denser working
  set may help, but free-range management does not automatically accelerate reads.
- **Writes:** allocation/freeing may search or index ranges, split them, maintain
  adjacency metadata and merge neighbors. Efficient algorithms exist; B must not
  be modeled as a deliberately slow full scan. In-place growth is possible when a
  suitable adjacent range is free, but it is not guaranteed.
- **Memory:** can reduce class-rounding slack and use mixed-size holes flexibly.
  Metadata, unusable small remainders and externally fragmented free space remain.
  A large request may fail despite sufficient total free bytes when those bytes
  are scattered among live allocations.
- **Complexity:** more free-range and adjacency bookkeeping for the common mixed
  allocation path. Coalescing free neighbors is not compaction: it does not move
  the live data separating non-adjacent holes.

#### Rationale and limits

The decision selects A as the v1 baseline for common record/payload blocks, because
a small set of capacity classes makes frequent reuse straightforward. Its memory tradeoff
must be checked against actual payload-size distributions; do not claim that A
always uses less RAM or is universally faster than B. Neither policy changes the
accepted 256-byte PostgreSQL saved unit or determines a network transfer unit.

The general size-class/slab pattern and its rounding tradeoff are described in the
[jemalloc manual](https://jemalloc.net/jemalloc.3.html).
That is design background, not a selection of jemalloc as a runtime dependency or
evidence for Lattice's throughput, class sizes or fragmentation bounds.

This settled the allocation subtopic of Q17. The later compaction acceptance below
settles its operating mode and trigger direction. The release decision below accepts
a bounded empty-segment reserve and background release of excess. Numerical work and
reserve budgets, exact capacities and broader pressure behavior remain open. Reusable
arena capacity and a decrease in OS resident memory are different measurements. A second full arena is not assumed for reclamation.

#### What to measure before choosing numbers

The existing [segment prototype](../../src/Prototypes/SegmentStorageComparison/README.md)
allocates and replaces whole segments without concurrent readers. It does not
implement either allocator, safe block reuse, classes or hole coalescing. There is
no new benchmark result in this decision.

Use bounded live-data workloads with record blocks, strings and collection payloads
of varying sizes; include sizes around class boundaries and changing size mixes.
Exercise growth, shrink, deletion/reinsertion, hot/cold records and delayed readers.
Any comparisons must use equivalent data and protection. Measure allocation/free
and full mutation cost separately from ordinary reads, with journal/index work
identified explicitly.

Report useful payload, headers/metadata, class/alignment slack, reusable free bytes,
retired bytes, committed segment capacity and peak relocation workspace separately.
Observe whether capacity stabilizes after warm-up or reaches the declared pressure
condition; an allocation-only speed test cannot establish bounded long-lived growth.
Choose class spacing, large-block threshold, region/segment sizes and retention
budgets from these measurements. No numerical value is accepted by this decision.
The later [compaction acceptance](#automatic-incremental-compaction) is recorded below.

### Automatic incremental compaction

**Accepted on 13 November 2026:** option A, including the region-accounting
and trigger clarification. Each node owns background maintenance that performs bounded
work coordinated with its local serialized writer or replica applier. Track region
capacity/class and live, reusable, retained-retired and reserved slots. Start from
expected useful whole-region reclamation, compatible destinations and available
workspace; use separate start/stop conditions. A global hole counter alone is insufficient.

Move owned blocks under the accepted root-document protection, repair all relevant
physical locators and preserve enumeration, logical contents and revisions. Shared
strings/indexes keep their own lifetime/publication protocols. There is no second full
arena, independent authoritative writer or database-wide pause mode selected for v1.
The engine uses known evacuation ideas; CLR GC does not compact its native records.

Numerical thresholds, polling cadence, candidate representation, fairness, reserve
sizes/release timing, chunk/directory implementation and exact time/memory budgets remain open. Acceptance
is not a claim that these mechanisms are implemented or their pause bounds measured.
See [question register](../design/questions.md#current-discussion).

#### What compaction solves

Reusing a free slot does not necessarily empty its allocation region. Several
sparse regions can retain much more native capacity than their remaining live data
needs. Moving surviving blocks into compatible destination regions can empty whole
regions. Empty regions may be reassigned; a whole native segment can be released
only when nothing live or retained remains in it and its metadata is safe to retire.

This question concerns the working arena in RAM. PostgreSQL journal application,
SQL space reclamation and backups retain their separate contracts. Physical packing
does not compress bytes, remove class-rounding slack automatically or justify freeing
memory still referenced by a reader, transfer, checkpoint or other owner.

#### What can be reused from .NET and known algorithms?

The CLR collector owns managed objects. Our native arena and its encoded records
are engine-owned allocations; the runtime cannot discover their ownership, indexes
or safe relocation points. This follows from the documented boundary between
[managed collection and unmanaged resources](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals#unmanaged-resources).
[NativeMemory.Alloc](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory.alloc?view=net-10.0)
is an allocation primitive backed by malloc, not a database-block compactor.

[LargeObjectHeapCompactionMode](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.gcsettings.largeobjectheapcompactionmode?view=net-10.0)
controls managed large-object compaction. Even a managed byte array would remain
one array to the GC: moving that array does not pack application-defined records
inside its bytes. Switching backing storage would not remove the internal allocator.
[GC.AddMemoryPressure](https://learn.microsoft.com/en-us/dotnet/api/system.gc.addmemorypressure?view=net-10.0)
informs managed collection scheduling about unmanaged consumption; it does not
register native records for relocation. No use of these APIs is selected here.

Reuse the established idea of region evacuation: move live blocks from sparse
regions into compatible denser regions, then reclaim the emptied regions. For
example, the [G1 documentation](https://docs.oracle.com/en/java/javase/25/gctuning/garbage-first-g1-garbage-collector1.html)
describes choosing productive regions and evacuating their live objects. This is
an algorithmic analogy, not a proposal to embed G1 or copy its runtime implementation;
G1 also has global pauses, and its pause targets are not hard guarantees.

Lattice already records allocation ownership and logical deletion/replacement.
Non-owning key references do not keep target records alive. It can use these states
and the accepted reader/retainer protocol instead of tracing an arbitrary CLR
object graph to infer record liveness. The engine-specific work remains significant:
finding owners, safe gate acquisition, repairing indexes, concurrent enumeration,
shared dictionary lifetime and bounded reclamation. Existing patterns reduce design
uncertainty; correctness still needs representative prototypes and concurrency tests.

#### A. Automatic incremental compaction — accepted for v1

When fragmentation offers useful reclaimable capacity, schedule limited maintenance
work. Assess candidate regions, reserve compatible destination capacity, move a
bounded amount of data, repair locators and yield to normal work. Coordinate physical
publication with the local serialized mutation/apply path, including on children;
a background scheduler is not a second independent writer or a separate service.

The policy should budget candidate scanning as well as copying, metadata/index
updates and retired memory. A byte-only copy limit does not bound the whole step.
Useful progress is emptying/reusing regions or reducing retained segment capacity,
not merely moving many bytes. Prefer compatible existing free slots where possible;
new destination capacity must fit the permitted temporary headroom.

Owned-block moves obey root protection and the accepted relative-address policy.
No active reader may dereference reclaimed old memory. Update parent descriptors,
primary-index locations and every other retained physical locator as applicable.
Shared dictionary entries and shared indexes have their own protection rules; one
root gate does not authorize moving text referenced by other roots. Preserve the
accepted enumeration contract as well: physical movement alone must not duplicate
a primary key or omit an unchanged matching record from a successful full pass.

- **Reads:** there is no intentional database-wide freeze. Reads touching a moved
  root may wait; shared index work and memory-bandwidth contention can also affect
  other reads. A working database does not imply unchanged read latency.
- **Writes:** maintenance competes for CPU, memory bandwidth and serialized apply
  time. One long move can delay later writes to unrelated roots in that queue and
  increase replica lag. Bounded steps should limit interference, but measurements
  must establish actual latency and throughput under concurrent load.
- **Memory:** destinations and retired old blocks temporarily coexist. Reuse can
  reduce added capacity; explicit headroom is still required. A second full dataset
  is not the default workspace. Sparse-region occupancy and class compatibility
  determine whether a useful move is possible.
- **Complexity:** candidate tracking, bounded work, safe publication, resumable
  progress and coordination with readers/retainers. Busy roots and changing data
  require a progress policy; repeatedly skipping the same regions cannot be
  presented as a guarantee of eventual reclamation.

Automatic does not mean continuously rewriting settled data. Trigger and stop
conditions should reflect expected reclaimable capacity and cost, with numerical
thresholds chosen later. Exact scheduling fairness, retry/deferral and pressure
behavior remain engineering/product work under Q14/Q17.

##### Counters maintained as allocation state changes

Use region metadata, grouped by size class and owning native segment. Update it
on allocation, publication, retirement and safe reclamation, rather than scanning
all record payloads to discover holes. A fixed-field update that keeps its allocation
does not change slot occupancy. These are accepted internal accounting categories, not finalized SDK fields.

| Region state | Meaning for compaction |
| --- | --- |
| Slot capacity and size class | Which blocks fit and how much region space is accounted for |
| Live slots | Published allocations that must be preserved or moved |
| Retired slots still retained | Not live values, but not safe free space yet |
| Reserved/in-flight slots | Destination or write-preparation capacity already committed to pending work |
| Reusable free slots | Slots immediately available to a compatible allocation |
| Encoded live bytes and metadata/slack accounting | Distinguish useful content, headers and class rounding from free slots |

For one class region, total slots equal live + retired + reserved + reusable slots.
Headers outside slots, alignment and unused region tails are accounted separately.
State transitions must not double-count an old block as both safely free and retained.
Reclamation updates reusable space only when all relevant retention has ended.

A count of deletion events or a single process-wide free-byte percentage is not
enough. Deletes may be followed by reuse, a newly empty document may still have
retired blocks, and free slots in another class may be unusable as destinations.
Internal slack inside a live slot is not a free slot and ordinary evacuation does
not automatically remove it. Empty reusable regions need no live-block copying.

##### Trigger on useful reclaimable capacity

When occupancy changes, update a bounded candidate structure with at most one
entry per region, or revisit region metadata with a bounded scan. Do not append an
unbounded work item for every mutation. The exact bitmap/list/bucket representation
and snapshot synchronization remain implementation choices. A candidate is a hint;
revalidate liveness, owners, readers and destination capacity before moving anything.

Start work when the candidate set offers enough whole-region capacity to recover
at acceptable move/index-repair cost, compatible destinations exist, and
the required workspace fits its budget. Low source occupancy is useful evidence;
it is not a sufficient trigger by itself. Candidate selection should account for
which native segments could actually become empty, not just count scattered holes.
Memory pressure can request earlier consideration but cannot create workspace or
make retained memory safe to free; pressure outcomes remain Q14 work.

For illustration, two regions of the same class each hold 100 slots. If A has 10
live and 90 reusable slots and B has 70 live and 30 reusable slots, A's ten live
blocks can fit into B. After safe movement/reclamation, B has 80 live slots and A
is entirely reusable. These numbers describe slots, not byte sizes or chosen
thresholds; assume no retained/reserved blockers. Releasing a native segment still
requires its other regions and metadata to be safe for release.

Use separate start/stop conditions so activity near a threshold does not repeatedly
wake and stop the worker. Exact thresholds, minimum benefit, observation cadence and
deferral rules remain open. A coalesced wake-up plus bounded periodic reconsideration
is a candidate for ensuring an idle node can reclaim space after readers finish;
it is not a timer that must compact the database on every tick.

##### Background scheduling and protected moves

Each master/child node needs its own local maintenance lifetime. An engine-owned
background task can observe metadata and schedule work; embedded hosting does not
require an external maintenance process or a mandatory application host framework.
Thread/task primitives and cancellation/disposal wiring are implementation details.

Planning can run in the background. The move/publication must coordinate with that
node's serialized writer or replica applier. For an owned block, use the accepted
root-document protection, not newly invented independent per-block gates. Safely
reserve a destination, revalidate current ownership/location, copy and publish the
repaired locators, retire the old block and return to ordinary work. Shared strings
and indexes retain their separate protection protocols. No gate spans I/O.

When a root is busy, a bounded step may defer it and consider other candidates;
fairness and pressure rules must prevent permanent starvation from being hidden as
progress. Yield/sleep when a work budget is consumed or no safe useful candidate is
available; do not spin under locks or grow a second arena while waiting. Background
execution still consumes CPU/bandwidth and may delay reads or the writer queue.

Metadata work scales with allocation regions rather than a new CLR object per record
or historical change. Slot transitions should add small bookkeeping work, but this
is a design expectation, not a measured allocation cost. Measure counter/candidate
maintenance separately from movement and full query/write latency.

#### B. Compact while the affected database is paused — not selected as the v1 compaction mode

Close affected reads and writes, drain active protected readers and compact the
database to a consistent state before reopening access. Pausing only the writer
does not make existing native readers safe. A temporary pause can be automatically
triggered; this alternative need not depend on an operator restarting the process.

- **Reads and writes:** affected operations are unavailable or wait under an agreed
  admission policy for the duration. Shared engine resources or a serialized writer
  may extend interference beyond that database. No pause duration is known yet.
- **Throughput:** exclusive access can simplify work and reduce contention while
  packing; it does not establish a universal speed advantage or eliminate copying.
- **Memory:** packing can reuse existing free capacity and bounded workspace. B does
  not inherently require a second full arena or zero temporary memory; large blocks
  and movement order still matter.
- **Complexity:** simpler concurrent-state handling after readers drain. Admission,
  cancellation, interruption, failure and reopening need explicit rules. The replica
  reload wait policy does not automatically define compaction waiting behavior.

#### Rationale and unresolved limits

The decision selects A for a database expected to serve local reads during long-lived
update churn. It trades more implementation work and continuing background overhead for
avoiding a planned whole-database maintenance outage. B is simpler to reason about
when downtime is acceptable, but its pause grows with the work that must finish
before reopening. No measured winner or fixed numerical budget is claimed.

Master-coordinated mutations, root gates, relative addressing and slot reuse
provide building blocks for A; they do not constitute a completed compactor.
Acceptance selects the maintenance policy. The following details still require work:

- A time budget is not a mechanism for interrupting an arbitrary copy or locator
  repair halfway through. A completed move must leave a valid published state.
  Choose a maximum independently movable unit and bound its complete work.
- [Bounded chunks for large payloads](physical-storage.md#large-payloads-in-bounded-chunks)
  are now accepted. Chunk/directory sizes and complete relocation work still need
  measurement; bounding data copies alone does not bound locator repair or gate
  waiting. Other metadata must also fit the movement policy or explicit limits.
  Whole-document reads/writes retain their existing materialization and gate costs.
- Define progress when readers/retainers pin memory or compatible destination
  capacity is unavailable. Exhausted headroom must produce the selected pressure
  behavior, not unlimited destination allocation or unsafe reclamation.
- The empty-segment decision below accepts a bounded reuse reserve and background
  release of excess. Choose numerical reserve/timing budgets and region-reassignment
  mechanics. Native release and lower OS resident memory remain distinct.
- Keep logical document identity, contents and revision unchanged by physical-only
  moves. Such moves are not application Save commands or logical replica mutations.
  If the eventual saved format depends on local placement metadata, recovery must
  preserve or reconstruct coherent mappings; this decision does not settle that
  format. No native gate may span storage/network I/O.

An optional administrative request may eventually ask the same mechanism to work,
but no console, endpoint, emergency pause fallback or extra v1 mode is selected here.

#### Evidence needed before numerical commitments

The existing [segment prototype](../../src/Prototypes/SegmentStorageComparison/README.md)
replaces full segments without concurrent readers. It does not evacuate sparse
size-class regions, repair all locators or establish pause bounds. No new experiment
is reported by this decision.

Use bounded live-data churn with several block sizes, hot/cold roots, growing and
shrinking collections, secondary indexes, dictionary text and active readers. Create
fragmentation deliberately, then verify that actual regions/segments become reusable
or releasable while data, ordering, keys and revisions remain correct. Include moving
parents, delete/reinsert races, busy readers and exhausted relocation headroom.

Report useful payload, class slack, reusable capacity, retired/pinned bytes, segment
capacity and temporary workspace separately. Measure candidate-scan work, bytes moved,
capacity recovered, maximum completed-step duration, read/write latency percentiles,
throughput and replica apply lag. A declining live-payload counter alone is not
evidence of compaction or of returning memory to the operating system.

Also verify counter conservation and trigger behavior: free-then-reuse before a wake,
retired readers draining while the node is idle, reserved destinations, incompatible
classes, many holes with no safely evacuable region, threshold oscillation and stale
candidates after parent movement/deletion. A timer/counter prototype alone cannot
certify safe production compaction or a maximum pause for large blocks.

See [question register](../design/questions.md#current-discussion).
Numerical work/headroom limits and broader pressure rules also remain open.

### Empty-segment retention and release

**Accepted on 13 November 2026:** option A for v1. Keep a bounded reserve
of completely empty native segments for reuse; release excess eligible segments in
bounded background work coordinated with the node's local writer/applier. Account
for the reserve across the database instance within its memory budget. Only safely
detached segments with no live allocations, retained users or reservations qualify.
Reserve size, timing, exact allocator structures and broader pressure rules remain
open. This acceptance applies to masters and MemoryOnly children and does not evict
live data, introduce demand loading or change PostgreSQL persistence.

#### What can actually be released

A reusable slot is free capacity inside an allocation region. A completely empty
region can serve later allocations without making its containing native segment
empty. Only a completely empty segment can be returned as a whole to its allocator.
Chunked payloads let compaction move bounded units toward compatible destination
space; freeing a chunk alone does not release the segment that contains it.

Eligibility requires no live allocations, retained retired blocks, active users,
reserved destinations or metadata that still depend on the segment. Relevant
locators must already have been repaired or removed. The engine must remove the
segment from allocation availability and prevent new reservations before freeing
it. An apparently empty segment cannot race with a writer allocating into it.
Root/document protection, shared-string/index lifetimes and safe address reuse
remain required. None of this permits freeing referenced memory under pressure.

There are three different measurements:

1. Capacity reusable by Lattice while it still owns the native allocation.
2. Capacity released by Lattice to the native allocator.
3. Actual reduction in OS resident memory or container memory consumption.

The second does not guarantee an immediate equal reduction in the third.
[NativeMemory.Free](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory.free?view=net-10.0)
wraps C `free`. For example, the Linux/glibc
[malloc_trim documentation](https://man7.org/linux/man-pages/man3/malloc_trim.3.html)
describes a separate attempt to return free heap memory to the system. These are
platform observations, not a decision to add allocator-specific trimming calls.

#### A. Bounded reusable reserve, release excess — accepted for v1

Keep some eligible empty segments available for later growth. Account for the
reserve in bytes across the whole database instance, within its total memory
budget; allocation classes and entity types must not multiply an unbounded allowance. If segments differ in size, a count alone cannot bound retained capacity.
Exact sizing and any idle grace period remain to be measured.

Return excess eligible segments through bounded maintenance work coordinated with
the node's local writer/applier. Recheck eligibility before detaching a segment.
The reserve competes for the same memory budget as useful data, indexes, pending
operations and compaction workspace; it is not another arena or extra entitlement
outside that budget. Broader pressure/admission rules remain Q14 work.

- **Reads:** existing values retain the same access path. Maintenance bookkeeping
  can still contend for CPU or coordination; no measured latency benefit is claimed.
- **Writes:** reuse can avoid reacquiring native memory during repeated growth and
  deletion. This can help bursts; it does not accelerate every fixed-field update.
  Any required initialization and ownership checks still apply to reused storage.
- **Memory:** intentionally retain a bounded amount of idle native capacity. Release
  capacity above the reserve; retained readers or partially occupied segments can
  prevent other reclamation and must be accounted for separately.
- **Complexity:** add reserve accounting and a retention/release policy to the
  already necessary safe segment lifecycle. Avoid repeated free/reallocate cycles
  around an unstable threshold; exact scheduling and hysteresis are engineering work.

This policy runs on masters and children. A MemoryOnly child still owns native
memory; it simply has no durable local database image. Releasing empty segments
does not evict live data, require demand loading or change PostgreSQL storage.

#### B. Release every eligible empty segment — not selected for v1

Once safe, release an empty segment without retaining it for expected future growth.
Safe detachment and bounded scheduling remain necessary; "immediate" means no
intentional reserve, not interrupting a running operation or promising zero delay.

- **Reads:** no change to how live records are resolved; freeing work still consumes
  resources. It does not require reloading any live value from storage.
- **Writes:** growth after a shrink phase must obtain new capacity if existing
  compatible space cannot satisfy it. Allocation and required page preparation may
  add latency; how much depends on the platform and workload and is not measured.
- **Memory:** minimizes Lattice's intentionally retained empty-segment capacity.
  This is useful for workloads that shrink and rarely regrow, but does not guarantee
  immediate OS/container memory reduction or eliminate fragmentation elsewhere.
- **Complexity:** fewer reserve-policy rules. The same lifetime accounting, race
  prevention, segment-table handling and allocator integration are still needed.

#### Availability lookup and compaction boundaries

Free-space metadata is maintained as allocation/reclamation changes occupancy; a
foreground allocation does not rediscover availability by scanning every segment.
An illustrative implementation keeps non-full regions by size class, free-slot
lists or bitmaps within regions, and a separate pool of eligible empty segments.
Select compatible reusable capacity first; use an empty segment when a new region
is needed, and acquire native capacity only if existing eligible space cannot serve
it within budget. Exact list/bitmap structures and concurrency mechanics remain E01
work. Removing a known list entry can be O(1); this is not a whole-write latency claim.

The illustrative clarification distinguishes three kinds of unused space:

- **Slack inside a live allocation:** moving it unchanged does not reduce its
  capacity. The later collection-shrink decision below accepts automatic bounded
  reclamation of substantial unused capacity; concrete algorithms still require design.
- **Free slots between allocations:** compaction may move live blocks between
  regions and segments to empty whole segments. Packing each segment independently
  may leave all of them partly occupied and release none. Physical moves preserve
  logical List order, data, keys, revisions and index/enumeration correctness.
- **Physical RAM placement:** the engine resolves virtual addresses. It does not
  need adjacent segments to occupy adjacent physical pages. The OS owns virtual to
  physical mapping; no physical-RAM compactor is selected for Lattice.

The [Microsoft virtual-address-space documentation](https://learn.microsoft.com/en-us/windows/win32/memory/virtual-address-space)
explains that mapping. A reusable native slot, an empty engine segment and memory
returned to the OS are distinct states. Empty-segment retention is decided only
after the segment has become safe to reuse or release.

#### Selection rationale and evidence boundary

The decision selects A for a reusable database that can experience bursts and repeated updates.
It trades a bounded idle reserve for fewer reacquisitions. B is a legitimate simpler
choice when returning idle capacity matters more than repeated growth latency.
Keeping all empty segments indefinitely is not the accepted bounded-memory policy.
No second user-selectable release mode is selected for v1.

Before choosing numbers, measure both policies with equal logical data: steady churn,
growth/delete/regrowth bursts and sustained shrink. Include protected readers that
delay reclamation and multiple allocation classes sharing the database budget.
Measure engine-owned/idle/retained bytes, peak resident/container memory, allocation
and release counts, compaction workspace and read/write latency separately. Verify
that all live data, indexes and enumeration remain correct while capacity is released.

The current segment-copy prototype has no concurrent readers or release reserve;
its allocation/free calls do not establish this policy's memory or latency behavior.
This acceptance selects a policy only. Numeric defaults and implementation remain
open; it adds no production worker, benchmark run, administrative command or platform fallback.

### Automatic shrinking of unused collection capacity

**Accepted on 13 November 2026:** option A for v1. Automatically reclaim
substantial, persistent unused capacity in live owned List/Map/Set storage through
bounded maintenance coordinated with the local writer/applier. Avoid rebuilding on
every deletion; use measured shrink/growth conditions to prevent repeated resizing.
Safely detach redundant empty chunks and schedule necessary element/directory/lookup
reorganization within the memory and work budgets. Preserve logical values, List
order, Map/Set uniqueness, root revisions, reader lifetime and physical locators.
Thresholds, spare capacity, timing and collection-specific algorithms remain open.
No manual trim API, second arena, runtime implementation or performance result is selected.

#### What is different from segment compaction

An allocation has both useful content and capacity. A collection can lose most of its
elements while still owning a large directory, data chunks or lookup capacity. Moving
that storage to another segment without changing its capacity does not remove this
slack. It is still charged to the collection rather than available for other records.

This question concerns physical capacity within live owned List/Map/Set storage.
Already detached or retired blocks must still be reclaimed safely under the accepted
lifetime rules; B is not permission to leak unreachable allocations. Document deletion
releases ownership normally. String replacement and shared dictionary-entry lifetime
retain their existing contracts; no mutable-string editing API is introduced here.

The distinction also exists in managed collections: .NET documents that
[List.Clear](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.clear?view=net-10.0)
retains capacity, while
[List.TrimExcess](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.list-1.trimexcess?view=net-10.0)
can reduce it and involves work proportional to the retained elements. These are
illustrations of the tradeoff, not a selection of managed List storage or its thresholds.

#### A. Automatic shrink after substantial sustained underuse — accepted for v1

Maintain useful-size/capacity information as collections change. When enough useful
capacity can be recovered, schedule shrink within the existing bounded maintenance
mechanism coordinated with the local writer/applier. Avoid a full-arena scan per
mutation or a new independent authoritative writer.

Where a redundant empty chunk can be detached without redistributing live elements,
retire it safely, retaining only the measured spare capacity policy. Where shrinking
requires moving elements, reducing a directory, changing a size class or rebuilding
lookup storage, do that work under the selected collection algorithm and resource
bounds. Exact structures remain Q16/E01 work; List, Map and Set need not use the same
algorithm or threshold.

Use separate shrink/growth conditions or another measured hysteresis mechanism so
small oscillations do not repeatedly copy the same data. Capacity retained for regrowth
is charged to the instance budget; this does not add an unlimited reserve per collection.
No numerical ratio, minimum size, delay or promised reclamation deadline is selected.

Preserve root-document coherence, List order/duplicates, Map/Set uniqueness, text
comparison and all physical locators. Pure capacity maintenance leaves logical data
and document revision unchanged and produces no application Save or logical replica
update. A node may therefore choose different physical capacity from another node.
Recovery must still restore equivalent logical data under the eventual storage format.

Chunking does not by itself make every shrink operation interruptible. In particular,
Map/Set lookup resizing and directory replacement need coherent algorithms and bounded
workspace; an unbounded rehash under a root gate is not justified by calling it a
background step. Define the complete move/publication work, respect reader lifetimes,
and defer work that cannot safely fit the current budget. Do not hold gates during I/O.
Detailed progress rules under sustained contention/headroom exhaustion require testing.

- **Reads:** logical access contracts stay unchanged. A smaller footprint may improve
  locality, but no speedup is measured. Maintenance consumes memory bandwidth and can
  briefly contend for root/index protection.
- **Writes:** removals update occupancy/accounting and need not immediately rebuild
  the whole collection. Deferred work still costs CPU; regrowth after shrink may need
  allocation. List shifts and normal mutation costs are not eliminated by this policy.
- **Memory:** lets other records reuse capacity formerly owned by shrunken collections.
  Slack, rounding, temporary workspace and retired-reader retention remain; freed
  chunks do not necessarily make whole segments empty or immediately lower OS RSS.
- **Complexity:** candidate tracking, anti-oscillation policy and collection-specific
  shrink/publication algorithms. Reuses accepted maintenance/lifetime mechanisms, but
  those mechanisms alone do not implement shrinking inside a live collection.

#### B. Retain capacity until explicit trim — not selected for v1

Ordinary element removal reduces logical contents while retaining reusable capacity
owned by the collection. A deliberate trim/rebuild request releases that excess under
the same coherence and lifetime rules. Its API, scheduling and delivery contract would
need definition if B were selected; no new maintenance command is accepted here.

- **Reads:** no additional automatic shrink work; the larger working set remains.
- **Writes:** repeated delete/regrow cycles can reuse existing capacity with less
  background rebuilding. Ordinary deletion/lookup maintenance still has its own cost.
- **Memory:** a live collection can retain capacity near its past peak until explicit
  action. This may consume the instance budget even though the useful dataset shrank.
  Unreachable blocks and deleted documents are still reclaimed normally.
- **Complexity:** simpler automatic scheduling, but explicit trim still needs a correct
  implementation and the application/operator must decide when to invoke it.

#### Selection rationale and evidence boundary

The decision selects A for a long-lived general-purpose database with a bounded memory budget.
It can recover capacity without requiring applications to recognize every historical
peak. B suits workloads known to regrow quickly or applications willing to schedule
trimming explicitly. Do not silently add both as configurable v1 modes.

Before selecting numbers, compare sustained shrink with repeated shrink/regrow cycles
for ordered List data, Map/Set lookup structures and many small collections. Include
active readers, dictionary values, replica application, secondary indexes and limited
maintenance headroom. Measure useful/capacity/retired bytes, workspace peaks, bytes
moved, reclaimed capacity and complete read/write latency. Verify unchanged logical
order, values, revisions and lookups through every maintenance boundary.

Existing numeric-layout and segment-copy prototypes do not measure these policies.
This acceptance selects the policy, without a runtime implementation, numeric defaults
or a new benchmark run.
