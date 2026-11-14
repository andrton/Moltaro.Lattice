# Operation identity and unknown commit results

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted on 13 November 2026 under Q13:** give every submitted write and
explicit WriteBatch an operation token; persist a compact outcome receipt atomically
with its PostgreSQL journal transaction. Allow authoritative result lookup and safe
resubmission of the same operation within a bounded retention window. Expired tokens
must be rejected, not silently treated as new commands.

[Decision owner](../../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1) · [Durable outcomes](../../decisions/write-acknowledgment.md#failure-outcomes-that-the-implementation-must-distinguish) · [Accepted instance recovery](../../decisions/checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure) · [Engine foundation](../engine-foundation.md)

**Implementation baseline, 14 November 2026:** the [operation protocol](../operation-retry-protocol.md)
now fixes durable first admission, token generations, immutable request binding,
terminal results and bounded retirement. It adds a short durable admission transaction
before mutation effects. The alternatives below preserve design rationale; they are
not an unresolved mechanism selection. See the [bounded evidence](../../../src/Prototypes/OperationRetry/README.md).

## Failure to resolve

PostgreSQL or the master may commit an operation and then lose the connection before
the caller receives its result. Repeating a List append can add an element twice;
repeating Save can overwrite a later edit. Current values or a changed revision do
not prove which operation committed. The existing Unknown outcome and prohibition
on blind retry remain necessary; the accepted protocol must resolve uncertainty
without assuming a lost response means rollback.

## A. Bounded durable receipts — accepted

The SDK can generate the token before first submission, with explicit reuse supported
for the same operation. The caller must be able to retain it even if the first result
is lost. It identifies a command, not an object key, revision or replica position.
An explicit WriteBatch has one token for its entire application transaction; independent
writes grouped internally for storage retain their own tokens.

Bind a token to database history, retention generation and an immutable request
fingerprint covering database, schema context, mutation contents and conditions.
The same token with different contents is an error. Exact token encoding and digest
representation remain engineering work, not chosen byte sizes or SDK signatures.

Claim the token and persist the receipt, journal effects and compact outcome in the
same PostgreSQL transaction. An existing token must select the existing-operation
path, never append another mutation. The design must enforce that claim atomically
for simultaneous duplicates; an in-memory check followed by an insert is insufficient.
Do not rerun a committed operation's revision conditions against newer object state.

Successful no-op writes also need a receipt if repeat safety is promised. Otherwise
an absent-key Delete whose reply was lost could later delete a newly created object
when repeated. Recording that outcome adds a durable metadata write; it does not
imply a logical data mutation. Exact no-op result, revision accounting and journal
representation must preserve the existing Q13 contracts and remaining choices.

Receipts contain only bounded identity, request binding, commit position and outcome
data needed by the API, not copies of document bodies or permanent historical versions.
Large batch outcomes count toward the batch budget. The normal write can add its
receipt within the existing commit; this is not a claim of zero extra SQL/index work.

## Results and local publication

A durable receipt proves commit, not that the master's arena or a child has applied
the operation. Normal successful completion still requires coherent master publication.
After restart, recover before reporting completed success; distinguish committed but
not yet published/recovered state when exposing an operation-status API. Duplicate
delivery must not apply the same committed effects to the live arena twice.

Result lookup is authoritative at the master/provider. A lagging child cannot report
that an operation never committed. A missing receipt while the original transaction
may still run is not proof of rollback. Keep Pending/Unknown distinct from definitive
rejection, and synchronize a same-token retry with any first attempt still in flight.
Storage unavailability keeps the outcome unknown instead of triggering a new token.

The accepted instance-recovery policy still applies if publication itself fails.
Automatic transport retry is not selected here. Safe retry means the caller can
resubmit the original operation with the same token under this protocol, not that
the SDK silently creates another command after every timeout.

Mutable input lifetime remains unchanged: callers may release input after completion,
including an Unknown result. A later resubmission requires retaining/reconstructing
the original immutable request; token-only status lookup does not require its body.

## Retention must include a rejection boundary

Keep receipts in PostgreSQL with bounded time/size retention. Keep only bounded active
coordination state in memory; do not load every historical receipt into the native arena.
Cleanup is separate from coalescing the recoverable base and retiring the mutation log:
a committed receipt may still be needed after its journal payload is no longer retained.
Capacity pressure must not silently discard receipts inside their promised window:
apply bounded write admission before exhausting that capacity. The configurable
15-minute default is accepted below; capacity and admission budgets remain open.

A concrete candidate uses token generations issued/validated by the master and a
durable admission/retirement boundary. Stop admitting an old generation, settle its
in-flight attempts, then retire it and remove its receipts. Reuse of a retired token
returns Expired/Unknown, never a fresh execution. Cleanup, token admission and delayed
transactions must be coordinated across restart; a caller-supplied timestamp or a
bare GUID with time-based row deletion does not establish this property.

For a retained committed operation, repeated identical tokens recover its original
compact result. Definitive pre-commit rejection need not create a durable receipt;
absence while an attempt is unresolved must still remain Unknown.
After the retention window expires, the caller must reconcile the operation; no
automatic token renewal or forever exactly-once promise is made. A restore creates new database history under
the existing contract, so old-history tokens cannot silently execute in the restored
history. Ordinary same-history restart preserves valid retained receipts and boundaries.
The generation protocol and capacity limits need specification and measurement.

MemoryOnly cannot offer durable receipts across restart. Any same-process duplicate
coordination must be bounded and explicitly scoped to its volatile history; this
contract does not add disk persistence to MemoryOnly. A MemoryOnly child forwarding
a write uses the authoritative master's mode/history for that operation.

## Accepted safe-repeat window

**Accepted on 14 November 2026 under Q13/Q09.2:** a configurable default
safe-resolution/repeat window of **15 minutes from first admission of the operation
by the master**. The master defines the authoritative expiry; a retry must not refresh
it. Expose the remaining validity/expiry through operation context or authoritative
status where available. This is a window for resolving the original operation, not
an operation timeout or permission to wait indefinitely for storage.

During the window, a repeated identical token resolves/coordinates the same operation.
A committed result is returned without reapplying the mutation; an unresolved first
attempt remains Pending/Unknown as appropriate. Availability during an outage is not
promised. After expiry, never turn the old token into a new write; report the accepted
expired/unknown outcome and let the application reconcile. Do not automatically issue
a replacement token or change request contents. Same-history Durable restart must
preserve promised validity, rather than silently starting a new window.

The generation/admission/retirement protocol must implement this minimum window across
cleanup, clock handling, late transactions and restart. A pending attempt cannot be
forgotten merely because its timer elapsed. Retention may exceed the minimum to settle
in-flight work safely; exact generation boundaries and SDK/wire types remain engineering
work. A caller timestamp or untracked GUID plus row TTL is still insufficient. Changing
configuration applies prospectively and cannot shorten already promised validity.

For the reference one-root-per-call workload, 200 operations/s over 15 minutes is
180,000 distinct operations. A one-hour alternative is 720,000 at the same ordinary
rate. A window containing one accepted 30-second, 10,000/s burst has about 294,000
additional operations relative to ordinary traffic: approximately 474,000 for the
15-minute profile before generation/cleanup overhead. These are sizing calculations,
not measured row counts or capacity guarantees. Count receipts per independent
operation, not per root in a WriteBatch; successful no-ops also need repeat safety.

Durable receipts remain compact PostgreSQL records, separate from document versions;
all retained receipts need not be loaded into the arena. Only bounded active
coordination is held in memory. Larger windows mainly retain more SQL data/index
entries and cleanup work; actual bytes and effects on writes need measurement. They
do not add SQL calls to ordinary resident reads. Capacity admission must preserve the
promised window and cannot evict a still-valid receipt merely to meet a byte limit.

The 15-minute default covers the initial timeout/disconnection recovery workflow. A
one-hour configuration gives callers longer to recover but increases retained history at
ordinary load fourfold. Both preserve the same atomic receipt/commit and safe-repeat
mechanism; duration alone does not prove that protocol. No implementation, new test
run, automatic retry policy or permanent exactly-once guarantee is selected here.

## B. Report Unknown and leave reconciliation to the application — not selected

No durable receipt registry or retry-resolution API is added. The caller receives an
explicit uncertain result, reads relevant business state and decides whether/how to
proceed. Such reads may help reconciliation but cannot generally prove the original
outcome. No automatic repeat of a potentially committed operation is safe in general.

This has less storage/index/cleanup work and a smaller SDK. It shifts uncertain-write
handling into every consuming application and is especially awkward for collection
edits or multi-object batches. Optional revision conditions detect stale state but
are not operation receipts.

## Comparison and required evidence

| Aspect | A: bounded durable receipts | B: application reconciliation |
| --- | --- | --- |
| Normal reads | Remain local; receipt lookup is a separate operation | Remain local |
| Writes | Additional token/receipt/index work in the commit, including successful no-ops | Smaller write metadata path; no receipt persistence |
| Memory | Bounded active coordination; retained receipts stay in PostgreSQL | Less engine coordination state; callers manage uncertain work |
| Disk | Bounded receipt/result retention, independent of object versions | No additional operation-result registry |
| Failure handling | Same-token result recovery/retry within the guaranteed window | Explicit Unknown; domain-specific reconciliation with no generic retry guarantee |
| Complexity | Atomic token claim, result state, generation retirement and recovery tests | Simpler engine; greater integration burden for callers |

A is accepted for the first durable release with WriteBatch and non-idempotent collection
edits. The implementation protocol above selects the concrete mechanism. Its small
SQL experiment checks duplicate/fingerprint/no-op/expiry/retirement behavior and
process-exit recovery. Full schema encoding, transport faults, native publication,
peak receipt cost and multi-node integration remain release verification work.

[Same-database references](../../decisions/database-scope.md#schema-identity-and-references) are accepted separately under Q12.
