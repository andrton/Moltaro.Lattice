# Q16: Large payload representation

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Small payloads stay contiguous;
large strings and collection storage use bounded chunks, including bounded directory
metadata. Thresholds and chunk sizes will be selected from measurements.

The [decision record](../../decisions/physical-storage.md#large-payloads-in-bounded-chunks)
owns the contract, access/growth/compaction costs and the unselected dedicated
contiguous alternative. Logical List order, Map/Set uniqueness, text policies and
root-document coherence remain unchanged. Chunking bounds an individual relocation
copy; it does not make a complete owned copy or full Save cheap or constant-time.
It changes neither logical replication nor full child startup loading.

Concrete directory/collection structures, numerical sizes and complete-operation
resource limits remain open. No new performance result or runtime implementation
is claimed. Q16 stays partially accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
