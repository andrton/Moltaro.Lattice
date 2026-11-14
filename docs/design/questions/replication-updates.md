# Q08.1.1: Live replication delivery and payload

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026.** The master actively pushes committed binary
logical mutations, without an invalidation mode. Complete Save/Insert/Replace/resolved
Upsert sends the complete resulting document; explicit partial updates send committed
field/collection effects. Delete sends ordered removal metadata. V1 has no automatic
diff of a complete Save.

The [decision record](../../decisions/replication-and-hosting.md#q08.1.1-how-are-live-changes-delivered)
owns the contract, alternatives and costs. Exact binary encoding, framing and batching
are E07 engineering work, not an unresolved choice between logical and physical updates.
Initial full-state capture/transfer remains separate. [Q19](index-lifecycle.md) now
accepts local index construction, without saved/transferred physical index images.

[Q08.1.2 local reads and progress checks](replica-freshness.md) are also accepted.
[Q08.1.3 bounded resume or full reload](replica-reconnect.md) is now accepted.
[Single-arena reload with bounded read waiting](replica-reload.md) is also accepted.
[MemoryOnly child restart](replica-restart.md) is accepted. See [question register](../questions.md#current-discussion).
See the [question register](../questions.md) for the remaining decisions.
