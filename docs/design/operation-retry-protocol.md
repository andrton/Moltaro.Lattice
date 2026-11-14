# Operation admission, results and safe retry

Implementation baseline for milestone 1, 14 November 2026. This completes the
mechanism behind the accepted [operation contract](../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1).
The [bounded PostgreSQL experiment](../../src/Prototypes/OperationRetry/README.md) checks
the state transitions; it is not the production writer or a throughput claim.

## Identity and ownership

The SDK creates and retains an immutable operation token **before submission**:
database history, master-issued admission generation, and a random operation UUID.
The master supplies the current generation during connection/open and refreshes it
when it closes. Generations are durable metadata, not caller timestamps. A token
never changes generation on retry. WriteBatch has one token for the whole batch.
Independent writes in an internal group commit have separate tokens.

The operation fingerprint is SHA-256 over the versioned canonical request encoding:
schema version, operation kinds, type/key identities, ordered commands, all supplied
values and revision conditions. Preserve text spelling, numeric representation and
collection command order. Bind database/history in the enclosing token. Map/Set bulk
input is normalized once under its accepted validation rules before encoding. A
client retains that immutable encoded request for resubmission; status lookup needs
only the token. A changed request under an existing token is a conflict, not a retry.

The master owns a bounded active-operation table for coordinating simultaneous calls.
PostgreSQL owns retained admission/result rows. Do not load the retained history into
the arena. Local data/index reads never consult this registry.

## Persistent metadata

Provider tables belong to the opened database. SQL types below describe values;
names are internal and are not public SDK signatures.

| Owner | Required fields / constraints |
| --- | --- |
| Database control | Database ID, history, current generation, retired-generation high-water mark, authoritative clock floor, committed position |
| Admission generation | Monotonic Int64 ID, admission-close time, configured window captured at creation, maximum promised expiry |
| Operation receipt | Unique `(generation, operation UUID)`; fingerprint; first-admitted UTC; expiry UTC; Admitted/Committed/Rejected state; compact outcome; optional committed position |
| Mutation journal | Whole transaction boundary, position, versioned effects and integrity information; independent of receipt cleanup |
| Leadership | A monotonic leadership number in a row of its own; advanced by a master when it takes the store, read under a shared lock by every transaction that changes the store |

There is no durable Executing state: a transaction may roll back after recording
such a state, making it ambiguous. Executing is an in-process activity attached to
an Admitted row. Committed and Rejected are durable terminal states within retention.
Rejected stores a compact stable error code and relevant member/field identity.
Do not retain request bodies or old document copies in receipts.

## Two durable transitions

1. Validate token/history, structural input and capacity. Under operation coordination,
   look up the receipt. If absent, check that the generation still accepts new IDs.
   Insert **Admitted**, fingerprint and `Expires = FirstAdmitted + configured window`;
   extend the generation's maximum promised expiry. Commit this short metadata
   transaction before dispatching any mutation effects. A lost admission reply can
   therefore be resolved without resetting the deadline.
2. Prepare the accepted mutation, reserve publication capacity and recheck current
   existence/revision conditions through the ordered writer. In one PostgreSQL
   transaction, validate the still-admitted receipt, append the complete mutation
   journal entry, advance the committed position and set the receipt to Committed
   with its result. Await commit, then coherently publish arena/index changes.
   Definitive semantic rejection instead durably marks the receipt Rejected without
   a data mutation. Transient resource/storage failure leaves it Admitted for resolution.
3. A master call succeeds only after publication. A child call also waits for its
   local applied prefix through that position. Lookup of a Committed receipt alone
   is not ordinary Save success. After publication failure, serving stops until reopen.

Admission and mutation are deliberately separate transactions: recording first
admission in a transaction that later rolls back would lose the promised deadline
and request binding. This adds a durable metadata transition, not another document
copy. Bounded admission groups and bounded mutation groups amortize flushes; no SQL
call per batch member is required. One original request can pipeline through both
stages without an extra caller round trip. Maximum grouping delay is bounded by
the write latency budget, never by waiting for a full batch.

SQL transitions use the same short provider-control ordering as the authoritative
commit stage: database control -> generation -> receipt. Acquisition and retirement
cannot race around a missing row. Unique constraints enforce identity; do not use a
plain unlocked SELECT-then-INSERT. The prototype uses a control-row lock and Read
Committed transactions. PostgreSQL documents [row locks](https://www.postgresql.org/docs/18/explicit-locking.html#LOCKING-ROWS)
and [conflicting inserts](https://www.postgresql.org/docs/18/sql-insert.html#SQL-ON-CONFLICT).
These are SQL locks, not arena/root locks; readers retain local access during I/O.

## Repeat and status behavior

| Authoritative state | Same-token submission / status |
| --- | --- |
| Active matching attempt | Join bounded coordination or return Pending/Unknown on timeout; never run another mutation concurrently |
| Admitted, unexpired, no active attempt | Same request may resume; status reports Admitted/Pending, not committed |
| Committed, unexpired | Return the original compact result/position; satisfy publication and calling-child application before ordinary write success |
| Rejected, unexpired | Return the stored rejection; do not re-evaluate against newer objects |
| Existing token, different fingerprint | PayloadMismatch, no execution |
| No visible receipt | Unknown; it is not proof of rollback while an admission/commit might be in flight |
| Missing token in a closed generation | Refuse new admission; do not silently obtain a new token |
| Expired receipt or retired generation | Expired/Unknown, no new effects; application reconciliation is required |
| Different history | HistoryMismatch; never execute against the restored/replacement history |

Status waits only within configured deadlines and returns explicit uncertainty on
storage failure. It never reports absent/empty success for an unavailable store.
Retries do not extend validity or automatically refresh tokens. There is no automatic
SDK transport retry policy in milestone 1.

## Expiry, cleanup and crash ordering

Use 60-second admission generations as an initial internal setting. The retry
window is the accepted configurable **15 minutes from first durable admission**,
not from generation creation. Closing a generation refuses only new operation IDs;
retained unexpired operations remain resolvable/resubmittable. New configuration
applies to new generations; it cannot shorten old receipts.

Check expiry before starting the effects transaction. A transaction admitted to
execution before expiry may finish afterward within engine failure deadlines.
Cleanup must wait for it, and its original caller may receive the actual result.
Expiry cannot cancel a possibly committed transaction. Subsequent expired-token
requests do not start effects, even when the old receipt is still physically present.

Retire generations in order. Under the same provider ordering, verify that admission
is closed, every promised expiry has passed, and any SQL transition has settled.
Advance the durable retired-generation boundary **before or atomically with** receipt
deletion. Production cleanup deletes rows in bounded batches after that boundary is
durable; a crash between marking and deletion only retains excess rows. The experiment
performs both in one tiny transaction. Missing generation rows at/below the boundary
always mean retired, never a new operation. Do not retain a tombstone per UUID forever.

Authoritative time comes from the master/provider UTC clock with a persisted
nondecreasing floor. Backward adjustment delays expiry; it must not revive expired
tokens. Within a process, monotonic elapsed time drives waiting. Operational UTC
correction forward can expire a wall-clock deadline sooner; this is not a guarantee
against arbitrary changes to the host clock. Test time is injected only in the
prototype; clients cannot submit it.

On ordinary reopen, finish/reconcile old SQL activity before serving; restore the
same history, clock floor, generations and receipts. Admitted rows with no surviving
active attempt need the original request to resume; do not invent or reconstruct
its payload from the fingerprint. Committed operations replay from the data journal
as usual. Receipt retention is independent of saved-base advancement/log removal.

Restore from backup creates new history before serving, so pre-restore tokens cannot
execute. MemoryOnly uses the same state machine in bounded process memory and a new
history after restart; it makes no cross-restart result-recovery promise. Its bound is
an option, by default the smaller of 32 MiB and 10% of the instance budget. When it is
full, new admissions are refused; an unexpired outcome is never dropped to make room.

## Capacity and failure invariants

- Account for receipts, indexes and cleanup backlog in the durable receipt budget.
  Refuse new admission before capacity runs out; never evict unexpired receipts.
- Account separately for active coordination and retained input/preparation bytes.
  Failed admission releases reservations and changes no data.
- Once admission storage is dispatched, accepted engine-owned completion/uncertainty
  rules apply. Caller cancellation cannot erase an accepted operation identity.
- No-op results also receive receipts. Repeating an already-absent Delete cannot
  delete a later insertion. Successful no-ops advance the operation stream position;
  confirmed unchanged roots keep their revisions. Full Save does not perform an
  equality diff and remains a new mutation even if supplied values happen to match.
- The original receipt's position is used on retry, not a new position. WriteBatch
  commits and publishes whole; the protocol never acknowledges its members separately.

## Evidence and remaining implementation work

The bounded experiment checks two durable transitions, duplicates, fingerprints,
no-ops, rollback, expiry, configuration changes, retirement races and actual worker
exit before/after commit. PostgreSQL service/power loss, transport cancellation,
full arena publication, group commit throughput and SDK integration are release
verification work. Earlier WriteBatch experiments cover separate arena/publication
and saved-unit failure boundaries. These results do not replace integrated tests.

This protocol is selected for the first implementation. Its correctness does not
depend on a particular generation duration, SQL batch size or guessed throughput.
Performance gates remain those in [performance requirements](performance-requirements.md).
