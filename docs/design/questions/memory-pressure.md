# Q14: Master writes under memory pressure

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Enforce an instance memory budget
and reject a new master mutation before Durable journal submission or MemoryOnly
publication when its preparation/publication capacity cannot be reserved. Release
partial preparation; stored data and revisions stay unchanged. No additional wait
for future reader completion or compaction is introduced on this path.

The [decision record](../../decisions/write-admission.md#master-writes-under-memory-pressure)
owns the contract, alternatives, costs and accounting boundaries. Account for peak
preparation, indexes and temporary memory as well as final data. The empty-segment
reserve is part of the budget. Existing PostgreSQL-outage waiting and post-commit
failure/recovery semantics remain in force; a committed write cannot be relabeled
as a clean admission rejection.

[A configurable root-size cap](../../decisions/write-admission.md#configurable-root-object-size-limit)
is also accepted. Its numerical threshold, other numerical budgets, exact accounting,
remaining protected-work limits and other pressure paths remain open. No runtime
implementation or new benchmark is claimed.
Q14 stays partially accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
