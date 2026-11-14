# Schema and types

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md)
now specifies the mechanisms and starting values described as open, partially accepted or
illustrative below, and the [decision register](../design/questions.md) owns the current
status. The earlier notes are kept as the decision's history; they do not reopen what was
specified. "v1" below is wider than the first production milestone, whose scope is in
the [planning handoff](../design/planning-handoff.md).

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

- [Q03](#q03-how-should-the-schema-change)
- [Q03.1](#q03.1-how-should-numeric-fields-be-typed)
- [Q03.2](#q03.2-when-may-a-field-have-no-value)
- [Q03.3](#q03.3-should-the-schema-support-list-map-and-set)
- [Q03.4.1](#q03.4.1-scalar-and-primary-key-type-catalog)
- [Q03.4.2](#q03.4.2-nested-and-collection-declarations)
- [Q03.5](#q03.5-should-schema-fields-support.net-enums)
- [Q03.6](#q03.6-should-flags-allow-combinations-of-declared-bits)
- [Q03.7](#q03.7-how-should-enums-and-flags-be-ordered)
- [Q03.8](#q03.8-can-enum-members-have-aliases)
- [Q03.9](#q03.9-when-should-schema-conversion-run)

## Q03: How should the schema change?

**Accepted on 12 November 2026:** start with explicit, controlled migrations
and one active schema/layout generation for each affected dataset. Changes that
require data transformation or a different physical layout temporarily stop reads
and writes to that dataset. Do not require online mixed-layout compatibility in
the first version. The exact scope of affected types within the database still needs
definition; this does not promise an independent upgrade for each field or child.

**Scope confirmed by the owner on 14 November 2026:** as the
[milestone contract](../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration)
specifies. On the master all writes pause, also those to unaffected types, because the
journal has to be drained to one fixed position; reads of unaffected types continue
there. Every child reloads fully, and its reads wait within the reload bound. A
migration is planned as a stop of writes for the whole database. Migration is for a
Durable master only: a MemoryOnly master has nothing saved to convert, and its schema is
changed by opening it with the new one.

Temporary dataset unavailability does not require terminating the host process.
A maintenance state can stop admission to affected data and drain active readers
before conversion. This is not a reader/writer lock held during disk or network
I/O. Q03.9 accepts the evolution foundations in v1 and defers mixed-layout access
and lazy conversion to v2, retaining this controlled v1 migration policy.

This decision selects an evolution policy only. Initial numeric types are discussed
in Q03.1. Other field types, nullability and collection declarations remain separate.

### Example

A settings document currently has Enabled and Limit. A new application adds
RetryLimit. Existing records have no value for that field, so the migration must
define a default or derive it from existing data. Changing Limit from an integer
to a different representation needs an explicit conversion and validation rule.
Moving a nested field or changing collection ownership may require restructuring
the document and its indexes.

Moltaro.Lattice is authoritative persistent storage. A migration transforms existing data;
it must not discard it and assume another system can repopulate the database.
A schema version identifies a format/contract, not a historical copy per update.

### A. Controlled migration with temporary unavailability — accepted for the first version

- **Behavior:** validate the proposed schema and conversion rules, stop access to
  the affected dataset, transform its durable state and rebuild affected indexes,
  then publish the new generation and resume service. This is a proposed protocol,
  not a runnable migration command.
- **Reads:** normal reads use one active layout without mixed-version adaptation.
  Reads of migrating data wait or fail explicitly; they do not receive half-migrated
  documents. This does not guarantee a short maintenance interval for large data.
- **Writes:** normal writes need no dual-format maintenance. Writes to migrating
  data are paused/rejected with bounded handling, not queued without limit.
- **Memory:** conversion can use bounded buffers instead of a second full RAM
  copy. The eventual arena/index implementation still determines working memory.
- **Disk:** the recoverable old generation and the new generation coexist until
  publication is durable. Temporary disk may approach the sum of both datasets,
  plus required journals and indexes. No fixed multiplier is promised.
- **Implementation:** smallest initial compatibility surface. It still needs
  conversion validation, restart/recovery and an unambiguous durable publication
  point. Failure before publication must leave the old generation recoverable;
  failure after publication must recover the new one. Old files are reclaimed only
  after the recovery contract permits it, not retained as permanent history.
- **Limit:** a deployment must tolerate planned unavailability of affected data.

Pure metadata changes that preserve field identity, meaning and physical layout
need not copy every record, but must still pass schema validation. Renaming a
generated .NET property may break application source compatibility even when
the persisted field ID and bytes are unchanged.

### B. Compatible additive changes online; other changes use controlled migration

- **Behavior:** allow a defined subset, such as adding an optional field with a
  specified default, while retaining service. Type/ownership/key changes remain
  explicit migrations. Which additions are compatible must be defined precisely.
- **Reads:** old data needs a missing-field/default rule. A packed row may need
  layout-specific accessors or side storage; adding a column has different costs.
  Adaptation can sometimes be resolved once per segment/query, but is not free
  by definition. No numerical slowdown is known.
- **Writes:** writers must know the active schema. An older whole-document writer
  must not silently erase new fields: it must preserve them, use compatible
  patches, or be rejected. This is a real SDK/protocol requirement.
- **Memory/disk:** extra metadata, side storage or migration buffers may be needed.
  Mixed formats require a bounded consolidation strategy, otherwise format
  generations and conversion code can accumulate indefinitely.
- **Implementation:** more validation and compatibility tests across readers,
  writers, recovery, indexes and replicas. Building a new index online needs its
  own completion/publication boundary; a field addition does not make it free.
- **Benefit:** avoids maintenance pauses for the supported common changes.

### C. General online migration

- **Behavior:** convert existing documents while reads and writes continue, then
  switch generations. Concurrent updates must reach the correct converted state.
- **Reads/writes:** migration competes for CPU, memory bandwidth and storage I/O;
  routing, change replay or dual writes add work and can increase tail latency.
- **Memory/disk:** old and new representations, indexes and a change tail can
  coexist. Streaming may bound RAM, but does not remove temporary disk needs or
  catch-up limits when updates arrive faster than conversion.
- **Implementation:** the largest contract: crash recovery at every stage,
  compatibility during transition, convergence and replica generation changes.
  A reliable cutover still needs coordination; zero interruption is not assumed.
- **Benefit:** supports more schema changes without a long planned outage.

### Schema, SDK and replica boundaries

Proposed prerequisites for every option are stable type/field IDs, explicit
defaults/conversions and schema/layout identifiers in persisted and replicated
metadata. Reusing a removed ID for a different meaning is not allowed by this
proposal. A version tag alone cannot validate compatibility.

Generated accessors must match the layout they read. An incompatible client must
fail explicitly instead of interpreting new bytes using old offsets. A replica
must receive/validate the new schema and an appropriate base before applying
changes encoded for it. Resume across a migration may require a new base image.
Whether an old client may continue serving its old local generation is a separate
compatibility/freshness decision, not an implicit migration guarantee.

**Decision:** A is accepted for the first version. B and C remain unselected
alternatives for possible later requirements. Detailed migration commands, the
scope of affected types/data, client coordination and recovery checks still need
design and implementation. No speed or memory ratios have been measured for these
migration strategies.

<a id="q03.1-how-should-numeric-fields-be-typed"></a>

## Q03.1: How should numeric fields be typed?

**Accepted on 12 November 2026:** give each numeric field a fixed type
in the schema. Support Int32, Int64, Float64 and Decimal, plus one-byte and
two-byte integers for compact numeric datasets.
Float64 maps to .NET double; Decimal uses .NET decimal value semantics. Fractional
precision is chosen per field rather than globally for the database.
This does not yet select a persisted byte order, a complete scalar-type catalog,
nullable representation or all query conversion rules.

### Small integer widths and Boolean values

The working catalog makes the requested widths concrete with signed and unsigned
forms, so a byte-sized value is not ambiguous about its range. This is the design
interpretation of the width support; the selected widths do not prescribe spelling
or alias grammar for the JSON schema.

| Schema type | .NET type | Value payload | Range |
| --- | --- | --- | --- |
| Int8 | sbyte | 1 byte | -128 to 127 |
| UInt8 | byte | 1 byte | 0 to 255 |
| Int16 | short | 2 bytes | -32,768 to 32,767 |
| UInt16 | ushort | 2 bytes | 0 to 65,535 |
| Int32 | int | 4 bytes | Signed 32-bit integer |
| UInt32 | uint | 4 bytes | Unsigned 32-bit integer |
| Int64 | long | 8 bytes | Signed 64-bit integer |
| UInt64 | ulong | 8 bytes | Unsigned 64-bit integer |
| Float32 | float | 4 bytes | Binary floating-point approximation |
| Float64 | double | 8 bytes | Binary floating-point approximation |
| Decimal | decimal | 16 bytes | .NET decimal precision and range |

The integer mappings/ranges follow the
[.NET integral numeric types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types).
The table includes the later [Q03.4.1 numeric extension](#uint32-uint64-and-float32),
accepted on 13 November 2026: UInt32, UInt64 and Float32.

Boolean is a distinct logical schema type exposed as .NET bool. Its values are
true and false, not the full UInt8 range. One byte per non-null Boolean, encoded
as 0/1, is the initial physical candidate; bit-packing multiple values is a
separate layout optimization that remains unselected. It is independent of the
bitmap proposed below for null validity. Native interop defaults do not define
our binary format: [.NET Boolean marshalling](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/customize-struct-marshalling)
can use representations other than one byte.

For 100 million densely stored values of one field, 1-, 2-, 4- and 8-byte payloads
occupy 100, 200, 400 and 800 MB respectively, in decimal units. These are payload
calculations, not measured engine footprints. Row alignment, nullable metadata,
collection handles, indexes and allocation granularity may change total savings.
Do not silently normalize all stored integers to 64-bit slots or box each value;
the layout investigation must show whether the requested narrow widths actually
reduce resident storage. Compact columns are one option, not an accepted layout.

Smaller payloads can reduce memory traffic and improve cache residency; they do
not guarantee proportionally faster individual reads or writes. Computation may
widen values in registers without widening the stored field. Adding these types
requires typed serialization, sign-aware comparisons/indexes and range validation;
it does not require a different storage algorithm for each width. An out-of-range
write should fail explicitly rather than wrap, truncate or widen that one value
silently. Detailed conversion rules still need to be specified.

The next layout experiment should cover narrow fields, mixed-width rows, Boolean
bytes versus packed bits, index overhead and projected reads. The existing 64-byte
numeric-row experiment does not measure those cases.

An identifier/counter remains an integer. A measurement that permits binary
floating-point approximation can use Float64. A decimal quantity whose decimal
digits matter can use Decimal within its representable range and precision.
No implicit conversion from exact decimal input to Float64 is proposed.

### A. Float64 and Decimal, selected per field — accepted

- **Benefit:** supports approximate and decimal-exact storage without imposing a
  fixed decimal scale on every application. The generated .NET member has the
  corresponding value type; ordinary typed reads need not box it as object.
- **Memory and transport:** double has an 8-byte value payload and decimal has a
  16-byte value payload. This is not a twofold total-database memory prediction:
  null metadata, alignment, references, indexes and encoding also matter. Only
  fields declared Decimal incur its payload width.
- **Reads:** a typed fetch/copy is distinct from calculation or predicate evaluation.
  Wider values move more bytes; arithmetic and comparisons have type-specific
  costs. The database need not format values as strings on a read.
- **Writes:** ordinary storage copies the selected representation and maintains
  relevant indexes. Decimal calculations/conversions can cost more than double;
  no fixed write-throughput ratio is known for this engine.
- **Implementation:** serializers, accessors, comparisons and indexes need both
  types. A schema-bound query can choose typed operations before scanning records.
- **Limit:** decimal is finite-precision, not arbitrary-precision mathematics;
  operations such as division may still round. Choosing it does not define every
  future rounding or overflow policy automatically.

[Microsoft's numeric-type reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/floating-point-numeric-types)
documents the widths and why a value such as 0.1 is not represented exactly by
double but can be represented by decimal. Here Decimal means .NET decimal,
not IEEE decimal128. Its coefficient/sign/scale representation is described by
[Decimal.GetBits](https://learn.microsoft.com/en-us/dotnet/api/system.decimal.getbits?view=net-10.0).

### B. Float64 as the only fractional type

- **Benefit:** one fractional representation reduces initial implementation and
  compatibility work. Its 8-byte value payload is compact.
- **Reads/writes:** avoids a second fractional numeric path and suits workloads
  that accept approximation. Fast arithmetic alone does not establish total
  database throughput, especially when indexes or persistence dominate.
- **Memory:** saves 8 payload bytes per value compared with a 16-byte decimal
  representation, before layout effects. This says nothing about integer fields.
- **Limit:** decimal inputs may lose their exact decimal value. Equality filters
  and repeated arithmetic need appropriate semantics; display rounding does not
  restore lost information. This is too restrictive for the intended reusable
  engine, so it is not recommended as the only fractional type.

### C. Float64 plus a schema-scaled Int64 for exact decimal fields

- **Representation:** an exact field declares a scale s; its value is a signed
  64-bit coefficient divided by 10^s. For scale 4, 1.2345 is stored as 12345.
  The scale belongs to the field schema, not a separate CLR object per value.
- **Benefit:** an 8-byte coefficient provides exact values at the declared scale.
  Comparisons within the same scale can use integer comparisons. Float64 remains
  available for approximate measurements.
- **Reads/writes:** converting to/from .NET decimal requires scale/range handling.
  Multiplication, division and comparisons across scales require additional rules
  and may need wider intermediate values; they are not all simple integer writes.
- **Memory:** can halve the exact-value payload relative to .NET decimal. Metadata,
  indexes, alignment and conversion costs are not included in that saving.
- **Limit:** scale trades range for fractional precision. Extra decimal places and
  overflow need explicit rejection or rounding; silent rounding is not proposed.
  Changing the scale can require the already accepted controlled migration.
- **Implementation:** introduces a custom numeric contract across generated types,
  queries, serialization and clients. It is a candidate optimization for a measured
  workload, not the recommended general initial representation.

### Example cost and remaining semantics

For ten million densely stored values of one field, 8-byte payloads occupy 80 MB
and 16-byte payloads occupy 160 MB, in decimal units. This is a size calculation,
not a measured database footprint. A Decimal field does not make unrelated fields
or entire documents twice as large.

Exactness is a contract choice. A performance experiment cannot decide whether
approximation is acceptable to an application. If numeric costs materially affect
layout selection, benchmark typed projections, predicates, indexed updates and
serialization separately; do not infer storage-write speed from an arithmetic loop.

Q07.3.6 accepts field-typed numeric query input binding with range/exactness checks.
Other numeric operators and field-to-field conversion rules still need decisions
before implementation. Decimal 1.0 and 1.00 compare numerically equal
despite differing representations, so raw-byte identity is not sufficient for
numeric equality indexing. Float32/Float64 NaN, infinities and signed zero use the
accepted [Equals/CompareTo contract](queries.md#floating-point-special-values),
including consistent hashes for equivalent NaNs and the two zero signs.
One-byte and two-byte integers are now included. Other unlisted numeric widths
and scalar types remain outside this decision.

**Decision:** per-field numeric typing is accepted with the narrow integer widths
described above. Scaled-integer decimal storage remains deferred unless a concrete
requirement or measurement justifies it. No new performance ratio is claimed.

<a id="q03.2-when-may-a-field-have-no-value"></a>

## Q03.2: When may a field have no value?

**Accepted on 12 November 2026:** declare nullability explicitly per field.
Fields are non-nullable by default; Nullable=true permits null as a separate
logical state. Zero, false, an empty string and an empty collection remain valid
values, distinct from null. Nullability does not change the field's numeric type.

This decision concerns value semantics. It does not yet select nullable storage
layout or the complete type/collection declarations. Q07.3.6 separately accepts
query-path null propagation where the schema permits a nullable owned parent.

### A. Explicit nullable fields — accepted

- **Behavior:** a required value must be supplied when creating a document unless
  the schema defines an explicit default. A nullable field may contain null.
  Defaults, patch operations and missing data must not be conflated.
- **Reads:** only nullable fields need a validity check. The generated API can use
  nullable values or an explicit presence result; a zero-allocation view can read
  validity separately without constructing a CLR object per value.
- **Writes:** validate non-nullable fields; update a nullable field's value and
  validity together within the document's publication boundary. This adds work
  for nullable fields, but its end-to-end cost is not yet measured.
- **Memory:** a candidate packed validity bitmap uses one bit per nullable slot.
  For 100 million slots that is 12.5 MB of validity bits before padding/metadata,
  in addition to value storage. The fixed-width candidate still reserves the
  value slot when null. It need not mirror the padded layout of CLR Nullable<T>.
- **Implementation:** queries and indexes must understand null. Q07.3.1 accepts
  two-valued predicates and explicit null tests; Q07.3.2 accepts nulls last by
  default with explicit per-key placement overrides.
- **Benefit:** only fields that need absence semantics pay the applicable storage
  and access cost; legitimate zero/false/empty values remain usable.

### B. Every field is nullable

- **Benefit:** application models can leave any field without a value without
  changing its schema declaration.
- **Reads/writes:** every field must support the validity path, even where a value
  is mandatory in the application. Actual cost depends on generated accessors.
- **Memory:** validity metadata may be needed for all fields. A bitmap can keep
  it small, but the extra state is not free or required by every dataset.
- **Implementation:** null-aware behavior is uniform, while required-field rules
  move to another validation layer. Generated client types become more optional.
- **Limit:** schema guarantees are weaker, and callers handle absence in more places.

### C. No stored nulls; use designated ordinary values

- **Benefit:** the basic value path needs no null metadata or validity checks.
- **Reads/writes:** engine access is simple, but application code and queries
  must remember each field's special value or external presence convention.
- **Memory:** no separate null bits, at the cost of losing part of the value domain
  when a sentinel is used. Additional application flags can erase the saving.
- **Limit:** reserving 255 as absent prevents UInt8 from storing 255 as data.
  Reserving false or zero is particularly unsuitable for flags and numeric series.
  This is not recommended as the universal absence model.

### Absence, defaults and incomplete replicas

For an update patch, an omitted field means leave its current value unchanged;
an explicit null means clear it only if the field is nullable. This transport
distinction does not require a third persisted state for every field. A full
document write must instead follow its creation/replacement validation rules.

Replica data that is not loaded is never a stored null, a default value or zero.
It still follows the established load/wait/unavailable contract. A default can
initialize a new document or an explicitly migrated field; it cannot substitute
for an unavailable authoritative value.

**Decision:** explicit per-field nullability is accepted, with Nullable=false
as the default and null allowed only for explicitly nullable fields. Zero, false
and empty values are preserved. The bitmap is still a physical-layout candidate;
collection position nullability is settled by [Q03.4.2](#collection-input-contract). Q07.3.1 accepts
null predicate behavior; Q07.3.6 accepts null propagation through valid paths
with absent nullable owned parents. Other query comparison rules remain open.

<a id="q03.3-should-the-schema-support-list-map-and-set"></a>

## Q03.3: Should the schema support List, Map and Set?

**Accepted on 12 November 2026:** include three logical collection
kinds in v1 through arena views: List preserves order and duplicates, Map has
unique keys, and Set has unique elements. Map and Set do not guarantee enumeration
order. Generated value-type views expose arena contents without requiring a
resident CLR List, Dictionary or HashSet instance for each field. Full API
compatibility with those classes is not implied. The Q02.2 array-order contract
also applies to Lists; physical layouts and detailed APIs are not selected here.

### Logical collection contracts

| Kind | Meaning and order | Intended local read surface | Example use |
| --- | --- | --- | --- |
| List<T> | Indexed sequence; preserve supplied order and duplicates | Count, access by position, ordered enumeration | Sorted neighbours, steps in a route, numeric history |
| Map<K, V> | One value per distinct key; no enumeration-order guarantee in v1 | Count, lookup by key, entry enumeration | Parameter name to value, node key to edge data |
| Set<T> | Unique elements under declared equality; no enumeration-order guarantee in v1 | Count, membership test, element enumeration | Allowed identifiers, unique node references |

The schema names above are conceptual, not finalized JSON or C# syntax. An input
array and a List can both supply an ordered sequence; fixed-length constraints,
capacity and mutation APIs are separate details. A Map key is local to that
collection and does not become the containing record's primary key or introduce
another independently stored object.

The accepted unordered Map/Set contract is explicit. Enumeration can differ
after rebuild, compaction or replication while retaining the same logical entries.
Algorithms that need order should use a List or an explicitly ordered local copy.
An ordered Map/Set would be a separate requirement with order-maintenance costs.
For context, .NET documents undefined Dictionary enumeration order and describes
HashSet as an unordered collection of unique values:
[Dictionary remarks](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2?view=net-10.0#remarks),
[HashSet remarks](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.hashset-1?view=net-10.0#remarks).
Those contracts do not select Moltaro.Lattice's storage algorithm.

### Ownership, equality and .NET access

Collections and their owned values remain inside their containing record's
atomicity and snapshot boundary. List items and Map values can be schema-defined
primitives, owned structures or typed references, subject to the permitted type
catalog. References keep key identity and do not pull their targets into the
collection's snapshot. Nested collection declarations need an explicit schema;
arbitrary CLR objects and reference cycles through ownership are not implied.

The later [Q03.4.2 type-position decision](#q03.4.2-nested-and-collection-declarations)
accepts scalars except Binary, enum/Flags and typed references for Map keys and Set
elements. List elements and Map values also allow owned structures and nested
collections. Structural equality over owned objects/collections and application-supplied
IEqualityComparer code are outside the accepted v1 identity contract. Collection
nullability/input rules are accepted in Q03.4.2; floating-point equality follows the
accepted [shared contract](queries.md#floating-point-special-values).

String keys/elements should use the schema's comparison policy: Ordinal by
default, explicit OrdinalIgnoreCase, with matching equality and hashing. Under
ignore-case comparison, Main and main denote the same Map key or Set membership,
although the retained text must preserve its spelling. The later
[Q13 collection decision](mutation-commands.md#collection-mutations-accepted-for-v1)
accepts explicit Map set-by-key as add or replace. The subsequent
[operation-outcome decision](mutation-commands.md#collection-operation-outcomes-accepted-for-v1)
accepts ordinary unchanged results for duplicate Set additions and absent Map/Set
removals in existing collections. A duplicate Set addition preserves the existing
member, including its spelling. The later [collection input contract](#collection-input-contract)
also preserves existing Map-key spelling on explicit set-by-key, rejects duplicate
keys in bulk Map input and coalesces bulk Set duplicates while keeping the first
representation. Full replacement uses its validated input, without merging old key text. A source .NET collection's comparer is
not automatically the database's comparer.

Generated ListView, MapView and SetView are illustrative access shapes. They
provide reads and enumeration over packed data without first reconstructing
complete CLR collections. Returning text as a CLR string may still allocate;
encoded-text views and suitable lookup inputs are needed on allocation-sensitive
paths. Interface adapters must not silently box every element or view. Exact
method names, implemented interfaces and lifetime enforcement remain API work;
Q06.2 records the accepted public protected-view restrictions.
For ordinary ID results, Q06 requires independent owned collection contents;
internal collection views must not silently escape as arena-backed result fields.

An explicit owned copy may materialize ordinary List, Dictionary or HashSet
collections, or fill a reusable caller-owned destination. Nested owned values
must also be copied safely. Read-only access alone does not freeze changing data;
copying a collection view descriptor does not make a snapshot. Collection reads
use the same Q01/Q06 coherence and lifetime rules as other record fields.
Application changes to persisted collections still pass through the master writer;
local Add, Remove or Sort operations on a caller-owned copy change only that copy.

The later [native Map/Set layout decision](physical-storage.md#map-and-set-lookup-layout)
accepts compact small values and hashed large values in the arena. It does not
change these ordinary .NET copy types or the separate protected-view lifetime.
The crossover threshold is measured physical-layout work, not part of the schema's
logical collection contract.

### Implementation choices and their costs

**A. Logical collections over arena storage — accepted direction.** List storage
reuses the ordered-sequence requirement. Map/Set need uniqueness checks and efficient
lookup/membership, adding metadata and update work. A packed hash table is one
candidate: expected constant-time lookup requires suitable hashing, capacity and
collision handling; it is not a worst-case or measured latency guarantee. Growth
can require allocation and rehashing. Compact linear storage can win for tiny
collections; sorted storage trades lookup against insertion/movement cost. The
logical contract does not select a layout or automatic switching between layouts.

Map and Set can reuse typed equality, hashing and collection-storage mechanisms
where their contracts match; Set need not store a dummy value per element. A local
collection lookup index is not a database-wide query index: finding all records
with a particular member is supported by Q07.2.1's predicates without automatically
adding multi-valued secondary indexes.

**B. Keep ordinary CLR collections as the resident representation.** This offers
familiar access and mature implementations, and may be fast for suitable value
types. Each container still has object/backing-storage overhead; reference-bearing
contents add GC tracing, and persistence/replication need a separate representation
or serialization. Sharing strings is still additional work. If both the arena
and reconstructed collections are retained, their storage adds up. This is useful
as a caller-owned projection but is not recommended as the mandatory stored model.

**C. Support ordered sequences only; let applications build Map/Set.** This keeps
the engine simpler and avoids mandatory lookup metadata. Without another index,
membership/key lookup scans the sequence. Building a local Dictionary/HashSet
adds construction time and memory, and an owned copy does not automatically track
later updates. Tiny or infrequently searched collections can suit this approach,
but it leaves the requested reusable lookup abstraction to every consumer.

Collections do not intrinsically require permanent versions or copying their
entire contents per mutation. They do add allocation/reclamation and publication
work: replacing one value, inserting in an ordered sequence and growing a hash
table have different costs. A Map's bucket changes and payload must publish as
one coherent record state. Chunking and rebuild strategies need measurement,
especially for large collections; large owned snapshots still cost proportional
copying and memory. Record size and operation limits remain unresolved.

Q04.2's Dictionary string mode shares identical text payloads. A Map instead
associates a user key with a value. Either Plain or Dictionary string storage can
underlie textual Map keys/values or collection elements; spelling the nested
storage declaration is Q03.4 work. Local Map uniqueness does not replace global
text sharing; Q04.3 separately defines the accepted dictionary scope.

Before selecting a physical layout, compare tiny and large collections with
integer/string keys, hits/misses, enumeration, inserts/removes and resize churn.
Measure lookup cost, retained/peak bytes, allocation and index-build/startup work
with the same logical data. No existing prototype measures these collection
layouts; accepted logical support does not establish their performance.

**Decision:** support List, Map and Set as schema-bound collections
with generated arena views in v1; preserve order/duplicates for List, require
uniqueness without ordering guarantees for Map/Set, and materialize ordinary CLR
collections only on an explicit owned-copy path. Q03.4.2 now settles collection
types and input rules; mutation capabilities have their own owner. Physical algorithms
remain implementation work.

<a id="q03.4.1-scalar-and-primary-key-type-catalog"></a>

## Q03.4.1: Scalar and primary-key type catalog

**Accepted for v1; catalog completed on 13 November 2026.** Ordinary scalar fields
support Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float32, Float64,
Decimal, Boolean, String, Guid, DateOnly, TimeOnly, TimeSpan, DateTimeOffset and Binary.
Primary keys support the eight integer types, String and Guid, always non-null.
Enum/Flags backing supports all eight integer types; Float32 is an ordinary field only.

Guid has a fixed 16-byte payload. DateTimeOffset uses 8-byte UTC ticks with zero offset
on reads; DateOnly, TimeOnly and TimeSpan also use numeric value storage. Binary is an
owned byte sequence with whole-value replacement. The following sections own the
individual contracts and their rationale. Numerical resource limits belong to Q14,
database/key scope to Q12, and collection declarations to Q03.4.2. Shared floating-point
comparison rules remain Q07.3.6. Exact encodings, SDK grammar and index layouts are
engineering work; these do not keep the scalar inventory open.

### Primary-key types and Guid fields

**Accepted on 13 November 2026:** option A. The schema selects the
non-null scalar primary-key type from the catalog above. Keep existing identity,
immutability, string-equality and key-reference contracts. Expose Guid as System.Guid
and store its value as 16 bytes, without mandatory text conversion. Narrow integer
keys retain their chosen width rather than occupying a universal Int64/Guid slot.

#### A. Existing integer widths, String and Guid — accepted for v1

Allow Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, String and Guid. The schema declares
the key type; records of that type do not choose a different key representation per
value. Narrow integers retain their selected width rather than being normalized to
Int64. UInt32/UInt64 were explicitly added by the later
[numeric catalog decision](#uint32-uint64-and-float32).

Guid is an explicit addition to the v1 scalar catalog for both ordinary fields and
keys, exposed as System.Guid and stored as a fixed 16-byte value. It is not stored
as text. Other additional field types are not selected by this decision.

Primary keys are required, non-null values. The existing immutable-key, primary
uniqueness and reference-by-key contracts remain. A reference uses the target's key
type and equality policy; it does not introduce a second public identifier or a
foreign-key integrity check. Q12 fixes scope to the open database and declared type.

String keys follow the accepted field equality policy: Ordinal by default, explicit
OrdinalIgnoreCase where configured, with original text preserved. Primary uniqueness,
reference resolution and hashing must agree with that policy. No implicit text
normalization or GUID-to-string conversion is introduced.

This choice adds no key-generation service, sequential-ID allocator or automatic
conversion of every key to a common format. Float32/Float64, Decimal, Boolean, enum, collection
and document-valued primary keys are outside the accepted v1 key catalog; their
ordinary field support is unaffected. Composite secondary indexes are already
accepted and remain independent of this scalar key catalog.

#### B. Int64 and String only — not selected

Use Int64 for numeric identity and String for textual identity. Applications widen
smaller integer identifiers and encode GUID identifiers as text. Both paths still
need schema validation, consistent equality, primary indexing, reference resolution,
recovery and replication.

This has fewer key-specific accessor/encoding/index cases, but does not remove the
existing numeric field implementations. Widening small identifiers adds key payload
bytes. Textual GUIDs use more bytes than their native representation and require
formatting/parsing at application boundaries that still use System.Guid.

#### Primary-key costs and rationale

| Aspect | A: typed integers, String, Guid | B: Int64 and String |
| --- | --- | --- |
| Read/lookup | Fixed-width lookup for integer/Guid keys; string work depends on content/length | Same paths for Int64/String; GUID callers use text representation |
| Write | Validate the declared type and maintain its primary index; no mandatory text formatting for Guid | Fewer key paths, but application conversion is needed for other ID types |
| Payload memory | Integer keys use 1/2/4/8 bytes; Guid uses 16; strings vary | Numeric keys use 8 bytes; GUID strings require textual payload |
| Implementation | Reuse existing integer codecs/comparers, add Guid and verify each key/reference path | Smaller initial key matrix, more adaptation in consumers |

The widths follow [.NET integer types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types)
and [System.Guid](https://learn.microsoft.com/en-us/dotnet/api/system.guid).
Payload bytes exclude alignment, key/index metadata, reference storage, dictionary
entries and managed result objects. Narrow fields do not guarantee a proportional
reduction in total arena or index memory. Longer keys can increase memory traffic;
no measured lookup/throughput ratio or universal speed ranking is claimed.

The decision selects A. It keeps common caller identities typed and avoids forcing compact
identifiers through wider or textual representations. An instance using Int64 does
not reserve a Guid-sized key in every record merely because Guid is supported.

Before implementation, define consistent Guid comparison/encoding across queries,
indexes, persistence and replicas, then verify key boundary values, typed references,
string equality and duplicate rejection. Numeric/string key input binding, exact
physical index layout, key-length budgets and additional scalar types remain explicit
engineering/open work. This is an accepted design, not a production API or new benchmark result.

### UTC timestamps

**Accepted on 13 November 2026:** option A. An instant uses 8-byte UTC
ticks. The .NET value is DateTimeOffset with zero offset on reads; valid nonzero input
offsets are normalized and not preserved. This is one UTC-normalized temporal type,
not an additional offset-preserving DateTimeOffset storage mode.

#### Timestamp scope and example

This is an instant such as CreatedAt or an event timestamp. A birth date, time of day,
duration or recurring local schedule is a different kind of value. DateOnly, TimeOnly
and TimeSpan are accepted below; they are not converted to UTC. This decision adds
no timezone rules, scheduling operations or timestamp primary keys.

2026-11-13T15:00:00+03:00 and 2026-11-13T12:00:00Z denote the same instant. A keeps
that instant and returns UTC. B additionally keeps +03:00 for the first value so the
original local time and offset can be returned. Neither representation preserves the
original text formatting. An offset alone is not a named timezone with daylight-saving
rules, as explained in the [.NET date/time guidance](https://learn.microsoft.com/en-us/dotnet/standard/datetime/choosing-between-datetime).

#### A. UTC instant, no retained input offset — accepted for v1

Use one Int64 value with the semantics and valid range of
[DateTimeOffset.UtcTicks](https://learn.microsoft.com/en-us/dotnet/api/system.datetimeoffset.utcticks?view=net-10.0):
100-nanosecond units since 0001-01-01T00:00:00, following .NET's tick model without
separate leap-second representation. This preserves .NET timestamp resolution;
it is not a claim that clocks are accurate to 100 nanoseconds.

Use DateTimeOffset as the accepted .NET value mapping. Valid offset-bearing input
is converted to UTC; reads return DateTimeOffset with zero offset. The original
offset is intentionally not recoverable. This normalization must be explicit in
the field contract, including generated and runtime-schema access.

Do not infer an offset from the machine running the master or child. If DateTime
input is exposed, accept Kind.Utc; Local/Unspecified input needs explicit application
conversion to an unambiguous instant rather than implicit host-timezone interpretation.
Exact overloads and text-input grammar remain engineering work.

Equality, chronological comparison, sorting and indexes use UTC ticks. All nodes
must share the same unit/range/encoding semantics. Nullable follows the existing
explicit-field rule, with validity metadata outside the value payload. Ticks are
scalar data, not database history positions or document revisions.

#### B. UTC instant plus original offset — not selected

Store UTC ticks and the original offset in minutes. A compact candidate uses 8 bytes
plus a signed 2-byte offset; it preserves valid DateTimeOffset values without storing
their display strings. Validate both the instant and reconstructed local value against
.NET range/offset constraints. Read back the original offset-bearing value.

Equality and chronological order can still use UTC ticks, following
[DateTimeOffset.Equals](https://learn.microsoft.com/en-us/dotnet/api/system.datetimeoffset.equals).
The offset remains stored information even when two values compare as the same
instant. Saving another offset is not necessarily the same stored representation.
This does not require timezone lookups or retaining named-zone rules.

#### Timestamp costs and rationale

| Aspect | A: UTC only | B: UTC plus original offset |
| --- | --- | --- |
| Read | Read ticks and construct a UTC value | Also read the offset and construct the original representation |
| Search/sort | Compare the instant's integer value | Can use the same UTC comparison/index key |
| Write | Validate/normalize the input once | Validate/store the instant and offset |
| Value payload | 8 bytes | Compact candidate: 10 bytes |
| Complexity | One stored value and explicit UTC normalization | Extra field component and round-trip validation |
| Information retained | Exact instant at .NET tick resolution | Same instant plus original offset |

These are value payloads, not measured record/index footprints or CLR struct sizes.
Alignment, metadata, nullable representation and allocator size classes can alter
actual memory use. B need not enlarge an index that stores only the UTC comparison
key. Neither option requires per-value managed objects or guarantees a speed ratio.

The decision selects A for v1: compact, timezone-independent instant storage and
integer range comparisons. B is not selected. DateOnly, TimeOnly and TimeSpan
support is also accepted, with their own distinct numeric representations below.

Before implementation, verify equivalent instants with different offsets, boundary
values, sub-millisecond preservation, non-UTC input handling and identical results
through ordinary fields, queries, persistence and replicas. Measure layout/index
overhead before claiming memory or latency savings. This is an accepted design; no temporal engine implementation or new
benchmark is supplied by this record.

### DateOnly, TimeOnly and TimeSpan

**Accepted on 13 November 2026:** support DateOnly, TimeOnly, TimeSpan
and DateTimeOffset in v1. Numeric storage is selected for the date-only,
time-only and duration types. Use the following direct .NET-compatible numeric
representations; byte order and field alignment remain format/layout implementation.

| Logical/.NET type | Numeric value in the arena | Value payload |
| --- | --- | --- |
| DateOnly | Int32 day number since 0001-01-01, within the .NET DateOnly range | 4 bytes |
| TimeOnly | Int64 ticks since midnight; nonnegative and less than one day | 8 bytes |
| TimeSpan | Signed Int64 duration in ticks, including negative durations and the full .NET range | 8 bytes |
| DateTimeOffset | Int64 UTC ticks; return offset zero, discard the input offset | 8 bytes |

The mappings follow [DateOnly.DayNumber](https://learn.microsoft.com/en-us/dotnet/api/system.dateonly.daynumber?view=net-10.0),
[TimeOnly.Ticks](https://learn.microsoft.com/en-us/dotnet/api/system.timeonly.ticks?view=net-10.0)
and [TimeSpan.Ticks](https://learn.microsoft.com/en-us/dotnet/api/system.timespan.ticks?view=net-10.0).
A tick is 100 nanoseconds. Preserve the full .NET value resolution rather than
rounding durations or times to whole seconds or milliseconds. Nullability metadata
and alignment are outside these payload sizes; no CLR object per value is stored.

DateOnly is a calendar date, not an instant at midnight UTC. TimeOnly is a wall-clock
time within a day, not a duration. TimeSpan is a signed duration, not a calendar
period in months/years; it is not restricted to positive values or one day. Only
DateTimeOffset describes an instant and undergoes UTC normalization.

Retain these distinct logical types in the schema, generated accessors and runtime
access. Numeric backing does not make arbitrary Int32/Int64 values interchangeable
with them. Validate their respective ranges; compare, sort and index values of the
same logical type using day numbers/ticks, with signed comparison for TimeSpan.
A time-of-day comparison is within a day; crossing midnight does not imply a hidden
calendar or scheduling calculation. Existing null, result-ownership and document
atomicity rules remain. Their field support does not expand the primary-key catalog.

Reads construct value types or expose protected typed views; normal numeric-backed
comparison needs no text parsing or timezone lookup. Writes encode and validate the
value, then use the ordinary mutation path. Supporting these types adds codecs,
bindings and boundary tests, not a temporal object heap or a new storage subsystem.
No per-value allocation or speed ratio is claimed for the unimplemented SDK.

Verify minimum/maximum dates, midnight/day-end, negative and boundary durations,
sub-millisecond values, UTC normalization and consistent query/index/recovery/replica
round trips. General date arithmetic, named timezones and recurring schedules are
not introduced by storing these types. This record adds no runtime implementation.

### Binary fields

**Accepted on 13 November 2026:** option A. Support Binary as a distinct
schema type in v1, with whole-value replacement. It is an ordered byte sequence
owned by its root document, using the existing arena/chunk and owned/protected-read
contracts. No byte-range patch commands, automatic text conversion or separate
binary storage service are introduced. This is a design contract, not implemented SDK.

#### A. Dedicated Binary field — accepted for v1

Store a length and byte payload owned by the containing document. Preserve byte order
and all values exactly. Empty differs from null; null remains explicitly declared.
Small payloads use the accepted contiguous allocation path, large payloads use bounded
chunks. No per-byte object, implicit Base64/text encoding or string dictionary is needed.

An ordinary read returns owned byte data, or copies into a caller-owned destination.
Protected access can expose read-only byte spans for contiguous chunks during the
existing short read lifetime. A chunked value does not promise one contiguous span
over the entire payload. Copying or assembling a contiguous result is explicit work.
Views cannot outlive protection or be used to mutate the stored document directly.

V1 writes replace the whole Binary field through Save or the explicit field-assignment
API, preserving root atomicity, input lifetime, memory admission and durable completion.
This decision does not add byte-range patch commands, unbounded upload queues or
streaming while holding a document gate. Existing instance/query budgets still apply.

The baseline capability is storage, retrieval and field projection. Content-search
operators and binary sorting/indexing are not selected by this field decision.
The later [collection type contract](#q03.4.2-nested-and-collection-declarations)
excludes Binary Map keys and Set elements in v1. Unsupported operations must fail
explicitly rather than use CLR reference equality or treat a byte buffer as text.
Binary is not a primary-key type. SDK shape and payload limits remain engineering work.

#### B. List<UInt8> only — not selected

Represent bytes as the existing ordered byte collection. This reuses its element
access and mutation semantics without a distinct schema type or generated Binary
accessor. Callers use the List contract even when the value is conceptually opaque.

A packed byte list can have the same payload density as Binary; it does not inherently
require one allocation or box per byte. Bulk-copy access could also be implemented for
such lists, so B is not assumed to be slower. Converting a list into an owned byte array
may require a copy, depending on the chosen access path, just as A's owned result does.

#### Binary costs and rationale

| Aspect | A: Binary | B: List<UInt8> |
| --- | --- | --- |
| Read | Explicit bulk byte access/copy | Existing collection access; a bulk path is possible |
| Write | Whole value replacement | Collection replacement and existing element commands |
| Payload memory | One byte per byte plus length/chunk metadata | Can be the same with packed storage |
| Implementation | One logical type, bulk accessors and field codec integration | No additional logical type; reuse list mechanics |
| Caller meaning | Opaque binary content | Collection of independently addressable byte elements |

The decision selects A for binary application payloads, hashes and serialized values. The benefit
is a clear bulk-data contract, not a claimed compression or speed advantage over a
packed list. Reuse the same allocation/chunk machinery rather than adding a second
binary allocator or external storage dependency. Schema key and field types remain
separate concerns.

Before implementation, verify empty/null, every byte value, order, chunk boundaries,
owned-copy isolation, protected lifetime and whole-field mutation/recovery/replication.
Measure copy throughput, peak temporary memory and chunk metadata; no speed ratio or
hard payload limit is selected here. No Binary engine/API implementation exists yet.

### UInt32, UInt64 and Float32

**Accepted on 13 November 2026:** option A. Include UInt32, UInt64 and
Float32 in v1. UInt32 and UInt64 also support primary/reference keys and enum/Flags
backing. This completes Q03.4.1 without changing the already accepted type contracts.

#### A. Add UInt32, UInt64 and Float32 — accepted

| Schema type | .NET type | Value payload | Purpose |
| --- | --- | --- | --- |
| UInt32 | uint | 4 bytes | Full unsigned 32-bit IDs, counters and numeric values |
| UInt64 | ulong | 8 bytes | Full unsigned 64-bit IDs, counters and numeric values |
| Float32 | float / System.Single | 4 bytes | Compact approximate measurements and numeric arrays |

The schema selects the type. Retain its width and signedness in storage; do not
automatically widen or change existing fields according to observed values. The
current Float64 and Decimal choices remain available and unchanged.

UInt32 and UInt64 also extend the supported integer primary/reference-key
catalog and enum/Flags backing types. Uniqueness, numeric ordering and query/index
comparison must use unsigned semantics over the full range. A value above Int64.MaxValue
must not be silently reinterpreted as a negative signed key. Float32 is an ordinary
field type, not a primary key or enum backing type.

Float32 uses binary32 representation. It uses half the value bytes of Float64 at
lower precision and range; it is not a lossless compressed double. Applications choose
it when that precision suits the data. Do not migrate or downcast existing Float64
or Decimal data automatically. The approximate precision is 6–9 decimal significant
digits for Float32 versus 15–17 for Float64, per the
[.NET floating-point documentation](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/floating-point-numeric-types).

Integer widths and ranges follow the
[.NET integer catalog](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/integral-numeric-types).
UInt32's entire range fits Int64, but UInt64's does not. Neither is a dynamically sized
integer or an instruction to use platform-dependent nint/nuint storage.

#### B. Keep the earlier numeric set — not selected

Retain Int8, UInt8, Int16, UInt16, Int32, Int64, Float64 and Decimal only.
Applications can widen UInt32 to Int64 and Float32 to Float64. Full-range UInt64
requires an explicitly suitable representation, such as Decimal for ordinary integer
data or String for keys, with the corresponding different storage/API semantics.
Do not silently wrap a UInt64 value through Int64 or claim that every ulong fits long.

This reduces supported numeric variants and their validation matrix. However, callers
adapt common .NET types, UInt32/Float32 payloads can occupy twice their source width,
and external unsigned enum definitions still require an explicit compatible binding.

#### Numeric extension costs and rationale

| Aspect | A: add all three | B: retain current set |
| --- | --- | --- |
| Read | Typed fixed-width values without mandatory caller adaptation | Widened numeric values or explicit alternative representations |
| Write | Validate/encode the declared type and maintain compatible indexes | Fewer variants, with adaptation performed by callers |
| Memory | 4-byte UInt32/Float32 and 8-byte UInt64 where selected | Common widening paths use 8 bytes; full-range UInt64 adaptation can cost more |
| Complexity | Codecs, generated/runtime access, unsigned ordering and additional boundary tests | Smaller type matrix, more conversion responsibility outside the engine |

Payload sizes exclude alignment, nullable/index metadata and allocator granularity.
Adding a type does not increase every record's footprint. Smaller payloads can improve
memory density, but no proportional throughput or total-memory reduction is established.
Float32's precision tradeoff matters even if an individual operation is no faster.

The decision selects A for v1: these are bounded extensions of the existing typed
numeric paths,
useful for compact data and .NET interoperation. They do not require a new allocator,
object graph representation or persistence backend.

#### Catalog boundary and remaining engineering

The accepted Boolean, String, Guid, temporal types and Binary remain unchanged. This
completes the v1 scalar inventory alongside the eight fixed-width integer
types, Float32/Float64 and Decimal. Arbitrary CLR serialization, BigInteger, Half,
Int128/UInt128, Char and a separate Kind-preserving DateTime type are not added.
Existing explicitly typed enum/Flags, references and owned collections keep their
own contracts; the accepted collection type positions are recorded in Q03.4.2 below.

Preserve the accepted query-binding principle: bind parameters to the field domain
before execution, with checked range and the existing exactness requirements. Do not
introduce silent lossy typed-parameter narrowing while extending the conversion matrix.
Float32 literals need their own field-domain parser. The accepted
[NaN/infinity/signed-zero contract](queries.md#floating-point-special-values) applies
to both floating-point widths; additional cross-width binding cases remain Q07.3.6.

After the catalog decision, schema spelling/codecs and concrete layout are E04/E05/E06
work; measured capacities and payload/key limits belong to Q14. Verify unsigned maxima,
high-bit Flags, signed/unsigned boundaries, Float32 precision/range, equivalent query/
index behavior and persistence/replica round trips. This records an accepted design,
not an implemented numeric codec or benchmark.

The [collection type-position decision](#q03.4.2-nested-and-collection-declarations)
and its input rules are accepted below.

<a id="q03.4.2-nested-and-collection-declarations"></a>

## Q03.4.2: Nested and collection declarations

**Accepted for v1 on 13 November 2026:** option A for type positions and the complete
option A collection input contract. Q03.4.2 is closed.
Complex types are allowed in List elements and Map values. Map keys and Set elements
use scalars except Binary, enum/Flags or typed references. Each position has a declared
type; existing order, ownership, equality and mutation contracts remain unchanged.

Q03.3 already settled collection kinds, List ordering and Map/Set uniqueness. This
decision settles where each type may appear, without reopening Q03.3. The accepted
[input contract](#collection-input-contract) below also settles position nullability,
duplicate bulk input and retained Map-key spelling.
No collection implementation or benchmark is supplied by this decision.

### The distinction

A List only needs to retain each element and its position. A Map also needs to
identify an existing key; a Set must recognize an existing member. Those latter
operations need a stable definition of equality. An owned object's equality could
mean its fields, selected fields, its storage location or some user-supplied rule;
these produce different results. Arena addresses cannot be logical identity because
compaction may move values.

A Map key is local to its collection, not the root's primary key. The permitted
catalog can therefore be wider than primary-key types without changing root identity.
Examples below describe schema shapes, not finalized JSON or generated C# syntax.

### A. Rich values, simple collection identity — accepted

| Position | Accepted v1 type support |
| --- | --- |
| List element | Any supported scalar, enum/Flags, typed key reference, schema-defined owned structure or nested List/Map/Set |
| Map value | The same value catalog as List elements |
| Map key | Scalars except Binary, plus enum/Flags and typed key references; no owned structures or collections |
| Set element | The same identity catalog as Map keys; no owned structures or collections |

The scalar identity catalog includes the eight integers, Float32/Float64, Decimal,
Boolean, String, Guid and the four temporal types. Every schema position has a
specific declared type. This does not add heterogeneous object lists, arbitrary CLR
serialization, composite primary keys or equality supplied by application code.
Binary remains permitted as a List element or Map value; content equality for Binary
keys/Set membership is deferred with complex value identity.

Examples:

- `List<OwnedPoint>` stores each point's declared fields in order, including duplicates.
- `Map<String, OwnedSettings>` looks up a settings structure by its string key.
- `Map<String, List<Reference<Node>>>` groups ordered node references by name.
- `Set<Reference<Node>>` tests unique target identity without copying/comparing nodes.
- `List<Binary>` stores ordered byte values; `Set<Binary>` is outside option A for v1.

Owned values remain under the root document's gate and lifetime. Nested collections
form an ownership tree; no shared owned children or ownership cycles are added.
References can target shared or cyclic graphs without making targets owned children,
adding referential integrity or providing a graph-wide snapshot. An algorithm may
still use a List of structures, or a Map from an explicit key to those structures.

Equality must be the schema's equality, consistent across scans, membership, hashing,
replication and rebuild. String identity uses Ordinal or explicit OrdinalIgnoreCase;
enum/Flags use numeric values, not aliases. References use declared target identity
and the target key's equality, never arena addresses or target contents. [Q12](database-scope.md)
now accepts reference resolution within the source database in v1. Floating-point
NaN/infinity/signed-zero behavior follows the accepted
[Equals/CompareTo contract](queries.md#floating-point-special-values). Collections
share query/index equality and matching hashes rather than using C# operators or
a separate collection-only floating-point policy.

### B. Also allow complex values as keys and Set elements — not selected

Allow owned structures, Binary and nested collections in Map-key/Set-element
positions. A complete contract must define structural equality: which fields count,
nulls, collection order, unordered Map/Set equality, reference boundaries and matching
hashes. Changes affecting identity must validate uniqueness and update lookup state
atomically. A user-defined comparer is not a substitute for a persisted, replicated
contract that all nodes can reproduce.

This supports shapes such as `Set<OwnedPoint>` or `Map<OwnedCompositeKey, Value>` directly.
It adds useful flexibility when value equality is the actual requirement. However,
lookup and mutation can inspect a large value or tree; a cached hash uses metadata
and still needs collision equality and invalidation rules. It does not automatically
reduce the stored payload or make an existing scalar-key collection faster.

### Read, write, memory and implementation effects

| Aspect | A: scalar/reference identity | B: complex identity too |
| --- | --- | --- |
| Reading values | Traverse rich owned values directly through existing protected views, or copy requested data | Same access; no inherent improvement for ordinary value reads |
| Lookup/membership | Fixed-size comparisons for fixed-size values; String work still depends on text length | Structural comparison/hashing may visit many fields or collection elements |
| Writes | Validate declared types and existing uniqueness; update the selected value or membership | Identity-affecting complex changes also require structural validation and lookup maintenance |
| Memory | Owned data plus the selected collection's normal metadata; references avoid copying target contents | Similar payloads; cached structural hashes/extra identity metadata may cost memory |
| Complexity | Reuse type codecs, scalar equality and typed-reference identity | Additional recursive equality, hashing, mutation and migration contracts/tests |

No O(1) guarantee or measured speed ratio is implied: hash tables, sorted arrays and
other physical structures are still implementation choices. A does not require a
managed Dictionary/HashSet for every stored collection. Typed nesting uses the same
arena ownership/access machinery; numerical nesting and payload limits remain Q14.

The decision selects A for v1. It preserves rich hierarchical documents and graph references
while keeping uniqueness understandable and bounded by the selected identity value.
Complex identity can be considered later if measured application needs justify it.

### Collection input contract

**Accepted on 13 November 2026:** option A in full, including all common
nullability, Set coalescing and Map-key spelling rules. Collection types and input
behavior are now settled for v1. API grammar and physical layout are not implied.

#### Why full-input rules were needed

A full Save, Insert, Replace, Upsert or collection replacement supplies a completed
collection value. What happens if two input entries identify the same Map key?
For example, an input sequence contains ("Main", 1) and ("main", 2), while the
schema selects OrdinalIgnoreCase. They identify one key even if the source container
considered them different. The accepted Map set-by-key command adds or replaces one
entry; it did not settle duplicate entries inside a single whole-value input.

The input rules below also close the remaining collection nullability and key-text
questions within Q03.4.2. No extra question identifier is introduced.

#### Accepted collection input rules

The decision selects all of the following rules together with option A:

- Apply the existing explicit-nullability principle separately to the collection
  itself and to List elements, Map values and Set elements: non-null by default,
  null only when explicitly permitted at that position in the schema. Nullable Set
  elements permit one null member. A null collection remains distinct from an empty
  collection, and a null element does not become a default value.
- Map keys are always non-null. An explicit nullable key declaration is rejected;
  this restriction does not change nullability of Map values or of the Map itself.
- Lists retain input order and duplicates. Bulk Set input coalesces equal members
  under schema equality, retaining the first supplied representation, including
  string spelling. This extends the already accepted unchanged outcome of adding
  an existing Set member to a whole-value input rule.
- Explicit Map set-by-key against an existing equivalent key retains that stored
  key's spelling while replacing its value. An insertion stores the supplied key.
  Full document/collection replacement instead uses its validated input contents;
  it does not merge previous key spelling into the replacement. No automatic case
  normalization or caller-comparer persistence is introduced.

A typed null still requires a compatible declared type. These rules do not add
structural equality, Binary identities or an independent floating-point policy.
Floating-point edge cases use the accepted [shared contract](queries.md#floating-point-special-values).
Existing root atomicity, pre-publication
validation, input lifetime and resource limits apply to collection construction.

#### A. Reject duplicate Map keys in whole-value input — accepted

Validate uniqueness using the schema's key equality. Any second equivalent key
rejects the complete submitted document/mutation, even if both values are equal.
Return a validation error identifying the field and duplicate-key condition. Keep
existing persisted data and revision unchanged; do not publish a partial collection.

This makes a completed Map value unambiguous. Applications that intend merging can
construct the final Map explicitly or use accepted set-by-key operations. It does
not change the outcome of those explicit commands or require automatic change tracking.
Set input has no competing associated value, so it can coalesce duplicates under the
common rule without choosing which value to discard. List duplicates remain data.

#### B. Use the last duplicate Map entry in whole-value input — not selected

Accept duplicates and retain the last supplied equivalent entry's value and key
spelling. The final Map remains unique. The same common nullability and Set rules
apply. Full-input duplicate precedence is explicit and depends on supplied order;
Map enumeration after storage still has no order guarantee.

This is convenient for intentional override lists and imports. It also means an
unexpected duplicate silently discards an earlier value. The caller must supply an
intentional input order when precedence matters; changing that order can change
stored values despite producing the same set of key identities.

#### Effects on speed, memory and implementation

| Aspect | A: reject duplicate Map input | B: last entry wins |
| --- | --- | --- |
| Reads | Same final unique Map representation and lookup contract | Same; no inherent read-speed advantage |
| Writes | Uniqueness checking while constructing/validating the collection; fail on a duplicate | The same duplicate detection, plus value/key replacement when duplicates occur |
| Temporary memory | Bounded construction and uniqueness state using the chosen collection algorithm | Similar state; discarded/replaced input values may add temporary work |
| Stored memory | Only validated unique entries; nullability metadata where declared | The same logical unique shape; no guaranteed memory saving |
| Complexity | Explicit validation outcome, easier diagnosis of ambiguous input | Additional precedence behavior; convenient automatic merging but order-sensitive results |

Map and Set already require identity checking. No separate unbounded validation
copy or a second full arena is required by this contract. Actual lookup structures, reuse and temporary
bytes require measurement; neither option has a demonstrated throughput ratio.
Nullability may need validity metadata and checks, but does not require a separate
managed object for every nullable value.

The decision selects A with the common rules. This completes Q03.4.2's input behavior
as one contract. The implemented grammar, API signatures, physical collection layout,
numerical bounds and shared float/reference semantics remain in their existing owners.

### Collection engineering and shared contracts

Q03.4.2 is closed. Floating-point edge cases follow the accepted shared Q07.3.6 contract. Q12 accepts
same-database reference context in v1; it is reused across fields, keys and collections.
Schema grammar, lookup structures, numerical capacities and nesting limits remain
engineering or measurement work in E01/E05 and Q14, not new collection-kind decisions.
Implementation verification must cover duplicate keys under schema equality, atomic
validation failure, nullable positions, first-representation Set coalescing, preserved
Map-key spelling on set-by-key and input-owned spelling on full replacement.

<a id="q03.5-should-schema-fields-support.net-enums"></a>

## Q03.5: Should schema fields support .NET enums?

**Accepted on 12 November 2026: option A.** Ordinary enums are logical schema
types stored as fixed-width integers and exposed as typed .NET enums. Int32 is
the default underlying type, with an explicit choice of another supported integer
type. Only values declared in the active schema are accepted. Q03.6 separately
accepts Flags combinations, and Q03.7 accepts numeric comparison/order by the
underlying type. Q03.8 accepts aliases as schema metadata; numeric evolution uses
the existing controlled schema-change rules.

### Representation common to both options

An enum definition in the schema owns its member names, stable explicit numeric
codes and underlying integer type. A record stores the code; names are metadata,
not repeated strings in every record. The accepted default underlying type is
Int32, matching the usual C# default. The schema may explicitly select another
supported integer width. No automatic narrowing or widening based on observed
values is included.

Illustrative generated .NET shape, not an implemented schema compiler:

```csharp
public enum ProcessingModeEnum : byte
{
    Disabled = 0,
    Manual = 1,
    Automatic = 2
}
```

With UInt8 selected, Automatic has the one-byte value payload 2. An Int32-backed
definition would use four payload bytes for the same code. Alignment, null
metadata and indexes still affect total storage. Both the generated .NET type
and its storage accessor must agree on the declared width and signedness.

The completed integer catalog permits Int8, UInt8, Int16, UInt16, Int32, UInt32,
Int64 and UInt64 for enum/Flags backing. UInt32 and UInt64 were explicitly accepted
in the later [numeric extension](#uint32-uint64-and-float32). A conflicting externally
defined enum must fail
explicit binding validation or use an explicit conversion; it must not be silently
reinterpreted as a different physical type. The exact binding API remains open.

Schema-defined types are the durable authority. Generate an enum from the schema,
or validate an explicitly bound existing enum against its definition. The engine
does not require a domain assembly to interpret stored codes. Names can be bound
to numeric values when compiling a future DSL query instead of formatting or
parsing a string for every record. Different enum definitions remain different
logical types even if they use the same numeric code.

Q03.2's nullability rule still applies. Code 0 is an ordinary value and is valid
in a closed enum only when declared; it is not null or a replacement for an
unknown code. A zero-initialized CLR enum does not itself establish schema validity.

### Option A: accept only declared values — accepted

For the example, writes of 0, 1 and 2 are valid. A write of 99 is rejected as a
validation failure of the mutation. The engine must not silently replace it with
Disabled, drop the field or ignore the document. Membership is checked against
the active database schema, not only against whatever CLR type a caller used.

- **Reads:** stored codes map to typed enum values without name lookup or a string
  allocation on the ordinary path. This can use integer reads/conversions without
  boxing; record materialization still has its existing costs.
- **Writes:** add membership validation before accepting a value, in addition to
  width/range checks. Prepare validation from schema metadata; no repeated
  reflection/name parsing is required by this design. Its actual cost is unmeasured.
- **Memory:** the value payload is the selected integer width, the same as option B.
  Member metadata and any validation lookup structure cost memory per definition,
  not a name string per stored value.
- **Correctness and evolution:** the active schema defines the valid ordinary
  states. A new code must be introduced through a controlled schema change before
  use; generated/bound client compatibility must be coordinated.
- **Complexity:** enum schema metadata, binding/generation and validation are needed.
  The storage/index numeric machinery can be reused; it is not an object graph or
  a reference-integrity subsystem.

### Option B: allow any representable numeric value — not selected

The schema still names known codes, but 99 may be stored even when unnamed.
Reads preserve that number rather than inventing a name or mapping it to 0.
Application code must handle it explicitly. C# permits numeric enum values without
a declared member, as described in the
[C# enum reference](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/enum).

- **Reads:** the numeric access path and payload are the same. Application branches
  must handle unnamed values; a typed enum variable is not proof of membership.
- **Writes:** retain range/type checks but omit declared-member validation. This
  can reduce validation work; it is not a measured throughput improvement.
- **Memory:** the same per-value payload. Membership lookup metadata can be smaller
  or absent, but names and enum definitions still exist for typed access.
- **Correctness and evolution:** permits new numeric codes without first declaring
  a member, but also accepts accidental codes. It does not by itself make schema
  layouts or old generated clients compatible with arbitrary schema changes.
- **Complexity:** less membership enforcement in the engine, more unknown-value
  handling in applications and their algorithms.

### Boundaries and follow-up decisions

Option A is accepted for ordinary schema-bound enums. An open-value mode is not
included in this decision.
Persist explicit numeric codes; reordering declarations must not renumber stored
values. Renaming, removing, reassigning or adding members must go through the
schema's compatibility rules rather than reinterpret existing records implicitly.
An added name/code may leave payload width unchanged, but that does not decide
whether older generated clients can continue using the definition. Q03's controlled
migration policy remains applicable where data/layout changes are required.

Flags represent combinations and need a different validation rule from ordinary
membership. In particular, Enum.IsDefined can reject a combination of declared
bits unless that combined value is itself a named member; see
[Enum.IsDefined](https://learn.microsoft.com/en-us/dotnet/api/system.enum.isdefined?view=net-10.0).
Q03.6 separately accepts Flags combinations and unknown-bit validation, and
Q03.7 accepts numeric ordering and Q03.8 accepts aliases. Name changes are a
schema/application responsibility; data and layout changes retain Q03's rules.

**Decision recorded:** schema-defined ordinary enums with numeric storage, Int32
by default, an explicit supported underlying type when needed and rejection of
undeclared values. Typed .NET access uses the same fixed-width numeric storage.
Exact schema grammar, binding APIs and migration details remain to be implemented
and verified. Flags follow their separately accepted Q03.6 contract.

<a id="q03.6-should-flags-allow-combinations-of-declared-bits"></a>

## Q03.6: Should Flags allow combinations of declared bits?

**Accepted on 12 November 2026: option A.** V1 includes an explicit Flags mode
for a schema enum. Store one fixed-width integer mask, permit any combination
of declared independent bits and 0 as the empty set, and reject unknown bits.
This is a separate logical contract from Q03.5's ordinary enum membership rule.

### What changes compared with an ordinary enum?

An ordinary enum selects one declared numeric value. Flags represents a set of
independent options within one integer. Illustrative generated .NET shape:

```csharp
[Flags]
public enum AccessFlagsEnum : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Export = 4,
    ReadWrite = Read | Write
}
```

Read, Write and Export occupy separate bits. Read | Export is the number 5 and
is valid even without a named member for 5. ReadWrite is a
convenient name for an existing combination; it adds no new independent bit.
The .NET attribute describes this bit-field interpretation; the database's schema
and validator must enforce its own rules. See
[FlagsAttribute](https://learn.microsoft.com/en-us/dotnet/api/system.flagsattribute?view=net-10.0).

| Value | Meaning in the example | Accepted result |
| --- | --- | --- |
| 0 | No options selected | Valid empty set |
| 3 | Read and Write | Valid, named combination |
| 5 | Read and Export | Valid, unnamed combination |
| 7 | Read, Write and Export | Valid, unnamed combination |
| 8 | An undeclared fourth bit | Reject the mutation |
| 9 | Read plus an undeclared bit | Reject the mutation; do not strip the unknown bit |

### Accepted v1 rules

1. The schema explicitly identifies Flags semantics; an arbitrary numeric enum
   does not become Flags merely because some member values happen to be powers
   of two. Generated/bound .NET types must agree with that schema contract.
2. Declare independent bits explicitly, each with one set bit within the chosen
   integer width. Optional named combinations may contain only those declared
   bits. Composite names do not expand the set of permitted bits.
3. Any subset of the declared bits is valid. This includes 0 as the empty set,
   even without a name for it; None = 0 is the recommended name. Zero remains
   distinct from null, which still requires Q03.2's explicit nullability.
4. A value with any undeclared bit fails validation. Do not silently remove bits,
   preserve them as an open-value mode or replace the value with 0 in v1.
5. Reuse Q03.5's storage choice: Int32 by default or an explicit supported integer
   type. There is no extra field payload for the Flags marker, no per-value list
   and no string representation in records. The marker and permitted-bit mask
   belong to enum schema metadata.

The contract permits all combinations of independent options. It does not enforce
business rules such as two options being mutually exclusive. An ordinary enum
with explicitly enumerated states may be more appropriate for those values.

### Validation and fixed-width arithmetic

Build AllowedBits from declared independent bits when loading the schema. Validate
the input's underlying type/range before converting it to that width's bit pattern.
Conceptually, with WidthMask containing exactly that width's bits:

```text
UnknownBits = ValueBits AND (WidthMask XOR AllowedBits)
Accept when UnknownBits = 0
```

For the example, AllowedBits is 7. This test accepts 5 without enumerating all
possible combinations and rejects 8. Enum.IsDefined is not the appropriate sole
Flags validator: an unnamed combination can consist entirely of valid bits.
See [Enum.IsDefined](https://learn.microsoft.com/en-us/dotnet/api/system.enum.isdefined?view=net-10.0).

Signed backing types need width-aware bit interpretation, including an explicitly
declared sign bit; numeric negativity alone is not the membership test. For
example, Int32.MinValue has one set bit in its 32-bit representation. Widening
must not introduce permitted high bits through sign extension, and an out-of-range
input must not be truncated before validation. C# promotes narrow integer operands
for many [bitwise operations](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/bitwise-and-shift-operators),
so the exact implementation needs focused width/sign tests.

A shortcut such as All = -1 sets every bit of a signed backing type. It is valid
only if every one of those bits is explicitly declared as permitted; a composite
name cannot make reserved bits valid. Prefer All as an explicit combination of
the currently declared options. Adding another option later does not silently
change the meaning of an already stored numeric mask.

### Options considered and costs

| Effect | A: explicit Flags mode — accepted | B: ordinary enums only in v1 — not selected |
| --- | --- | --- |
| Allowed values | Any combination of known bits, including the empty set | Only explicitly declared numeric values; each desired combination needs a member |
| Reads | Read one integer; inspect options with bit operations without string conversion | Read the same integer; unnamed combinations remain invalid |
| Writes | Validate type/range and check for unknown bits against a prepared mask | Reuse Q03.5's declared-member validation |
| Memory | Same per-value integer payload; per-definition mode and allowed-bit metadata | Same per-value payload; more named values may be needed to describe combinations |
| Implementation | Add schema mode, bit validation and typed binding/generation cases | Reuse ordinary enums; application/schema authors must enumerate valid combined values |

For a byte-backed definition, both options use one payload byte per value. Eight
independent declared bits provide 256 possible combinations including zero; no
256-entry value table is required by the mask validator. This is arithmetic, not
a measured engine-memory footprint or latency result. Alignment, indexes, nullable
metadata and overall read/write synchronization still have their existing costs.
The validation work is a small fixed sequence of integer operations after the
schema mask is prepared; no total throughput improvement is claimed.

### Query and evolution boundaries

Equality on the complete mask is different from asking whether a particular bit
is present. A normal equality index can locate one complete numeric value; it
does not automatically provide a selective lookup for every Flags membership
predicate. HasAny/HasAll-style predicates, their zero-mask behavior and specialized
index support belong to Q07.3, not to this storage decision. Direct protected .NET
reads can inspect the mask without materializing a collection.

Unknown bits remain invalid until introduced through the active schema's
controlled change process. Replication, durable recovery and generated bindings
must agree on the definition; this decision does not introduce mixed-schema
operation or permanent historical values. Q03.7 accepts numeric ordering and
Q03.8 accepts aliases as metadata. Flags changes also obey the existing schema,
record mutation and read-protection boundary.

**Decision recorded:** explicit Flags enums in v1, permitting any combination
of declared bits and 0 as the empty set; unknown bits are validation errors.
The accepted ordinary-enum membership rule remains unchanged. Schema grammar,
generated accessors and validation implementation still need verification.

<a id="q03.7-how-should-enums-and-flags-be-ordered"></a>

## Q03.7: How should enums and Flags be ordered?

**Accepted on 12 November 2026: option A.** Enum and Flags comparisons, sorting
and ordered indexes use numeric order by the declared underlying integer type.
Member names and declaration order do not define storage/index order. This selects
the comparison contract, not the physical index structure or DSL syntax.

### Why the choice matters

Stable numeric codes need not match a display or workflow sequence. Consider
this illustrative ordinary enum with an Int32 backing:

```csharp
public enum JobStateEnum
{
    New = 10,
    Running = 30,
    Done = 20
}
```

| Ordering rule | Ascending result |
| --- | --- |
| A: numeric codes — accepted | New (10), Done (20), Running (30) |
| B: explicit business ranks following this workflow | New (10), Running (30), Done (20) |
| C: names in ordinal text order | Done (20), New (10), Running (30) |

The three orders answer different questions. Reordering declarations or changing
a label must not accidentally choose a different comparison rule for existing
records. The accepted numeric codes remain the durable values in every option.

### A. Use the underlying numeric order — accepted

For ascending order, compare the actual signed or unsigned values according to
the schema type. Descending order reverses that non-null ordering. Int32 values
-1, 0 and 1 therefore sort in that sequence; UInt8 values 0, 128 and 255 remain
non-negative and sort numerically. Preserve width and signedness when widening
values for comparison; treating a negative signed value as an unsigned bit
pattern would change its position.

For Flags, compare the complete stored mask as that underlying number. This does
not count enabled bits, rank permissions or express set containment. For example,
Read = 1, Write = 2, ReadWrite = 3 and Export = 4 sort as 1, 2, 3, 4, although
ReadWrite contains more options than Export. With signed Int32 storage, a valid
mask containing the sign bit can be negative and sorts before 0. Its membership
validity is still determined separately by Q03.6's known-bit rule.

Equality compares the numeric value within its logical enum type. Q03.8 accepts
aliases: two names for the same code compare equal. Unrelated enum definitions do not
become interchangeable merely because they use the same numeric code. Query
literal binding and permitted coercions remain Q07.3 work.

- **Reads and comparisons:** use typed integer comparisons; no name resolution,
  per-value formatting or business-rank lookup is required. .NET's
  [Enum.CompareTo](https://learn.microsoft.com/en-us/dotnet/api/system.enum.compareto?view=net-10.0)
  provides a numeric-comparison reference, not a requirement to box values through
  its object-based API.
- **Writes and indexes:** maintain ordered keys with the same numeric comparison
  used by query evaluation and sorting. A label-only change does not itself
  change these keys, though schema/binding compatibility still needs validation.
- **Memory:** no additional enum rank table or name-based sort key is required by
  this policy. Existing member metadata, validation and index storage still cost
  memory; the record payload remains the selected integer width.
- **Complexity:** reuse numeric comparison and boundary handling. Physical index
  keys must preserve that order; comparing arbitrary persisted/native bytes
  lexicographically is not automatically equivalent to comparing their numbers.

Do not infer this order from reflection enumeration. In particular,
[Enum.GetValues](https://learn.microsoft.com/en-us/dotnet/api/system.enum.getvalues?view=net-10.0)
returns values by unsigned magnitude, which differs for negative values. The
engine and generated accessors must implement the chosen schema comparison
explicitly. A future test must distinguish signed numeric order from this API's
enumeration order.

### B. Add a separate enum ranking rule to the schema — not selected

Store the same numeric codes but declare a rank per value, for example New before
Running before Done. This can express business sequence even when external codes
are fixed. The ranking is additional metadata, not incidental declaration order.

- **Reads:** comparisons need rank resolution or prepared rank-bearing index keys.
  Cached ranks can keep comparisons numeric, at the cost of maintaining that data.
- **Writes and indexes:** indexed writes need the appropriate rank; changing ranks
  can reorder existing index entries and require rebuilding affected indexes.
- **Memory:** rank metadata per enum definition and potentially additional cached
  keys, depending on implementation. It need not duplicate strings per record.
- **Complexity:** validate a complete and consistent ranking, define ties and
  schema evolution, and handle Flags masks without individual member names.
  Enumerating every valid Flags combination is not a scalable default ranking
  mechanism; a separate rule would be required for those cases.

### C. Order by member names — not selected

Resolve values to names and compare those names under an explicit text comparison
policy. This can suit presentation, but ties storage ordering to identifiers that
may be renamed. It is not automatically the desired business sequence or a
localized display order.

- **Reads:** require name resolution and text comparison, or prepared name/rank
  sort keys. This is additional work or metadata compared with direct numeric
  comparison; no measured latency ratio is claimed.
- **Writes and indexes:** name changes can reorder all affected index entries.
- **Memory:** names already exist in schema metadata, but cached comparison keys
  can add memory. A string allocation per record is not inherently required.
- **Complexity:** define canonical names for aliases and unnamed Flags combinations
  and keep the text comparison stable across nodes. A runtime ToString result is
  not an implicit database collation contract.

### Decision boundaries

Option A is accepted for v1. If an application needs a distinct business/display
sequence, it can sort its owned results or model an explicit ordinary priority field and
declare an index for that field. Do not renumber durable enum codes merely to
change screen order. These use existing application/schema capabilities; they do
not add a hidden enum-ranking system to the engine.

Null placement is accepted in Q07.3.2; explicit-only sort keys are accepted in
Q07.3.3. Pagination and the actual DSL operators remain separate query decisions.
Neither numeric ordering nor Flags validation implies a full-query snapshot.
A Flags membership query is
still distinct from a numeric equality/range query. Existing stored arrays retain
their own order; this decision concerns comparisons and query/index order only.

**Decision recorded:** use numeric order according to the declared underlying
integer type for enum and Flags comparisons, sorting and ordered indexes in v1.
Preserve signedness; Flags order compares the entire numeric mask. Names and
declaration order do not affect it. Implementation and comparator/index checks
remain to be completed; no benchmark result is claimed for these alternatives.

<a id="q03.8-can-enum-members-have-aliases"></a>

## Q03.8: Can enum members have aliases?

**Accepted on 12 November 2026: option B.** Allow multiple names for the same
numeric value, with one explicit canonical name when that value has aliases.
Do not retain which alias the caller used. Names are schema metadata and an
application concern; numeric storage, validation and ordering remain as accepted
in Q03.5 through Q03.7.

For example, an application may already have this .NET definition:

```csharp
public enum StateEnum
{
    Disabled = 0,
    Active = 1,
    Enabled = Active
}
```

Active and Enabled represent the same value, not two distinguishable states.
The stored field contains 1 in either case. A typed .NET enum value also does not
retain which of these names the caller used. Distinct states require distinct
numeric codes. The example illustrates the naming issue, not a finalized schema
syntax or implemented generator.

| Choice | Numeric reads, writes and indexes | Memory | Design cost |
| --- | --- | --- | --- |
| A. Require one name per code | Same numeric operations | Fewer schema names; unchanged field width | Simpler validation; existing .NET aliases need explicit mapping or rejection |
| B. Allow aliases with one canonical name — accepted | Same numeric operations; symbolic inputs resolve to the same code | Extra names in schema metadata; unchanged field width | Validate aliases and canonical names; cover them in bindings and name resolution |

### A. Require one name per numeric value — not selected

Every declared member has a unique numeric value. This keeps the schema and name
lookup straightforward. It does not make the stored numeric field smaller than
option B, and it does not inherently accelerate typed reads or numeric indexes.

A .NET enum containing duplicate numeric values cannot be imported unchanged.
Binding must reject it or require an explicit mapping to the schema's chosen
name. Silently picking whichever alias reflection returns is not a deterministic
schema contract. Applications that never use aliases lose little with this option.

### B. Allow aliases with a canonical name — accepted

Several names may resolve directly to one numeric value. If only one name exists,
it is canonical automatically. If there are aliases, the schema explicitly selects
one canonical name for that named numeric value; declaration order does not select
it implicitly. In the example, Active could be canonical and Enabled an alias.
Names within the enum must be unambiguous; exact identifier syntax and name
comparison remain part of the schema grammar work.

This accommodates .NET definitions with aliases and can retain an old name while
introducing a new preferred name. The database does not store an alias identifier,
alias chain or the caller's original spelling in each record. Consequently:

- **Reads:** typed reads return the numeric enum value. Numeric comparisons,
  sorting, equality and ordered indexes are unchanged. There is no per-record
  alias traversal or additional string allocation on that path.
- **Writes:** membership and known-bit validation still operate on codes or masks.
  When a future input/query interface accepts names, its binder resolves a name
  to the same code; it must not repeatedly resolve that name for every scanned row.
- **Memory:** additional names and their mappings consume schema metadata memory.
  Record width and numeric index keys do not grow. The overhead depends on the
  number and length of aliases, not the number of stored enum occurrences.
- **Complexity:** schema validation, generated bindings and symbolic name handling
  need additional cases. There is no measured performance ratio for the choices;
  this decision does not introduce a new record layout or comparison algorithm.

Canonical names govern database-controlled metadata and any future symbolic
formatting contract. They do not override the generated CLR enum's ToString or a
caller-selected JSON serializer. With duplicate numeric values, .NET ToString
does not guarantee which alias it returns; see
[Enum.ToString](https://learn.microsoft.com/en-us/dotnet/api/system.enum.tostring?view=net-10.0).
Stable database-controlled naming must use the schema's explicit mapping.

### Flags and schema evolution boundaries

Aliases for the same bit do not introduce another bit. Aliases for an explicitly
named combination do not expand AllowedBits. Unnamed valid combinations remain
valid, including 0 even if no member names it. The accepted contract requires a canonical
name only for explicitly named numeric values, not a declaration for every
possible mask. Name lookup may find no declared name; how a future symbolic
formatter represents such a mask is a separate interface detail.

Adding an alias or changing the canonical name while keeping numeric codes fixed
does not itself change record bytes or numeric index order. It can still affect
generated bindings and symbolic consumers. Updating application names is the
application's responsibility; the engine does not add enum-specific negotiation,
name migration or a compatibility layer for that purpose. Existing schema
validation and layout compatibility checks remain applicable. Changes to numeric
codes, permitted bits or representation follow the existing schema rules and,
where needed, Q03's controlled data migration. Ordinary enum membership and Flags
unknown-bit rejection remain unchanged.

**Decision recorded:** aliases are allowed in v1, with one explicit canonical
name when several names share a numeric value. Store only the numeric value,
without preserving which alias the caller used. Naming and renaming are schema/application concerns rather than a separate engine subsystem.

<a id="q03.9-when-should-schema-conversion-run"></a>

## Q03.9: When should schema conversion run?

**Accepted on 12 November 2026: option A.** Establish the schema-evolution
foundations in v1 and retain controlled migrations with affected data temporarily
unavailable. Mixed physical layouts and lazy conversion belong to v2. Q06.3's
runtime access without generated C# classes remains required in v1; it does not
depend on implementing mixed-layout execution first.

### Foundations to establish now

The following foundations are part of the accepted v1 architecture. Exact metadata
encoding, SDK types and the physical version-tag location remain design work:

1. Give logical types and fields stable identities independent of their names,
   declaration order and physical offsets. A deleted identity must not silently
   become a different field. The catalog is authoritative without a domain assembly.
2. Distinguish the logical schema revision from the physical layout identifier.
   A rename may change metadata without moving bytes. Persist and replicate enough
   format metadata to identify the layout of stored data. Whether the identifier
   belongs to a record, segment or containing image depends on the physical layout;
   do not impose an extra per-record header before that layout is selected. The
   format/access design must allow different layout descriptors to coexist later,
   rather than permanently assuming one global set of offsets.
3. Route runtime views, generated accessors and OQL through the same schema/layout
   binding rules. Bind field identities to typed accessors once where possible;
   do not require string lookup or reflection for every value. Generated code can
   have an optimized matching-layout path, but must not blindly reuse old offsets.
4. Scope field handles, query plans and borrowed views to a valid schema/layout
   generation. Publication must drain active protected access and invalidate or
   rebind incompatible handles before they can access new storage. A handle retained
   by application code is not an indefinitely valid native pointer.
5. Give conversion a defined input/output schema, defaults/conversions, validation
   and publication boundary. Q03's controlled conversion uses this boundary first;
   a future scheduler can convert records on access or in background batches without
   replacing the logical field-access API.

This is schema/layout versioning, not a historical document version for every
business update. It does not require permanent copies of old data. Descriptors,
conversion paths and retired identities need a bounded lifecycle coordinated with
persisted data, recovery and replicas; exact retention rules remain open.

### A. Controlled conversion in v1; deferred execution in v2 — accepted

Keep one supported active layout per affected dataset in ordinary v1 operation.
The process can remain running while the dataset enters maintenance, finishes
active reads, converts and validates its data, prepares affected indexes and
publishes the new durable generation. Other data remains usable only to the extent
that the eventual migration scope and shared dependencies permit it.

Conversion can stream through bounded buffers into a new durable generation.
It need not be one enormous in-memory transaction or retain a second full RAM
copy. Old recoverable files and new files can coexist temporarily; Q03 already
requires recovery to select one complete generation after a crash. The durable
publication protocol remains to be designed. This is maintenance admission control,
not holding record gates during storage I/O.

- **Reads:** ordinary access binds to one layout; affected data is temporarily
  unavailable during conversion. No on-read conversion latency in the normal path.
- **Writes:** affected writes pause or fail explicitly during migration. Normal
  writes do not maintain multiple physical representations.
- **Memory/disk:** layout metadata and bounded conversion buffers cost memory;
  temporary durable old/new generations cost disk. No fixed multiplier is promised.
- **Complexity:** schema identities, binding invalidation and recoverable publication
  are necessary now; mixed-layout query execution and conversion-on-read are v2 work.

Pure metadata changes do not inherently require rewriting all records. This option
does not automatically classify adding or removing any field as metadata-only.

### B. Mixed layouts and conversion on access — deferred to v2

The following describes the deferred direction, not an implemented capability or
an additional v1 execution path. Detailed supported transformations remain v2 work.

After publishing a new logical schema, older records can remain in their old
physical layout. Reads interpret them using the matching descriptor and conversion
rules. There are two materially different mechanisms:

- **Logical adaptation:** ignore a removed field or synthesize an added field's
  constant default while reading old bytes. The stored record does not change.
  Repeated reads may repeat adaptation, and removed bytes are not reclaimed yet.
- **Physical lazy conversion:** allocate/build the new representation and publish
  it when the record is accessed or rewritten. Later reads use the new layout.
  A formerly read-only path now needs writer coordination, lifetime management,
  index updates where applicable and durable recovery rules.

The second mechanism must not expose a partially converted root. Old borrowed
views must finish before their storage is reclaimed. A child replica may transform
its local representation under an agreed format protocol, but cannot invent new
logical values independently of the authoritative master. General defaults or
converters that depend on time or external services cannot be independently
reevaluated on each node or every read.

- **Reads:** migration pauses may be shorter, but layout dispatch/adaptation costs
  remain and first-access conversion can increase tail latency. A full scan may
  touch every old record, bringing forward the entire conversion cost.
- **Writes:** changes must reconcile the old layout, new schema and index state.
  Conversion and application updates must not overwrite one another.
- **Memory/disk:** descriptors and conversion rules coexist. A converting record
  can temporarily have two representations; untouched records retain removed data.
  Background completion/compaction is needed if reclamation must eventually finish.
- **Complexity:** mixed-layout filters, ordering, indexes, replication, reopen and
  failure handling must agree. An index or new validation rule may need to inspect
  existing data before becoming usable; lazy conversion alone does not solve this.

There is no measured latency or memory ratio for either option. Neither option
grants a full-query snapshot or unrestricted retention of arena-backed objects.

### Industry precedents and their limits

The technique is established, but often limited to particular schema changes.
[PostgreSQL's column-addition documentation](https://www.postgresql.org/docs/18/ddl-alter.html)
describes returning a constant default for older rows without updating every row
when the column is added; physical application can wait for a table rewrite.
This is a specific missing-field optimization, not arbitrary conversion on every read.

[MongoDB's schema-versioning pattern](https://www.mongodb.com/docs/manual/data-modeling/design-patterns/data-versioning/schema-versioning/)
keeps different document shapes in one collection and makes queries/updates account
for those versions. It demonstrates coexistence, not a native-memory layout or an
automatic migration implementation to reuse. Neither system is a dependency.

**Decision recorded:** evolution foundations belong in v1, with migrations pausing
access to affected data. Mixed layouts and lazy conversion belong in v2. V1 must
not bake permanent field offsets or a single forever-fixed layout into its public
access contract. Implementation, recovery verification and performance measurements
remain outstanding; this decision does not claim a working migration engine.
