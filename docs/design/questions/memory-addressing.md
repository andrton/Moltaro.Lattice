# Q16: Addressing movable blocks

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Use relative segment/offset
locations for roots and owned payloads. When a block moves, safely update its owning
descriptor and every structure retaining its physical location, including relevant
index locators. Incoming cross-document references remain primary keys.

The [decision record](../../decisions/physical-storage.md#relative-block-addressing)
owns the contract, alternatives and costs. The baseline has no universal per-block
stable-handle table. Shared dictionary text retains its separate StringId/lifetime
protocol. Native pointers are temporary protected-access values, not public or
portable persistent identities. Exact encoding and reuse tracking remain engineering work.

Active readers must remain safe across relocation and reclamation. This acceptance
selects neither block sizes nor compaction policy and reports no new measurement.
See [question register](../questions.md#current-discussion).
Q16 remains partially accepted in the [question register](../questions.md).
