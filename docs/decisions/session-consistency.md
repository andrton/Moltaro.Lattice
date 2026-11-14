# Session consistency through applied positions

**Accepted on 14 November 2026.** A write forwarded through a child succeeds only
after master completion and coherent application on that calling child. Related
requests carry a minimum applied position and wait for it, with a finite timeout,
on whichever node serves them. Do not wait for all replicas. Sticky routing is not
required for correctness.

This extends Q08.1.2/Q08.1.3. Ordinary reads without a minimum position retain their
accepted local-state semantics, including during disconnection. The master's Durable
completion remains PostgreSQL commit plus coherent arena/index publication.
It does not add external dependencies, quorum voting, historical snapshots or a
PostgreSQL connection on children. This is a design contract, not implemented SDK
behavior or a completed replication test.

## Meaning of a position

A token identifies **one database history and a complete published transaction
position**. It is a logical ordering value, not a wall-clock timestamp, object
revision, permanent snapshot identifier or an exact count of elapsed changes.
A later applied prefix includes the earlier prefix; the result may contain later
committed values, deletions or recreations. A multi-object query still has the
accepted live-traversal semantics, not a shared snapshot.

Positions are comparable only within the same history. Ordinary Durable reopen
preserves history; restore/new history follows the existing reset contract. A token
from another history must fail explicitly, not be compared numerically or silently
discarded. The client should carry an opaque typed value or its wire encoding; a
bare number without history identity is insufficient across restarts/restores.

For example, a request requiring position 120 waits until the local coherent applied
prefix is at least 120. New writes 121 and 122 do not change that fixed target.
A disconnected node already at 120 can satisfy the request locally; one at 117
cannot return stale data as success. An unbounded wait is not permitted.

## Facade handoff

Names below describe responsibility and are illustrative; final signatures and wire
encoding remain with the facade implementation. Keep generated/typed and dynamic
access equivalent.

1. Provide a common request consistency context with `MinPosition` and a finite
   synchronization deadline/budget, plus cancellation. Support it for ID reads,
   absence checks, enumeration/query, protected access and writes, including whole
   WriteBatch. It is a required capability of the facade, not a mandatory non-null
   argument at every call site.
2. Use `MinPosition = null` to request ordinary local reads without an additional
   freshness constraint. Do not use -1 or a bare zero as an absence sentinel. Position
   zero with a valid history identifies an initial prefix; a default/invalid token
   must not masquerade as a valid history. A supplied constraint is never ignored.
3. A constrained read waits **before** evaluating or exposing data, outside arena,
   object and index protection. Use asynchronous entry points or an explicit
   asynchronous request-context admission step; do not hide potentially long network
   waits in the synchronous protected-view callback. Capture the target once.
4. A write carries the dependency to the authoritative master and is ordered after
   that history/prefix before its state-dependent execution. It does not require
   refreshing the receiving child's whole arena before merely forwarding a command.
   Success through a child requires local application through both the dependency
   and the committed operation, without waiting for unrelated replicas.
5. Successful data-operation results expose an observed/applied token, including
   not-found, empty results and successful no-op outcomes. Write results identify
   their operation boundary as needed for result recovery; the session watermark
   must also cover any newer incoming dependency. A replayed old operation receipt
   must not move a session backwards.
6. Advance session context monotonically: merge returned and existing positions
   using the greater position **within the same history**, including when concurrent
   responses arrive out of order. Export that context to the next related request
   regardless of which pod receives it. No external session database is required.
7. Return an explicit timeout/history-mismatch outcome when the constraint cannot
   be satisfied. No stale-success fallback is implied. After a write commits on the
   master, a timeout waiting for the child does **not** roll the write back. Preserve
   its operation ID and the accepted result-resolution/safe-retry semantics.

For live enumeration/query, response metadata must cover the complete published
effects actually exposed, not merely an unsynchronized position sampled before
later rows were read. Streaming must carry/update context as results are exposed,
including a final context for an empty result. It must not claim all returned rows
belong to one snapshot. Exact result wrappers/context ownership are implementation
work; the request context must not be global mutable state shared by unrelated users.

Keep three identities distinct:

| Identity | Purpose |
| --- | --- |
| Consistency position | Minimum applied database history/prefix for dependent operations |
| Entity revision (`LatticeRevision` in the facade) | Detect a concurrent change to one object |
| Operation ID | Resolve a submitted write and prevent duplicate application on retry |

Waiting for a position does not prevent another writer from changing a record after
it was read. Use the separate accepted revision precondition for that protection.
Receipts and position tokens also have different lifetimes: removing old journal
payload does not invalidate a position that a compatible replica has already passed.

## Explicit local position wait

**Facade clarification accepted on 14 November 2026:** provide a separate asynchronous
wait for a supplied position, illustrated as `WaitForPositionAsync`. An application
can admit a request by waiting once, then perform local ID/index reads using the same
request consistency context. This exposes the existing minimum-position mechanism;
it does not introduce another synchronization protocol or change write completion.
The name and exact signatures remain illustrative.

- Capture a valid history/position target once. Complete only when this instance is
  ready and its coherently applied prefix covers that target, including all affected
  data, primary/secondary indexes and the complete WriteBatch. Receiving bytes,
  committing on the master or advancing the saved PostgreSQL base is not sufficient.
- If already satisfied, complete from local state without a master round trip. If
  behind, asynchronously wait for ordinary ordered application with a finite deadline
  and cancellation, outside arena/object/index protection. Do not chase new master
  positions or wait for other replicas. This call does not fetch just one object or
  independently mutate a child.
- A timeout, cancellation or incompatible history is explicit; none establishes
  permission to return a stale result as success. Cancellation ends this wait, not
  replication or a previously committed write. Waiter state remains bounded under the
  existing resource policy; exact defaults and implementation remain engineering work.
- Success establishes a lower bound, not exact equality with the master's current
  state. Later changes may already be visible. It does not hold a lock, freeze data,
  retain an old version or create a snapshot across subsequent reads.
- Preserve `MinPosition` in the following operations' request context/options. At
  access admission, revalidate current readiness, history and applied progress under
  lifecycle coordination. A previous successful wait does not pin an arena: reload,
  close/reopen or history change between wait and read must cause bounded re-waiting
  or an explicit failure where appropriate, never an unconstrained stale read. Within
  one uninterrupted ready lifecycle, the already-satisfied check adds no new wait.

For example, the host receives a token covering position 120, waits locally through
120, then searches `Users.ByEmail`. It may find the value from 120 or a later committed
value, including later deletion; it must not query an index that is still at 117.
An explicit wait is useful before several operations. Passing `MinPosition` directly
to an individual indexed operation provides the same admission guarantee without
requiring a separate call. Typed and dynamic access share these rules.

## Costs and implementation boundary

An already-satisfied read needs a local history/position check; a lagging read waits
for replication. A child-facing write additionally waits for that child's application,
not the slowest child in the deployment. Retained state is a small request/session
token and bounded waiter bookkeeping, not another arena or historical data versions.
Exact wait/count/memory defaults remain Q14/Q08.1.3 measurement work.

The existing ordered stream, complete-WriteBatch application and applied-prefix
tracking provide the foundation. Verify delayed/out-of-order responses, a pending
earlier batch, multiple receiving pods, timeout after durable commit, safe retry,
disconnection, empty results, explicit wait followed by index access, and reload or
history change between wait and access before implementing the guarantee
as production behavior. These checks are not performed by the storage prototypes.

## Established pattern

This is read-your-writes/session consistency using a propagated dependency token.
[Azure Cosmos DB documents carrying session tokens between web nodes](https://learn.microsoft.com/en-us/azure/cosmos-db/how-to-manage-consistency#utilize-session-tokens),
and [MongoDB documents causal sessions across members](https://www.mongodb.com/docs/manual/core/read-isolation-consistency-recency/#causal-consistency)
under its read/write-concern requirements. These are examples of the approach, not
dependencies or a claim that Lattice implements those products' complete guarantees.
With one authoritative master and an ordered database stream, Lattice uses one
history-scoped position rather than introducing multi-master vector clocks.
