# Q09.4: Durable backup responsibility

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** option A. Durable-master backups use
PostgreSQL/deployment-native mechanisms, without a Lattice-owned backup archive.
Lattice validates and opens restored storage: load saved state, replay the required
application journal, rebuild indexes and become ready after coherent initialization.

The [decision record](../../decisions/backup-and-restore.md#durable-backup-responsibility)
owns the consistency boundary, alternatives, costs and PostgreSQL technique distinctions.
Capture must cover required schema/format metadata, saved base, applied-through metadata
and necessary journal tail. A current base alone is not an independent backup.

[Isolated replacement restore](../../decisions/backup-and-restore.md#restore-destination)
is also accepted, preserving former storage until verification and using one arena.
[New history/full child reload after restore](../../decisions/backup-and-restore.md#replica-history-after-restore)
is accepted too, with identity preserved on ordinary same-history Durable restart.
[Infrastructure-owned execution](../storage-and-maintenance.md#accepted-backup-and-restore-control)
was accepted on 14 November 2026: no Lattice v1 API commands launch backup/restore.
Concrete deployment technique, recovery objectives, artifact retention/location and
engine activation/validation mechanics remain work. This does not supply MemoryOnly export,
partial portability or a second arena. No backup, restore, infrastructure change or
new measurement was performed. Q09.4 stays partially accepted in the
[question register](../questions.md).
See [question register](../questions.md#current-discussion).
