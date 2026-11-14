# Q19: Physical index lifecycle

**Accepted for v1 on 13 November 2026:** keep index definitions in the schema; each
node builds its own physical primary/secondary indexes during data loading/recovery,
before readiness. V1 neither persists nor transfers physical Lattice index images.
After loading, ordinary mutations maintain affected indexes coherently with data.

The [decision record](../../decisions/index-lifecycle.md#q19-should-physical-indexes-be-persisted-or-rebuilt)
owns the contract, costs and alternatives. Durable schema/data persistence remains in
PostgreSQL; the provider's own indexes are unaffected. MemoryOnly children receive
schema definitions and rebuild locally. Builder workspace and full startup time need
measurement; acceptance does not establish a performance result or a binary format.

Q19 is closed in the [question register](../questions.md). See [question register](../questions.md#current-discussion); concrete index structures and builder
implementation remain engineering work.
