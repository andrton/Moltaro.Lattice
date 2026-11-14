# Q09.4: Replica history after restore

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. A restored Durable master activates
with a new Lattice history identity. Children detecting that identity fully reload
the database, using the accepted one-arena path and bounded read waiting.

An ordinary master restart against the same continuous Durable history preserves
its identity and remains eligible for bounded incremental resume. The complete required
tail and compatible database identity/schema must still be available. MemoryOnly state loss
remains a separate history-reset case; a process restart cannot preserve lost data.

The [decision record](../../decisions/backup-and-restore.md#replica-history-after-restore)
owns identity activation, alternatives and costs. History identity is small current
replication metadata, not permanent object versions. New-master writes do not wait
for all children to reload. Disconnected children retain their accepted local-read
behavior until learning of the new history; returned application copies are not revoked.

Exact identity representation, activation/replay boundaries and
reload scheduling remain engineering work. Q09.4 stays partially accepted in the
[question register](../questions.md). No restore, protocol implementation or benchmark
was performed. See [question register](../questions.md#current-discussion).
