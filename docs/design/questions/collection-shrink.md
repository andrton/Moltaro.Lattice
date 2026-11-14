# Q17: Unused collection capacity

**Accepted for v1 on 13 November 2026:** option A. Automatically shrink substantial,
persistent unused collection capacity through bounded maintenance coordinated with
the local writer/applier. Avoid rebuilding after every deletion and prevent repeated
shrink/growth oscillation. Thresholds, spare capacity and work budgets require measurements.

The [decision record](../../decisions/allocation-and-compaction.md#automatic-shrinking-of-unused-collection-capacity)
owns the contract, alternatives, read/write/memory costs and the distinction between
shrinking live capacity and moving unchanged blocks between segments. Safely reclaim
redundant chunks; more complex List/Map/Set restructuring needs coherent bounded
algorithms. Logical values, List order and root revisions remain unchanged by physical
maintenance. No manual trim API or new mutable-string operation is selected.

Concrete algorithms, timings and numerical bounds remain open within Q16/Q17/E01.
No new performance result or runtime implementation is claimed. Q17 stays partially
accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
