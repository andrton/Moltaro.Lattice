# Q09.4: Restore destination

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Restore into an isolated replacement
PostgreSQL target, preserve the former target until verification and switch the sole
Lattice writer using one full arena at a time.

The [decision record](../../decisions/backup-and-restore.md#restore-destination) owns the
procedure, alternatives and costs. Stop the former writer, drain protected users and
release the old arena before loading/replaying restored data and rebuilding indexes.
Readiness follows coherent initialization. Full engine validation therefore requires
a master availability gap; provider-level preparation alone does not prove readiness.

The extra storage is on disk. Provider buffers or an additional PostgreSQL instance
can still require memory. Preserving former storage does not guarantee it is healthy
or permit blind rollback after new writes succeed on the replacement. Restoration
recovers the chosen boundary; later old-history writes are not automatically merged.

[New history/full child reload after restore](../../decisions/backup-and-restore.md#replica-history-after-restore)
is accepted; ordinary same-history Durable restart preserves identity. Concrete
orchestration, recovery objectives, retention and activation mechanics remain open.
No restore, endpoint change, production implementation or benchmark was
performed. Q09.4 stays partially accepted in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
