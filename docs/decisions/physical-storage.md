# Physical storage

**Milestone 1 implementation baseline:** [concrete mechanisms and initial settings](../design/milestone-1-engine-contract.md)
now resolve the engineering selections described as open below. Product contracts
remain in force; measured production validation is still required.

Accepted Moltaro.Lattice decisions, rationale and alternatives. Acceptance applies
only to the stated subtopic; unresolved layout and lifetime choices remain open.

[Decision index](README.md) · [Question register](../design/questions.md) · [Storage and maintenance](../design/storage-and-maintenance.md)

- [Q16](#q16-physical-storage-and-layout)

## Q16: Physical storage and layout

**Partially accepted:** allocated native backing, record-oriented fixed fields with
separate variable payloads, relative segment/offset addressing, small contiguous
payloads with bounded chunks for large strings/collections, database-owned segments
with a bounded empty reserve, a stable control entry per root object in managed
arrays of structs, compact small Map/Set values with hashed lookup for large values,
targeted mutation preparation with protected post-commit publication and an independent
binary persisted base with arena reconstruction on load are settled. Numerical
block/segment/chunk sizes and thresholds, exact address encoding, collection
internals and detailed copy boundaries remain open within Q16. The accepted
[persisted-base rationale](../design/questions/persisted-base-layout.md) separates durable
placement from live allocation; exact saved-to-live encoding remains Q16/E06 work.
[Master write coordination](../design/questions/master-write-order.md) accepts per-object
protection, concurrent independent preparation and sequential saving/publication
of short bounded groups with internal group commit; exact budgets and scheduler
integration remain engineering work. [Q17 allocation](allocation-and-compaction.md)
accepts size-class reuse and automatic bounded incremental compaction with region
accounting/benefit-based triggers and a [bounded empty-segment reserve](allocation-and-compaction.md#empty-segment-retention-and-release)
with background release of excess. Numerical Q17 budgets and broader pressure rules
remain open; see [question register](../design/questions.md#current-discussion).

### Independent persisted base and arena reconstruction

**Accepted on 13 November 2026:** the PostgreSQL base uses an independent versioned
binary representation of records and owned payloads. Provider-local addresses locate
saved data; native segment/offset locations describe live allocation. The two address
spaces need not match. The accepted 256-byte saved-unit baseline is unchanged.

The base preserves logical type/key identity, field values, owned structure and
recoverable schema/layout metadata at its applied-through position. It does not make
native pointers, root gates, physical indexes, free lists or arena slack authoritative
durable state. Provider-local addresses do not introduce another public key or a
universal runtime handle for every owned block.

The existing background coalescing worker maps committed changes onto saved units
and advances their applied position atomically. Whole Save remains a logical whole
replacement; explicit partial updates can replace the affected units without always
rewriting a large root. Actual write amplification still needs measurement.

On load, stream a coherent base through bounded buffers and reconstruct the native
arena and its required runtime/dictionary metadata. Replay the retained journal tail
and construct local physical indexes before readiness. No intermediate full CLR
object graph, second full arena or complete resident copy of the saved base is
required. Loader lookup/decoding buffers and metadata still need memory budgets.

Pure native relocation does not change the logical journal or saved locations.
Provider packing/reclamation is separate and must preserve its own references and
recovery boundaries. This independence does not eliminate fragmentation or make
either representation free to maintain.

Exact directory layout, headers, packing, persisted addresses, delete/reuse rules,
version encoding and coherent base/tail capture remain engineering work. Measure
cold loading, peak memory and small-update amplification. Initial replica loading
uses the separate [binary arena-block transfer direction](replication-and-hosting.md#initial-replica-state-representation);
the [fenced capture and readiness handoff](replication-and-hosting.md#initial-capture-and-readiness-handoff)
is accepted, while production block/lifetime integration remains E07 work.
MemoryOnly remains independent of PostgreSQL.

### Database-owned native segments

**Updated on 13 November 2026:** the [single-database scope](database-scope.md)
removes partition ownership. The open database owns its native segments and one
bounded reserve of completely empty segments. Roots, owned payloads and string
storage from different entity types can occupy compatible regions; there is no
segment allocation per type or logical partition.

Allocate on demand using the accepted size-class availability/free-slot structures.
A segment enters the empty reserve only after live and retired blocks, protected
readers and other retained references have been released safely. A logically deleted
but protected block is not free capacity. Release excess reserve under Q17's policy.

Compaction remains bounded, uses the owning root/shared-string protection protocols
and repairs current locators. This change does not prescribe segment/block sizes,
change the 256-byte PostgreSQL saved unit, add a second arena or create a process-wide
reserve shared between independent Lattice databases. Numerical budgets and compact
metadata representations remain engineering/measurement work.

### Stable control entry per root

**Accepted on 13 November 2026:** each root object lifetime has one stable control
entry. Lookup reaches that entry before resolving the root's current relative
segment/offset location. The entry coordinates location, revision and protected
access/lifetime state. Root data may move without changing the public primary key.

Owned payload descriptors keep relative segment/offset addresses. This does not
introduce a universal handle table for every owned block, permanent record versions
or another public object identifier. The control entry is runtime metadata; its
process-local address is not a persistence or replication identity.

A lookup must safely retain the entry before it can lose index protection, acquire
root protection and revalidate the retained lifetime before dereferencing the current
location. Deletion/reinsertion under the same key must not redirect an old retained
entry to a different lifetime. Retired entries become reusable only through a safe
retention/reuse protocol. Exact generation widths and reclamation mechanics remain
engineering work; generation checks alone are not sufficient.

A root move updates the entry's location under exclusive root protection. Indexes
using a safe stable root locator do not need physical-address repair solely because
the root moved. Every structure that does retain a physical location must still be
repaired; moving an owned payload updates its owner descriptor. Dictionary text
continues to follow its separate shared-entry protection protocol.

The entry does not require a managed wrapper or separate managed lock object per
root. Exact gate representation, full lock order and per-entry fields/bytes remain
to be designed and measured. Entry lifetime protection does not extend borrowed-view
lifetimes or provide a snapshot across objects. [Rationale and alternatives](../design/questions/root-location.md).

#### Managed arrays of structs for control entries

**Accepted for v1 on 13 November 2026:** store root control entries in compact
managed arrays of structs. Grow the table by adding arrays instead of copying all
existing entries into a larger array. A directory of those arrays may grow separately;
its own old/new capacity remains part of the memory budget. Production entry fields,
array capacities and directory-growth factors are not fixed by the prototype.

The primary index's root locator identifies a logical array/slot and protected root
lifetime. It is not a raw pointer into the managed heap or a second public object
key. CLR movement of an array does not change that logical identity; the engine
uses managed references for access and does not require pinning every array.
Native root/payload locations remain relative segment/offset addresses in the arena.
Control entries are reconstructed locally, not persisted or copied as CLR memory.

After deletion, reuse a control slot only when the accepted lifetime protocol makes
it safe. Managed backing keeps arrays alive while referenced; it does not protect
against stale logical locators, unsafe slot reuse or concurrent publication on its
own. Synchronization, safe acquisition/reuse and directory publication remain E03
engineering work. No separate managed object and lock per root is selected.

The [bounded comparison](../design/prototype-evidence.md#bounded-root-entry-storage-comparison)
found comparable storage and incremental-growth costs for managed/native pages;
native was faster in some access loops, without establishing an overall engine
speed winner. The decision favors managed storage lifetime for control metadata.
It does not accept the synthetic 32-byte entry or 4,096-slot array as production
defaults, change the native data arena or claim completed concurrency integration.

[Safe acquisition, deletion and reuse](../design/questions/root-location.md#implementation-task-safe-lookup-deletion-and-entry-reuse)
are E03 implementation tasks, not a pending algorithm-approval question. If deletion
publishes before protected read access, the old target is absent; if the read holds
access first, actual removal waits. The engine chooses and verifies the mechanism
without treating a particular counter or epoch scheme as accepted.

### Targeted mutation preparation and publication

**Accepted on 13 November 2026:** prepare explicit partial changes before commit.
After durable PostgreSQL commit, assign prepared fixed-size values in place and
install the necessary prepared replacement blocks under exclusive root protection
and the coherent index-publication protocol. Preserve untouched owned payloads;
do not clone the complete owned tree for every partial update.

Full Save/Replace prepares a complete replacement from the supplied final object,
without automatic diff or change tracking. It may temporarily retain both old and
new owned data. Dictionary strings may reuse entries under the existing sharing
policy; that does not infer object edits. Collection edits can require restructuring
several blocks, so a small logical update does not guarantee a constant copy cost.

Validate and acquire the required publication resources before journal submission.
Account for retained inputs, prepared values/blocks, journal buffers, index work and
dictionary lifetime holds within the instance budget. Ordinary preparation/capacity
failure must leave the published object unchanged and occur before durable commit.
No root or index gate spans the PostgreSQL wait; readers can still access the old
published state. Success follows durable commit and coherent root/index/revision
publication, not merely successful preparation.

MemoryOnly uses the same preparation and protected publication responsibilities,
with no PostgreSQL commit or durability wait, as required by its existing mode contract.

Conflicting logical writes must not invalidate prepared state before publication.
Accepted per-object coordination and database WriteBatch set the conflict
boundaries; exact scheduling remains engineering work. Many callers may submit
concurrently. Physical locations may change during the wait, so publication must
resolve them through protected current entries/descriptors rather than stale pointers.
Retire displaced blocks and release them only after relevant retainers finish.

This is an engine-internal write path, not writable public arena access. It introduces
no permanent versions or second full arena. Unexpected post-commit publication
failure and uncertain commit responses still require the recovery protocol; neither
may be disguised as an ordinary pre-commit rejection. Exact failure transitions,
index reservation mechanics and numerical bounds remain engineering work.
[Rationale and alternative](../design/questions/mutation-preparation.md).

### Live-arena backing

**Accepted on 12 November 2026:** option A, explicitly allocated native
segments for mutable live data, with persistence handled separately. A file-backed
mapping of the durable database is not the primary mutable arena in v1. Managed
.NET control structures, root-document consistency and full replica initialization
before reads retain their accepted contracts.

Q16 also owns physical record/collection layout and handle/copy boundaries. Choosing
backing alone did not select record layout, an allocator or fragmentation thresholds.
The later layout acceptance is recorded below; remaining mechanisms stay in Q16/Q17.
Persistence acknowledgment is owned separately by Q09.1. The decision does not select a complete journal/checkpoint protocol
or an index format, and is not a measured performance conclusion. The later
[Q19 decision](index-lifecycle.md) accepts local index reconstruction on every node.

### Distinguish operations, memory backing and layout

- Native allocation supplies memory blocks; the engine subdivides and manages them.
  [NativeMemory.Alloc](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory.alloc?view=net-10.0)
  is one possible .NET primitive and is documented as a wrapper over C malloc.
  Accepting A does not select that particular allocator API or one allocation per record.
- [MemoryMappedFile](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)
  maps file-backed views into virtual memory; the OS manages their pages. Non-persisted
  mappings also exist, but B here specifically means disk-backed live mutable storage.
- [Buffer.MemoryCopy](https://learn.microsoft.com/en-us/dotnet/api/system.buffer.memorycopy?view=net-10.0)
  copies bytes between addresses. Copying can be needed in either design for owned
  results, growth, movement, installation or checkpoint output. It is not a storage backend.

Logical documents need not be contiguous. Small scalar updates need not copy a
whole segment. Reusable destinations and protected reads retain their accepted
allocation/ownership contracts regardless of the backing choice.

### A. Allocated native live segments with separate persistence — accepted for v1

The mutable working set lives in engine-owned native segments. Persistence consumes
committed changes under Q09's accepted journal/base and durable acknowledgment
contracts; detailed format and budgets remain separate. On restart, the engine
reconstructs/loads the live arena before
readiness. Replicas still receive their full selected state and catch up before reads.

- **Reads:** a resident arena supports direct typed access. Native allocation alone
  does not pin all pages in physical RAM or eliminate every OS page fault; residency
  and container limits still need measurement. A normal read need not consult a
  database file merely to resolve a resident record.
- **Writes:** update engine-owned blocks under the record/index publication rules.
  When enabled, persistence still costs work and may delay acknowledgment. The
  durability/recovery guarantees remain Q09 decisions; Q16 itself does not disable
  persistence. The later [Q09.5 decision](persistence-modes.md#q09.5-how-is-persistence-mode-selected)
  accepts Durable by default or explicit MemoryOnly at instance open time.
- **Memory:** budget live segments, free capacity, dictionaries/indexes, retired
  blocks and persistence/transfer buffers. File buffering may also consume OS
  memory. Avoid assuming the arena is the process's entire footprint.
- **Startup:** loading and reconstruction can involve copying and index work.
  A raw memory-copy throughput result does not establish complete readiness time.
- **Complexity:** mutable allocation/compaction is less directly coupled to the
  durable file's placement. The engine still needs a persistence format, recovery
  protocol and safe reclamation; it is not the simpler choice in every workload.

This fits the accepted local-arena-first development boundary and does not require
live RAM addresses to match durable offsets. It leaves the use of mapped immutable
images as a possible later implementation choice, not an additional promised v1 mode.

### B. Map the durable file as the mutable live arena — not selected for v1

Data and allocation metadata occupy mapped regions of a file. The engine accesses
resident pages through those mappings. A compatible file layout may reduce explicit
startup copying, but opening a mapping does not demonstrate that all pages are
resident, indexes valid or recovery complete.

- **Reads:** resident access can also be direct. Non-resident mapped pages can incur
  storage I/O. Warm-up/readiness must honor the accepted full-load contract; B does
  not reintroduce application-visible demand loading as a hidden v1 fallback.
- **Writes:** changed pages eventually need writeback. Mapping does not supply
  record-level crash atomicity, a commit boundary or ordered durable acknowledgment.
  Recovery must account for partially changed mapped state after a crash.
- **Memory:** mapping can avoid a separate copy of the same file payload in an
  explicitly loaded arena, but resident mapped pages still consume memory. Reconcile
  mapped/file-cache accounting rather than adding overlapping measures or treating
  virtual file size as a promise of RAM residency.
- **Startup:** possible reduction in copying; validation, recovery, page population,
  index preparation and replica catch-up still remain. No speedup is measured yet.
- **Complexity:** file growth, mapping/view lifetime, free-space metadata, compaction,
  shrinking and durability interact. Raw pointers cannot be persisted as portable
  addresses; reopening/moving storage still needs a valid offset/handle scheme.

### Fragmentation is an independent obligation

Both alternatives can develop holes and allocation slack. Both need reuse and
reclamation; a mapped file's size can also grow despite a bounded live dataset.
Q17 decides when to relocate live blocks, how much temporary space is allowed and
when empty segments/file regions are released. A relocation cannot invalidate an
active protected view or break index/replication/checkpoint references.

An allocation freed to a runtime allocator, reusable arena capacity and a reduction
in resident OS memory are different observations. Selecting allocated segments
does not solve unbounded growth merely by supplying an allocation primitive.

### Evidence and remaining decisions

Compare representative hot updates, variable-sized strings/collections, local and
replica startup, restart/recovery, steady-state churn and memory-pressure behavior.
Include peak memory and writer/read tail latency, not only memcpy bandwidth or file
open time. Existing row/column and native/managed prototypes do not compare these
two complete storage backends. Validate the selected backing under bounded workloads
after the budgets are specified; no load experiment is started by this document.

The separate journal/checkpoint details and backup/restore contract still need
choices after this acceptance. Q18 selects a diagnostic API for the first release;
future maintenance interfaces invoke the engine-owned operations. The later
record-layout acceptance below settles the baseline; block
sizes, exact address encoding, collection internals and detailed copy boundaries remain Q16
work. Q17 accepts size-class reuse, automatic incremental compaction and a bounded
empty-segment reserve with release of excess, and automatic shrink of substantial
unused collection capacity; numerical budgets, concrete algorithms and remaining
pressure details stay open.
Q19 accepts local reconstruction
without saved/transferred physical index images; bounded builders and startup
measurements remain engineering work.

### Record-oriented layout with separate variable payloads

**Accepted on 13 November 2026:** option A as the v1 baseline. Keep
fixed-size fields of a record or owned structure together; store variable strings
and collections in separate payload blocks. Preserve hierarchical ownership and
one logical gate per root and all its owned blocks. Dictionary strings continue to
use the accepted database dictionary. This is an arena layout decision, not an
implementation or a measured full-engine performance result.

Block sizes, addressing and defragmentation have separate design records.
Relative addressing is now accepted below. Exact field packing and collection
representation remain engineering work. Q16 remains partially accepted; no additional
counted question is introduced.

#### The question in practical terms

The logical document remains a schema-bound ownership tree. Its root and all owned
blocks share one logical read/write gate. References to other roots remain keys.
Physical record-oriented storage does not flatten that tree into relational tables
or require joins to reconstruct ownership.

The physical choice determines which bytes are close together and which must move
when a field changes. It is independent of the accepted 256-byte PostgreSQL saved
unit: a saved unit is neither an arena allocation size nor a wire-message size.
The accepted logical replication format also does not require identical addresses
or allocation layouts on master and children.

#### A. Fixed fields together, variable payloads separately — accepted for v1

Place fixed-size values of a record or owned structure together in a compact,
schema-defined region. Store descriptors for strings and collections there; their
variable payloads live elsewhere in the native segments. This does not require
one contiguous allocation for the entire ownership tree.

```text
Record / owned structure
  metadata | null validity | fixed numeric fields | text descriptor | list descriptor
                                                       |                 |
                                                   text payload      list payload
```

For a Dictionary string field, the descriptor refers to the accepted database
dictionary. Plain fields retain their own text. List, Map and Set retain their
accepted ordering/uniqueness semantics regardless of physical placement. The
allocator subdivides native segments; this is not one operating-system allocation
or managed object for each value.

- **Reading a complete record:** fixed fields are close together. Reading variable
  data follows descriptors; a large tree still visits multiple regions.
- **Reading only a few fields across many records:** more unrelated bytes may pass
  through CPU caches than with columns. Indexes can reduce candidate records but
  do not eliminate this tradeoff for full scans.
- **Small fixed-field updates:** an explicit numeric assignment need not copy the
  whole document or arena segment. Publication still includes revision, affected
  indexes and durable journal work; it is not merely an unprotected memory store.
- **Variable-size updates:** growing text or a collection may allocate and copy
  that payload, then update its descriptor. It need not move unrelated fields.
  Large collections still need a bounded representation; this decision does not
  promise constant-time growth or select collection chunk sizes.
- **Memory:** no permanent version per update is required. Descriptors, metadata,
  alignment, allocation slack and holes consume memory. Preparation and safe
  reclamation can temporarily retain old/new payloads; their budgets still matter.
- **Complexity:** multiple owned regions and relocation must be managed correctly,
  but the common access unit matches the accepted document gate and view API.

Generated and schema-driven views read these regions during short protected reads.
Ordinary Get still copies into caller-owned data. A value-type view copies its
descriptor, not the underlying data; string conversion/materialization may allocate.
The decision does not extend borrowed-view lifetimes or permit I/O under a gate.

#### B. Store fixed fields in columns across records — not selected

Keep each fixed field in its own sequence across many documents. Variable strings,
owned structures and collections still need their own representation and mappings.

- **Projected scans:** a scan of a few fields can read compact sequences without
  bringing unrelated fields into CPU caches. This is attractive for numeric series
  and analytical passes.
- **Complete-record and graph access:** gather values from multiple regions.
  Stable root identity, nested ownership and nullability must map to column slots.
- **Updates:** fixed-field assignment can also change only the affected bytes.
  Columns are not required to avoid copying an entire document. Whole-document
  insert/delete/replace coordinates membership across multiple columns.
- **Memory:** reading fewer bytes does not itself mean storing fewer bytes.
  Without a selected compression scheme, field payload sizes may be identical;
  column/slot metadata and unused capacity can add overhead.
- **Complexity:** ownership, gates, index publication, slot reuse and compaction
  must remain coherent across separate columns. Nested collections increase this
  work compared with a simple fixed numeric table.

#### C. Store each whole document as one packed contiguous blob — not selected

Place fixed and variable parts of the ownership tree into one allocation, with
enough offsets/metadata to navigate it.

- **Reads:** whole-document copying and sequential traversal can benefit from
  contiguous data. Field access still needs a navigable schema/layout; it need not
  decode every preceding field if offsets are available.
- **Updates:** fixed fields can still be overwritten in place where the layout
  permits. Growing a string or collection may shift trailing data or replace a
  much larger blob; not every scalar update requires a whole-blob copy.
- **Memory:** fewer separate descriptors/allocations may help, but offsets,
  alignment and reserved growth capacity have costs. Replacing a large blob can
  briefly require both old and new allocations under the existing reclamation rules.
- **Complexity:** serialization is straightforward for a fixed snapshot. Efficient
  partial mutation, growth and offset maintenance are the harder parts.

#### Comparison and rationale

| Concern | A: record plus variable blocks | B: columns | C: whole-document blob |
| --- | --- | --- | --- |
| Whole-record access | Local fixed fields; follow variable payloads | Gather across columns | Contiguous document; navigate offsets |
| Scan a few fields | Can read unrelated cache-line data | Strong candidate for compact projected scans | Can visit large strides or unrelated data |
| Fixed scalar update | Change affected value plus metadata/index work | Same principle | Possible in place with suitable layout |
| Growing variable data | Move affected payload when necessary | Depends on variable-data representation | May move a large part or the whole blob |
| Memory risk | Descriptors, slack, holes, temporary payloads | Slot/column metadata, capacity, variable payloads | Growth slack and temporary replacement blobs |
| Main implementation cost | Owned regions and relocation | Coordinated column membership and ownership | Efficient partial mutation of variable blobs |

The decision selects A as the initial general-purpose layout for mixed ID reads,
protected traversal and small explicit updates. It matches the current document
model while avoiding obligatory whole-document relocation for variable-field growth. It is a
design decision, not a claim that A wins every benchmark. Do not introduce
automatic layout switching or several configurable v1 layout engines through this
decision.

Full Save still submits a complete final document, without automatic diff. Physical
layout does not change that API or turn its replicated payload into a sparse update.
The limited fixed-field update path applies to explicit partial updates.

#### Existing evidence and remaining measurements

The [packed-record layout prototype](../design/prototype-evidence.md#packed-record-layouts)
compares managed arrays containing the same fixed numeric payload. In that bounded
experiment, rows were faster for complete-record reads and two-field random updates;
columns were faster for scanning a small field subset. Payload sizes were equal.

That experiment has no native allocator, variable strings, nested collections,
primary-key lookup, indexes, root gates, journal or replication. It cannot establish
the full engine's throughput, memory footprint or best native layout. No new
benchmark is reported by this decision.

Validate the accepted native layout before tuning its implementation. Measure
ID lookup plus full copy, protected traversal, projected scans, small explicit
updates, variable growth and repeated deletion/reinsertion. Include dictionary
strings, nested collections, affected indexes and root protection. Report steady
live bytes, allocator/metadata overhead, peak temporary bytes and process memory
separately; bounded-live-data churn must expose retained holes and reclamation cost.

#### Remaining physical choices

Exact field offsets/alignment, inline thresholds, null packing, growth capacity,
collection chunking and exact address encoding remain Q16/E01/E02 work. Hole reuse,
compaction triggers, headroom, relocation coordination and release to the operating
system remain [Q17](../design/storage-and-maintenance.md#allocation-and-fragmentation-q17).
Separate variable blocks reduce some copying; they do not solve fragmentation.
The later [addressing decision](#relative-block-addressing) accepts segment/offset
locations. No block size, exact address encoding or compaction policy is selected by
the layout decision.

### Relative block addressing

**Accepted on 13 November 2026:** option A as the v1 baseline. Root
locations and owned payload descriptors use relative segment/offset addresses.
Moving a block requires safely updating its owning descriptor and every relevant
physical index locator. There is no universal stable-handle table for every owned
block in the v1 baseline. Public document identity remains its one primary key.

This acceptance preserves root/read protection, shared dictionary lifetime and
coherent index publication. It does not select byte widths, allocation classes,
block sizes or a defragmentation algorithm. No production addressing implementation
or comparative performance result is established by this record.

#### Why addressing is a separate decision

Q16 accepts native segments and record-oriented fixed fields with separate variable
payloads. Growing payloads and eventual compaction can move blocks. The engine must
find their current bytes after a move without changing document identity or leaving
readers with invalid memory. Selecting relative addressing does not select a block
size, allocator, online compactor, compaction budget or persisted binary format.

Existing contracts already provide useful boundaries:

- A root has one public primary key. Non-owning graph references store that key,
  not a physical address. The primary index resolves through the subsequently
  accepted stable root entry to the current local record location.
- An owned mutable block belongs to one document tree. Its parent/collection owns
  the descriptor leading to it; arbitrary shared mutable child graphs are excluded.
- Shared dictionary text has its own accepted StringId lookup and lifetime rules.
  It is not a uniquely owned mutable child and needs separate relocation protection.
- Public native views are short and protected. They cannot outlive protection,
  cross an await or retain an address for later unprotected access.

#### A. Relative locations, updated through owners — accepted for v1

Represent a location conceptually as `(segment, offset)`. Resolve the segment's
current base address, then add the offset for protected local access. Field offsets
within a fixed record remain schema metadata, not separately allocated blocks.

```text
Public primary key -> primary index -> stable root entry -> root location (segment, offset)
Root / owned parent -> payload descriptor (segment, offset) -> payload bytes
Segment identifier -> segment base address
```

If a uniquely owned payload moves, update its parent's descriptor. With the accepted
stable root entry, a root move updates that entry's location and any other structures
that actually retain the old physical location. The primary index's stable root
locator and incoming key references do not change solely because of movement. Secondary indexes must either
resolve through an appropriate stable identity or have their stored locators safely
updated; the concrete index representation remains E04 work. It is not safe to assume
that updating the primary index alone repairs every possible secondary-index design.

The collector/compactor still needs a way to identify live allocations and locate
their owners. Root traversal or owner metadata are candidates, not free operations
or an accepted compaction algorithm. An owner-aware move can update descriptors
without introducing a global lookup table for every owned allocation.

- **Reads:** after reaching an owner, follow its relative descriptor using segment
  resolution. There is no additional per-block handle lookup. This still costs memory
  accesses; it is not equivalent to every field already being in a CPU register.
- **Ordinary writes:** a fixed-field update does not change its address. Growth may
  copy a payload, replace its descriptor and retire the old allocation safely.
- **Movement:** update all actual physical locators while preserving root/index
  publication and reader lifetime. Indexes using the accepted stable root locator
  need no address repair solely because of movement; other physical locators do.
- **Memory:** no universal per-block handle-entry table. Descriptors, segment bases,
  lengths/capacity, allocator and ownership metadata still consume space; total RAM
  savings depend on the concrete representations.
- **Complexity:** efficient direct access, with relocation bookkeeping concentrated
  in the owner/allocator/index code. Ownership must be recoverable without trusting
  a stale pointer to a parent that has itself moved.

#### B. Stable local handles with a location table — not selected as the v1 baseline

Descriptors and eligible indexes store an internal handle. A table resolves that
handle to the block's current relative location. Moving a block changes its table
entry while references to the handle stay unchanged.

```text
Owner -> local block handle -> location-table entry -> segment + offset -> bytes
```

This is an internal implementation choice, not a second public object ID. A compact
table or paged table can implement it; it does not require a CLR Dictionary object
per block or a managed wrapper for every stored value.

- **Reads:** an additional dependent lookup for independently resolved blocks can
  add cache misses. The engine may reuse a resolved location during one protected
  access; it need not re-resolve a handle for every scalar field in that block.
- **Ordinary writes:** fixed-field updates remain similar once the block is resolved.
  Creating/deleting blocks also allocates/retires table entries.
- **Movement:** callers using handles need no physical-locator rewrite. Relocation
  still copies bytes, establishes safe publication and waits for readers that already
  resolved the old location; changing a table entry does not make them safe by itself.
- **Memory:** handle entries, capacity slack and safe reuse metadata add space. A
  smaller reference might offset part of this cost, depending on chosen widths.
  For scale only: one million entries at an assumed 16 bytes each use 16,000,000
  bytes, about 15.3 MiB, before spare capacity. This is not a chosen entry size,
  measured result or estimate of the net difference between A and B.
- **Complexity:** simpler relocation of widely referenced blocks, plus a table
  allocator and safe slot/handle reuse. Neither lookup indirection nor generation
  bits alone solve all lifetime and concurrency issues.

#### Rationale for the accepted model

The decision selects A because uniquely owned payloads have controlled incoming descriptors,
cross-document references already use primary keys, and shared dictionary strings
already have a separate lookup mechanism. These properties reduce the need for an
extra universal indirection layer on every owned block. B becomes more attractive
when many structures retain a block reference and physical moves are frequent.

This decision does not forbid measured internal locators for an index or dictionary.
It chooses the baseline for the arena, rather than promising every internal structure
will use an identical reference format. A change to that baseline remains explicit.

#### Correctness and lifetime requirements

Both alternatives must preserve the existing root gate and safe index acquisition.
A block must not move or be reclaimed while an active reader may dereference its old
address. Resolve/revalidate locations under appropriate protection; primary lookup
races with delete/reinsert and address reuse cannot turn an old lookup into another
document. Exact lock ordering and reuse tracking remain E03 work. No gate is held
across disk/network I/O; preparation and publication must obey that existing contract.

Raw native pointers may be temporary implementation values inside protected access.
They are not persistent keys, public references or portable wire addresses. Logical
replication retains keys and values. Recovery and initial transfer must reconstruct
or translate local locations; accepting A does not settle their binary formats
or promise numerically identical locations across node/process lifetimes.

Shared string relocation follows the string store's protocol, not merely the gate
of one referencing root. Array order, Map/Set uniqueness, full-Save semantics and
the absence of a whole-graph snapshot remain unchanged.

#### Evidence and next engineering check

The existing [segment prototype](../../src/Prototypes/SegmentStorageComparison/README.md)
resolves segment bases and replaces entire segments without concurrent readers.
It does not compare A/B block addressing, ownership repair, secondary-index relocation
or safe reuse. No addressing throughput or memory advantage has been measured.

Validate A with bounded native workloads before implementing production relocation:
root lookup, sequential collection traversal, random owned-block access, growth and
repeated small-block moves. Include variable parent moves, secondary-index locators
and stale-lookup/reuse races. Report total metadata and temporary bytes as well as
read/update latency and move cost. A future A/B experiment must keep equivalent
protection and block grouping; a handle per scalar cannot fairly be compared with
a descriptor per whole collection. Implementing B is not required by this acceptance.

Exact address widths, generation/reuse tracking, ownership metadata and collection
internals remain engineering choices within the accepted contracts. Numerical sizes
and compaction budgets remain open. The later large-payload decision below selects
bounded chunks. The [allocation/compaction decision](allocation-and-compaction.md)
accepts size-class reuse, automatic incremental maintenance and a bounded empty-segment
reserve with release of excess. See [question register](../design/questions.md#current-discussion).

### Large payloads in bounded chunks

**Accepted on 13 November 2026:** option A for v1. Keep small payloads
contiguous; represent large strings and collection storage as bounded chunks.
Select the threshold and chunk sizes from measurements. Directory metadata must
also fit bounded allocations rather than hide one arbitrarily large movable block.
This decision preserves logical collection order, root-document coherence and the
existing owned-copy/protected-view API boundary. It bounds individual relocation
copy size, not the total time or memory required by a complete read or Save.

The later [Binary field decision](schema-and-types.md#binary-fields) also reuses this
contiguous/chunked payload machinery. A large Binary value exposes protected chunks,
not an implicit contiguous span over all its bytes; whole-value owned copying remains
bounded by the ordinary operation/instance budgets.

#### Why large payloads need a separate layout

The compactor can yield between completed moves. A time limit cannot interrupt an
arbitrarily large copy or locator repair halfway through and publish a valid result.
The large-allocation path therefore needs an explicit representation/lifetime policy.
This is Q16 layout work supporting Q17, not a new counted question or a change to
logical document ownership, schema types or replication payloads.

#### A. Small contiguous payloads, large payloads in bounded chunks — accepted for v1

Below a measured threshold, retain the accepted compact allocation. Above it,
store the payload in an ordered collection of separately addressable chunks. Use
relative segment/offset locations and owned descriptors. Moving one data chunk can
repair its descriptor without moving unrelated chunks or the entire document.

```text
Root / owned structure
  length + bounded directory metadata
       -> chunk 0 -> first logical range
       -> chunk 1 -> next logical range
       -> chunk 2 -> final logical range
```

The arrows describe ownership/addressing, not a mandatory linked-list implementation.
Fixed-width elements can use an indexed directory; variable-size structures may
need bounded tree/directory nodes. Exact structures remain engineering work. The
directory itself must not become one unbounded movable allocation. This is local
owned metadata, not the universal per-block stable-handle table rejected in Q16.

- **Sequential reads:** process consecutive chunks while preserving logical order.
  Within a suitable chunk, a tight loop can visit adjacent values. Crossing chunks
  adds boundary and address-resolution work; throughput relative to one contiguous
  buffer must be measured.
- **Random access:** resolve the chunk and position before reading. This can add
  indirection and cache misses. Efficient indexing need not traverse every preceding
  chunk, but its exact cost depends on the selected directory and element layout.
- **Updates/growth:** a small explicit update can affect a limited data chunk.
  Appending to a sequence can add capacity without copying its entire existing
  payload when the chosen collection structure supports it. Insert/remove shifts,
  Map/Set resizing, full replacement and large individual elements still have
  their own costs; no constant-time mutation promise follows from chunking alone.
- **Compaction:** a relocation copies one bounded data unit plus the required
  descriptor/index bookkeeping. This limits copy size and per-move destination
  capacity, not total request latency or the time to acquire protection.
- **Memory:** chunk/directory metadata and partly filled tails add overhead. Local
  growth can avoid a full old/new contiguous payload pair, but complete Save,
  materialization and atomic mutation preparation can still require substantial
  temporary memory.
- **Complexity:** generated/runtime views, collection operations, comparison/hash,
  copying and encoding must handle boundaries. Small payloads keep the simple path;
  the engine owns this representation choice rather than requiring callers to
  configure a storage profile for every field.

##### Logical contracts remain unchanged

List/array order and duplicates are preserved. Map/Set uniqueness and comparison
rules stay unchanged, with no new ordering promise. Chunk layout must support these
abstractions; it does not select their lookup algorithms or element indexes.

Text remains one logical string under the selected UTF-8/UTF-16 database encoding
and field comparison policy. Boundary-aware decoding, comparison and hashing must
handle split encoded values correctly. [Q04.5](strings.md#input-validity-and-preservation)
accepts well-formed Unicode in both modes and rejects malformed input before commit
without substitution; a chunk boundary must not invalidate a valid sequence.
Dictionary strings still share logical entries per database;
chunk ownership and movement belong to the string store's protection/lifetime
protocol, not merely one referencing document's gate.

A protected view need not expose one contiguous span for a whole large payload;
the [accepted array contract](document-model.md#q02.2-must-arrays-preserve-their-supplied-order)
already preserves logical order without promising contiguous physical storage.
The exact .NET access surface remains engineering work. An ordinary owned copy
still materializes independent values; producing a CLR string or complete array
can allocate its full result size even when source storage is chunked.

One logical root gate still protects a coherent owned-document read or mutation.
Reading chunks under separate gates and combining different record states would
violate that contract. A compactor can release protection between independent
physical moves, but a whole-document copy may hold protection much longer. Chunking
does not solve large-read or full-write latency; Q14 limits remain relevant.

Root fixed-field regions, index nodes and other metadata also need compatible
movement bounds or explicit limits. Bounding string/list chunks alone does not
establish a hard maximum pause for every engine operation.

#### B. Dedicated contiguous large allocations — not selected as the v1 baseline

Place a large payload in its own allocation or dedicated segment range rather than
mixing it with many small blocks. While it remains live, ordinary background
compaction leaves that payload in place. Deletion/replacement retires the entire
allocation; it becomes releasable when readers and other retainers have finished.

This is a deliberate whole-allocation lifecycle, not silently abandoning scattered
small-block holes. It can fit the accepted bounded compactor: the compactor does
not need to copy the large payload merely to clear neighboring small allocations.

- **Reads:** one contiguous range permits simpler sequential and indexed access
  for suitable data. It may suit read-mostly numeric workloads, but a measured
  speed advantage is not established.
- **Updates:** fixed-size in-place updates can remain cheap. Growth that cannot fit
  may copy the entire payload into a replacement allocation. Shrinking logical
  length does not return unused capacity unless a separate shrink/replacement
  policy does so; that policy remains open.
- **Compaction:** ordinary maintenance avoids a large relocation pause. Large
  application writes, replacement and copying can still be expensive.
- **Memory:** less chunk-directory overhead; alignment/reserved capacity and
  simultaneous old/new allocations can consume more peak memory during growth.
  Whole-allocation release is useful, but native free and immediate OS resident
  memory reduction remain distinct observations.
- **Complexity:** simpler large-payload access and movement policy; still requires
  capacity limits, safe whole-allocation reclamation and growth/shrink behavior.

#### Selection rationale and evidence boundary

The decision selects A as the general-purpose baseline because it supports incremental
relocation and avoids making every large collection growth a full-payload move.
B is a credible simpler alternative for mostly fixed-size/read-mostly large values.
This is a tradeoff, not a claim that B cannot control memory or that chunks win
every workload. Do not silently add both as user-selectable v1 modes.

The threshold and chunk size are not the accepted 256-byte PostgreSQL saved unit.
Logical replication still sends complete values for Save and explicit partial
effects for partial writes, with no automatic diff. Chunking does not introduce
on-demand loading: a child still loads its full selected state before readiness.

Existing row/column and whole-segment prototypes do not compare these large-payload
paths. No new benchmark is reported. Before choosing numbers, compare equal logical
data with sequential scans, random access, appends, small patches, replacement and
shrink/delete churn. Include native protection, directory maintenance and relocation
locator repair, not just raw copy bandwidth.

Measure live capacity/metadata, partially filled tails, temporary peaks, complete
read/write latency, per-move work and reclamation delay. Cover chunk boundaries,
text decoding/hash equality, List order, Map/Set uniqueness and null/empty values.
Verify independently reconstructed storage and replication preserve logical data.
The representation is accepted. Numerical thresholds/chunk sizes and performance
remain unselected/unmeasured; no production implementation is established here.

### Map and Set lookup layout

**Accepted on 13 November 2026 for v1:** use compact native entries with linear
lookup for small Map/Set values and a native hash lookup structure for larger
values. Choose the conversion threshold from bounded measurements of element count,
key cost and retained/temporary memory. This settles the representation direction
within Q16/E01; collection kinds, uniqueness, equality, null rules and API views
remain unchanged. Exact hash layout, collision handling and sizes are engineering work.

**Layer: resident storage inside the native arena segments.** This decision governs
the database's binary collection representation, including protected access and
internal lookup. Ordinary owned .NET results may use standard `List<T>`,
`Dictionary<TKey, TValue>` and `HashSet<T>`, constructed or filled into reusable
caller-owned destinations with the schema's equality rules. Their contents are
independent of later database changes. The protected no-copy path uses arena views;
it does not return a standard managed collection backed directly by these segments.

The primary index finds a root by type/key. This separate collection lookup finds
a key/member inside one owned root, for example a setting in a user's Map or a
role reference in a Set. It does not introduce an automatic OQL index over elements
across all roots. Primary/secondary index package selections remain unchanged.

#### A. Compact small collections, hashed large collections — accepted for v1

- A small Map stores key/value entries compactly; a small Set stores members
  compactly. Lookup and duplicate detection compare the few entries directly,
  without a separate hash lookup table and its spare capacity.
- A larger collection adds hash-based lookup over native entry storage. Equality
  and hashing follow the accepted field/type rules, including text, floating-point
  special values and reference-key identity. Native addresses are not value identity.
- Fixed-size values and variable-payload descriptors reuse the accepted layout.
  Large data and lookup metadata obey the bounded-block/chunk rules. Choosing a
  hash lookup does not authorize one arbitrarily large movable allocation.
- Conversion/growth must prepare required resources before a committing mutation,
  publish a coherent collection under root protection and retire old storage safely.
  The transient old/new lookup memory is part of admission, not free workspace.
  Exact table layout, collision handling and chunk directory remain engineering work.
- Substantial persistent unused capacity follows accepted bounded background shrink.
  Avoid switching representation repeatedly near one boundary; exact thresholds
  and hysteresis are measured implementation parameters. Layout-only maintenance
  preserves logical values and does not create a new logical root revision.

Small collections avoid hash metadata and can have good locality, but linear
lookup still compares entries and is not automatically faster for every key type.
Large collections avoid scanning every member for ordinary key lookup; collisions,
text cost and chunk addressing still affect performance. Growth/conversion has a
temporary memory and write-latency cost. The engine must implement and verify both
representations through the same generated/runtime views.

#### B. Hash-based lookup for every non-empty Map/Set — not selected for v1

Use one hash representation regardless of collection size. This avoids a compact
lookup path and its conversion boundary, simplifying implementation and testing.
Small collections still pay hash computation, lookup metadata and spare-capacity
costs; it is not automatically faster for a few entries. Large collections have
the same general hash lookup/growth concerns as A. Normal resizing and shrinking
still need temporary memory and coherent publication.

| Cost | A: compact small / hashed large | B: hashed throughout |
| --- | --- | --- |
| Small-collection reads | A short scan; may benefit from locality, must be measured | Hash and probe even for very few entries; must be measured |
| Large-collection reads | Hash lookup rather than full membership scans | Same general lookup approach |
| Writes | Small scans for uniqueness; representation conversion plus ordinary hash growth | One representation, but ordinary hash growth remains |
| Retained memory | Avoids hash lookup metadata/capacity for small collections | Minimum hash overhead in every non-empty collection |
| Peak memory | Account for conversion, resizing and prepared changes | Account for resizing and prepared changes |
| Complexity | Two physical paths and a safe transition | One physical path; fewer transition cases |

The accepted direction has no measured crossover point yet. A small experiment should
compare equal typed data with successful/missing lookups, insert/update/delete,
growth/shrink peaks and preparation before publication, including numerical and
string keys. Exact hash/probe algorithms, dependencies, thresholds and performance
remain unselected/unmeasured; no production collection implementation is established
here. List ordering/storage and shared-string lifetime contracts remain unchanged.
