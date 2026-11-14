# Query contracts

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

**First production milestone clarification, 14 November 2026:** OQL is excluded
from that milestone. Secondary indexes and searching by one or several indexed
fields remain required. [Explicit declared-index access](index-access.md) is accepted:
callers select an index from the schema, with no automatic selection or silent scan
fallback in this API. The OQL contracts below remain broader design decisions.
Their language-specific delivery is deferred; shared comparison, ordering, atomicity
and memory/lifetime rules still govern the first milestone's indexed operations.

- [Q07](#q07-how-should-filtering-ordering-and-indexes-work-together)
- [Q07.1](#q07.1-should-queries-run-without-a-suitable-index)
- [Q07.1.1](#q07.1.1-should-query-execution-have-resource-limits)
- [Q07.2](#q07.2-what-can-a-v1-query-return)
- [Q07.2.1](#q07.2.1-should-oql-filter-inside-owned-collections)
- [Q07.3.1](#q07.3.1-how-should-null-affect-query-predicates)
- [Q07.3.2](#q07.3.2-where-should-null-appear-in-ordered-results)
- [Q07.3.3](#q07.3.3-how-should-equal-sort-keys-be-ordered)
- [Q07.3.4](#q07.3.4-how-should-v1-limit-and-page-results)
- [Q07.3.5](#q07.3.5-what-may-a-traversal-observe-while-records-change)
- [Q07.3.6 (partially accepted)](#q07.3.6-remaining-comparison-and-operator-rules)

## Q07: How should filtering, ordering and indexes work together?

**Accepted on 12 November 2026: option B.** V1 supports filtering and ordering by
one or several fields, with single-field and composite indexes explicitly
declared in the schema. The later [primary-index decision](index-structures.md#primary-key-index-hash-table)
selects a hash table and excludes the standard .NET Dictionary from its production
implementation. Arc.Collections UnorderedMapSlim is selected for the first primary
index; Arc OrderedMultiMap is accepted for v1 ordered secondary indexes, including
its measured memory tradeoff. Native key/lifetime integration and detailed query
semantics remain open. Filtering by several fields does not require a composite index,
and sorting is possible without a matching index; Q07.1 accepts scan fallback and
Q07.1.1 accepts configurable resource limits. Primary-key lookup remains part of
the accepted identity model.

### Three separate concepts

| Concept | Meaning | Illustrative request |
| --- | --- | --- |
| Filtering | Decide which documents match | GroupId equals 7 and Priority is at least 10 |
| Ordering | Arrange the matching results | Priority ascending, then Name ascending for equal priorities |
| Index | Maintain an auxiliary search structure to avoid some scanning/sorting | An ordered index on GroupId, Priority, Name |

The accepted v1 query surface supports filters on one or several fields and
ordering on one or several fields. An optional result limit, equality, ranges
and combined conditions are intended DSL building blocks. Q07.3.1 accepts null
predicate behavior; Q07.3.2 accepts null ordering. Remaining operators and non-null
comparison rules stay Q07.3 work. Q07.2 accepts the result boundary;
exact syntax is not selected yet.
Field paths may address a scalar inside an owned nested structure. Crossing a
reference to another root in a database query belongs to v2 under Q07.2; it is
not an implied indexed path. Q07.2.1 accepts owned-collection predicates.

Multi-field ordering is lexicographic: compare the first field, then the next
only if the preceding fields compare equal. It does not mean running independent
sorts in an arbitrary order. Ordering the query result never reorders stored
documents or their owned List/array values. Q07.3.3 leaves ties across all requested
fields unspecified; callers add the primary key for unique ordering. Q07.3.4
accepts Limit/Offset in v1 and defers generic continuation to v2. No hidden global
snapshot is introduced.

Illustrative notation only, not a selected SQL parser or DSL grammar:

```text
Filter: GroupId = 7 AND Priority >= 10
Order:  Priority ASC, Name ASC
Limit:  20
```

### One field versus a composite search key

A secondary index locates records using values other than their primary key.
The representation stores search keys and compact record locators, not full document
copies. [Accepted key ownership](index-structures.md#search-key-ownership-copied-fixed-values-and-shared-text)
copies fixed-size values into keys and shares immutable stored text through protected
stable references. Exact descriptors and locator safety remain physical engineering
work. Several documents may have the same secondary key by default. The later
[Unique decision](unique-indexes.md) adds explicit secondary uniqueness without
changing primary-key identity or the selected index container.

For the example above:

- With no suitable secondary index, the engine can scan documents, filter them,
  then order matching results. A bounded top-k algorithm may avoid retaining all
  matches when a limit is present, but still may need to inspect every document.
- With a single-field index on GroupId, the engine can find that group's records,
  check Priority, then order the survivors by Priority and Name.
- With a matching ordered composite index on (GroupId, Priority, Name), equality
  on GroupId selects a group and the Priority bound selects a range. Inside that
  group the index already has the requested Priority/Name order. It can avoid a
  separate sort when comparison rules and the execution protocol are compatible.

The field sequence matters. (GroupId, Priority, Name) is not interchangeable with
(Name, Priority, GroupId). Without a restriction on GroupId, the former does not
provide global Priority/Name order across all groups. Separate indexes on GroupId
and Priority also do not constitute the same composite ordering. Combining their
candidate sets is another execution strategy with its own cost.

Ordered indexes can serve both equality and range predicates. A separate equality
index is not automatically required on every ordered field. Hash-based lookup is
an alternative for equality-focused access, not a range/sort replacement. These
capability distinctions are illustrated in the official descriptions of
[index types](https://www.postgresql.org/docs/current/indexes-types.html),
[multicolumn indexes](https://www.postgresql.org/docs/current/indexes-multicolumn.html)
and [ordered retrieval](https://www.postgresql.org/docs/current/indexes-ordering.html).
They are algorithm references, not runtime dependencies or measurements of Moltaro.Lattice.

### Alternatives considered for the first version

Both options below include multi-field filtering and ordering. The difference
is which combinations the engine can accelerate with a declared secondary index.
Both retain the primary-key index and make secondary indexes explicit schema
choices, not automatic copies of every field or every field combination.

| Effect | A: single-field ordered secondary indexes only — not selected | B: single-field and composite ordered secondary indexes — accepted |
| --- | --- | --- |
| Reads | Can accelerate a useful individual condition/order; combined requests may process many candidates and sort | A matching composite index can reduce candidates and remove a separate sort; unmatched queries still need work |
| Writes | Maintain declared single-field indexes on insert/delete or indexed-value changes | Maintain the declared composite keys too; changing one component can relocate an entry |
| Memory | Keys, locators and index metadata, plus query workspace | Composite keys are wider than individual keys, but one useful composite can replace several indexes; total memory depends on declarations |
| Implementation | Fewer key-comparison and planning cases | Tuple comparisons, prefix/range bounds and matching sort order add work and tests |
| Startup | Build selected single-field indexes locally before readiness | Also build composite indexes locally; actual extra time depends on their number and size |

**Rationale for option B.** Combined filters and ordered results are useful for
generic configuration and reference-data workloads. Supporting a small explicit
set of composite indexes avoids making an important class of these requests rely
on large scans/sorts. Single-field keys can share tuple-key machinery with the
composite case, but that does not make correct comparisons and concurrent index
traversal free. No measured speedup or fixed memory multiplier is claimed.

The capability decision does not choose a B-tree, skip list or sorted array for
secondary indexes. The [primary hash-table contract](index-structures.md) is accepted;
ArcSlim is selected for the first implementation with engine-owned capacity
reservation; production integration still needs validation. The later
[secondary decision](index-structures.md#secondary-index-arc-orderedmultimap)
accepts Arc OrderedMultiMap for pre-commit node preparation, including its measured
larger retained footprint than Kaos RankedMap.
Secondary structures have different update, locality and allocation costs. A specialized
equality-only secondary implementation is an optimization candidate to measure,
not a mandatory second family for every declared ordered index.

### Costs and boundaries that remain explicit

An index helps most when it avoids substantial work. A condition matching most
records may benefit little; a sequential scan can be cheaper than indirect
lookups. Returning many full documents still costs materialization and result
memory. Limit caps the returned count, not necessarily scanned records, elapsed
time or temporary memory. Memory/work budgets and behavior without a suitable
index are separated: Q07.1 accepts scanning, while Q07.1.1 accepts configurable
execution-time and temporary-memory limits. Numerical defaults and exact accounting
still need to be established before implementation.

The writer and replica applier must maintain affected index entries within Q01's
record publication contract. Changing an unindexed field does not inherently
require rebuilding all indexes. More indexes consume memory on each replica and
increase mutation/startup work; they do not by themselves require another
network dependency. [Q19](index-lifecycle.md) accepts rebuilding physical indexes
locally on every node before readiness; v1 does not persist or transfer their images.

Indexes cannot bypass record gates or the string-store lifetime rules. The query
executor must coordinate candidate enumeration with mutations, deletion and slot
reuse. It must keep predicate values, projected values and any saved sort keys
consistent within each returned record. Q07.3.5 accepts live traversal without
repeated primary-key identities and with completeness for unchanged matches in a
successful full pass. Moving records may change membership. Q07.3.4 accepts
Limit/Offset without a database-wide snapshot. Q06.2's public callback restrictions
do not grant an application permission to retain internal index cursors across calls.

String comparisons must follow the field policy; StringId order is not text
order. Ordered Float32/Float64, Decimal, nullable and nested fields need defined rules
before implementation. Composite indexing here means one scalar tuple per root;
array/Map/Set membership indexes, expressions and covering data remain outside the
first milestone. [Explicit secondary uniqueness](unique-indexes.md) is now accepted
for that milestone. Cross-document joins belong to v2 under Q07.2.

Prototype candidate structures with identical records, key distributions and
declared indexes. Compare a single-field plan plus residual filtering/sorting
with a matching composite plan for the same result. Measure retained/peak bytes,
build time, result allocations, query latency and update latency under concurrent
readers. Include hot updates and conditions matching few versus many records.
Earlier arena/layout benchmarks do not measure these costs.

**Decision recorded:** filtering and ordering by one or several fields in v1,
with single-field and composite indexes explicitly declared in the schema.
Ordered index access is the capability direction, not a selected data structure.
Q07.1 accepts the scan policy and Q07.1.1 accepts configurable resource limits.
Q07.2 accepts the result boundary and Q07.2.1 accepts owned-collection predicates.
Detailed language/comparison semantics and physical index selection follow separately.

<a id="q07.1-should-queries-run-without-a-suitable-index"></a>

## Q07.1: Should queries run without a suitable index?

**Accepted on 12 November 2026.** Provide enumeration of entity
collections from .NET for application-defined searches. OQL falls back to scanning
when it cannot find a suitable index. Permit a per-query option to disallow that
fallback; scans are allowed by default. OQL is a working language name, not a
commitment to an external standard or a finalized grammar.

### .NET enumeration

Every root entity collection in the node's loaded database must be enumerable
without requiring an index on the fields inspected by application code. Owned
nested collections retain their existing List/Map/Set traversal contracts.
Enumeration is local and does not require knowledge of all IDs
in advance. A .NET predicate can implement logic outside the OQL language; automatic
translation of arbitrary delegates or expression trees is not required in v1.

Use the existing owned-copy contract for records returned to an ordinary caller
loop. Allocation-conscious traversal can evaluate one record through a short
protected operation, including Q06.3's class-free view, without materializing every
candidate. A borrowed native view cannot be yielded out of its protected lifetime.
Do not keep a record/collection lock across arbitrary consumer processing or I/O.
Exact iterator, visitor, batching and reuse signatures remain open.

Each inspected record is coherent under the accepted root-document gate. Q07.3.5
accepts live traversal without repeated primary-key identities in one pass. Unchanged
matching records must be included in a successful full pass; concurrent membership
and values are not frozen. Q07.3.4 accepts Limit/Offset without a cross-call snapshot.
No collection enumeration order is promised without an explicit query order.

### OQL planning and the opt-out

If a suitable index is available, use it to produce candidates and apply residual
predicates as needed. Otherwise scan the selected collection and evaluate the
predicate against protected stored values. Apply the requested ordering even if
it requires a separate sort. An index is an optimization, not a prerequisite for
expressing an otherwise supported query.

An illustrative AllowScan=false query option forbids falling back to a full
collection scan. If that fallback is required, reject the plan explicitly before
starting it; do not silently ignore the option or return an empty successful result.
The exact option name and plan classification remain design work. Checking residual
conditions on index candidates is not automatically a forbidden full scan, and
prohibiting scans does not by itself prohibit a separate sort or guarantee low cost.

Scanning does not require a full collection copy: evaluate records incrementally
and materialize only required results. Retaining results and sorting can still use
substantial memory. Application code can also consume owned records one at a time;
collecting them all is the caller's decision.

Limit bounds the result count, not necessarily the records inspected. Scanning
and sorting are distinct operations, as illustrated in
[SQLite's query-planning explanation](https://www.sqlite.org/queryplanner.html).
The Moltaro.Lattice proposal is not a dependency on that engine or a measured performance
claim. No automatic indexes, disk-spill implementation or numerical resource
limits are selected by this scan decision; Q07.1.1 separately accepts configurable
budgets without fixing their numerical defaults.

**Decision recorded:** .NET enumeration for application-defined searches and
automatic OQL scan fallback, with an explicit per-query opt-out. Q07.1.1 separately
accepts configurable resource limits; scans do not imply unlimited CPU or memory use.

<a id="q07.1.1-should-query-execution-have-resource-limits"></a>

## Q07.1.1: Should query execution have resource limits?

**Accepted on 12 November 2026: option A.** Use configurable execution-time and
temporary-memory limits, with database defaults and explicit per-query overrides.
Exceeding a limit produces an explicit failure. Q07.1's scan
fallback remains accepted; this question controls resource use, not whether scans
are supported. No numerical defaults or SDK option names are selected yet.

### Why a result limit is insufficient

Consider finding the first 20 records after filtering and ordering a large
collection without a suitable index. Returning 20 records can still require
inspecting the whole collection. A top-k plan may retain fewer sort candidates
than a full sort, but it does not automatically avoid that inspection. Conversely,
an incremental filter can inspect many records with little temporary memory.
Execution time and working memory therefore need separate consideration.

| Effect | A. Configurable limits — accepted | B. Caller cancellation only — not selected |
| --- | --- | --- |
| Reads | Check cancellation/deadline and account for working buffers; expensive queries can fail explicitly | Fewer budget checks; execution continues until completion, cancellation or another failure |
| Writes and replica application | Write protocol unchanged; stopping expensive reads can reduce resource contention | Write protocol unchanged; long queries can keep competing for CPU, bandwidth and record access |
| Memory | Bound accounted query workspace; no up-front allocation of the entire budget | Workspace can grow with the chosen plan and result buffering |
| Implementation | Budget ownership, checks, cleanup and failure reasons need implementation | Simpler executor accounting, more resource control delegated to the application |
| Caller control | Defaults for ordinary queries; explicit larger or smaller limits for known workloads | The caller decides when to cancel; cancellation alone does not track query workspace |

Neither choice changes stored record sizes, permanent index sizes or the
master-authority contract. No latency or throughput ratio has been measured.

### A. Configurable execution budgets — accepted

The database configuration supplies defaults. A query can explicitly override
them, for example to allow a longer analytical scan or a larger sort. Exact
configuration syntax and any host-enforced maximum remain separate API details.
The two accepted limits are:

- **Execution time:** a cooperative deadline for engine query execution, including
  its waits, not just CPU time. Check it at safe progress points in scans, sorting,
  materialization and waits where supported. A deadline requests stopping; it is
  not a hard real-time guarantee that all resources are released at that instant.
- **Temporary memory:** account for engine-controlled execution buffers such as
  candidate lists, retained sort keys, sorting workspace and result buffers still
  owned by the executor. Sum simultaneously live buffers across operators in one
  query; do not grant each operator a separate copy of the full query allowance.
  Check before growing workspace, accounting for buffer capacity rather than only
  the number of filled entries. The exact managed/native allocator and pool
  accounting requires implementation verification.

The temporary-memory allowance excludes the stored arena and permanent indexes.
It also cannot bound objects already handed to and retained by application code.
A high configured allowance does not itself reserve that amount of RAM. Multiple
queries can consume their allowances concurrently, and pools/runtime overhead add
memory: this is not a total process or container memory cap. Aggregate query
admission and pool budgets remain separate work, not implied by this choice.

On exhaustion, stop at a safe point, release query-owned resources and report the
reason. Timeout, memory exhaustion under the configured budget and caller-requested
cancellation should remain distinguishable. Do not report an empty success or
silently label a truncated result complete. If a future streaming API has already
delivered records, it cannot retract them; terminal failure must remain observable.
This does not require buffering every query result before delivery or adding a
whole-query snapshot.

The v1 response is failure at the limit; it does not add automatic
spill-to-disk sorting, a different query retry or hidden expansion of the budget.
The caller can explicitly change the budget, reduce the result/projection, change
the request or add a suitable schema index.

Cancellation must preserve native memory and gate lifetimes. A caller's long-running
protected callback cannot be forcibly revoked while it still accesses the arena;
the executor regains control when that callback returns. Likewise, an arbitrary
.NET loop processing owned copies is application code, not an engine-controlled
query whose allocations and duration can be capped. This follows the existing
Q06.2 contract and .NET's
[cooperative cancellation model](https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads).

### B. Caller cancellation without engine budgets — not selected

Expose cancellation but do not track an execution deadline or temporary-memory
allowance inside the engine. The application can arrange a timeout that requests
cancellation. The executor still needs safe cancellation points and cleanup; those
obligations do not disappear when resource accounting is omitted.

This reduces accounting work and makes unrestricted in-process calculations easy
to invoke. However, the caller may not know the memory required by a sort or plan,
and a deadline alone does not prevent rapid allocation before cancellation is
observed. OS/process failure is not a controlled query-memory policy. This option
does not inherently make scans or sorting algorithmically faster.

**Decision recorded:** v1 queries have execution-time and temporary-memory limits
with database defaults, per-query overrides and explicit failure on exhaustion.
Choose numerical defaults after representative measurements; exact accounting,
configuration/API shape and public error types still need design and focused
verification. This is an accepted contract, not an implemented or benchmarked executor.

<a id="q07.2-what-can-a-v1-query-return"></a>

## Q07.2: What can a v1 query return?

**Accepted on 12 November 2026.** V1 supports whole-document results
and selection of fields from one document per result. Joins, combined results
from independent documents and computed result fields are deferred to v2. This
boundary applies to both OQL and the database selection API, not just text syntax.

An illustrative request, not a finalized grammar:

```text
Collection: Orders
Filter:     Status = @status
Fields:     Id, CustomerName
Order:      CreatedAt DESC
```

Each result's Id and CustomerName come from the same source document. No generated
result class or particular CLR ValueTuple representation is required. A runtime
result schema can describe the selected fields; exact owned-result and typed SDK
shapes remain design work. Predicates and ordering may inspect fields that are
not returned, so projection does not make those inputs unnecessary.

Field selection can reduce copied data and result allocations compared with
returning the entire source document. It does not make a scan or sort disappear.
The compiler/executor needs a projection description, but this does not add joins,
new persisted fields or automatically maintained indexes. No measured speedup or
allocation ratio is claimed.

Selected values follow the existing lifetime and record-coherence contracts.
Ordinary returned data must own its contents; a selected collection cannot expose
a borrowed arena descriptor after protection ends. Returning a reference field
returns its key, not an implicit fetch of the referenced document. Selecting fields
inside an owned nested object stays within the root's ownership boundary; flattening
collections into multiple result rows or arbitrary computed output is not implied.

A result combining an Order document's fields with a referenced Customer document's
fields is a cross-document result and belongs to v2, even if exposed as a C# tuple
or a dynamic result object. Runtime record access does not make that selection free:
it needs source lookup, result assembly and concurrency semantics across roots.

Application code remains free to read documents, follow keys and construct its own
tuples or computational graph. Such code uses the existing individual-record
contracts; it does not gain a database join executor or a multi-record snapshot.
Computed output fields are separate from evaluating supported query predicates;
the exact scalar predicate operators remain Q07.3 work.

**Decision recorded:** v1 returns documents or selected fields from one document;
database-provided joins, combined cross-document results and computed result fields
belong to v2. Q07.2.1 separately accepts owned-collection predicates.

<a id="q07.2.1-should-oql-filter-inside-owned-collections"></a>

## Q07.2.1: Should OQL filter inside owned collections?

**Accepted on 12 November 2026: option A.** OQL v1 supports Any, Contains,
ContainsKey and Map-entry predicates inside a root document's owned collections,
without automatic indexes on elements. These predicates select roots based on
their own contents under the accepted gate; they do not cross a non-owning
reference, join independent documents or return a combined tuple.

### A. Owned-collection predicates in OQL — accepted

Accepted capabilities, with illustrative notation rather than a selected grammar:

| Predicate | Meaning | Example |
| --- | --- | --- |
| Any over a List/array | At least one element satisfies its predicate | An order has a line with Quantity above 5 and UnitPrice below a parameter |
| Contains over a scalar List/array or Set | The collection contains an equal scalar value | Tags contains a supplied tag |
| ContainsKey over a Map | The map contains the supplied key | Settings contains the key RetryLimit |
| Any over Map entries | One entry's key and value satisfy the predicate | A settings entry has a selected key and an eligible value |

Only schema-supported element/key/value types are involved. Comparisons obey the
selected type and field/collection equality rules; this decision does not settle
the remaining Map/Set type declarations. Q07.3.1 accepts predicate null behavior.
Owned nested objects can be inspected; a reference element can be compared by key but is not
implicitly followed to its target.

For example, consider two lines in one order:

```text
Line A: Quantity = 10, UnitPrice = 100
Line B: Quantity = 1,  UnitPrice = 5
```

Any(line => line.Quantity > 5 AND line.UnitPrice < 10) is false: no single line
satisfies both conditions. Two independent Any predicates could be satisfied by
different lines and express a different request. Keep the element scope explicit.
This same-element distinction also appears in
[MongoDB's elemMatch documentation](https://www.mongodb.com/docs/manual/reference/operator/query/elemmatch/);
it is a semantic reference, not a syntax, schema model or runtime dependency.

Any can finish at the first match, and membership tests need not materialize the
entire collection. Multiple matching elements still select the parent document
once in a stable dataset; collection matches do not expand it into multiple rows.
This does not settle concurrent root enumeration guarantees in Q07.3.

- **Reads:** execute predicates directly over protected collection storage. Work
  can grow with the total number of examined elements, not just the root count.
  A scalar root index can narrow candidates before checking each owned collection.
- **Writes:** no additional index maintenance is implied. A long collection scan
  holds the root's read protection longer and can delay an update to that root;
  engine checks must respect the accepted cancellation and lifetime rules.
- **Memory:** Any and membership can use bounded traversal state without copying
  all elements. Result projection and sorting retain their own working memory.
  No zero-allocation or measured latency guarantee is made.
- **Implementation:** add typed collection traversal, element-scoped predicate
  binding and tests for same-element conditions, empty collections and cancellation.
  This extends the query executor, not the persisted ownership model.

A Map/Set lookup structure inside one document is not an index across all root
documents. This decision does not add multi-valued secondary indexes or promise
that AllowScan=false can execute a collection-only filter. An outer root scan may
still be needed; scanning a collection inside an already selected root is distinct
from that outer scan. Exact plan classification remains implementation work.

### B. Inspect collections only in .NET in v1 — not selected

OQL filters scalar fields and fields inside owned single objects. Applications
perform collection tests through the already accepted enumeration/runtime views
or on owned copies. Protected .NET traversal can also avoid copying collections;
it is not inherently slower or more allocating than engine traversal.

This keeps the initial OQL compiler/executor smaller. The application must compose
the collection test with its candidate search, sorting and result selection; a
root-level result limit before that additional test does not necessarily implement
the intended filtered top-k request. More query logic moves into C#.

The work of inspecting elements does not disappear. Read gates, cancellation,
copies chosen by the caller and computation outside the engine retain their
existing costs and obligations. Neither option needs an extra stored field per
element merely to enable its predicates.

**Decision recorded:** include Any, Contains, ContainsKey and Map-entry predicates
over owned document collections in OQL v1, without automatic element indexes.
Cross-document traversal and result flattening are not introduced. Q07.3.1 separately
accepts null predicate behavior. Exact syntax, remaining comparison rules and
implementation verification remain outstanding; the accepted capability is not
an implemented query engine.

<a id="q07.3.1-how-should-null-affect-query-predicates"></a>

## Q07.3.1: How should null affect query predicates?

**Accepted on 12 November 2026: option A.** Use two-valued predicates with
explicit null tests, null-aware equality and defined range/collection behavior.
Q03.2 already accepts nullable fields as an explicit schema choice. This question
selects query behavior, not a different null storage layout, schema default or
result-sorting position. Operator spelling below is illustrative OQL notation.

### The visible difference

For a record whose nullable numeric Price is null:

| Predicate | A. Two-valued predicates — accepted | B. SQL-style three-valued predicates — not selected |
| --- | --- | --- |
| Price = 100 | false | unknown |
| Price != 100 | true | unknown |
| Price > 100 | false | unknown |
| NOT (Price > 100) | true | unknown |
| Price = null | true | unknown |
| Price IS NULL | true | true |

A filter selects a record only when its predicate is true. Therefore Price != 100
includes absent prices in A and excludes them in B. With A, explicitly write
Price IS NOT NULL AND Price != 100 when the request requires a known price.
This is a result-semantics decision, not merely an optimization setting.

### A. Every predicate returns true or false — accepted

For operands with compatible schema types:

- **Equality:** two nulls compare equal; null and a non-null value compare unequal.
  Otherwise use the type's accepted equality rules. Inequality negates equality.
- **Range comparisons:** less-than, greater-than and their inclusive forms return
  false if either operand is null. They otherwise use the type's comparison rules.
  This applies to comparisons supported for that type; it does not invent an order
  for every schema value. Float32/Float64 use the accepted
  [special-value contract](#floating-point-special-values).
- **Boolean composition:** AND, OR and NOT operate on true/false predicate results.
  NOT false is true even if the inner comparison was false because its operand
  was null. No third unknown predicate state is propagated.
- **Explicit absence:** IS NULL and IS NOT NULL expose null directly. Null is not
  replaced with zero, false, an empty string or an empty collection.

The equality/range behavior is familiar from built-in nullable value comparisons
in [C#'s nullable type reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/nullable-value-types).
This is an explicit OQL contract, not a claim that every nullable C# expression or
CLR method behaves this way. In particular, nullable Boolean values and instance
method calls need their own typed handling; do not invoke CLR methods on null and
rely on an exception to implement predicate semantics. A nullable Boolean can be
tested explicitly against true or false; implicit predicate conversion is not
selected by this question.

An optimizer cannot replace NOT (Price > 100) with Price <= 100 when Price may
be null: the former is true for null and the latter is false. Indexed plans must
include qualifying null records for inequalities/negations just as a scan does.
If an index omits null entries, it needs another valid access path for them; it
cannot silently present an incomplete indexed result as equivalent. No particular
nullable index representation is selected here.

For schema-supported nullable collections, the accepted A rules are:

- Any over a null or empty collection is false. Contains and ContainsKey over a
  null collection are false. IS NULL distinguishes a null collection from an empty
  one; NOT Any can consequently select either unless explicitly guarded.
- Any evaluates its element predicate with the same two-valued rules. Where
  nullable elements are permitted, Contains(collection, null) matches an actual
  null element using the equality rule above.
- A Map key can exist with a null value if the schema permits that value. ContainsKey
  still reports true; an absent key reports false. Key existence is not inferred
  from whether a value is null.

The accepted [collection input contract](schema-and-types.md#collection-input-contract)
now permits explicitly nullable List/Set elements and Map values while excluding null
Map keys. Nullability defaults to false at each position. Invalid key/argument types
remain binding/validation errors, not false predicates disguised as success.

### B. Predicates can also return unknown — not selected

Ordinary comparisons with a null operand return unknown, including equality of
two nulls. NOT unknown remains unknown. Boolean combinations use three-valued
truth tables; for example false AND unknown is false, while true OR unknown is
true. IS NULL and IS NOT NULL still produce definite true/false results. This
matches the relevant SQL comparison and logical behavior described by PostgreSQL:
[comparisons](https://www.postgresql.org/docs/18/functions-comparison.html),
[logical operators](https://www.postgresql.org/docs/18/functions-logical.html).

This is familiar to SQL users and excludes unknown comparisons from filtered
results even under negation. Applications wanting null to count as different from
a known value must express that explicitly. OQL's collection operators would also
need a defined treatment of null collections and unknown element predicates;
SQL scalar rules alone do not define an OQL Map/collection API.

### Costs and boundaries

| Effect | A. Two-valued predicates | B. Three-valued predicates |
| --- | --- | --- |
| Reads | Check nullable inputs, then combine Boolean results | Check nullable inputs and carry unknown through relevant expressions |
| Writes | Existing null validation and storage contract | Same existing null validation and storage contract |
| Stored memory | No change to the chosen nullable field representation | No change to the chosen nullable field representation |
| Query workspace | Boolean results; representation depends on executor design | May need transient truth/validity state; not another persisted value per record |
| Implementation | Explicit rules and null-aware optimizer/index equivalence checks | Additional truth tables and propagation rules, also requiring optimizer/index checks |
| Application use | Close to nullable equality/range tests commonly used in C#; missing values must be excluded explicitly in negative filters | Familiar SQL filtering; comparisons with absent values remain unknown |

No measured speed or memory advantage is claimed. The alternatives can select
different records, so result count and materialization can differ independently
of expression-evaluation overhead. Non-nullable paths can avoid unnecessary null
checks when binding proves they are not needed in either design.

Unknown field names and incompatible parameter types remain query errors. Unloaded
replica data and storage failures are not null values. Q07.3.6 accepts automatic
null propagation through valid paths with absent nullable owned parents and
field-typed numeric query input binding. Other comparisons/operators remain open;
missing schema fields are still errors and projection ownership is unchanged. Null ordering is accepted in Q07.3.2.

**Decision recorded:** OQL v1 uses two-valued predicates, explicit null tests,
null-aware equality, false range comparisons on null and the stated collection
behavior, without SQL-style unknown propagation. Exact grammar, optimizer/index
equivalence checks and implementation verification remain outstanding.

<a id="q07.3.2-where-should-null-appear-in-ordered-results"></a>

## Q07.3.2: Where should null appear in ordered results?

**Accepted on 12 November 2026: option A.** Put nulls last by default for both
ascending and descending query order, with an explicit NULLS FIRST/NULLS LAST
override on each sort key. This keeps present values first when callers reverse
the requested value order. It does not change Q07.3.1's predicate semantics.

For the values null, 10 and 30:

| Request | A. Nulls last in both directions — accepted | B. Nulls low in the default total order — not selected |
| --- | --- | --- |
| Ascending, no null override | 10, 30, null | null, 10, 30 |
| Descending, no null override | 30, 10, null | 30, 10, null |
| Ascending, explicit nulls first | null, 10, 30 | null, 10, 30 |
| Descending, explicit nulls last | 30, 10, null | 30, 10, null |

Both alternatives allow an explicit first/last override; they differ in the
default. Operator spelling remains illustrative, not a finalized parser grammar:

```text
Order: Price DESC NULLS LAST, Name ASC NULLS FIRST
```

### A. Keep nulls last regardless of direction — accepted

For each sort component, compare null presence using its chosen first/last policy.
Apply ascending/descending direction to non-null values using the field's type and
comparison rules. Two nulls tie on that component, so a composite comparison can
continue to the next component. Null placement applies within each preceding-key
group, not as one global rearrangement of every record with any null field.

An explicit NULLS FIRST or NULLS LAST overrides the default for that component.
For non-nullable scalar fields it has no effect on their value order. Null, an
empty string, zero and an empty collection remain different values; this decision
does not make collections sortable or treat absence as an ordinary numeric value.

The benefit is a stable default for result consumption: meaningful values precede
missing ones in either direction. A caller looking for missing values first can
request that directly without inventing a sentinel value or a computed result field.

The cost appears mainly in index-plan matching. Reversing an index that yields
10, 30, null yields null, 30, 10. That is not descending with nulls last. A matching
physical order, separate null/non-null ranges where supported, or an explicit sort
may be needed. For a single sort key, visiting the two regions in a chosen order
can be an implementation candidate; it is not a general guarantee for arbitrary
composite orderings or a selected v1 traversal algorithm.

### B. Nulls precede values in the default ascending order — not selected

Treat null as the lower category in the default sort comparer. Ascending order
then puts nulls first, and descending reverses the whole order so nulls are last.
This resembles [Nullable.Compare in .NET](https://learn.microsoft.com/en-us/dotnet/api/system.nullable.compare?view=net-10.0),
which orders an absent nullable value before a present one. It is a sorting rule,
not the behavior of nullable range predicates.

For one matching ordered key, forward/reverse traversal can satisfy both default
directions without moving the null region separately. This does not remove the
cost of explicit opposite null placement or mixed directions on composite keys.
Because both options permit overrides, both need general null-aware comparison
and plan matching; B primarily makes that default direction pair straightforward.
Its user-visible cost is that ascending results start with missing values unless
the caller overrides the default.

### Performance and index boundaries

| Effect | A. Nulls last by default | B. Nulls low by default |
| --- | --- | --- |
| Reads | Default direction changes can require more than reversing the same index | A matching single-key order can serve both defaults by reversing traversal |
| Writes | No record mutation or extra index maintenance solely from choosing the query default | Same; query ordering alone does not add stored data |
| Stored memory | Existing null representation; no new payload byte per record is required by this policy | Same existing null representation |
| Temporary memory | A separate sort can require workspace when the available index order does not match | Overrides or other order mismatches can also require sort workspace |
| Implementation | Independent direction and null placement per component; match full ordering requirements | Same override machinery, with a simpler default reversal pair for one key |

No extra index is automatically created for either choice. An application may
eventually declare indexes suited to frequent requests; additional declared
indexes cost memory and mutation work under the existing index contract. Physical
index encoding and declaration syntax remain open. Query overrides select a plan;
they do not reinterpret an existing index's stored order in place.

The planner must check field/comparison identity, direction and null placement
before claiming an index supplies the requested order. If it must sort, apply
the accepted execution-time and working-memory budgets. Do not silently return
another order to avoid that cost. AllowScan=false does not itself forbid a sort,
as already stated in Q07.1. There are no measured performance ratios for these
policies; non-nullable fields avoid the null-placement issue.

[PostgreSQL's index-ordering documentation](https://www.postgresql.org/docs/18/indexes-ordering.html)
illustrates how forward/reverse index traversal changes null placement and how
composite directions affect index suitability. That is a reference for the
planning distinction, not a requirement to adopt its defaults or B-tree format.

Ordering nulls after values does not make Price > null true in a predicate.
Sorting uses an ordering policy; the accepted predicate still returns false for
that range comparison. Q07.3.3 accepts explicit-only sort keys with unspecified order for ties;
Limit/Offset is accepted in Q07.3.4. Q07.3.5 accepts coherent records and no repeated
primary-key identities in one live pass, without a common snapshot.
Stored List/array order is unchanged, and no full-query snapshot is introduced.

**Decision recorded:** v1 orders nulls last in both ascending and descending
queries by default, with explicit NULLS FIRST/NULLS LAST per sort key. Exact
syntax, index-plan support and performance verification remain implementation
work; this decision does not claim an implemented ordering engine.

<a id="q07.3.3-how-should-equal-sort-keys-be-ordered"></a>

## Q07.3.3: How should equal sort keys be ordered?

**Accepted on 12 November 2026: option A.** Order by the
explicitly requested keys only. If every requested component compares equal, the
relative order of those records is unspecified. Callers requiring an unambiguous
order include the primary key explicitly, using the already accepted multi-field
ordering capability. This does not select a pagination API or a query snapshot.

### Example

Assume Id is the collection's unique numeric primary key:

| Id | Priority |
| --- | --- |
| 30 | 5 |
| 10 | 5 |
| 20 | 2 |

With Priority ASC, both ID sequences 20, 10, 30 and 20, 30, 10 satisfy the request.
A limit of two can therefore select either record 10 or record 30 after record 20.
With Priority ASC, Id ASC, the sequence is 20, 10, 30 for this unchanged input.
The additional key resolves the tie; it does not change the Priority ordering.

A tie means equality under the selected sort comparers, not necessarily identical
stored bytes. It includes two nulls on a component and can include text differing
only in case under an applicable case-insensitive comparison.

### A. Only explicit sort keys; callers request a unique order — accepted

The executor must satisfy all requested directions and null-placement rules, but
does not promise an additional order inside equal-key groups. Neither insertion
order, primary-key order, local arena location nor a previously observed index
iteration order becomes an implicit public contract. A different valid plan or
replica can produce a different order within those groups even for unchanged data.

When repeatable ordering matters, the caller can request, for example:

```text
Order: Priority ASC, Id ASC
```

Use a key whose comparison uniquely orders the queried collection's identities.
Q02.1 scopes the primary key to its declared collection/type; the final key-type
and comparison catalog must preserve that uniqueness. This decision does not add
a synthetic public ID or use process-local addresses to break ties. Q12 fixes the
query context to one database; v1 does not define an order spanning databases.

The key can participate in sorting without appearing in the selected output fields,
as already allowed by Q07.2. No automatic deduplication is implied by equal selected
values or equal sort keys. Separate root documents remain separate results.

- **Reads:** compare requested components; comparing a primary key is needed only
  when it was requested and earlier components tie. Explicit unique ordering may
  require a matching composite index or additional sorting.
- **Memory:** no implicit primary-key sort component is required for every ordered
  query. Requested keys and chosen physical indexes still have their normal costs.
- **Writes:** no new ordering requirement for secondary index entries solely from
  this policy. Internal duplicate-entry handling remains an implementation detail.
- **Complexity:** keep ordering requirements explicit. The application must add a
  unique key when the identity of limited results or repeatable output matters.

### B. Automatically append the primary key to ordered queries — not selected

If the primary key is not already among the requested sort components, append it
as a final ascending component. Priority DESC would consequently mean Priority DESC,
Id ASC. A caller explicitly specifying Id DESC keeps that direction. The output
projection need not include Id, but the execution plan must honor it.

This makes otherwise tied ordered results unambiguous for unchanged input without
requiring every caller to remember the extra key. It does not order queries that
have no explicit ordering at all, unless separately specified by a future contract.

- **Reads:** tied records need primary-key access/comparison even when the caller
  only requested Priority. Long string keys can cost more than numeric keys. The
  planner must satisfy the full order, including the implicit suffix; reversing an
  index can also reverse its tie order and fail to provide a fixed Id ASC suffix.
- **Memory:** sort workspace may retain key values or safe handles. If indexes
  embed the primary key to supply the order, their entries may be larger. This is
  not a requirement to copy every string or every document; representation matters.
- **Writes:** the logical document mutation contract is unchanged. Wider index keys
  or additional comparison work can affect index maintenance if the chosen physical
  design stores that suffix. No additional index is automatically required or created.
- **Complexity:** define and consistently apply the implicit suffix in planning,
  comparison, index matching and result limiting. It offers stronger defaults at
  the cost of ordering work that some callers do not need.

No measured time or memory ratio is claimed. Neither option adds persistent record
versions. Both honor the accepted query time and temporary-memory budgets.

### Repeatable ordering is separate from concurrent consistency

Unique ordering makes the order of a fixed set of matching records unambiguous; it does not
freeze which records exist or their sort/filter values. Inserts, deletes and updates
can change results between calls. Neither option guarantees gap-free, duplicate-free
multi-page reads while data changes. Pagination scope is accepted in Q07.3.4;
Q07.3.5 accepts no repeated primary-key identities within one pass. That rule does
not promise uniqueness across separate paging requests or a common snapshot.

Also distinguish a stable sorting algorithm from repeatability across database
calls. [Enumerable.OrderBy](https://learn.microsoft.com/en-us/dotnet/api/system.linq.enumerable.orderby?view=net-10.0)
preserves input order for equal keys. Preserving the input order cannot make it a
persistent database ordering guarantee when the input enumeration itself can change.
The engine is not required to use that LINQ method or retain insertion order.

[PostgreSQL's LIMIT documentation](https://www.postgresql.org/docs/18/queries-limit.html)
likewise explains why selecting a predictable subset requires a unique requested
order. This is a semantic reference, not an adoption of its pagination syntax.
Stored List/array sequence remains unchanged by either option; ordinary collection
enumeration without an explicit query order keeps its existing contract.

**Decision recorded:** v1 guarantees the explicitly requested order only. Equal-key
relative order is unspecified; callers add the primary key explicitly when they
need unambiguous ordering. Exact implementation and comparison/index verification
remain engineering work. Q07.3.4 accepts Limit/Offset in v1 and continuation in v2.

<a id="q07.3.4-how-should-v1-limit-and-page-results"></a>

## Q07.3.4: How should v1 limit and page results?

**Accepted on 12 November 2026: option A.** Provide an optional result limit and
offset in v1. Generic continuation by the last sort key is deferred to v2.
This selects scope; it does not claim that offsets are faster.

### A. Limit and offset in v1 — accepted

Illustrative names: Take/Limit returns at most N matching records after skipping
Skip/Offset matching records. Offset defaults to zero. A missing limit means no
count cap, while time and temporary-memory budgets still apply. Negative arguments
are invalid. A zero limit returns no records; an offset beyond the result is empty.
Exact syntax, argument types and iterator signatures remain implementation work.

Filtering and requested ordering logically precede skipping and limiting. The
engine may use an index or an equivalent bounded selection algorithm; it must not
arbitrarily take N candidates and sort only those when that changes the answer.
For repeatable pages over unchanged data, the caller requests a unique order,
normally by explicitly including the primary key under accepted Q07.3.3.

| Effect | Consequence |
| --- | --- |
| Reads | Small limits can avoid copying many results. Deep offsets may still process skipped matches; a small page does not guarantee a cheap scan or sort. |
| Memory | Skipping a streaming ordered result does not require storing every skipped document. A separate sort may retain many keys/results; the page size is not a temporary-memory cap. |
| Writes | No new persistent structures or indexes solely for this API. Existing root-gate, index and reclamation costs remain. |
| Complexity | Reuse filtering and ordering; no engine-owned cross-request cursor, token format or retained historical snapshot. |

For example, skipping 1,000,000 matches and returning 100 may require processing
at least those preceding matches with a conventional ordered traversal. The exact
cost depends on the plan and index representation; no special rank index is chosen.
For a full application-side pass, use the accepted .NET enumeration rather than
expressing every batch as a new query with an increasing offset.

This option does not promise a total result count alongside every page. Computing
one can require more work. Selecting such a convenience API is not part of this decision.

### B. Also provide generic key-based continuation in v1 — deferred to v2

A caller asks for records after the last returned sort tuple. With a unique order
Priority ASC, Id ASC and a last tuple (5, 30), continuation means Priority > 5 or
(Priority = 5 and Id > 30), assuming both keys are non-null in this example.
It does not mean holding a native index pointer or a record lock between calls.

- **Reads:** a matching ordered index can seek after the boundary instead of
  revisiting earlier matches. Residual predicates can still require scanning;
  a missing suitable index can still require a scan/sort.
- **Memory:** retain the owned boundary tuple rather than the whole result set.
  Sorting workspace remains plan-dependent. A continuation token is not a snapshot.
- **Writes:** no new index is automatically required. Explicit indexes chosen to
  accelerate continuation have their existing memory and maintenance costs.
- **Complexity:** define a complete unique order, compound directions, null placement,
  field comparers, boundary ownership and schema/query compatibility. Exact token
  representation and backward traversal would need explicit scope.

Key-based continuation is suitable for sequential next-page access; it does not
by itself jump to an arbitrary page number. Applications can still write an
explicit range filter over an immutable key under option A when supported by the
accepted predicates; that is not a generic database continuation API.

### Consistency and decision rationale

Neither option supplies a snapshot across calls. Inserts/deletes can shift offset
positions; changes to ordering/filter values can also move records across a key
boundary. Unique ordering alone cannot guarantee gap-free, duplicate-free traversal
under all concurrent updates across requests. Q07.3.5 separately accepts coherent
records and no repeated primary-key identities inside one live pass.

The offset/seek tradeoff is documented in
[Microsoft's pagination guidance](https://learn.microsoft.com/en-us/ef/core/querying/pagination)
and [PostgreSQL's LIMIT/OFFSET reference](https://www.postgresql.org/docs/18/queries-limit.html).
These are conceptual references, not dependencies or an adoption of either API.

A is selected because v1 already provides .NET enumeration and no requirement for
repeated deep-page queries has been established. B is worthwhile if that is a real
workload. No measured latency or memory ratio is claimed; a representative prototype
can compare physical plans after that workload and index layout are specified.

**Decision recorded:** v1 supports Limit/Offset. Generic continuation by the last
sort key is deferred to v2. Explicit unique ordering, record-level consistency
and existing query budgets remain unchanged. Q07.3.5 separately defines the accepted
single-pass concurrent traversal contract.

<a id="q07.3.5-what-may-a-traversal-observe-while-records-change"></a>

## Q07.3.5: What may a traversal observe while records change?

**Accepted on 12 November 2026: option A.** Allow live
traversal with coherent records and at most one result per primary-key identity
in one pass. Membership and values are not frozen at a common instant. Do not
abort merely because a writer committed a change to the collection.

This decision covers one root-collection enumeration or one OQL execution on the
local node. Separate Limit/Offset requests are separate passes. It does not add
graph-wide consistency, automatic graph-cycle detection, a cross-call cursor or
a master-wide snapshot to a child's local query.

### A. Live traversal with coherent records and no repeated identities — accepted

Keep the accepted root-document gate. Each evaluated record, including its owned
blocks, is read from one coherent published state. Different records may be read
at different times. Ordinary results are owned copies; protected views retain
the short synchronous lifetime already accepted in Q06.2.

| Concurrent event | Accepted behavior |
| --- | --- |
| A record is updated before it is inspected | The reader can observe the later state. A race can yield either coherent published state, never a torn mixture. |
| A record changes after its owned result was copied | The returned copy stays unchanged. The same identity is not yielded again in this pass. |
| A record is deleted before it is inspected | The reader may omit it. A result captured before deletion remains a valid owned result. |
| A record is inserted during the pass | It may be included or missed; the pass need not keep chasing new arrivals. |
| Filter or indexed ordering fields change during the pass | A moving record may be included or missed. It must satisfy the predicate in the state used for its returned values. |
| An unrelated record changes, an index rebalances or storage compacts | These events alone must not lose an unchanged matching record or yield it twice in a successful full pass. |

With unchanged data, a successfully completed unlimited traversal must visit the
whole selected collection, or all matches for its predicate. With concurrent
changes, a continuously present record whose relevant selection/order fields
remain unchanged and which continuously matches must still be included in a full
pass. Offset, Limit, cancellation and explicit errors delimit this completeness
obligation; a limited result is not a request to return every match.

The contract does not promise the complete set from the start or end of the pass.
For example, record 1 can be copied before an update and record 2 after it. A sum
computed over those copies need not equal a total that existed at any one instant.
Record consistency is preserved; collection snapshot consistency is not supplied.

#### Identity and duplicate handling

At most one result means at most one occurrence of the declared primary-key
identity within the queried collection/scope, under its key equality policy.
Q12 fixes the query context to one database. Separate records with equal
projected values remain separate results; this is not DISTINCT over selected fields.
Owned Lists still retain duplicates, and application graph code can revisit a
node intentionally. The database does not turn arbitrary graph walks into a
visited-node algorithm.

A delete followed by insertion of the same key does not permit a second result
if that key was already emitted in the pass. Reused native slots must not confuse
identities. [Q13 keeps primary keys immutable](mutation-commands.md#immutable-primary-keys-accepted-for-v1)
for a root's lifetime in v1. Creating under another key and deleting the original are
separate writes; a traversal treats the keys as separate identities, even if their
contents describe the same business object. No public internal ID is introduced.

Repeated physical index candidates must be suppressed before they count toward
Offset or Limit, so they cannot consume a page position. The same key must not be
counted once in the skipped prefix and returned again after it moves in the index.
A .NET enumeration/visitor must likewise not deliver the same identity to the
consumer twice. Internal candidate rechecks are an implementation detail.

#### Filtering, sorting and returned values

Validate the candidate and capture predicate values, projected values and retained
sort keys from the same protected record state. Do not filter an old value and
silently return a newly reread incompatible value after releasing the gate.
An index hit alone does not authorize returning a record that no longer matches.

An ordered result must still obey the requested directions, null placement and
explicit tie policy using its captured values. Concurrent key movement does not
permit out-of-order output. A stale candidate may need revalidation, omission
when permitted by the membership contract, or execution through a buffered plan.
That algorithm remains E03/E04 work; a root lock alone does not solve index traversal.

Release gates before yielding owned results or running arbitrary consumer work.
A retained native address is not a safe cursor across that release. All access,
reclamation and buffer-lifetime guarantees continue to apply.

#### Costs and implementation consequences

- **Reads:** take short record protection and coordinate traversal with changing
  indexes. Some plans also check identities already encountered. Ordinary writes
  do not force the consumer to restart an otherwise valid pass.
- **Writes:** do not freeze the whole collection for a query. The writer still
  waits for conflicting short reads and synchronizes index publication; this is
  not a lock-free or zero-contention claim.
- **Memory:** no copy of the entire collection and no historical payload versions
  are required solely by this contract. A plan may, however, need an O(K) set of
  processed identities for K candidates/countable matches, plus sorting/result
  buffers. One million raw Int64 keys alone occupy 8,000,000 bytes; a hash table
  additionally needs capacity/occupancy metadata, and string keys have different
  costs. Multiply per-pass workspace by concurrent queries. This is arithmetic,
  not a measured allocator or HashSet size.
- **Complexity:** maintain safe progress and identity handling across deletion,
  key movement, index reorganization and slot reuse. A suitable stable traversal
  structure can avoid a general-purpose visited set in some plans; that is an
  optimization to prove, not a promise of constant temporary memory for all plans.

Identity tracking is execution workspace and belongs inside the accepted query
memory budget. Hitting a budget fails explicitly; do not silently disable duplicate
suppression or omit unaffected matches. Candidate boundaries must permit progress
without indefinitely chasing new inserts. Engine deadlines and cancellation still
apply, and cleanup must release retained resources on completion, disposal or error.
No automatic query retries or new public result modes are selected here.

### B. Fail the traversal when the collection changes — not selected

Invalidate the pass if a published record mutation occurs in its target collection
after traversal starts and before completion. This includes updates to existing
records, even if the predicate would not use the changed field. Exact detection
must cover the entire pass; a best-effort fail-fast collection enumerator is not
sufficient proof of this stronger engine contract.

- **Reads:** the engine can check a mutation generation at safe points in addition
  to the existing record/lifetime protection. Busy collections can repeatedly
  invalidate long traversals and waste work. A caller retry is a new attempt.
- **Writes:** no query-long collection lock is required. Writers publish the change
  and invalidate relevant passes; they do not wait for readers to finish the full pass.
- **Memory:** no historical payload or full-copy snapshot is implied. Simple
  invalidation bookkeeping can be small, but query sorting/results still consume
  memory and safe storage lifetime handling remains necessary.
- **Complexity:** fewer successful interleavings to support, but add a new public
  failure condition and reliable detection across traversal and completion.

A streaming consumer may already have received records before the failure. The
error cannot undo application side effects or reinterpret those records as a
complete successful answer. Returning all results only after validation would
require buffering and a separate result-delivery choice; it is not assumed here.

This can suit collections that rarely change, but it is a poor default for the
accepted high-update workload. It also makes a caller's own writes between owned
results capable of invalidating its enumeration.

### Why a collection snapshot remains outside v1

A stable collection image would require retaining captured data/versions or
holding broader protection while capturing it. Copying only the initial keys
freezes a candidate set, not the associated record values; it still needs deletion
and identity-reuse rules. These approaches have distinct memory and writer-delay
costs. Whole-query/graph snapshots and long application-held broad locks were
already deferred beyond v1 and are not reopened by this question.

### References and verification boundary

Concurrent traversal without a moment-in-time snapshot is an established model:
[ConcurrentDictionary.GetEnumerator](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getenumerator?view=net-10.0)
allows concurrent reads/writes and can expose later modifications. Java describes
[weakly consistent iteration](https://docs.oracle.com/en/java/javase/25/docs/api/java.base/java/util/concurrent/package-summary.html)
for concurrent collections. These are conceptual precedents, not proofs of this
contract's native-memory, compound-index or no-duplicate guarantees, and neither
library implementation is selected as the database engine.

Focused concurrency verification must exercise unchanged matching
records alongside hot updates; forward/backward key movement; deletion and same-key
reinsertion; slot reuse; record predicate/projection agreement; sorted output; and
identity handling before Offset/Limit. Measure query latency, writer tail latency
and peak workspace under concurrent scans. No benchmark for this contract exists
yet; physical plans must be compared before claiming throughput or memory savings.

**Decision recorded:** v1 supports live traversal with coherent records, no repeated
primary-key identities within one pass and completeness for unchanged matches in a
successful full pass. Membership may change; there is no common snapshot. Identity
tracking and sorted output must respect the accepted query budgets and record-state
contract. Physical traversal, publication and reclamation algorithms remain to be
implemented and verified.

<a id="q07.3.6-remaining-comparison-and-operator-rules"></a>

## Q07.3.6: Remaining comparison and operator rules

For milestone 1, [shared typed binding/comparison](../design/milestone-1-engine-contract.md#index-preparation-and-traversal)
is now specified, including Float32/Float64 cross-width cases. Remaining OQL-only
operators are deferred beyond this milestone; they do not block the direct-index facade.

**Partially accepted.** Nullable-parent paths, numeric query input binding,
Float32/Float64 special values, schema-defined string ordering and literal String
Contains/StartsWith/EndsWith are accepted below. Other scalar predicates and
additional cross-width numeric binding cases
still require decisions. This section is not a blanket approval of
those rules and does not close Q07.3.6 in the question register.

### Paths through nullable owned parents

**Accepted on 12 November 2026: option A.** A valid owned-field path evaluates
to null when a schema-permitted nullable ancestor is null. Use the
same path value for OQL filtering, ordering and supported single-document field
selection. Do not require a separate null-safe navigation operator for this case.

This subsection accepts optional-parent paths. Numeric query input binding is
accepted in its own subsection below, as is the shared Float32/Float64 special-value
contract. String ordering and literal text predicates now follow the field policy,
as accepted below; other predicates and additional cross-width binding cases are
still unresolved within Q07.3.6.
Collection type positions and explicit nullability are now settled by Q03.4.2.
This decision applies where the schema allows a nullable owned parent; it does not make every
owned object or collection element nullable.

#### Example and effective nullability

Illustrative schema, not finalized JSON syntax:

```text
Address: owned object, Nullable = true
  City: String, Nullable = false
```

City must exist and be non-null whenever Address exists. A record may instead
contain Address = null. Under A, the query path Address.City then evaluates to
null, without constructing an Address or storing a separate null City value.
The stored child-field constraint is unchanged; the path's effective result type
is nullable because an ancestor can be null.

If all ancestors exist, read the leaf normally. If a permitted ancestor is null,
stop before touching absent child storage and return a typed null path value.
This extends to a longer valid chain of owned fields. It is not navigation through
non-owning key references to another root; OQL joins remain outside v1.

For Address = null, the already accepted two-valued rules produce:

| OQL predicate | Result under A |
| --- | --- |
| Address.City = 'Warsaw' | false |
| Address.City != 'Warsaw' | true |
| Address.City IS NULL | true |
| Address.City IS NOT NULL | false |
| Address IS NOT NULL AND Address.City != 'Warsaw' | false |

If City itself is declared nullable in a different schema, the path result alone
does not distinguish a null Address from an existing Address with a null City.
Address IS NULL tests the parent; Address IS NOT NULL AND Address.City IS NULL
tests an existing parent with a missing leaf value. There is no new stored
missing-value category or three-valued predicate logic.

#### A. Propagate null automatically through valid owned-field paths — accepted

Bind and type-check the entire path against the schema before execution. An unknown
field such as Address.Ctiy remains an error even if every current Address is null.
At execution, inspect the relevant ancestor validity before following its storage
handle. A legitimate absent parent yields null; invalid storage or an illegal null
in a non-nullable field remains an error.

Reuse the resulting typed value consistently:

- **Predicates:** use Q07.3.1's accepted equality, range and Boolean rules. In
  particular, inequality against a non-null constant includes a null path unless
  the caller explicitly excludes nulls.
- **Ordering:** use Q07.3.2's null placement, including per-key overrides. Do not
  drop the record merely because its sort path passes through a null parent.
- **Field selection:** preserve the effective nullability in result metadata and
  the selected value. Do not replace null with zero or empty text to fit a required
  destination field. This does not choose a projection shape, alias syntax or new
  computed-output capability.
- **Declared indexes:** indexed and scanned plans must agree about the path value.
  If an index representation omits null paths, the plan still needs a valid access
  path for queries that select them. Parent presence changes can affect indexes on
  descendant fields; those entries publish with the same root-document mutation.

The parent-validity check and child-value read happen inside the existing root
protection, so an update cannot remove the parent halfway through evaluation.
No snapshot, new lock boundary or retained record version is introduced.

#### B. Require explicitly null-safe navigation — not selected

Use an explicit form, illustratively Address?.City. Ordinary Address.City through
a schema-nullable parent is rejected during query binding, unless a separately
defined proof rule makes it safe. V1 need not implement such flow-sensitive proofs;
the explicit form can be required even alongside a parent-is-not-null predicate.
No explicit navigation operator is selected by this decision.

The explicit form returns the same typed nullable value as A, with the same
predicate, ordering, projection and index requirements. The benefit is visible
caller intent: the query author acknowledges optional navigation. The cost is an
additional query-language form and validation rule for an operation needed on
ordinary optional data. This alternative does not fail a valid null-safe query
merely because a particular record has no Address.

C#'s [null-conditional member access](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/member-access-operators#null-conditional-operators--and-)
illustrates the explicit form: a null receiver produces null, including a nullable
result for a non-nullable value-type member. This is a semantic reference, not a
requirement to use C# syntax, dynamic binding or CLR objects inside OQL.

#### Speed, memory and complexity

| Effect | A: automatic propagation | B: explicit null-safe form |
| --- | --- | --- |
| Reads | Check nullable ancestors while following a bound path; stop early on null | A valid null-safe expression needs the same checks; no inherent speed advantage |
| Stored memory | No extra object or copy for an absent parent; existing validity storage is sufficient in principle | Same data representation; explicit syntax does not reduce stored bytes |
| Query/index memory | Effective nullable result and any null index representation need accounting; physical format remains open | Same requirements for equivalent queries |
| Writes | Parent changes update any declared descendant-path indexes under the existing root mutation | Same maintenance for equivalent indexes |
| Implementation | One path evaluator derives nullability from ancestors and applies existing null rules | The evaluator plus an explicit navigation form and unsafe-path validation; optional proof analysis would add further work |

Fields are bound to schema metadata once; neither option requires per-record
reflection or allocating a CLR object for each path step. The exact number of
checks and index bytes depends on layout and compilation. No throughput or memory
ratio has been measured. Physical path/index implementations belong to E04/E05/E08.

#### Boundaries and verification

This policy applies to schema-bound OQL paths and supported selection paths inside
one owned document. It does not change ordinary C# member access on an owned POCO,
which follows C# rules. Generated/runtime arena access must expose optional parents
safely under its eventual API, not fabricate a borrowed child view when no child
exists. Exact accessor signatures remain open.

An unknown root, unavailable database, stale schema binding, missing Map key,
out-of-range List position or dangling non-owning reference is not silently
converted to a null owned-parent path. Those cases retain their existing error,
absence or unresolved collection contracts. Q07.3.1's null-collection predicates
remain unchanged; this does not introduce Map indexer or element-navigation syntax.

Implementation verification must cover multiple nullable ancestors, a required
leaf below an optional parent, parent-null versus leaf-null distinctions, projection
nullability and agreement between scan/index plans for equality, inequality,
IS NULL and both sort directions. Unknown fields must fail binding even when no
parent is present. Parent insertion/removal must keep predicates, selected values
and indexes consistent under the already accepted root gate.

**Decision recorded:** valid owned-field paths in OQL and supported field selection
automatically evaluate to null when a schema-permitted nullable parent is absent.
Schema/type errors remain errors, leaf constraints remain unchanged, and existing
null predicate, ordering and record-consistency rules still apply. The remaining
Q07.3.6 subtopics are not accepted by this decision.

### Numeric query inputs

**Accepted on 12 November 2026: option A.** Bind a numeric literal
or parameter to the compared field's schema type before executing the query. For
typed parameters, allow only the exact conversions listed below; reject implicit
Decimal/Float64 mixing and conversions that exceed the target integer range.

Nullable-parent paths are already accepted within Q07.3.6. This subtopic covers
numeric inputs to field comparisons. Float32/Float64 NaN/infinity/signed-zero behavior
is accepted in the [shared contract](#floating-point-special-values), and string
ordering follows the [field policy](#string-ordering-and-query-overrides).
[Literal text predicates](#literal-text-predicates) are also accepted; other predicate
operators are still open. Numeric binding does not
select arithmetic expressions, a general CAST operator, computed output fields
or mixed-type field-to-field comparisons. Those forms cannot silently inherit
parameter conversion rules that depend on one known value before execution.

The later [numeric catalog extension](schema-and-types.md#uint32-uint64-and-float32)
adds UInt32, UInt64 and Float32. UInt32/UInt64 participate in the supported-integer
rows below with full unsigned ranges; same-type Float32 binding uses its own domain.
This extension preserves checked ranges and exact typed-parameter conversion:
it does not authorize silent lossy Float64-to-Float32 narrowing. Additional
cross-width floating-point conversion cases must be finalized with Q07.3.6 before
implementation, using an explicit allowlist. Context-typed Float32 literals bind
directly to Float32 rather than first materializing a different numeric domain.
The older Float64 examples below remain valid for Float64 fields.

#### A. Bind to the field type, with an explicit conversion allowlist — accepted

The schema fixes the stored type. Normalize the input once for its use with that
field, then evaluate the typed predicate and any index bounds in that domain.
Do not convert every stored value to the caller's parameter type.

##### Typed parameters

The accepted conversion table applies to non-null values in the supported numeric
catalog. It describes query binding, not automatic conversion of stored data.

| Parameter type | Target field type | Accepted rule |
| --- | --- | --- |
| Same numeric type | Same type | Use the value directly, including the accepted Float32/Float64 NaN, infinities and signed zero |
| Supported integer | Supported integer | Accept if the actual value fits the target's range, preserving its sign and exact value; otherwise fail binding |
| Supported integer | Decimal | Accept exactly for the current integer catalog |
| Supported integer | Float64 | Accept only if this particular integer is exactly representable; otherwise fail binding |
| Decimal | Float64 | Reject implicit conversion |
| Float64 | Decimal | Reject implicit conversion |
| Decimal or Float64 | Any integer | Reject implicit conversion, even for a value such as 42.0; the caller supplies an integer explicitly |
| String, Bool, enum or reference | Ordinary numeric field | Do not treat it as an ordinary numeric parameter; use the existing distinct type contracts |

An unsupported combination or out-of-range input is an explicit binding/validation
error before candidate enumeration and before any result is delivered. Do not wrap,
truncate, clamp, silently round a typed integer parameter or disguise the error as
an empty successful result. This also applies when an optimizer could otherwise
prove a particular range predicate always true or false.

For nullable inputs, compatibility is still checked by declared type. A compatible
null follows the accepted null predicate rules; it is not zero. A typed null does
not make an otherwise incompatible parameter type valid. The exact SDK parameter
declarations remain design work.

Illustrative cases:

| Field and input | A result |
| --- | --- |
| Int32 field, Int64 parameter 42 | Bind once to Int32 42 |
| UInt8 field, Int32 parameter 300 | Binding error: outside 0–255 |
| Decimal field, Int32 parameter 42 | Bind once to Decimal 42 |
| Decimal field, Float64 parameter 0.1 | Binding error: explicit caller choice is required |
| Float64 field, Int64 parameter 9007199254740993 | Binding error: conversion would change the integer |
| Float64 field, Int64 parameter 9007199254740994 | Exact conversion is possible; values above 2^53 are not all forbidden |
| Int32 field, Float64 parameter 42.0 | Binding error: no implicit fractional-type-to-integer conversion |

##### Numeric literals are bound from their text

An untyped OQL literal gets its numeric type from the field context; it is not
first parsed as a .NET double and then converted to every other type. For a
Decimal field, 0.1 is interpreted directly as Decimal. For an integer field, the
literal must denote an integral value within range; no fractional truncation is
allowed. An out-of-range or unrepresentable Decimal literal is an error rather
than silent decimal rounding.

For a Float64 field, a context-typed real literal follows Float64's approximate
representation: 0.1 is not an exact binary fraction. That is the explicitly chosen
field domain, not an implicit conversion from an already typed Decimal parameter.
Typed-parameter exactness does not make every context-typed Float64 literal exact.
Overflow must not silently become infinity. Explicit NaN/infinity values are accepted
by the [special-value contract](#floating-point-special-values), including same-type
parameters; this does not permit numeric overflow during binding. Literal spelling,
suffixes and parsing details remain parser work.

The same schema/domain rules apply when the operand order is reversed. A reused
parameter must be valid for every field context in which the query uses it.
Literal binding can be part of compilation; actual parameter values must be checked
and normalized for every execution. A cached plan cannot retain a previous call's
converted parameter value. No culture-dependent string parsing or general-purpose
reflection conversion is implied.

##### Caller control

A caller needing a cross-domain conversion performs it explicitly before supplying
the parameter, choosing the desired rounding and checking range. For example, a
.NET caller normally supplies a decimal value for a Decimal price field. The
engine cannot recover precision already discarded by application code.
This decision does not add an OQL CAST feature or relax schema write validation.

#### B. Automatically promote operands to a common numeric type — not selected

A broader alternative promotes mixed comparisons to a common domain. An illustrative
precedence is Float64 when either side is Float64, otherwise Decimal when present,
otherwise Int64 for the current integer catalog. This alternative deliberately
permits approximate comparisons involving Float64; it is not C#'s exact promotion
rule and is not selected.

This accepts more caller combinations without an explicit conversion. It can also
change the equality/range meaning for stored values: converting two distinct Int64
values 9007199254740992 and 9007199254740993 to Float64 produces the same value.
Their equality cannot then represent exact integer equality. Decimal values can
likewise lose distinctions in a Float64 domain.

It also does not mean every combination is representable or that all conversion
failures disappear. The planner needs a complete precedence, overflow and rounding
contract. Indexed evaluation must reproduce the promoted predicate exactly, rather
than reusing native-type bounds that select a different set of records.

#### Speed, memory, writes and complexity

| Effect | A: bind the input to the field | B: promote both operands |
| --- | --- | --- |
| Query setup | Validate and convert each actual input once; incompatible inputs fail early | Select a common comparison domain and its conversions |
| Reads | Keep stored fields in their schema domain; no per-record conversion solely to accommodate the parameter | Some mixed comparisons convert each candidate's field value; same-type cases can cost the same as A |
| Index use | Typed bounds align with the declared field/index comparator; this does not guarantee every query is indexable | Promotion may require translated bounds and residual checks, or a different access path, to preserve semantics |
| Memory | A bounded set of normalized parameters and plan metadata; no widening of stored records | Stored records still need not widen; transient converted values need not allocate per record, but plans can require different workspace |
| Writes | No new persistent representation or automatic index required | Likewise; query promotion alone does not change the schema or add indexes |
| Complexity | Maintain an explicit allowlist, checked range/exactness and per-execution binding | Maintain precedence, rounding and optimizer/index equivalence across mixed domains |

These are design consequences, not benchmark results. A can avoid unnecessary
conversion work; it does not promise a speedup for every predicate. Neither policy
changes the accepted compact integer storage or chooses a new numeric storage format.

#### Sources and verification boundary

Microsoft documents that [some integral-to-floating conversions lose precision](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/numeric-conversions)
and that Decimal and Float64 have no implicit conversion between them in C#.
Its [floating-point type reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/floating-point-numeric-types)
explains the differing representations, including Decimal's exact representation
of 0.1 and Float64's approximation. These sources describe the underlying types;
Moltaro.Lattice's accepted binding allowlist is its own explicit contract.

A small local .NET arithmetic check confirmed the two distinct integers above
compare equal after conversion to double, while 9007199254740994 remains exactly
representable. This verifies an example, not database throughput or an implemented
query binder. No performance prototype is needed to decide whether silent loss of
comparison precision is acceptable.

Implementation checks must cover integer boundaries and signs,
exact/inexact large integer-to-Float64 inputs, direct Decimal literal binding,
reversed operand order, nullable typed inputs, reused parameters and cached-plan
executions with new values. Index and scan results must agree for accepted inputs;
rejected inputs must fail before producing results. Apply the accepted shared
Float32/Float64 special-value rules; future field-to-field comparison rules remain
outside this binding contract.

**Decision recorded:** numeric query literals/parameters bind to the field type
before execution. Typed parameters use the stated exact-conversion/range rules;
implicit Decimal/Float64 mixing is rejected. Float32/Float64 special values are
accepted below, as are schema-defined string ordering and literal text predicates.
Other operators and
additional cross-width conversion cases remain unresolved within Q07.3.6.

### Floating-point special values

**Accepted for v1 on 13 November 2026: option A.** Float32 and Float64 support
NaN, infinities and signed zero with one Equals/CompareTo contract.
It applies once across stored fields, OQL, indexes, Map keys and Set elements.
It does not add floating-point primary keys or change numeric conversion rules.

#### A. Support special values with one collection-style comparison contract — accepted

Accept finite values, NaN, positive/negative infinity and signed zero. Use the
semantics of typed .NET Equals/CompareTo, with matching hash equality:

- NaN values form one equality class for lookup and uniqueness.
- Positive and negative zero compare equal and have equal hashes. Preserve the sign
  in the stored value; comparison does not rewrite it.
- Ascending non-null order is NaN, negative infinity, finite values in numeric
  order, positive infinity. Descending reverses it. Existing explicit null-placement
  rules remain unchanged; NaN is a floating-point value, not null.
- OQL equality and range predicates, scan evaluation, index bounds and collection
  identity use this same contract. Typed same-domain parameters can carry the
  special values; literal spelling remains parser work.

This is a deliberate query rule, not the semantics of C# floating-point operators.
For example, OQL NaN equals NaN and sorts/compares below zero;
ordinary application-side `double.NaN == double.NaN` is false, and its `< 0`
comparison is false. Application calculations remain ordinary .NET code. Microsoft
documents the distinction in [Double.Equals](https://learn.microsoft.com/en-us/dotnet/api/system.double.equals?view=net-10.0)
and the NaN order in [Double.CompareTo](https://learn.microsoft.com/en-us/dotnet/api/system.double.compareto?view=net-10.0).

#### B. Restrict v1 to finite values — not selected

Reject NaN and infinities before commit and reject such query parameters before
execution. Keep positive/negative zero equal while preserving the stored sign.
This simplifies the comparison domain, but applications must reject or explicitly
model non-finite calculation results outside these fields. Never silently replace
them with zero or null.

| Effect | A — accepted | B — not selected |
| --- | --- | --- |
| Ordinary reads and storage | Existing 4-byte/8-byte values; no additional per-value wrapper or storage tag | Same widths; no memory saving for valid finite data |
| Writes | Accept the complete floating-point domain; index/key maintenance follows the shared comparer | Add a finite-value admission check; the remaining write pipeline is unchanged |
| Search, sort and collection identity | Handle special values consistently in equality, hashes and ordered comparison; exact throughput needs measurement | Fewer value cases; no quantified speed advantage is established |
| Implementation | One semantic owner and checks for NaN variants, infinities, both zeros, null separation and indexed/scan agreement | Smaller initial domain, but rejects otherwise valid .NET calculation results |

The accepted contract supports computational workloads without adding a new storage
type. Standard typed methods supply the semantic reference; native codecs, key
comparers and query execution still need integration tests. Equal values must have
equal hashes: do not hash differing NaN representations or the two zero signs as
different keys. Index and scan predicates must agree, including range predicates
with NaN; ordinary C# relational operators are not a substitute for this contract.

Existing collection input rules apply without a new duplicate policy. Bulk Map
input rejects equivalent keys, so two NaN keys or positive/negative zero keys
conflict. Bulk Set input coalesces equal members and preserves the first supplied
representation. Neither ordering nor equality introduces approximate numeric matching.

No new benchmark or production implementation is claimed. String ordering and
literal text predicates are accepted below. Other operators and cross-width
parameter conversion details stay separate unresolved work.

### String ordering and query overrides

**Accepted for v1 on 13 November 2026: option A.** String ordering and range
comparisons follow the field's schema policy, without a per-query override.
Q04 selects Ordinal by default and explicit OrdinalIgnoreCase, preserving original
text. Language-aware policies remain deferred.

#### A. Use the field's comparison policy — accepted

Use explicit .NET StringComparer.Ordinal or StringComparer.OrdinalIgnoreCase
semantics, as selected by the field, for ordering and supported range predicates.
Indexes, full scans and explicit result sorts must agree. UTF-8 and UTF-16 storage
produce the same logical order; physical byte ordering is not the contract.
Microsoft describes explicit ordinal comparison in its
[string comparison guidance](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-strings).

V1 has no per-query ordering-comparer override. Under OrdinalIgnoreCase, values
equal ignoring case remain tied; do not add original spelling or the primary key
as a hidden tie-breaker. Existing explicit-key tie rules, null placement and List
order remain unchanged. This is lexicographic ordering, so Item10 precedes Item2;
numeric-looking substrings do not acquire numeric sorting semantics.

#### B. Also permit an explicit ordering override per query — not selected

Keep the field policy as the default but let a query choose Ordinal or
OrdinalIgnoreCase separately for sorting. Stored identity and equality are unchanged.
An index ordered with a different comparer cannot directly supply that requested
order; use a compatible plan or a separate sort within the existing query budgets.
This does not propose language-aware collation or automatic additional indexes.

| Effect | A — accepted | B — not selected |
| --- | --- | --- |
| Read by primary key | Unchanged | Unchanged |
| Sorted queries | One schema-defined comparer; a suitable ordered index can supply its order | Same cost when compatible; a mismatched override can require an additional sort |
| Writes | Maintain the declared index order | Same without extra indexes; the override itself does not require more write work |
| Memory | No second stored spelling or mandatory CLR string per comparison | Same stored payload; an extra result sort can need temporary workspace |
| Complexity | Shared comparison contract for indexes, ranges and sorting | Additional query syntax/API, planner compatibility checks and sort cases |

Both need an encoding-aware implementation of the chosen .NET semantics; UTF-8
comparison cannot always be replaced with raw byte ordering. Exact comparator costs
remain measurement work. The accepted contract is A for v1, with application-side
sorting available for consumers that need another presentation order. No new
benchmark or engine implementation is supplied by this acceptance. Implementation
checks must compare indexed and scan results, both encodings, supplementary
characters, case-insensitive ties, chunk boundaries and the accepted null rules.
Literal text predicates are accepted below. Other scalar predicates and cross-width
numeric binding cases keep Q07.3.6 open.

### Literal text predicates

**Accepted for v1 on 13 November 2026: option A.** OQL supports String Contains,
StartsWith and EndsWith using the field's comparison policy. LIKE, regular
expressions and full-text search are excluded from v1. Q07.2.1 separately accepts
membership tests inside collections.

#### A. Contains, StartsWith and EndsWith in v1 — accepted

Support a stored String field or valid owned-field path and a String literal or
parameter. Illustrative expressions, not finalized OQL grammar:

| Expression | Meaning |
| --- | --- |
| Contains(Name, @text) | The field contains the supplied literal text |
| StartsWith(Name, @text) | The field begins with that text |
| EndsWith(Name, @text) | The field ends with that text |

Use the field's Ordinal/OrdinalIgnoreCase policy in both storage encodings, without
a query override or normalization. The non-null semantic reference is the explicit
comparison overloads of .NET string methods, such as
[Contains](https://learn.microsoft.com/en-us/dotnet/api/system.string.contains?view=net-10.0)
and [StartsWith](https://learn.microsoft.com/en-us/dotnet/api/system.string.startswith?view=net-10.0).
This does not require materializing each stored string as a CLR object.

- An empty pattern matches any non-null string, including an empty stored string.
- A null field or a compatible null String pattern returns false for all three
  predicates. This is an explicit OQL rule, not a call to a CLR instance method on
  null. Existing two-valued NOT semantics still apply.
- Validate non-null patterns as well-formed Unicode during binding. Wrong argument
  types or malformed text fail before enumeration; do not report an empty result.
- Patterns are literal text. Percent signs, asterisks and other characters have no
  wildcard meaning. LIKE, regular expressions and full-text search are outside v1.
  No automatic substring, suffix or full-text index is added.

Use existing candidate access paths and query budgets. StartsWith may use a suitable
ordered index when correct bounds under the field comparer are established. Contains
and EndsWith generally need to inspect candidate text; an ordinary value-ordered
index does not by itself make substring/suffix matching selective. Other indexed
conditions may still reduce candidates. The accepted scan-fallback policy applies;
having an operator is not a promise that every use is indexed.

#### B. Defer these three string predicates beyond v1 — not selected

Keep accepted equality/range comparisons in OQL. Applications implement substring,
prefix and suffix matching through the existing .NET enumeration/access contracts.
This reduces the initial executor scope but moves the filtering work to callers;
it does not eliminate scanning or guarantee cheaper reads.

| Effect | A — accepted | B — not selected |
| --- | --- | --- |
| Reads | Filter within the engine; avoid copying whole objects merely to test text, but still inspect candidates when no selective access path exists | Application code performs the filter; copying depends on its chosen access API |
| Writes and retained memory | No additional index or stored string copy required; existing write costs remain | Same data layout; fewer executor components |
| Temporary memory | Bind the pattern once and use bounded matching workspace within query budgets | Caller owns its matching workspace and any materialized data |
| Complexity | Three typed predicates, null/empty rules and matching across UTF-8/UTF-16 chunks; this needs more than wrappers around CLR string methods | Smaller OQL implementation, with more application-side filtering code |

The accepted contract is A as a small explicit text-search surface. It does not
settle other scalar operators or numeric conversions. Encoding-aware matching,
prefix optimization and measured costs remain engineering work. Verify both
encodings, supplementary characters, case handling, empty/null operands, malformed
patterns, chunk-spanning matches, scan policy and query budgets. An indexed prefix
plan must return the same matches as scanning. No new benchmark or production
implementation is supplied by this acceptance.
