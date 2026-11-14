# Direct index access for the first production milestone

**Accepted on 14 November 2026: option A, explicit declared-index access.** The scope requires single-field
and composite secondary indexes but excludes OQL from the first production milestone.
The main workload is users, groups, roles and settings with 5–10 child replicas.
See [workload and scope](../design/performance-requirements.md#accepted-primary-workload-and-first-milestone-access-scope).

Index structures, schema-defined field comparison, index maintenance and local
reconstruction are already accepted. This decision concerns the facade, not choosing
different containers or reopening index ownership. No facade code is changed here.

## Selected: explicit declared-index access

Resolve an index declared for the entity type, by its schema name/identity or a typed
handle bound to that definition. Submit structured key values and bounds directly.
The engine knows which physical index to use; it does not parse OQL or optimize an
arbitrary predicate. Both typed objects and dynamic records use the same contract.

The public contract declares this access as `ILatticeIndex`, `IndexKey`, `IndexBound` and
`LatticeFindOptions`; the search behind it is not implemented yet. The calls below show the shape:

```csharp
await users.Index("ByEmail").FindAsync(
    IndexKey.Equal(email), readOptions);

await settings.Index("BySectionAndName").FindAsync(
    IndexKey.Equal("mail", "sender"), readOptions);
```

The first index covers Email. The second covers the ordered pair (Section, Name).
Named or typed handles can be resolved once and reused; their schema-version
validation and exact parameter representation belong to implementation. The sketch
does not select object arrays, reflection, boxing or allocation per predicate.

First-milestone capability set:

- Equality over the full key of a single-field or composite index.
- Equality over leading fields of a composite key, followed where requested by a
  range on the next field, with explicit inclusive/exclusive bounds. This is key-field
  prefix matching, not an implicit string StartsWith/LIKE operator.
- Forward/reverse traversal in the declared index order, using existing comparison,
  null-order and tie rules. No hidden primary-key tie breaker or stored-list reordering.
- Existing Limit/Offset and execution/memory budgets, without eager materialization
  of all matches. Exact page/iterator/result signatures await facade integration.
- The accepted consistency context (`MinPosition`, finite waiting and response token),
  with no-copy access protected only after consistency waiting has completed. An
  explicit [position wait](session-consistency.md#explicit-local-position-wait)
  can precede indexed reads; the following access retains the dependency in its
  options or request context and revalidates current readiness/history.

An index on (Section, Name) supports Section equality plus a Name range. It does not
give an efficient Name-only lookup: declare another index or explicitly enumerate
in application code. Do not silently present a whole-index/table scan as a selective
lookup. The direct-index API rejects absent indexes and unsupported key/bound
shapes; it does not fall back to a full scan. Existing explicit .NET enumeration is
separate. The broader deferred OQL scan-fallback policy is unchanged.

Secondary keys are non-unique by default. The subsequent [Unique decision](unique-indexes.md)
adds explicit schema enforcement for complete non-null keys; keys containing null
are exempt. Equality may return zero, one or several roots depending on that definition
and its nulls, with the existing traversal/root/WriteBatch guarantees. No cross-root
join follows from these examples. A highly nonselective index can still produce many matches;
using an index is not a promise of constant latency.

This decision does not add arbitrary executable .NET predicates, expression-tree
translation, index intersection, computed keys, automatic indexes or a query optimizer.
Those are not prerequisites for this access path. The physical index representation
remains internal; selecting a declared schema index does not expose tree nodes or
native addresses to application code.

**Added by the owner on 14 November 2026:** a declared index may name a field inside an
owned value. A part of the index key is then a path from a field of the entity type
through owned values to the indexed field. A path never goes through a List, a Map or a
Set: indexes over the members of a collection stay outside this milestone. When an owned
value on the path is absent the component is null and follows the accepted rules for a
null component, including the exemption from a [Unique](unique-indexes.md) index.

## Not selected for this milestone: automatic index selection

The caller supplies structured field conditions; the engine resolves a compatible
declared index. This also needs no textual OQL. It reduces application coupling to
index names and can eventually share planning with a language layer.

However, the facade must define supported condition combinations, selection when
several indexes qualify, composite-prefix rules and what happens when none qualify.
A small deterministic resolver is possible; a general optimizer is not inherently
required. It is still more scope than selecting an index explicitly. Cached plans
could keep steady-state read costs close; no speed difference is claimed without
measurement. Missing-index behavior must remain explicit rather than an accidental
scan in this milestone's indexed path.

## Costs and decision boundary

| Aspect | A: explicit index, accepted | B: resolve from field conditions |
| --- | --- | --- |
| Reads | Direct schema binding and index lookup; result work depends on selectivity | Adds index resolution/validation, potentially cached |
| Writes | Maintain affected indexes before coherent publication | Same index maintenance for the same schema |
| Memory | Existing index keys/locators plus bounded request/result state | Same index memory, with additional condition/plan metadata |
| Startup | Rebuild all declared indexes before Ready | Same reconstruction, plus resolver preparation where used |
| Complexity | Small direct contract, application knows the selected schema index | More flexible field-based facade with selection and failure rules |

Deferring OQL removes language parsing/planning work, not the dominant memory cost
of the required indexes or their write/rebuild cost. No memory or speed multiplier
has been measured for this facade choice.

The selection policy is settled. The signatures, typed/dynamic bindings and result
wrappers are those of the public contract in this repository; the engine implements them.
The calls above are not an implementation plan.
