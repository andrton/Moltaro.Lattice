# Q14: Remaining resource-pressure and write-admission questions

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Partially accepted on 13 November 2026:** new Durable write calls may wait for
PostgreSQL recovery within time, request-count and retained-memory limits. Expired
waiting calls fail explicitly; requests that never started are removed and cannot
execute later. The illustrative 30-second example is not an accepted default.
[Master memory admission](../../decisions/write-admission.md#master-writes-under-memory-pressure)
also rejects a new mutation before journal submission/publication when required
capacity cannot be reserved within budget, without adding a reclamation wait.
[Child memory pressure](../../decisions/write-admission.md#child-replica-memory-pressure)
also accepts pausing application before publication, intact local reads, bounded
buffers and ordered resume. Memory pressure alone triggers no full reload.
[Root-object size](../../decisions/write-admission.md#configurable-root-object-size-limit)
also has an accepted configurable cap; its numerical threshold is not selected.

The [accepted record](../../decisions/write-admission.md#q14-write-admission-and-resource-pressure)
owns the policy, alternatives and distinction between queued and possibly committed
work. Required durable acknowledgment and hard journal-limit refusal are unchanged.

## Remaining resource decisions and engineering

- Select numerical waiting deadlines and queue count/byte budgets using measurements.
- Measure the accepted root-size cap and implement its byte accounting; define
  remaining mutation preparation/in-flight and protected-work limits.
- Master admission under memory pressure is accepted for Durable and MemoryOnly.
  Child application under controlled pressure also has an accepted pause policy.
  Resolve numerical budgets, remaining startup/recovery and read-pressure boundaries
  without losing writes or treating every pressure condition as a storage outage.
- Implement bounded ownership, dequeue/expiration/cancellation coordination and
  clear failure outcomes; the existing prototype does not validate these guarantees.

These unresolved topics keep Q14 open in the [question register](../questions.md).
Q13 now accepts document/scalar updates and collection replacement/element commands.
Unchanged collection outcomes and invalid List position failures are now accepted.
Partial updates require existing targets and explicit initialization. Complete-document
Insert/Replace/Upsert, ordinary deleted/already-absent results and optional revision
checks are accepted. Primary keys are immutable in v1, without a rename command.
Numeric writes assign ready values, with arithmetic commands deferred beyond v1.
Full-document Save and a separate explicit partial-update API are accepted, without
automatic change tracking. Mutable write inputs remain unchanged until asynchronous
completion, including error/cancellation completion. Active push of binary logical
changes is accepted, with full documents for full writes and explicit partial effects,
without invalidation or automatic Save diff. Q08.1.2 now accepts intact local reads
during lag/disconnection and explicit master-progress comparison. Q08.1.3 accepts
bounded resume/full reload and one arena with bounded readiness waiting for reads.
[MemoryOnly child restart](replica-restart.md) is accepted. See [question register](../questions.md#current-discussion).
[Repeated scalar targets in one explicit partial mutation](../../decisions/mutation-commands.md#repeated-scalar-targets-in-an-explicit-partial-update)
reject that mutation or its containing WriteBatch before commit. Duplicate roots in an
explicit WriteBatch are also rejected. Local setters are not database writes.
