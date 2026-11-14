# Q07.4: Explicit unique secondary indexes

**Accepted on 14 November 2026: option A, included in milestone 1.** An index may
declare `Unique: true`; the default remains non-unique. This applies to supported
single-field and composite keys. It supplies the required concurrent normalized-name
constraint for the first Identity consumer, without changing the selected Arc container.

[Index access](index-access.md) · [Engine baseline](../design/milestone-1-engine-contract.md#index-preparation-and-traversal) · [WriteBatch](document-model.md#atomic-writebatch-within-one-database)

## Observable contract

- At most one root of the index's entity type may own a complete non-null index key.
  Equality follows every component's schema comparator, including case policy,
  floating-point NaN/zero rules and enum numeric values. Empty text is a value, not null.
- If any key component is null, including an absent nullable owned parent, exclude
  that key from uniqueness enforcement. It still participates in ordinary index
  queries; a null lookup may therefore return several roots.
- Insert, Save/Replace/Upsert and explicit partial updates all enforce the constraint
  at the authoritative master. A conflict is `UniqueConstraintViolation`, identifying
  the declared index and failing input member. It changes no application data.
- A conflict inside an explicit WriteBatch rejects the entire batch before effects
  commit. Validate its final state: two roots may exchange their unique values, and
  a delete may release a value claimed by another member of that same batch.
- Two independent concurrent writes for the same unique value cannot both succeed.
  Replacing a root without changing its own unique key does not conflict with itself.
- A confirmed violation is a stable rejected operation outcome under the retained
  result protocol. Repeating that token does not rerun it against later state.

An application-side lookup followed by Save is not enforcement. Children forward
writes and do not reserve a globally unique value locally. Uniqueness is scoped to
one declared index in one database, not to all databases or different entity types.
This introduces neither referential integrity nor collection-member indexes.

## Master validation and publication

Use the existing OrderedMultiMap and schema comparer. No second full copy of the
index or additional per-key uniqueness table is required. A small temporary map/set
of proposed keys is bounded by operation/group admission limits.

1. Prepare root changes and affected old/new index keys using the accepted lifetime
   and resource reservations. Per-root reservations alone cannot prevent a conflict
   between different roots, so they are not sufficient as a uniqueness mechanism.
2. In the single ordered commit stage, validate each explicit operation against the
   published index plus earlier accepted operations in the current internal group.
   For a WriteBatch, project removal of old entries from all affected roots, then
   add all final non-null keys. Reject repeated proposed keys or ownership by an
   unaffected root. Check every affected unique index before effects commit.
3. Reserve all publication nodes/keys before SQL. Release memory gates for I/O;
   the ordered writer prevents another logical operation from invalidating this
   decision. If an independent group member fails validation, record its rejection
   without adopting its proposed index effects into the group's overlay.
4. Commit effects/results and publish through the ordinary root/WriteBatch barrier.
   Remove old keys before installing final entries; readers cannot observe an
   intermediate duplicate during whole-batch publication. Unexpected publication
   failure follows the accepted instance-stop/reopen contract.

Initial load/recovery and explicit schema migration validate unique indexes before
readiness/activation. Existing duplicate data prevents activation of a new Unique
definition; do not pick a winner or discard data. Child application validates complete
transaction state before acknowledging it; an impossible violation indicates an
integrity/protocol failure, not permission for a local repair or partial publication.

## Costs and scope

Reads retain the existing index path and result shape. Non-null full-key equality
has at most one matching root; null and prefix/range queries may have many. Writes
add key lookups and bounded proposed-key bookkeeping. Stored index cardinality is
unchanged. Full index copies and database-wide long-held read locks are unnecessary.
Actual latency and temporary-memory costs remain integration measurements, not an
assumed speedup. Scalar/composite indexes remain explicit; uniqueness does not add
OQL, joins, automatic index selection or multi-valued collection indexes.

The alternative was consumer-owned claim records plus atomic WriteBatch. It remains
possible application modeling but is not required for this common schema constraint.

## Evidence

The bounded Arc check in [OperationRetry](../../src/Prototypes/OperationRetry/README.md#unique-index-extension)
passed nine scenarios: duplicates, concurrent different-root claims, composite scope,
null exemption, comparer equality, atomic swaps, same-batch delete/reclaim and whole-
batch rejection without partial effects. It is a tiny managed projection/validation
model over the selected package, not the native/SQL/replica integration. Production
acceptance must also cover node reservations, recovery, schema activation and actual
concurrent facade calls.
