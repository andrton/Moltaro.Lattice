# Q03.4.1: Primary-key types

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Primary keys support Int8, UInt8,
Int16, UInt16, Int32, UInt32, Int64, UInt64, String and Guid, selected in the schema
and non-null. UInt32/UInt64 were added by the accepted [numeric extension](numeric-catalog.md).
Guid is also an ordinary scalar field, exposed as System.Guid with a fixed 16-byte
value payload. Narrow integer keys retain their declared width.

The [decision record](../../decisions/schema-and-types.md#primary-key-types-and-guid-fields)
owns the catalog, alternatives and costs. Existing primary uniqueness, immutable-key,
string equality and typed-reference contracts remain. This adds no automatic ID
allocator or composite primary-key feature.

Q03.4.1 is now fully accepted, including the later temporal, Binary and numeric
extensions. Numerical limits remain Q14; database/key scope is Q12. No engine/API
implementation or benchmark was performed. The current proposal is
[external transaction integration](external-transactions.md).
