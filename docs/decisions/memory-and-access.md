# Memory ownership and .NET access

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md#reader-protection-publication-and-compaction)
now specifies native layout, gates and acquisition order, claims/generations,
publication, borrowed-scope enforcement and initial resource limits. Earlier
open-mechanism notes below record the decision's history; they do not reopen
those specified mechanisms. Implementation and integrated verification remain required.

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

- [Q05](#q05-should-the-arena-use-managed-or-native-segments)
- [Q06](#q06-what-do-id-reads-and-dsl-execution-return)
- [Q06.1](#q06.1-should-one-gate-cover-an-entire-owned-record)
- [Q06.2](#q06.2-should-v1-expose-a-protected-view-to-application-code)
- [Q06.3](#q06.3-can-records-be-used-without-generated-classes)

## Q05: Should the arena use managed or native segments?

**Accepted on 12 November 2026:** use native segments for the main packed
arena, with C#/.NET control code and managed metadata where appropriate. Allocate
large blocks with explicit owners, then suballocate records, collections and
encoded strings inside them. Do not allocate one native block per logical object.
The alternative is the same packed data in managed byte arrays, not a CLR object
graph. Neither choice selects rows versus columns or a persistent binary format.

The main reason for the decision is explicit large-block lifetime and
capacity control. It is not a claim that native bytes are intrinsically smaller,
that every operation is faster, or that a .NET process stops having GC pauses.
Both options still need Q04.4's safe record/string reclamation and Q06's coherent
reads; whole-segment ownership is a separate lifetime layer.

### A. Native bulk segments with managed control code — accepted

- **Memory:** packed payload bytes can be identical to managed segments. The bulk
  allocation is outside the GC heap but remains real process/container memory.
  Live capacity, retired data and temporary allocations must share an explicit
  engine budget. Free returns a block to its allocator; OS working-set reduction
  is not guaranteed. Internal fragmentation still belongs to the arena.
- **Reads:** generated accessors can operate on bounded spans over valid memory.
  No per-field P/Invoke or object marshalling is required. Addresses stay fixed
  until the owner moves/frees a block, but external record references still use
  keys and internal locations use offsets/handles, not persisted pointers.
- **Writes:** ordinary fixed-width changes can reuse allocated bytes. A completed
  safe retirement can release an entire block without waiting for CLR collection.
  Allocation, initialization, copying and the native allocator still have costs;
  record-level updates need not replace their whole segment.
- **Complexity:** every allocation needs an owner and cleanup for failures,
  shutdown and partial construction. Bounds, read lifetimes and concurrent
  disposal must be enforced; a span over a native pointer does not keep its
  allocation alive. Incorrect reclamation can corrupt memory or crash the process.
  Prototype using/Dispose ownership is insufficient proof of a production API.

.NET provides [NativeMemory.AllocZeroed](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory.alloczeroed?view=net-10.0)
and [NativeMemory.Free](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory.free?view=net-10.0).
These are allocation primitives, not a complete arena, persistence layer or
concurrent ownership protocol. This direction does not require a separate C++
service or an external application dependency.

### B. Managed segments of packed bytes or reference-free values

- **Memory:** the same payload can be stored compactly in relatively few arrays.
  Byte arrays do not contain per-element object references for GC to traverse.
  There is no inherent requirement for duplicated strings or per-record CLR
  objects. Large arrays still participate in GC heap allocation and collection.
- **Reads:** safe spans/interior references fit naturally into .NET. Correctly
  retained managed references let GC preserve array lifetime. Long-lived raw
  addresses require pinning; the design must not assume an unpinned array can
  never move. Segment IDs and offsets do not require permanently fixed addresses.
- **Writes:** in-place field updates can also allocate nothing. Replacing/dropping
  a segment makes the old array eligible for collection; the engine does not
  directly control when GC recycles it. Reusing existing capacity reduces churn,
  so the cost depends strongly on the actual allocator and workload.
- **Complexity:** simpler whole-buffer lifetime and fewer unsafe ownership paths.
  The engine still manages holes, StringIds and safe slot reuse inside a live
  array. GC cannot establish record-level snapshot consistency for it.

Microsoft's [Windows LOH documentation](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap)
describes large-array allocation/collection and the distinction between arrays
with and without references. It supports taking this packed alternative seriously;
the Windows experiment below does not establish Linux/container GC behavior.

Both backends can expose typed values without constructing POCOs. Reference-free
value reads from spans are supported by APIs such as
[MemoryMarshal.Read](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.memorymarshal.read?view=net-10.0);
the prototype uses BinaryPrimitives over a declared little-endian layout instead.
Alignment, representation validity and bounds remain the engine's responsibility.
Converting encoded text to a CLR string is a separate materialization cost in
both cases, including when the stored encoding is UTF-16.

### Bounded comparison completed on 12 November 2026

[SegmentStorageComparison](../../src/Prototypes/SegmentStorageComparison/README.md)
uses identical 32-byte records in 4 MiB segments, with shared read/write code.
The final comparison ran managed/native/native/managed in four fresh sequential
processes, each with 256 MiB of payload. Two earlier 16 MiB correctness smokes
also passed. Every final run verified all fields before updates, after updates
and after replacement of every segment, with independent expected checksums.

Environment: Windows x64, Intel Core i9-13900H, .NET 10.0.15, SDK 10.0.404,
workstation GC and tiered compilation disabled for repeatability. Random loops
repeat 131,072 seeded positions; this warms a subset of the data. Each timing is
a per-process sample median of batch averages. Ranges below span the two final
processes per backend, not request-latency percentiles or confidence intervals.

| Observation | Managed segments | Native segments |
| --- | --- | --- |
| Logical arena payload | 256 MiB | 256 MiB |
| Allocate and initialize all records | 48.50–54.39 ms | 44.27–48.45 ms |
| Random four-field read | 25.22–30.76 ns/record | 21.74–24.44 ns/record |
| Sequential two-field scan | 1.74–2.05 ns/record | 1.90–2.17 ns/record |
| Random two-field update | 6.51–9.51 ns/record | 9.72–9.98 ns/record |
| Maximum managed allocation in measured read/scan/update invocations | 0 bytes | 0 bytes |
| Retained managed-heap increase after setup and forced GC | About 256 MiB | Below 0.01 MiB; excludes native payload |
| Process working set after setup | 285.08–285.14 MiB | 284.93–285.14 MiB |
| One full segment replacement pass: allocate, copy, install, release old ownership | 47.91–49.77 ms | 56.88–63.55 ms |
| Managed bytes allocated during replacement pass | About 256 MiB | 0 bytes; excludes 256 MiB allocated natively |
| GC collections during replacement pass, generations 0/1/2 | 0/0/0 | 0/0/0 |
| Process working set after replacement, before forced collection | 541.26–541.32 MiB | 285.05–285.26 MiB |

After replacement the managed heap was about 515.16 MiB versus 3.16 MiB in the
native case, including common helper data. The native payload remains outside
that heap. Process private memory after setup was 270.82–270.85 versus
267.46–267.73 MiB; after replacement, 530.44–530.47 versus 267.54–267.76 MiB.
All memory observations are snapshots, not measured peaks or arena-only overhead.

Native read batches were faster in these final runs, while managed update and
replacement batches were faster. Scans were close. Startup/operation timings
include this access implementation and are not universal allocator rankings.
The nearly identical initial working sets show that moving payload outside GC
does not remove its RAM cost. During replacement, GC did not collect the dropped
arrays, while the native backend explicitly freed each old block. This is a
capacity-churn scenario; it does not mean every document update doubles memory.
Reusing managed segments or different GC pressure can change that result; an
application-level segment pool was not tested in either backend.

Forced collections isolate retained managed storage before/after setup only;
they are excluded from setup timings and absent from the replacement pass.
The experiment does not model concurrent access, safe reclamation, strings,
indexes, multi-gigabyte heaps, server GC, Linux/container limits or request load.
Those remain required follow-up measurements before making production memory or
latency guarantees. The decision favors explicit bulk lifetime control,
with the measured complexity/performance tradeoffs above.

### Memory mapping and the later backing decision

[MemoryMappedFile](https://learn.microsoft.com/en-us/dotnet/standard/io/memory-mapped-files)
provides OS-managed mappings/views and can be accessed from managed code. It may
be useful for checkpoint access or another storage strategy, but mapping does
not provide record transactions, WAL ordering, replication or residency guarantees.
Page faults, flushing and cold-start behavior need their own measurements. Q05
alone does not select a mapped arena or replace the full-load-before-read contract.
[Q16](physical-storage.md#q16-physical-storage-and-layout) subsequently accepts
explicitly allocated live segments with separate persistence for v1. Mapping the
durable database as the primary mutable arena is not selected.

**Decision:** native bulk segments with explicit ownership and
managed C#/.NET control structures for v1. Keep the generated read/copy API and
logical schema independent of process addresses; this does not authorize a
second production backend, runtime allocator switching or a native-language engine.

## Q06: What do ID reads and DSL execution return?

**Clarified on 12 November 2026:** the normal ID-read path protects
access while the engine fills a .NET result, then releases protection before
returning. The result owns its data and is the natural record snapshot. It may
be a newly allocated object/value or a caller-owned reusable destination. DSL
execution protects its actual arena reads and can evaluate fields directly.
No application-held read lock is required while the caller processes a returned
owned result. GetById and ReadInto are illustrative names, not a finalized SDK.

### ID lookup and owned results

The conceptual sequence is: locate and safely acquire the record, read one
coherent state into the destination, end protected access, then return. A
conflicting update cannot change that state halfway through materialization.
The engine must release protection on failure as well as success. The caller
must not observe or concurrently use a destination while it is being filled;
partial destination contents after failure need an explicit API error contract.

The result must own its nested mutable data too. Arrays, List/Map/Set contents
and owned child structures cannot retain unprotected native slices. Strings must
be materialized or safely shared as independently retained immutable values.
A shallow copy of offsets, handles or collection-view descriptors does not meet
this ownership guarantee. Reference fields preserve target keys; they do not
recursively snapshot the referenced graph.

GetById can create an owned result; ReadInto can refill a result/buffer supplied
by the caller. Reusing that destination changes it by the caller's explicit
request. Database updates must never silently mutate a previously returned
object. Editing a caller-owned copy does not modify the database; persistence needs
an explicit write submission. A complete submitted value contains its final state,
not a history of local property assignments.
[Q13 accepts full-document Save and a separate explicit partial-update API](mutation-commands.md#full-document-save-and-explicit-partial-updates-accepted-for-v1),
without automatic change tracking in v1.
[Mutable write input stays unchanged until operation completion](mutation-commands.md#stable-write-inputs-until-completion-accepted-for-v1),
including failure/cancellation completion. Do not reuse or return its buffers early;
the engine cannot depend on that caller-owned mutable input after completion.
Pooling reduces allocation frequency, not the work of copying bytes.
An object and its nested buffers must not be returned to a pool or assigned to
another user while the previous caller still owns them. Exact pool/lease APIs
remain undecided; a pool is not an automatic ownership guarantee.

After the read returns, the application can keep several result objects, follow
keys to other records, await other work and run computations without retaining
the earlier arena gate. Each later record fetch is independently coherent, not
part of a whole-graph snapshot. The application need not manually release its
first result before fetching a second. Scope restrictions belong to the engine's
internal access, not to the lifetime of caller-owned objects.

### DSL execution and result materialization

Parsing and binding the DSL to a schema do not themselves require reading record
payloads; schema-catalog lifetime is a separate concern. The executor protects
its record/index access while evaluating predicates, projections and aggregates.
It need not construct a complete CLR object merely to inspect a numeric field
or compare encoded text. Only the selected output needs to become an owned result.

For a returned record/projection, evaluate its predicate and materialize its
selected values from the same coherent record state. Returning a match from an
old state and unrelated projected values from a later state is not the intended
record contract. An ID-only result may be fetched later and observe a newer
state. A scan can still observe different commits across different records under
Q01; this is not a query-wide snapshot or a default lock on the entire database.

Release internal gates before handing a result to application code, yielding it
from an iterator or waiting for network/disk I/O. Streaming results do not imply
one long lock across the caller's enumeration. Do not recycle a returned row
instance while the caller can retain it; any explicitly reusable result-buffer
API needs its own contract. Large record copies or expensive predicates can
still hold an internal gate for substantial time and require measurement.

### Native storage does not itself require a full object copy

A .NET value can contain copied fields, or it can be a small descriptor pointing
to native storage. Creating the descriptor does not copy its target. For example,
[Span can represent native memory](https://learn.microsoft.com/en-us/dotnet/api/system.span-1?view=net-10.0#memory),
while [MemoryMarshal.Read](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.memorymarshal.read?view=net-10.0)
can read a reference-free value from bytes. Generated views use that distinction
for internal access; full object materialization is a result-ownership choice.

The earlier generated-view direction remains useful. It does not mean ordinary
GetById hands out a native alias whose data can change after return. Q06.2 accepts
a separate public protected-read operation with lifetime and escape restrictions.
Those restrictions are not imposed on owned ID/query results.

### Cost and unresolved implementation

- **Reads:** full ID results pay for copying/materialization; reusable destinations
  can reduce allocation. Internal DSL predicates can avoid copying unneeded data.
- **Writes:** conflicting changes can wait during the engine's actual read/copy,
  not during later application processing of an independent result. A single
  ordered writer can delay subsequent queued writes while it waits.
- **Memory:** result copies coexist with arena data, but ordinary returned results
  do not require historical record generations or arena pins. Independently
  retained checkpoint/index/transfer users still obey Q04.4.
- **Implementation:** safe lookup/acquisition, publication with indexes, gate
  representation, failure cleanup and large-copy limits remain Q06.1 work. This
  clarification does not require copy-on-write record versions or a public lease.

**Clarification recorded:** engine-controlled protection during copying or DSL
execution; owned results remain stable with respect to subsequent database
updates. The accepted logical gate granularity is recorded in Q06.1. The separate
public protected-read operation is accepted in Q06.2.

<a id="q06.1-should-one-gate-cover-an-entire-owned-record"></a>

## Q06.1: Should one gate cover an entire owned record?

**Accepted on 12 November 2026:** one logical reader/writer gate per root document
protects its fields and every mutable block it owns, regardless of their physical
layout. This matches Q01's accepted atomicity boundary. It selects the logical
protection boundary, not a measured optimum or one managed lock instance for
every record.

For example, a root, its scalar block, owned child blocks and an array split
across several chunks form one protected logical record. The copy/query reader
acquires shared access; a mutation of any owned part requires exclusive access.
Multiple readers can overlap. The engine releases read access when its copying
or evaluation completes, before returning an owned result as specified in Q06.

A non-owning reference to another independent root does not bring that target
under this gate. Shared immutable dictionary text is not an owned mutable child
either: the record gate protects the field's StringId, while dictionary lookup,
entry lifetime and any relocation require the string store's own protocol.
Similarly, shared index structures require their own safe traversal/publication
rules; a record gate is not a lock on every index or every segment containing it.

### Why use the record boundary?

- **Correctness:** a copy observes the root, collection lengths, contents and
  owned children from one state. Physical segmentation cannot expose a mixture
  of old and new blocks within that record.
- **Reads:** one logical acquisition can cover all required owned fields. There
  is no need for the caller to acquire locks for each chunk. Shared access still
  incurs synchronization and may wait for an active writer.
- **Writes:** a change to one hot child waits for readers of another part of the
  same root. Large document copies and long array scans broaden contention.
  One writer waiting here may hold up later queued changes. Protecting the whole
  document does not require copying it for each update: fixed-width fields may
  change in place under exclusive access, subject to commit/failure rules.
  **Superseded on 16 November 2026 (owner).** Reads take no exclusion since they are
  protected by epochs, so nothing a reader can reach is changed in place. A partial
  update writes a new root block with the changed fields, shares the unchanged value
  blocks and publishes it as a replacement; the old root block is retired. The owner's
  reason: reads are the first purpose of the library and are not made dearer to make a
  write cheaper.
- **Memory:** no per-block version is required solely to support an ordinary
  copied result. Gate metadata still costs space. A native state word or shared
  stripes are candidates; stripes reduce gate count but can make unrelated
  records contend. No physical representation is selected yet.
- **Complexity:** simpler ownership and lock ordering than independently mutable
  block gates, while retaining Q01's whole-record guarantee.

Independent per-block locks would allow more concurrent work on disjoint parts,
but acquiring and releasing them one at a time can produce a mixed-state copy.
Preserving whole-record consistency then needs a coordinated acquisition set,
a root/version protocol or another mechanism. It adds lock ordering, metadata
and retry/copy work. The extra complexity should follow evidence of unacceptable
record-level contention rather than the fact that storage contains many blocks.

Safe acquisition must cover primary lookup racing with delete/reinsert and slot
reuse. Record and index changes must publish coherently, with an explicit order
for internal gates. Shared text relocation, compaction and reclamation must
respect readers. No gate may remain held across network/disk I/O; any borrowed
native access must end before its backing memory is reclaimed, including after
timeouts. Exact primitives, admission/fairness and pressure behavior remain open.

A focused prototype should cover hot-child updates against whole-record copies,
large owned arrays, unrelated records, mixed readers and one writer, index
publication and delete/reinsert races. Measure throughput, tail latency, gate
memory and apply delay. The single-threaded Q05 allocator measurements do not
measure these concurrency costs. Large, frequently changing aggregates may
motivate a future proposal to revisit their ownership boundary or synchronization;
measurements do not silently change the accepted consistency contract.

**Decision recorded:** one logical gate for the root document and all owned
mutable blocks. For normal ID reads and DSL evaluation, the engine acquires and
releases it internally. Physical lock representation and detailed publication
remain open. Q06.2 separately accepts lending a protected view to caller code.

<a id="q06.2-should-v1-expose-a-protected-view-to-application-code"></a>

## Q06.2: Should v1 expose a protected view to application code?

**Accepted on 12 November 2026: option B.** Normal GetById/ReadInto returns owned
data under Q06. V1 also provides a separate short, synchronous, read-only operation
over a protected arena view, subject to the restrictions below. It does not
reopen the accepted root-document gate or require a whole-graph snapshot.

Retain the ordinary owned-result path alongside this direct access. This serves
the requested generated-view traversal and large-array computations without
requiring full document copies for every operation. Its value and overhead need
measurement; the accepted contract is not a throughput guarantee or a finalized
SDK signature.

### Option A: owned results only in the public v1 API — not selected

GetById creates a result; ReadInto fills caller-owned storage. Generated arena
views remain internal to the engine and DSL. Application graph algorithms use
keys and owned values, or build their own computational representation.

- **Reads:** copying and materialization remain part of each requested result.
  Reusable destinations reduce allocation but do not remove copying. Reading a
  small part of a large document is costly if the API requests the whole document;
  DSL projections can select data, but do not replace arbitrary C# algorithms.
- **Writes:** the root gate lasts through engine copying/evaluation and is released
  before the caller's computation. Application processing cannot prolong this
  read lock, although a large copy can already be expensive.
- **Memory:** returned data coexists with arena data. An algorithm retaining many
  full records can add substantial memory; bounded buffer reuse limits retained
  copies but cannot preserve all previous results in the same reused buffer.
- **Complexity:** fewer public lifetime and reentrancy rules. This is the simpler
  first release, at the cost of postponing direct application traversal through
  the accepted generated arena views.

### Option B: owned results plus a separate short protected view — accepted

The engine acquires the root's shared gate, calls a synchronous application
operation with a read-only generated view, and releases protection when that
operation returns or throws. The operation may read owned fields and collections
directly and return a scalar or independently owned result. A sum over a numeric
array, for example, need not first materialize that array in a CLR collection.

The following are accepted obligations for this additional API, not restrictions
on existing owned results:

- Views, encoded-text slices and native references cannot escape the protected
  operation. Copying a view descriptor does not extend its validity. No writable
  references to replica or master storage are exposed through this read API.
- The operation must not await, yield, perform I/O or wait on external work.
  Normal GetById/ReadInto results remain suitable for such later application work.
- No nested database reads or writes while the view is active in the accepted
  v1 API. Reference fields expose keys; copy the next key or a bounded group of
  keys, end this operation, then resolve another record. Graph traversal remains
  possible, but is not a chain of simultaneously retained protected views.
- No global graph snapshot is implied. Each subsequent record access can observe
  a different commit, just as with ordinary independent ID reads.
- User code can still take arbitrarily long even without await. Exception cleanup
  releases the gate when code unwinds; a timeout cannot safely revoke a view or
  free its memory while that code is executing. This API cannot promise a hard
  maximum writer delay.

These restrictions require an explicit SDK contract and verification. A ref
struct is a candidate for preventing heap storage and some escapes, but is not
by itself a proof of native allocation lifetime, gate ownership or bounded
execution time. See the [C# ref-struct rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct)
and [.NET buffer ownership and consumption guidelines](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines).
Exact signatures, callback/state allocation and reentrancy enforcement require
a focused prototype before the API is finalized.

- **Reads:** avoids full result construction when the algorithm reads directly
  from the view. It still pays for lookup, synchronization, getters and the
  algorithm itself. Converting encoded text to string or keeping collections
  beyond the operation can still allocate/copy. Segmented storage does not
  promise one contiguous Span for every stored array.
- **Writes:** the root remains protected for the entire application operation.
  Inspecting a few fields may hold it for less time than a full copy; a long
  calculation may hold it for much longer and delay conflicting mutations and their dependent work.
- **Memory:** no full document copy is required for this access. Per-operation
  bookkeeping and selected outputs still cost memory; delayed reclamation can
  retain other storage. This does not require permanent record versions.
- **Complexity:** a second explicit access contract, generated public views,
  lifetime/reentrancy checks and focused misuse/concurrency tests. The existing
  record gate is reused; no new distributed lock protocol is required.

Neither option changes network traffic for a ready local replica. Delayed local
application of updates can increase replication lag and pressure on its bounded
queues; the API must not assume unlimited buffering. Both options keep unrelated
reference targets outside the root's ownership boundary.

A useful comparison exercises small scalar reads, large owned arrays and
key-based traversal with concurrent updates. Measure materialization/allocation,
total traversal time and writer tail latency. A sum alone can have little lock-time
advantage if both copying and summing scan the entire array; user code and data
shape determine the outcome. The earlier allocator prototype cannot answer this.

**Decision recorded:** ordinary owned copies plus a separate short protected
read-only operation in v1, with the lifetime and execution restrictions above.
This preserves direct access for computational C# code and owned copies for
long processing. It does not authorize a long-lived graph lock. Physical gates,
API signatures and enforcement remain implementation work to verify.

<a id="q06.3-can-records-be-used-without-generated-classes"></a>

## Q06.3: Can records be used without generated classes?

**Required for v1 on 12 November 2026.** Applications must be able to
discover fields from the schema and access records without generating a domain
C# class. This includes direct protected reads without materializing the entire
record. The physical storage and Q06.2 lifetime contract remain shared with
generated views and OQL; this is another access surface over the same arena.

A runtime RecordView with typed field handles is the proposed API direction,
not a finalized SDK. Illustrative usage:

```csharp
var amountField = schema.BindField<decimal>("Invoice", "Amount");

database.Read("Invoice", id, record =>
{
    decimal amount = record.Get(amountField);
    // Perform short synchronous work with the scalar value.
});
```

No Invoice class is needed. Field discovery can happen at runtime; binding verifies
identity and type before repeated use. Field handles must obey Q03.9's generation
rules. Exact type names, signatures and cancellation behavior remain design work.

- **Reads and allocations:** scalar getters return values, while nested objects,
  strings and collections can be borrowed views within the protected operation.
  This avoids constructing a CLR object graph or Dictionary<string, object?> for
  each record. It does not mean that loading a scalar copies no bytes at all.
  Requesting an owned CLR string or materialized collection can allocate/copy;
  UTF-8 storage is not an existing CLR UTF-16 string. No zero-allocation benchmark
  is claimed for the eventual implementation.
- **Performance:** runtime descriptor/type dispatch can cost more than a generated
  specialized accessor. Resolve names once and reuse typed handles where practical;
  a C# dynamic binder, per-value boxing or reflection is not required by this model.
- **Lifetimes:** the view cannot escape, cross await/yield, hold locks during I/O
  or make nested database calls. Collection enumeration stays within its owner's
  protected access. Reference fields expose keys; following a reference requires
  another read after the current protected operation ends. Long calculations still
  use owned values or a separate application representation.
- **Writes:** runtime schema access does not grant direct mutation of native memory.
  Class-free mutations must use schema-bound commands through the same master writer,
  validation and publication rules. The mutation API is still to be designed;
  arbitrary property setters on borrowed views are not implied.

Generated domain accessors are an optional ergonomic/optimization layer. Runtime
views are mandatory even when no generator is used. A long-lived persistent object
that transparently holds mutable native storage is not part of this requirement.
Whether the convenience surface eventually uses an indexer or C# dynamic is separate
from the required class-free capability and must not replace typed efficient access.

**Decision recorded:** class-free schema-driven access is required in v1, including
the existing protected read path without a whole-document copy. The exact public
API and its overhead must be verified before implementation is treated as complete.
