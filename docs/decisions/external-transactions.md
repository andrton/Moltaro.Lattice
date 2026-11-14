# External transaction integration

[Decision index](README.md) · [Decision register](../design/questions.md)

<a id="q09.3-transactions-spanning-lattice-and-application-sql"></a>

## Q09.3: Transactions spanning Lattice and application SQL

**Accepted for v1 on 13 November 2026:** no participation in external transactions.
Internal Durable commits and single-document atomicity remain unchanged. Caller-owned
SQL transactions, ambient transaction enlistment and distributed coordination are
outside v1. Synchronizing application SQL with Lattice requires an application-owned
workflow; no cross-store atomicity or automatic repair is promised.

This excludes transactions with unrelated application storage, not Lattice-owned
write transactions. The accepted [database WriteBatch](document-model.md#atomic-writebatch-within-one-database)
is a separate Q01 contract and does not reopen external transaction participation.

### The actual guarantee being considered

An application updates an ordinary SQL business table and a Lattice document. Must
both updates succeed or both be cancelled together, including after a process crash?
For example, changing a rule in SQL and its associated configuration in Lattice may
need one outcome. Two ordinary sequential Save/Commit calls cannot guarantee that
outcome: the first may commit and the second fail.

PostgreSQL already durably stores Lattice's journal. That accepted internal storage
transaction does not automatically include the application's unrelated SQL changes,
shared arena publication or replica progress. No option promises a common snapshot
between arbitrary SQL readers and asynchronous child replicas.

### A. No external transaction participation in v1 — accepted

Lattice owns and commits its mutations through the accepted writer and persistence
protocol. V1 does not accept a caller-owned SQL transaction, enlist in an ambient
TransactionScope or expose a distributed prepare/commit/rollback protocol. These are
explicitly unsupported integration capabilities, not silently successful no-ops.

Retain coherent single-document writes and durable storage confirmation before
success. Existing commit-uncertainty handling and ordered replica propagation remain
unchanged. MemoryOnly still has its own accepted persistence boundary.

When an application writes both stores, it owns a separately designed synchronization
or recovery workflow. This choice does not add a built-in outbox, retry service,
compensation framework or an external messaging dependency. A failure between the
writes may leave the two stores temporarily or persistently different until that
application workflow repairs it; automatic repair is not promised.

This closes the v1 scope question, without promising a particular v2
implementation or delivery date. It does not remove ordinary PostgreSQL transactions
inside the existing storage provider.

### B. Shared PostgreSQL transaction contract — deferred

Consider this only when the application tables and Lattice storage are in the same
PostgreSQL database and a supported embedded integration can share one transaction.
Journal append and business SQL changes could then commit together without a general
distributed transaction coordinator. Sharing only the server or connection string is
not enough: both changes must actually belong to the same database transaction.

The engine still needs a public participation protocol: when it validates and reserves
memory, how the master coordinator orders pending work, what readers see before commit,
when publication/acknowledgment happens, and how rollback, timeout and crash recovery
resolve pending mutations. No record gate may be held during SQL I/O. A driver
transaction is not the engine's complete transaction contract.

This could satisfy a valuable restricted scenario, with fewer moving parts than C.
It also couples application transaction lifetime to engine write progress/resources,
and requires focused failure/restart tests. MemoryOnly and remote or different-database
writers cannot automatically use the same contract. No implementation is selected here.

### C. General distributed transaction participation — deferred

Coordinate Lattice and separate transactional resources with a durable outcome protocol,
such as two-phase commit where supported. This needs participant/coordinator contracts,
prepared-state retention, recovery after uncertain outcomes, timeouts and administrator
resolution. An in-memory pending list alone cannot provide recovery after a crash.

PostgreSQL's [PREPARE TRANSACTION documentation](https://www.postgresql.org/docs/current/sql-prepare-transaction.html)
describes prepared transactions as a facility for an external transaction manager and
notes that they retain locks until resolution. This is evidence of the required
coordination work, not a selected Lattice architecture. Ordinary local transactions
and driver usage are described separately in [Npgsql's transaction documentation](https://www.npgsql.org/doc/basic-usage.html#transactions).

General transaction participation would be a material expansion of v1. Coordinator
placement need not be a mandatory new service for every deployment, but its durable
recovery responsibility has to exist somewhere; transport support alone is insufficient.

### Read, write, memory and implementation effects

| Aspect | A: defer external participation | B: same-database integration | C: general distributed integration |
| --- | --- | --- | --- |
| Ordinary local reads | Existing record-read path | Committed read path can remain similar; pending mutations need a visibility contract | Also needs committed/prepared visibility rules; contention can affect involved records |
| Writes | Existing validation, storage commit and publication | Wait for the application transaction; writer scheduling/reservations may live longer | Additional coordination phases and waiting for outcome resolution |
| Memory/resources | No new external pending-transaction state | Pending changes/reservations and longer transaction lifetimes | Pending/coordinator metadata, retained resources and durable recovery state |
| Complexity | Smallest v1 scope; no cross-store atomicity | Restricted but substantial integration and crash/rollback testing | Largest scope: participant recovery, coordination and operational handling |

These are design consequences, not measured latency or memory ratios. None of these
options makes normal field reads intrinsically faster. B may be much cheaper than C
for the specific same-database scenario, but neither is free merely because PostgreSQL
already exists underneath Lattice.

Option A is selected for v1 given the accepted one-writer, local-reader design and low need for
complex transactions. B is the first narrower candidate to investigate later if a
concrete application requires it; C should follow only from a demonstrated cross-resource
requirement. This decision does not select a future delivery date.
