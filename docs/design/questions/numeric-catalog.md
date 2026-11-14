# Q03.4.1: Numeric catalog completion

**Accepted for v1 on 13 November 2026:** option A. Add UInt32, UInt64 and Float32.
UInt32 and UInt64 also support primary/reference keys and enum/Flags backing, with
unsigned ranges and ordering. Float32 is an ordinary field type, not a key or enum
backing type. Schema-selected widths remain fixed; there is no automatic narrowing.

The [decision record](../../decisions/schema-and-types.md#uint32-uint64-and-float32)
owns the full contract, rationale, alternatives and costs. UInt32 and Float32 use
4 value bytes, UInt64 uses 8. Float32 trades precision/range for density; payload
sizes do not establish total record savings or a throughput improvement.

Q03.4.1 is now fully accepted. Resource limits remain Q14, database/key scope Q12,
and shared Float32/Float64 comparison rules are now accepted in
[Q07.3.6](../../decisions/queries.md#floating-point-special-values). Exact codecs/layout/API grammar
remain engineering work. No runtime implementation or benchmark was performed.
The current proposal is [external transaction integration](external-transactions.md).
