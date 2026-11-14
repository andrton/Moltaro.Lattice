# Q17: Block allocation and reuse

**Accepted for v1 on 13 November 2026:** option A. Use a bounded set of size classes
and reusable slots for small/medium record and payload allocations inside native
segments. Large allocations follow a separate path. Prefer suitable reusable space
before acquiring more capacity within the engine's memory budget.

The [decision record](../../decisions/allocation-and-compaction.md#q17-allocation-and-fragmentation-lifecycle)
owns the contract, alternatives and costs. Reuse requires completion of protected
readers and other retention obligations. Payload length and capacity remain distinct;
class rounding applies to a whole block. Exact class sizes, large-block threshold,
segment sizes, shrink/retention policies and numerical budgets are not selected.

This does not select compaction timing or promise immediate return of free space
to the OS. Q17 remains partially accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
