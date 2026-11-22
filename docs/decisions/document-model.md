# Document model and consistency

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

- [Q01](#q01-what-must-change-together)
- [Q02](#q02-flat-records-or-owned-documents)
- [Q02.1](#q02.1-can-typed-references-support-imperative-graph-algorithms)
- [Q02.2](#q02.2-must-arrays-preserve-their-supplied-order)

## Q01: What must change together?

**Accepted for v1 on 13 November 2026:** ordinary per-root consistency plus an
explicit bounded atomic WriteBatch within one database. Application-side batch
construction acquires no engine locks. Readers remain available during preparation
and PostgreSQL I/O; access to the database is closed only for the complete batch's
protected publication. Conflicting database writes are scheduled while it executes.

Internal storage batching is [accepted separately](write-acknowledgment.md#per-object-coordination-and-internal-group-commit):
independent writes may share a PostgreSQL transaction without shared read atomicity.
That optimization does not grant the explicit application batch contract by itself.

### Atomic WriteBatch within one database

- The caller submits one group of supported mutations across entity types in one
  database. Ordinary [complete-object batch saves use Upsert](mutation-commands.md#atomic-batch-save-with-upsert-semantics):
  create absent objects and fully replace existing ones. Each member belongs to the
  same transaction; adding a Save does not commit it independently. Validate the
  entire group and reserve its preparation/publication resources before commit. A
  definitive validation/admission/rollback failure changes none of the batch's data.
- In Durable mode the journal commits the complete batch in one PostgreSQL transaction.
  Publish all members and affected indexes/revisions so that a reader sees all of the
  batch or none of it, then report success. In MemoryOnly, omit the storage commit/wait while
  retaining the same atomic publication boundary.
- The means of that atomic publication is the engine's to choose (amended on 22 November
  2026, the owner's answer to Q57 of the open questions): an exclusive barrier that
  drains readers is not required. No root/index/database read-write gate spans storage
  I/O, and no gate is held while application code builds the batch.
- Separate reads and root-by-root scans may cross the publication boundary and combine
  observations from before and after it. WriteBatch does not add a multi-object read
  snapshot, interactive read/write transaction or long application-held lock.
- No second full arena or permanent versions are required. Temporary memory includes
  all prepared mutations, full Save replacements, index work and buffers. Limits are
  required; their numerical values and detailed reservation mechanics need measurement.
- Journal replay, saved-state positions/retention/capture and replica application must
  preserve the logical batch boundary or an explicitly proven equivalent protocol.
  A child must not expose a prefix or advance applied progress past a partial batch.
  Master success still does not wait for replica freshness.
- An unknown commit response is not a definite rollback. After durable commit, a local
  publication failure must not expose partial data or masquerade as a rejected batch.
  The accepted [instance-recovery policy](checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure)
  stops service and recovers through close/reopen. Exact failure transitions remain
  Q09.2/E03/E06 work.

Cross-database batches and external transaction participation are excluded from v1.
The [duplicate-identity rule](mutation-commands.md#repeated-roots-inside-an-explicit-writebatch)
accepts one submitted operation per type/key in an explicit WriteBatch; reject the
whole batch before commit if an identity repeats. Exact SDK names and remaining
validation/failure details remain Q13 work. Save is still full replacement without
diff; partial commands retain their existing meaning. [Rationale and alternatives](../design/questions/write-batch.md).

**Added by the owner on 15 November 2026:** a batch has no limit of its own on the number
of roots or on its size. It is bounded by the resources of the instance: a batch that the
memory budget or the journal cannot hold is refused as a whole before it has any effect.
The size of a batch is the application's choice.

**Confirmed for v1 on 12 November 2026:** atomicity and coherent reads
of one logical record, including affected indexes. Q02 defines the logical record
as an owned hierarchical document. The synchronization mechanism and operation
limits remain unresolved.
This remains the ordinary-write baseline; the accepted WriteBatch extends atomic
publication to an explicitly submitted group in one database. Neither contract
claims that a complex document costs the same to commit as one flat tuple.

Example: a record contains Enabled and Limit. A reader sees either the old pair
or the new pair. It never sees the writer halfway through changing the pair.
Two independently submitted ordinary writes do not promise an all-or-nothing
publication unless the application explicitly submits them in the accepted WriteBatch.

An owned record snapshot includes its owned nested values and arrays in their
logical order. It retains reference keys rather than recursively copying the
referenced graph. A later resolution is another record read and may observe a
different commit. Copying a view descriptor is not a record snapshot. Q06 defines
the ordinary ID-result path as an owned copy made under engine-controlled
protection. Q06.1 owns gate details; Q06.2 records the lifetime contract for the
separate public protected-read operation.

Application-held locks or snapshots covering a whole traversal, table or record
set are deferred beyond v1. This does not prohibit short internal synchronization
needed for root access or an atomic batch publication. A consistent copy may outlive that synchronization;
the caller owns the copied arrays and can sort them locally without mutating storage.

### A. One record at a time — accepted ordinary-write baseline

- **Benefit:** publication is bounded to one logical document. Its size and patch
  scope still determine synchronization cost and replica application work.
- **Reads:** one-record reads are consistent. A query reading several records
  can observe different commits; it is not a database-wide point-in-time snapshot.
- **Writes:** potentially lower coordination overhead and shorter reader stalls.
  Exact throughput depends on the chosen synchronization mechanism.
- **Memory/network:** no cross-document staging requirement. A multi-block patch
  inside one document may still require staging. Copying, WAL buffers, checkpoints
  and ordinary replication buffers still exist.
- **Limitation:** rules spanning several records cannot rely on one atomic update.
- **Implementation:** record mutation, index consistency, publication and recovery
  must share one contract. An array/column write alone does not provide this.

### B. An atomic batch inside one database — accepted for v1

- **Benefit:** related records can be published as one batch without changing
  their logical schema into a single record.
- **Reads:** protected access cannot run while only a prefix of the batch is
  installed. Separate reads and root-by-root scans can straddle publication; a
  stable multi-record snapshot remains an additional, unselected contract.
- **Writes:** stage and validate a batch, then publish it. Large batches can
  increase reader/writer waiting and delay subsequent writes.
- **Memory/network:** temporary state grows with batch size; children need
  transaction boundaries and must not expose half an applicable batch.
- **Implementation:** more commit/recovery and failure-path work, but one master
  does not require a distributed consensus protocol to order these batches.

### Separate databases

WriteBatch belongs to one open Lattice database. Transactions spanning independently
opened databases or unrelated application SQL storage are outside v1. No partition
layer is retained to implement the batch boundary.

No percentage speed difference or memory multiplier is known for these choices.
Those require a concurrency prototype after selecting the intended guarantees.
The illustrative consistency concern spans related data that may be stored separately.
The selected ownership model can group those parts into one document. Consistent
publication still needs coordination across its physical parts, as described in
Q02. Independently submitted writes remain separate operations.

## Q02: Flat records or owned documents?

**Accepted on 12 November 2026:** schema-bound hierarchical documents
with owned nested structures and collections. A flat record is the simplest
document. Each mutable child has one owner; cycles and sharing are excluded from
ownership links. Q02.1 adds non-owning key references, which can form cyclic/shared
relationships without changing ownership. Joins remain a separate query feature.
Do not choose the largest possible business object as the atomic unit automatically.

This accepts logical ownership. It does not select row/column placement, managed
or native allocation, a synchronization algorithm or a supported schema grammar.
Separate typed blocks with leaf-level patches remain the proposed physical
direction to validate, not an implemented or measured document engine.

Four decisions must stay separate:

| Decision | Examples | What it does not determine |
| --- | --- | --- |
| Logical ownership | Independent tuples or an owned tree | Whether data is contiguous |
| Physical layout | Rows, columns, segmented child collections | Atomicity or source consistency |
| Publication boundary | One tuple, one document, a batch | Whether the writer received all related changes |
| Memory allocator | Managed arrays or native/mapped segments | Whether values are deduplicated |

### Candidate comparison

| Model | Reads | Writes | Memory and implementation cost |
| --- | --- | --- | --- |
| Flat records with explicit IDs | Good for independent lookups and cross-entity filtering; assembling an object needs indexed lookups or joins | Small independent mutations; coherent changes across records need a batch contract | No duplicated shared entities; keys and relationship indexes cost memory. Simplest initial record engine |
| One immutable serialized blob per document | Convenient whole-document retrieval; projected queries need decoding or auxiliary indexes | Replacing a leaf can reserialize/copy the whole blob in this model | Simple ownership and publication, but old/new blobs may coexist. Poor candidate for large frequently updated documents |
| Owned tree stored in separate typed blocks | Natural traversal and selective projection; indirection still costs CPU; cross-document queries need declared indexes | A leaf patch touches changed fields, index entries and necessary metadata. Collection growth may allocate/copy a chunk | Avoids a CLR object per node and mandatory whole-document copying, but needs child identity, collection storage, reclamation and coherent multi-block publication |

The accepted logical model is an owned hierarchy. The third row describes the
proposed physical implementation to investigate. It is not proven faster than
flat indexed records. The first row/column prototype does not test any of these
ownership or concurrency contracts.

### A document is not a free transaction

An operation changing a root and several children must publish all affected
fields and indexes together. Physically this resembles a multi-record batch even
when the API calls it one document update. WAL recovery and replication must keep
the same boundary; a child cannot expose half the applied patch.

One candidate implementation prepares allocations and validation before a short
publication gate. In-place mutation needs a defined failure/rollback policy.
Whole-document reads can hold a scoped read gate, at the cost of delaying writes,
or use a more complex retry/version mechanism. Long user processing must use an
owned copy. Q06 clarifies that the engine owns normal copy/evaluation protection;
Q06.1 accepts the root-document gate. Its physical implementation and record/index
publication remain open.

Large aggregate reads and hot children contend on a wider logical boundary.
Copying a full document still costs its full size. A copied projection need only
own the selected values. Under Q08, reads start only after the selected state is
available. Locks must never be held during network or disk I/O.

### Source completeness is a separate requirement

Suppose a producer intends to change a root flag and one child value together,
but sends them as two unrelated writes. The database cannot infer the missing
relationship and may validly publish the intermediate state, with either layout.
The producer must submit one operation, or provide a shared revision and a
completion boundary that an adapter can assemble before committing.

A stable read prevents mixing commits during the read; it does not repair an
already committed incomplete business update. Unchanged children need not have
the same timestamp as changed children to form a valid document state.

### Strings and fragmentation are independent of ownership

A shared immutable string dictionary is compatible with owned mutable documents.
String IDs are internal storage references, not user-visible foreign keys or shared
mutable children. Updating one name replaces its ID; it does not mutate bytes
used by another record. Reclamation must wait for every applicable reader.

For N string occurrences, U distinct values, mean encoded length L, ID width H
and dictionary-entry overhead D, a rough comparison is N × (H + L) for plain
storage versus N × H + U × (L + D) for dictionary storage. This assumes comparable
handles and excludes alignment, capacities and indexes. High repetition helps;
mostly unique or short strings can lose. UTF-8 size depends on the actual text.

Variable-length strings and collections can leave holes in both flat and nested
storage. Fixed-size slots or size classes can reuse holes while leaving slack;
compaction is an optional way to recover that slack, not a consequence of using
documents. Stable IDs through a location table can support movement, with extra
indirection and explicit borrowed-view lifetimes. The GC cannot reclaim holes
inside a still-live byte array; native allocation does not remove fragmentation.

### Memory ownership question

Q05 accepts native bulk segments with managed control code. Its comparison uses
the same packed representation in managed and native segments, including measured
segment replacement. Both avoid one CLR object per stored record; row/column
layout and string deduplication are independent decisions. Q05 owns the decision,
alternatives, sources and evidence.

<a id="q02.1-can-typed-references-support-imperative-graph-algorithms"></a>

## Q02.1: Can typed references support imperative graph algorithms?

**Accepted on 12 November 2026:** one primary key per independent
object, typed references by key without referential integrity, and generated
value-type views for local .NET traversal. Computational C# algorithms can traverse
graph/tree structures without becoming DSL queries. The storage engine need not
execute those algorithms or implement joins for this access path.

Q06 clarifies result ownership: ordinary ID resolution materializes an owned
result under engine-controlled protection, or fills a reusable destination.
Generated views also support Q06.2's accepted short protected application reads.
Their references expose keys: finish the current protected operation before
resolving another record. Owned results do not inherit that restriction.

### One primary key is the accepted identity model

Keep one logical primary key per independent object, scoped to
its declared target collection/type. The later [key-type decision](schema-and-types.md#primary-key-types-and-guid-fields)
accepts Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, String and Guid as non-null keys
selected in the schema. A primary index maps this key to
the object's current arena location. Do not require an additional externally
visible object ID merely to support a reference field.

The three concepts have different responsibilities, without implying three IDs:

| Concept | Purpose | Candidate representation |
| --- | --- | --- |
| Primary key | Logical identity used by callers and reference fields | Declared scalar key |
| Object reference | Typed relationship to an independently stored object | Target type from schema plus a key value |
| Record location/view | Access to the currently installed local bytes | Segment/slot/offset and a valid read lifetime |

Conceptual traversal: reference key → target's primary index → local record
location → generated typed view. No DSL planning or join operator is required.
For normal GetById, the engine copies from that view into the owned result before
returning; it does not leave an arena read lock attached to the returned object.
Raw pointers or process-specific addresses must not be persisted as references.
Moving a record can update its index location without rewriting every incoming
key reference. Replicas can use different local locations for the same key.

Each key-based hop still pays for lookup, including key hashing/comparison and
memory access. Large string keys can be expensive for repeated graph traversals;
numeric keys are a useful schema choice when the application controls identity.
Shared text storage can reduce duplicate key bytes, but does not make lookup free.
Resolving a stored string key should not silently materialize a new CLR string
on every hop: encoded-key lookup or another explicit representation is needed to
meet that allocation goal. This is a design requirement to test, not implemented
behavior of an assumed Dictionary<string, ...>.
Dense numeric indexing, generation-checked local handles or a computation-specific
index are possible measured optimizations, not another mandatory public identity.

### Ownership remains separate from connectivity

An independently stored node can remain an owned document: for example, it owns
its fields and outgoing edge collection. An edge's target is a non-owning
reference to another independent document. These reference edges can form cycles
and share targets while the owned-child hierarchy remains acyclic.

This distinction extends Q02. It does not make an entire multi-gigabyte
graph one atomic document. The existing publication boundary remains per
owned document unless separately expanded. For a narrow first version, references
to independent root records are simpler than references to interior fields or
movable collection elements; this restriction has not been selected.

No referential integrity means no required target-existence check, inverse
relationship maintenance, deletion cascade or graph reachability collector.
Key-based references have the following consequences; exact API names remain open:

- An empty nullable reference is distinct from a nonempty key whose target is absent.
- Resolving an absent key fails explicitly, for example through TryResolve; it
  must not access reclaimed memory or fabricate an object.
- Under key-based identity, deletion makes the target absent. Recreating the same
  key makes the reference resolve to the replacement. References to a particular
  incarnation would require a different identity contract, not an assumed property.
- [Q12 defines one database per instance](database-scope.md). A child loads the whole
  database before reads. References resolve by target type and key in that database;
  a missing target is reported as absent, without an implicit network fetch.
- [Q13 accepts immutable primary keys](mutation-commands.md#immutable-primary-keys-accepted-for-v1)
  for a root's lifetime, without a rename operation. Creation under another key and
  deletion of the original are separate writes; references are not retargeted.
  References do not create cross-database links or a wider read transaction.

The absence of foreign-key validation does not remove index correctness, bounds,
schema compatibility or read-lifetime requirements.

### A typed object model without one CLR object per stored node

Generated value views and typed references expose access to arena storage;
NodeView and EdgeView are illustrative names. Enumeration without per-node
allocations is a performance goal, not a measured implementation. These are
explicit views, not arbitrary classes transparently mapped onto native bytes. Struct is a value
type, not a promise that every value lives on the stack; boxing, interface-based
enumeration and helper allocations still need to be avoided and measured.

References that callers retain can contain keys. A resolved view has a narrower
lifetime tied to the arena/read scope, preventing storage from being reclaimed
under it. A ref struct is one enforcement option, with real restrictions on heap
storage, collections and use across await; it is not yet a selected API. See
[the C# ref-struct rules](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct).

Numeric getters can read typed values directly. Text needs a distinct contract:
an encoded view can avoid allocating a CLR string; requesting string performs
materialization unless a compatible immutable string already exists. UTF-16
storage simplifies char access but does not turn an arena slice into a CLR string.
Creating a view copies its descriptor, not the referenced record or graph.

| Application representation | Traversal and memory | Costs and suitable use |
| --- | --- | --- |
| Ordinary CLR node objects with object references | Direct reference traversal after construction; object/collection overhead and GC-managed references | Familiar class API. Full materialization needs construction, preserved shared identity/cycles and extra memory if the arena is also retained |
| Generated value views and key references over the arena | Internal/protected access avoids full object construction; each resolution has lookup/lifetime cost | Accepted. Q06 uses owned results for ordinary ID reads; Q06.2 provides separate short protected application reads |
| An application-owned compact computational representation | Dense arrays/adjacency ranges can improve locality and remove repeated key lookup from hot loops | Build time and additional storage; can project only needed fields. Appropriate when repeated calculations repay conversion cost |

Pooling node classes is not the proposed default. It does not remove per-node
object overhead while objects are in use, and recycled instances cannot safely
remain visible to earlier callers. Pooling may help particular owned-copy workloads
after measurement. CLR allocation and reference tracing are runtime responsibilities;
see [.NET GC fundamentals](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals).

### Computation is a consumer of the storage engine

The accepted boundary keeps storage, indexing, persistence and replication in
Moltaro.Lattice. Algorithms use the typed local API and own their work arrays, queues,
visited sets and temporary results. They do not need to become query operators
or part of the database implementation. Optional graph materialization/adapters
can sit above this API; they are not a second mandatory database or storage copy.

This follows the existing master-only write rule. Local algorithm work memory is
application-owned; mutating it does not implicitly change a read replica. Changes
to persisted records still go through the authoritative writer.

### Computation stability: v1 boundary and deferred locks

Q01 specifies that v1 provides coherent reads/copies at record scope, not
one frozen graph for a whole algorithm. A traversal can observe different commits
at different nodes. Copying records individually during mutation does not turn
them into a global snapshot. Broader snapshots and application-held table/set
locks are deferred to a possible later version, not requirements for reference fields.

An advisory-lock primitive alone would not provide this guarantee. PostgreSQL
advisory locks have application-defined meaning and rely on cooperating users;
see [its advisory-lock documentation](https://www.postgresql.org/docs/current/explicit-locking.html#ADVISORY-LOCKS).
For Moltaro.Lattice, every conflicting writer, replication applier and structural mutation
would need to participate in the chosen protocol. This would be an enforced
engine read/freeze contract, not merely a named application mutex. PostgreSQL's
snapshot isolation is a separate concept; see
[its transaction-isolation documentation](https://www.postgresql.org/docs/current/transaction-iso.html).

| Future approach | Read/computation effect | Write, memory and implementation cost |
| --- | --- | --- |
| Hold a lock while computing in place | Stable protected data without copying it | Delays conflicting writes/applications for the entire computation; requires scope, release/cancellation and lock-order rules |
| Lock only while making an owned copy, then compute | The copy remains stable after release | Copy time still blocks conflicting work; extra memory proportional to copied data; acquiring a coherent multi-record scope is additional work |
| Keep a stable old version while applying updates | Readers can retain older state while writes continue | Version/copy retention and reclamation; requires budgets rather than permanent history |

On a master, a long conflicting lock delays the authoritative writer; a single
ordered writer can also delay later unrelated changes behind that wait. On a child,
a local lock does not freeze the master. It delays the child's application of
affected changes and makes the child's state older. Local queues may retain the
change tail, or the receiver may stop pulling and rely on bounded upstream journal
retention. There is no requirement for an unbounded local RAM queue, and disk
spooling would be additional storage/recovery work rather than a free alternative.

If reception continues while application is paused, pending payload grows roughly
as arrival bytes per second multiplied by pause duration. For example, a hypothetical
20 MiB/s for 15 seconds means 300 MiB of payload before queue/record overhead. This
is arithmetic, not a measured workload. Once unlocked, applying changes while new
ones arrive only reduces the backlog if sustained application capacity exceeds
arrival rate. Queue and journal limits need a defined outcome, such as ending the
freeze or invalidating/resynchronizing a lagging replica; no outcome is selected here.

The pause must respect publication boundaries and an applied sequence position.
Receiving/retaining changes is not the same as applying them. Pausing one table
while applying dependent changes elsewhere can break the promised view; selective
progress requires an explicit independence/order contract. Retention cannot be
replaced by silently dropping or coalescing arbitrary deltas. These costs are why
long application-held locks are not a small v1 extension.

Safe storage lifetime and stable field values remain different guarantees.
Pinning locations alone does not freeze their values. Short synchronization for
one coherent read is still needed; Q06 chooses its implementation. No lock may
remain held during network/disk I/O.

**Decision:** key references and generated value views are accepted; whole-traversal
snapshots and broad computation locks remain beyond v1. The logical read guarantee
is not reopened merely to add graph traversal.

An implementation experiment should compare the same graph and algorithm through CLR
objects, arena-style key/view access and a packed computational representation.
Include construction/startup cost, retained and peak memory, allocations, repeated
traversal and string-key versus numeric-key resolution. Existing row/column and
encoding experiments do not measure this. The logical access contract is accepted;
there is no graph implementation or finalized SDK syntax yet.

<a id="q02.2-must-arrays-preserve-their-supplied-order"></a>

## Q02.2: Must arrays preserve their supplied order?

**Accepted requirement on 12 November 2026:** an array is an ordered sequence.
Its logical contents must be returned in the supplied order, including repeated
values/references. For example, [C, A, C, B] remains [C, A, C, B], not a sorted or
deduplicated sequence. This applies to primitive values, owned elements and keys
in reference arrays, subject to the eventual schema's permitted element types.

- Reads, owned copies, persistence and replica installation preserve element order.
- String deduplication can share immutable text behind equal elements without
  removing array positions or changing their order.
- Indexes maintain separate search structures; their ordering does not reorder
  the stored array. Compaction can move chunks but must preserve logical order.
- Insertions, deletions, replacements or sorting change the sequence only through
  an explicit application mutation. Exact collection-mutation APIs remain open.
- Sorting a caller-owned copy is local work. Sorting stored data is a master
  mutation; a borrowed view does not grant direct write access to a replica.

Order does not require the entire array to occupy one contiguous allocation.
Chunked storage may preserve the same sequence and expose indexed/ordered access.
A Span over the entire array is therefore not promised. Array positions can change
after explicit insertions/deletions; they are not stable object identities.

Within one coherent record read, the array's length and elements must belong to
that same committed record state. An owned snapshot must copy the owned array
contents (or use an equally safe immutable ownership mechanism), not merely copy
an array-view descriptor. Reference-array targets are separate records and retain
their own consistency boundaries. Shape/rank, nested element declarations,
element nullability, capacity and patch granularity remain separate design details.
