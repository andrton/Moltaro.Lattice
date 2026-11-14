# Q17: Memory compaction

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Automatic incremental compaction
runs as bounded background maintenance inside each node, coordinated with its local
writer/applier. Region occupancy/retention counters and expected useful reclamation
trigger work when compatible destination capacity and workspace are available. Use
separate start/stop conditions; there is no second full arena.

The [decision record](../../decisions/allocation-and-compaction.md#automatic-incremental-compaction)
owns the contract, .NET GC boundaries, accounting, trigger rationale, costs and
alternatives. Owned-block moves respect the root gate, repair physical locators and
preserve logical data, revisions and enumeration. Shared strings/indexes retain their
own protection. CLR collection does not pack engine-owned native records.

Exact sizes, thresholds, timing/headroom budgets, candidate structures, scheduling
fairness, numerical reserve/release timing and broader pressure rules remain open. No numerical latency guarantee or
runtime implementation is accepted by this record. Q17 remains partially accepted.
See [question register](../questions.md#current-discussion).
