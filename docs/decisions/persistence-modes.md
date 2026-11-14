# Persistence modes

Accepted Moltaro.Lattice decisions, rationale and explicitly unresolved details.
The mode contract is settled; the engine and its configuration API are not implemented.

[Decision index](README.md) · [Question register](../design/questions.md) · [Storage and maintenance](../design/storage-and-maintenance.md)

- [Q09.5](#q09.5-how-is-persistence-mode-selected)

<a id="q09.5-how-is-persistence-mode-selected"></a>

## Q09.5: How is persistence mode selected?

**Accepted on 12 November 2026:** v1 selects one persistence mode when
opening each local engine/database instance. Durable is the default; MemoryOnly
must be selected explicitly. The selected mode remains fixed for that instance's
lifetime. Embedded and standalone hosts use the same contract; role does not
implicitly select persistence. The later [Q08.1.3 decision](replication-and-hosting.md#memoryonly-children-and-process-restart)
limits v1 children to explicit MemoryOnly, with full loading from the master after
process restart. A Durable child request is unsupported and fails configuration
validation; it is never silently downgraded. Durable remains the general default,
and masters retain their explicit MemoryOnly option. Configuration syntax and SDK
signatures remain engineering work.

The [Q09.6 record](persistence-backends.md#q09.6-durable-storage-backends) separates persistence mode
from destination. MemoryOnly does not persist to a remote SQL service either;
using remote durable storage without local database files is not this mode.
Q09.6 now selects PostgreSQL as the first Durable backend; MemoryOnly remains
independent of that service and driver.

### Relationship to the accepted design

Q16 already separates allocated native working memory from persistence. The same
arena, schema, index/query engine, root-document gates and owned/protected reads
can serve both modes. A mode does not change which node accepts application writes
or whether the engine is embedded or hosted as a service. No second storage engine
is required. PostgreSQL-backed Durable operation does require access to its service;
MemoryOnly does not gain a network dependency from sharing the local engine.

| Concern | Durable operation | MemoryOnly operation |
| --- | --- | --- |
| Working data and indexes | Native live arena plus appropriate managed control state | Same representation and query contracts |
| Database files | Recoverable data/schema and required journal/checkpoint state | No local database, schema/index image, WAL, checkpoint or temporary spill files |
| Local write completion | Reliable storage acknowledgment and coherent local publication, accepted in Q09.1 | Atomic local publication, with no promise of surviving process loss |
| Restart | Recover from persisted state | Recreate schema and populate a new instance; no implicit recovery of the lost local state |
| Child initialization | Durable children are outside v1; Durable master recovery remains supported | V1 children fully load database state from the master and catch up before reads after each process restart |

MemoryOnly means disabling engine persistence, not merely delaying flushes.
Normal operation and shutdown must not silently save a dump. The schema comes
from the caller/configuration or replication source; the engine does not persist
even the schema in this mode. Host/application I/O and OS paging are separate
from this engine file-I/O contract. Disk-free operation must not rely on a RAM
disk or on temporary files being deleted at shutdown.

Q09.1 requires reliable storage acknowledgment in Durable mode. This does not claim
that every child has already applied the mutation. Q08.1.2 accepts local reads during
lag/disconnection and explicit progress checks, without a child-acknowledgment barrier. Schema validation, primary-key
uniqueness, coherent record reads and ordinary in-memory index maintenance remain
required. MemoryOnly does not weaken these guarantees.

### Replication and restart

A memory-only embedded master can serve local computation without replicas. V1
children explicitly use MemoryOnly and fully load from the master after their own
process restart. A surviving child with intact state may continue local reads through
a master connection outage and resume compatible retained history. An empty restarted
child cannot serve data until it can fully initialize from the master. The Durable
master persists in PostgreSQL. A fully volatile topology remains a possible use case;
several current copies alone do not supply a durable recovery source.

Replication still needs ordered changes, a coherent initial base and bounded
catch-up retention. With no disk storage, those buffers/images live in RAM;
Q08.1.3 accepts bounded retained-tail replay or full reload with one arena and bounded
readiness waiting. Numerical history/wait limits remain Q08.1.3/Q14 work. Replica
startup must not create hidden spool files to implement the diskless mode.

If a memory-only master loses its state and starts a new database history, children
must recognize that change rather than apply the new history to an old base.
Q08.1.2 permits intact local reads during lag/disconnection and reports incompatible
history in progress checks. Q08.1.3 requires full reload when history is incompatible;
replacement uses one arena and bounded read waiting. V1 children have no local
persistent restart image. Numerical budgets remain Q08.1.3 work. Q15 assigns master lifetime to the environment;
the concrete history identifier/handshake remains engineering work. A
surviving child is not automatically promoted and does not silently restore the
master; neither behavior is introduced by this decision.

### Expected costs and benefits

- **Reads:** no inherent speedup for the same resident arena and indexes. There may
  be less contention from persistence work, which needs measurement.
- **Writes:** avoid database-file serialization, writes and durability flush waits.
  Validation, index updates, allocation, locking and configured replication work
  remain. No throughput multiplier is assumed.
- **Memory:** remove persistence-specific buffers and checkpoint retention where
  they are not needed elsewhere. The dataset, indexes, query workspace and bounded
  replication buffers still consume RAM; an overall reduction is not guaranteed.
- **Startup:** an empty local engine avoids recovery work, but useful populated
  readiness still depends on application loading or full replica initialization.
- **Complexity:** optional persistence fits the selected ownership boundary, but
  needs explicit mode/acknowledgment/lifecycle behavior and tests. Durable and
  memory-only combinations add validation work; this is not just skipping fsync.

### Accepted scope and remaining choices

V1 has one mode per local instance, selected at open time with Durable as the
default. Live switching and mixed persistence within one instance are outside
this v1 contract. The later Q08.1.3 scope decision requires explicit MemoryOnly
for v1 children; a Durable master can therefore serve MemoryOnly children. Child
process restart requires a full load, and incompatible history requires full reload.
Numerical budgets remain Q08.1.3 work; Q15 settles the configured endpoint and
environment-owned master lifecycle.
Reopening in a different mode does not define an automatic data conversion,
import or export mechanism.

The later [Q19 decision](index-lifecycle.md) keeps definitions in the schema and
requires local physical index construction on every node before readiness. V1 neither
persists nor transfers physical Lattice index images, in either persistence mode.
Explicit export/backup and its destination are separate Q09.4 decisions, not
automatic persistence or an already-promised feature of this mode.

### Verification to plan

Use the same bounded dataset and declared indexes in both modes. Check record
atomicity, query results and resource limits through the same engine operations.
Verify that omitted mode selection chooses Durable, MemoryOnly is explicit and
mode selection is fixed for the instance lifetime in both host shapes. Verify the
later v1 restriction: child instances explicitly select MemoryOnly, Durable children
fail configuration validation, and Durable masters retain PostgreSQL persistence.
Prove memory-only startup, mutation, replication and shutdown without writable
database storage or engine-created backing/spill files. Verify that reopening
does not recover lost local data, and that a child fully reloads before reads.
Include a master history reset with existing children and bounded catch-up memory.

Measure steady-state read/write latency, CPU, peak RAM and populated readiness;
report durable acknowledgment settings explicitly. These are planned checks, not
measurements or an implementation delivered by this document.

### Related precedent

SQL Server distinguishes durable data from non-durable memory-optimized table
data. Its SCHEMA_ONLY option still persists schema, so it is not equivalent to
the fully diskless engine mode accepted here. See
[Microsoft's durability documentation](https://learn.microsoft.com/en-us/sql/relational-databases/in-memory-oltp/defining-durability-for-memory-optimized-objects?view=sql-server-ver17).
