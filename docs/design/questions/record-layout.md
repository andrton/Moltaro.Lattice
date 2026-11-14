# Q16: Physical record layout

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Keep fixed-size fields of a record
or owned structure together, with variable strings and collections in separate
payload blocks. The logical model remains a hierarchical document, with one gate
covering the root and all its owned blocks. Dictionary text retains database sharing.

The [decision record](../../decisions/physical-storage.md#record-oriented-layout-with-separate-variable-payloads)
owns the contract, alternatives and costs. Small explicit updates need not copy
an entire document or segment. Full Save still submits the complete final document,
without automatic diff; journal and coherent index/revision publication remain required.

The later [addressing decision](memory-addressing.md) accepts relative segment/offset
locations. Block/segment sizes, exact encoding, collection internals and detailed
copy boundaries remain open within Q16. Hole reuse and defragmentation remain Q17. This acceptance
neither fixes a binary format nor establishes a measured performance guarantee.

See [question register](../questions.md#current-discussion). The
[question register](../questions.md) retains Q16 as partially accepted, with four
settled subtopics, including [large-payload chunks](large-payloads.md).
