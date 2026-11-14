# Q17: Empty-segment retention and release

**Accepted for v1 on 13 November 2026:** option A. Keep a bounded reserve of completely
empty native segments for reuse; release excess eligible segments in background work.
The reserve is accounted across the database instance within its memory budget.
Numerical reserve sizes and timing will be selected from measurements.

The [decision record](../../decisions/allocation-and-compaction.md#empty-segment-retention-and-release)
owns the contract, safety conditions, alternatives, costs and the clarification of
free-space lookup versus compaction. Maintain availability metadata rather than
scanning every segment for each allocation. Concrete free-list/bitmap/pool structures
remain engineering work. Moving blocks across segments can empty whole segments;
slack within live allocations needs a separate shrink/repacking policy. The engine
does not manage the physical placement of RAM pages.

Only completely empty, safely detached segments without retained users or reservations
may be released. Reusable capacity, native release and reduced OS memory consumption
are separate measurements. No performance result or runtime implementation is claimed.
Q17 stays partially accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
