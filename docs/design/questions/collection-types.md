# Q03.4.2: Types inside List, Map and Set

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. List elements and Map values allow
supported scalars, enum/Flags, typed key references, schema-defined owned structures
and nested collections. Map keys and Set elements allow scalars except Binary,
enum/Flags or typed references; no owned structures or collections in these positions.

The [decision record](../../decisions/schema-and-types.md#q03.4.2-nested-and-collection-declarations)
owns the type table, examples, rationale and rejected alternative. Each position is
schema-typed. List order/duplicates, unordered Map/Set uniqueness, root ownership,
key references and protected arena views retain their previously accepted contracts.
Binary remains allowed as a List element or Map value. Equality follows declared
scalar/reference identity; floating-point edge cases follow the accepted
[Equals/CompareTo contract](../../decisions/queries.md#floating-point-special-values).

Q03.4.2 is now fully accepted, including the [collection input rules](collection-input.md).
Collection declarations and input behavior are closed; physical algorithms and
integration of the shared float/reference contracts remain engineering work.
No runtime implementation or
benchmark was performed. The next proposal is [external transaction integration](external-transactions.md).
