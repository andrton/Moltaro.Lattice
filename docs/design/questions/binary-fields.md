# Q03.4.1: Binary fields

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Binary is a distinct schema type
for an opaque ordered byte sequence, replaced as a whole value through Save or
explicit field assignment. Its bytes belong to the containing root document.

The [decision record](../../decisions/schema-and-types.md#binary-fields) owns the access,
mutation and storage contract. Ordinary reads return owned data or fill a caller-owned
buffer. Short protected reads expose read-only contiguous chunks; a large chunked
value does not promise one span over its whole content. Existing lifetime, root
atomicity and memory-admission rules apply. Empty and null remain distinct.

Reuse the existing contiguous/chunked arena machinery. No byte-range patching,
automatic Base64/text conversion, per-byte objects or separate binary store is added.
Storage, retrieval and projection are in scope; content search, binary sorting/indexing
are not selected. The accepted [collection type contract](collection-types.md) excludes
Binary Map keys and Set elements in v1. Binary is not a primary-key type.
A packed List<UInt8> can have the same payload density; no speed advantage is measured.

Q03.4.1 is now fully accepted; numerical limits remain Q14. No engine/API implementation or benchmark was performed.
The current proposal is [external transaction integration](external-transactions.md).
