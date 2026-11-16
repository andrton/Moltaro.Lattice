# Physical index structures

Accepted structure choices under Q07/E04. Index persistence and reconstruction are
owned by [Q19](index-lifecycle.md); safe root access is owned by
[Q16](physical-storage.md#stable-control-entry-per-root).

[Decision register](../design/questions.md) · [Query contracts](queries.md) · [Engine foundation](../design/engine-foundation.md)

The first production milestone requires secondary indexes and direct facade search,
while deferring OQL. This changes the access-layer scope, not the selected structures
or their maintenance/lifetime contracts. [Explicit declared-index access](index-access.md)
is accepted for the facade; exact SDK signatures remain implementation work.

## Primary-key index: hash table

**Accepted on 13 November 2026:** use a hash table for v1 primary-key lookup. Choose
the concrete layout, capacity policy and growth mechanism through measurements.
The standard `System.Collections.Generic.Dictionary<TKey, TValue>` must not implement
the production primary index, including behind a wrapper. The implementation selection
below uses an existing package; no custom hash table or package fork is required.

Lookup uses entity type and primary key within the open database and reaches a safe
stable root control entry. Hash collisions require full equality checks; hashing and
equality must follow the schema's key semantics, including string comparison. The
structure stores keys or their valid representations and compact root locators, not
copies of complete objects. It does not introduce a second public object identifier.

An unchanged primary key needs no hash-entry relocation when ordinary fields change.
A root move changes its stable entry's current location; the hash entry continues to
identify the same root lifetime. Delete/reinsert and index growth must preserve safe
entry acquisition and reuse. They must not turn a retained locator into another root.

The primary hash index provides exact lookup, not ordered traversal. Queries requiring
key order or ranges can use an explicitly declared suitable ordered index or the
accepted bounded scan/sort path. The accepted secondary implementation is recorded
below; it is separate from the primary hash table.

The existing per-root and WriteBatch publication rules apply on every node. Index
capacity needed by a Durable write must be reserved before journal commit. Rebuilding
and growing an index must respect the total memory budget, including temporary old/new
storage and safe reclamation. A hash-table decision does not select lock-free access,
a single execution thread, an unbounded resize or one mandatory native/managed layout.

### Reuse and implementation selection

Evaluate existing .NET collection libraries before committing to a custom hash table
or ordered tree. A ready implementation may hold value-type keys/locators while the
database objects remain in the native arena. Managed metadata is not automatically
excluded by the native bulk-storage contract; the standard Dictionary prohibition
for the primary index remains explicit. Native allocation alone is not evidence of
lower total memory consumption.

A package is suitable only if its actual capacity, comparer, lifetime and mutation
behavior fits the engine. It must permit safe growth/reservation and integration with
coherent publication; fast standalone lookup is insufficient. Collection enumerators
do not automatically implement Lattice's accepted traversal-during-mutation contract.
String keys must not require a new managed string for every arena lookup, or an
unmeasured permanent duplicate of all key text.

### Selected implementation: Arc.Collections UnorderedMapSlim

**Superseded for the primary index on 16 November 2026 (owner, Q48).** Reads are
protected by epochs and a lookup has to run while a write being published changes the index.
`UnorderedMapSlim` requires external synchronization whenever a writer is present, so it
cannot serve. The primary index is a hash table of the engine's own, modelled on the
hash index of Garnet/Tsavorite: changes published one at a time, readers without locks, entries of one
machine word, replaced tables retired by the epoch. The reservation and accounting rules
of this section (capacity reserved before a durable commit, old and new table counted
during growth) hold for the new table. The owner's direction: where the engine can
replace a package by its own structure written after an established open-source one, it
does.

Measured in Epic 1, sprint S08, part 1 (scenario `primary-index`, manifest
`development-3`, 16 November 2026): the engine's table takes 22.3 bytes per entry at
100,000 entities and 17.8 at 1,000,000, for an Int64 key and for a String key alike; the
package map took 36.7 and 29.4 bytes for an Int64 key and 47.2 and 37.7 for a String key.

**Engineering selection on 13 November 2026:** use
`Arc.Collections` 2.2.0 `UnorderedMapSlim<TKey, RootLocator>` for the first primary-index
implementation. `RootLocator` here denotes the engine-owned compact locator contract,
not an existing SDK type. Keep the package behind the engine's index owner; public
schema/API and persisted data must not expose package nodes or physical slot numbers.
Faster.Map DenseMap remains a measured alternative, not a second v1 implementation.

This selection follows the [memory comparison and bounded integration checks](../design/prototype-evidence.md#primary-index-integration-and-selection).
It closes the package-selection investigation, not production validation of the
index owner, root lifetime protocol or the complete engine. Speed superiority is
not claimed: the earlier workstation timing comparison remains preliminary.

Reasons for choosing ArcSlim:

- Its allocated slots and reusable free slots give a direct capacity bound. Deletion
  does not trigger a table rebuild, unlike the inspected DenseMap deletion path.
- Native UTF-8 key views can supply content hashing/equality through a value type;
  a separate managed string per stored key or lookup is not required by this API.
- It does not impose DenseMap 9.0.3's constructor requirement for accelerated
  Vector256. The bounded hardware-disabled probe confirmed this difference.
- Its one-million-entry memory result is favorable, although DenseMap used less
  memory at 100,000 entries. Capacity policy remains part of the memory budget.

The package has no public `EnsureCapacity`. The selected integration direction uses
its public constructor and enumeration, without reflection or changes to its internals:

1. Before journal submission, account for live entries plus all outstanding insertion
   reservations. Reserve the old-plus-new index memory if more capacity is required.
2. Build a larger package map containing the same published entries. Serialize index
   mutations during the copy; readers may continue using the old map. Briefly protect
   the pointer swap. This changes physical metadata, not visible logical contents.
3. Release index synchronization before waiting for PostgreSQL. Keep bounded slot
   credits for pending writes so other preparations cannot consume their capacity.
4. After commit, publish validated entries under the normal publication protection,
   consuming their credits. A definitive pre-commit rejection releases credits.
   An uncertain commit must retain its reservation until the existing recovery/status
   protocol resolves it; it is not an ordinary cancellation.

The small prototype proves this direction for numeric keys, including two pending
writers, readable old contents during the simulated commit wait, cancellation,
budget refusal and allocation-free reserved insertion. It does not perform SQL I/O,
prove full WriteBatch atomicity or implement durable uncertainty handling. The engine
must extend the same reservation accounting to its actual mutation and index owners.

Cost: growth is an O(n) index copy and temporarily retains old and new metadata.
It can delay index mutations; no fixed pause bound is established. It does not copy
the native object arena. Small writes within reserved capacity require no such copy.
Deleted slots are reusable, but ArcSlim does not automatically return its arrays to
a smaller size. Any later shrink/rebuild must use the same budget/publication owner.

Native key lifetime remains the engine's responsibility. The bounded test uses a
16-byte direct UTF-8 descriptor, repairs it before freeing moved text, and delegates
case-insensitive Unicode equality to .NET through stack scratch. Its 1,024-byte test
limit is not a schema limit or the final key format. Production work still includes
large/chunked keys, explicit UTF-16 mode, lifetime-safe relocation, root retention,
complete lock order, traversal guarantees and target-environment performance checks.

Inspected exact-version sources:
[ArcSlim](https://github.com/archi-Doc/Arc.Collections/blob/d9edfc9db902a8eea1505031b0f4cbfea90f921b/Arc.Collections/Collection/UnorderedMapSlim.cs),
[DenseMap](https://github.com/Wsm2110/Faster.Map/blob/c8e847e835926d959aab82839c79f2444a4aa194/src/Core/DenseMap.cs).

### Candidates for evaluation

This is the original shortlist checked on 13 November 2026, not a general adoption
of these libraries or an independent quality certification. ArcSlim's selection
above applies only to the primary index; the secondary comparison below supports
the accepted OrderedMultiMap choice with an explicit memory tradeoff.
Published package versions and repository heads can differ; pin the evaluated version.

| Candidate | Relevant structures | Fit to investigate |
| --- | --- | --- |
| [Faster.Map](https://www.nuget.org/packages/Faster.Map) | BlitzMap and DenseMap hash-table implementations | Primary lookup candidate; measure retained and resize memory, key equality support, capacity preparation and mutation behavior rather than adopting publisher throughput claims |
| [Arc.Collections](https://github.com/archi-Doc/Arc.Collections) | Compact UnorderedMapSlim, other hash maps, ordered maps/sets and multimaps | Broad collection candidate; comparer support and node/allocation behavior differ between types, so evaluate each required type separately |
| [Kaos.Collections](https://www.nuget.org/packages/Kaos.Collections) | RankedDictionary, RankedMap, RankedSet and RankedBag built on order-statistic B+ trees | Ordered-index candidate with ranges and duplicate-key collections; published 4.1.0 dates from 2018, so verify the chosen package against its source, target runtime and tests |
| [C5](https://www.nuget.org/packages/C5) | Broad generic collection library, including tree collections and priority queues | Established collection alternative for comparison; its broader functionality is not evidence of minimal retained memory for Lattice |
| [NativeCollections](https://www.nuget.org/packages/NativeCollections) | Native/unsafe collection families using unmanaged storage | Investigate allocator ownership, key constraints and memory accounting; NuGet marks multiple older versions, including 1.7.3, deprecated for critical bugs, so no blanket quality claim or version-independent recommendation is made |

### Measurement evidence and remaining integration checks

The [primary-index prototype](../../src/Prototypes/PrimaryIndexComparison/README.md)
compares Faster.Map 9.0.3 (`BlitzMap`, `DenseMap`) with Arc.Collections 2.2.0
(`UnorderedMapSlim`, `UnorderedMap`) using numeric, Guid and string keys with 8-byte
locators. The [13 November measurements](../design/prototype-evidence.md#primary-index-collection-comparison)
cover retained storage, a controlled old-plus-new resize peak, lookups, insertion
and existing-locator replacement. Memory findings repeat exactly across the three
processes per case; timing is preliminary because the shared workstation experienced
interference. That initial comparison alone selected no package. The subsequent
bounded checks support the ArcSlim selection above; no custom hash table was written.

The measured memory ordering changes with entry count and capacity slack. These
results narrow candidate evaluation but do not establish native string-key support,
concurrent root protection or capacity reservation before a durable commit.

Compare equal logical contents and supported key semantics with numeric, Guid and
string keys and compact root locators. Include insertion, hit/miss lookup, deletion,
reuse and repeated growth/shrink cycles. Measure retained bytes per live entry,
capacity slack, managed and native allocations, peak growth memory, latency spikes
and initial build time. Include shared root/index protection and concurrent reads
in integration checks; do not infer engine thread safety from a package label.

Continue integration with the selected package. Reopen its choice only for a concrete
unmet requirement, not to keep the shortlist indefinitely open. No universal speedup,
bytes-per-entry target or ordered-secondary-tree choice follows from the primary tests.

## Secondary index: Arc OrderedMultiMap

**Accepted on 13 November 2026:** use
`Arc.Collections` 2.2.0 `OrderedMultiMap<SearchKey, RootLocator>` for the first ordered
secondary-index implementation in v1. The choice accepts its measured retained-memory
disadvantage against Kaos in exchange for public node reuse and simpler preparation
before commit. Package selection is closed; complete engine integration is not proven.
`SearchKey` and `RootLocator` denote engine-owned representations, not existing SDK
types. Keep package nodes private to the index owner; no persisted/public format
depends on this collection. No custom tree or fork is proposed.

The [bounded prototype](../../src/Prototypes/SecondaryIndexComparison/README.md) and
[results](../design/prototype-evidence.md#bounded-secondary-index-comparison) compare it
with `Kaos.Collections` 4.1.0 `RankedMap`. Arc uses a red-black tree with duplicate
groups; Kaos uses a B+ tree. Both can represent the accepted explicit single-field
and composite indexes with repeated search keys. The package comparison does not
change schema comparison, result ordering or uniqueness contracts.

The earlier preference for a compact B+ tree was a hypothesis about structure costs.
The measured Kaos implementation is indeed smaller: 33–38 retained bytes per entry
against Arc's 88 for a 16-byte numeric key and 8-byte locator at 10,000 entries.
Arc costs about 2.3–2.6 times as much index memory in this fixture. This applies to
each declared index, excludes documents/key payloads, and is not total database RAM.
The decision prioritizes predictable preparation before durable commit and
implementation scope, while accepting that memory disadvantage explicitly.

Reasons:

- Arc exposes detached-node reuse and changes an existing node's key without
  allocating a replacement. The small preparation test preserved the live index,
  then published 32 moves, eight removals and eight prepared insertions with zero
  managed allocation. The temporary plan retained 1,920 bytes.
- Kaos allocates navigation paths during mutation and can allocate on tree splits.
  Its `Capacity` is branching factor, not a reservation API. The tested public-API
  control prepared a full private index copy: 259,952 additional retained bytes
  and 2,359,856 allocated bytes for the same small change set. Full copying per
  batch would add O(n) work and concurrent-writer coordination; it is not proposed.
- Arc's numeric reads and unique-key changes were faster in these short samples.
  Duplicate-heavy updates/ranges had overlapping timing intervals, so no universal
  performance advantage is claimed. Arc's tested range and mutation paths allocated
  zero bytes; Kaos allocated 104 bytes per range and about 416–447 per key change.
- Kaos 4.1.0 `ElementsBetween` reproducibly threw on a 300-entry range case.
  `ElementsFrom` plus an upper bound passed, and was the measured adapter. This is
  a concrete package defect, not a claim that all Kaos operations or B+ trees fail.

### Integration direction and remaining checks

Prepare insertion nodes through a private staging map using public Add/RemoveNode;
keep keys/locators in the bounded plan because removal clears the node's values.
Existing mutations may retain protected node handles for that operation. Validate
the target root/revision and own the pending mutation before durable submission.
Account for nodes, key storage and the plan before commit; a definitive rejection
releases them, while an uncertain commit follows the existing recovery protocol.
Publication uses the existing database/root/index protection and must not hold a
lock across PostgreSQL I/O. A stale handle must never apply to a different root.

The prototype has no competing writers and performs no SQL commit. It proves the
numeric package operations, not those lifetime and publication guarantees. Ordered
native keys, nullable/composite/schema comparison rules and all allocations in the
engine path still need integration checks. Comparers must not introduce a hidden
allocation or throw halfway through confirmed publication. Package enumerators do
not establish Lattice's traversal-during-mutation semantics.

No permanent per-root/per-index handle table is selected. Without one, finding a
specific locator among equal keys costs a duplicate-group scan, included in the
measurements. Any later handle optimization needs its own memory accounting. Removed
managed nodes become collectible after references are released; GC reclamation is
not immediate physical RAM release. Index count and retained/temporary memory must
remain part of the database budget.

These integration checks remain implementation work with the selected package;
a larger package benchmark is not needed merely to repeat this choice. Reopen the
choice for a concrete unmet requirement, including an unacceptable index memory
budget. A future compact tree can replace this internal structure without changing
the persisted database or public schema because physical indexes are rebuilt.

The subsequent [bounded arena integration](../../src/Prototypes/ArenaLifecycle/INDEXES.md)
on 13 November 2026 checks an Int32 primary index and one `(text, Int64)` secondary
index against real native relocation. It verifies root-candidate lifetime handoff,
prepared/aborted key ownership, equal-value descriptor replacement, deletion and
duplicate-group promotion. A separate native String-key hash check covers index-only
text ownership. Sixteen warmed prepared publications allocated zero managed bytes.
These fixtures support the selected packages; they do not close general schema,
parallel preparation/growth, WriteBatch, durable publication or multi-core checks.
The report preserves unresolved latency outliers rather than asserting a production
pause bound. The original package comparison above remains historical evidence.

### Search-key ownership: copied fixed values and shared text

**Accepted on 13 November 2026: option A.** Fixed-size search components are copied into index keys;
string components retain the stored text through a stable, lifetime-protected
descriptor instead of making a text copy for every index. This applies to primary
and secondary keys. It settles the ownership direction within E04; exact descriptors,
protection and native-key integration still require implementation and verification.

The shared text value is immutable while referenced by a record, index or prepared
mutation. A text change prepares a replacement before commit and publishes the
record/index changes together. Replaced values become reclaimable after all legitimate
owners and protected readers release them. This requires no permanent value history.
Compaction may move the bytes but must preserve the descriptor and protect every
active dereference; retaining a descriptor alone does not make relocation safe.

Plain still avoids content-based deduplication between independent stored occurrences.
A record and its own indexes may share the same occurrence. Dictionary fields keep
their accepted exact-text sharing; index ownership participates in the existing
stored-reference/deferred-reclamation contract. No CLR string per index key, raw
unprotected pointer, or comparison of text handles as if they were text is selected.
Ordering/equality still compare values according to the schema. General handles for
all owned blocks are not implied by this specific text-lifetime mechanism.

The unselected alternative is an independently owned text copy for every affected index. It
simplifies the relationship between record storage and index keys, but increases
resident text, copy work and temporary old/new payload during updates. Native copies
would still need their own reclamation/compaction protection; managed string copies
would add GC-owned text. Neither option removes index publication synchronization.

| Effect | A: share the stored immutable text — accepted | B: own a text copy per index — not selected |
| --- | --- | --- |
| Reads | Resolve/protect a descriptor before comparing text; no record lookup or root gate merely to obtain a search value | Independently owned key text can simplify access; actual speed depends on representation and locality |
| Writes | Prepare new text once, retain it for affected indexes and release old ownership after publication/readers | Copy the changed text into each affected index and retire each old copy |
| Memory | Avoid repeated payload; descriptor and ownership metadata still cost memory and can dominate very short text | Extra payload per index plus its metadata and temporary copies |
| Complexity | Shared immutable-value lifetime, relocation protection and balanced ownership across failed/prepared/published changes | Simpler ownership boundaries, with duplicated data and separate reclamation work |

Exact descriptor layout, protection/gate order and byte costs remain engineering
work. The [arena integration report](../../src/Prototypes/ArenaLifecycle/INDEXES.md)
measures the short-text ownership mechanism and specifies the checked gate order.
Its keys share native text without additional payload copies; equal comparisons
must still install a replacement descriptor when its allocation changes. Long/chunked
text, Plain occurrences and general schema integration remain to be verified. Do not
claim that sharing makes every string length or query faster. The already accepted
string storage mode and comparison semantics remain unchanged.
