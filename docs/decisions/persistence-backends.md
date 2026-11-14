# Durable storage backends

Accepted Moltaro.Lattice storage direction, rationale and remaining engineering
work. Backend selection is a design decision, not an implemented engine.

[Decision index](README.md) · [Question register](../design/questions.md) · [Persistence modes](persistence-modes.md) · [Storage and maintenance](../design/storage-and-maintenance.md)

- [Q09.6](#q09.6-durable-storage-backends)

<a id="q09.6-durable-storage-backends"></a>

## Q09.6: Durable storage backends

**Accepted on 12 November 2026:** start the first Durable implementation
with PostgreSQL. This closes the backend/initial-delivery choice in Q09.6. Managed
backup/restore, operational trust and deployment simplicity, together with the
initial performance evidence, support this choice.

PostgreSQL is the selected first backend, beyond its earlier candidate status.
An engine-owned file backend remains a possible later addition; its release timing
is not selected and it is not a prerequisite for implementing PostgreSQL storage.
The native core and MemoryOnly mode stay independent of SQL persistence. Durable
operation through this first backend requires a PostgreSQL service, including when
the master is embedded. This refines the earlier dependency-free Durable direction.

Production PostgreSQL version/hosting, the precise provider contract and persisted
format remain engineering work under E06, subject to the open durability, recovery
and deployment requirements. Npgsql is the evaluated .NET driver; the prototype's
versions and table layout are not production contracts. No production backend is
delivered by this record.

A bounded local PostgreSQL/Npgsql experiment is available.
[PostgresStorageComparison](../../src/Prototypes/PostgresStorageComparison/README.md)
implements that isolated baseline; selecting the backend does not certify its
production throughput or recovery behavior.

### Separate persistence mode from storage destination

Q09.5 accepts persistence selection at instance open time: Durable by default or
explicit MemoryOnly. This question asks where Durable state is stored. MemoryOnly
has no local or remote persistence destination;
storing data in a remote SQL database is durable operation even if the application
host has no database files. PostgreSQL persistence stays outside the core arena and
ordinary read path. MemoryOnly does not require the PostgreSQL driver or service.

The native arena, root gates, OQL, in-memory indexes and replica protocol remain
engine responsibilities. A SQL backend can store opaque binary chunks without a
relational row for each application document. It need not understand object fields
or execute application predicates. SQL indexes would address storage metadata and
chunk/log identities, not replace the engine's search indexes.

### Managed SQL's operational value

The design favors a service that a team can provision in its cloud and operate
using established database procedures. Automatic backups and point-in-time restore
are concrete capabilities of services such as
[Azure SQL Database](https://learn.microsoft.com/en-us/azure/azure-sql/database/automated-backups-overview?view=azuresql)
and [Amazon RDS](https://docs.aws.amazon.com/AmazonRDS/latest/UserGuide/USER_WorkingWithAutomatedBackups.html),
subject to service configuration and retention. Familiar access management,
monitoring and restore procedures can also matter more than minimum write latency.
There is no assumption that every SQL product, cloud, region or tier has identical
features, nor that running a SQL server on a self-managed VM gives those benefits.

File storage does not necessarily require a dedicated VM: a persistent volume is
also possible. It still needs a coherent backup/capture policy, storage lifecycle,
recovery tooling and operational ownership. Compare that complete operational
package against managed SQL, including the effort to verify successful restoration.
An opaque byte format does not prevent SQL-managed backup and restore.

Trust in an established persistence service is a meaningful criterion. It does not
prove that the engine saved a complete, interpretable state or that its in-memory
publication protocol is correct; those remain engine obligations under Q09.

### Candidate binary representation

[Q09.2](checkpoint-and-journal.md#q09.2-postgresql-base-and-compact-journal) accepts
a compact journal and recoverable saved state advanced by background application
with coalescing. PostgreSQL provides physical WAL/recovery; the provider interprets and replays its stored
changes. The strategy is accepted; exact SQL layout and encoding remain open:

| Stored material | Purpose |
| --- | --- |
| Base units | A complete recoverable base at a known committed boundary; 256-byte baseline payload units, independent of native segments |
| Schema and publication metadata | Recoverable format/schema/dictionary state and ordering identity, consistent with the corresponding data |
| Compact changes | Provider-owned replay input after the base boundary; requires safe compaction and bounded retention |
| Optional delivery history | Defined by child catch-up/reconnect guarantees; reuse of a recovery journal is not automatic |

Transactions must persist the journal/base representation and metadata coherently.
After restart, PostgreSQL recovers its rows; the provider loads the published base
and replays its required later changes before readiness. Application replay follows
from the accepted data model, not a gap in PostgreSQL durability. Automatic size/age
triggers, hard-limit refusal and background application with atomic saved-data/
position commits are accepted under Q09.2, together with the 256-byte saved-unit
baseline and short bounded application batches with immediate catch-up. The applier
runs inside the master process. During its transient failure, healthy journal
appends continue within budget while it retries with pauses. Detailed failure
classification, retained consumers and numerical limits remain open.

Persisted chunks need not equal native allocation segments. Their sizes and copy
boundaries should suit update and transfer costs. A native address is not a portable
persisted locator. Schema, dictionaries, allocator/record enumeration metadata
and the selected index policy must remain recoverable together.

The comparison of batched current-state updates illustrates why persisted unit
size matters even though that ordinary-write strategy was not selected for v1. Replacing a 1 MiB payload for every 8-byte field update
would send 131,072 times that field's bytes before batching/compression; this is an
illustration of a naive payload choice, not measured physical SQL write amplification.
Some engines support partial BLOB updates: SQL Server exposes
[UPDATE .WRITE for large value types](https://learn.microsoft.com/en-us/sql/t-sql/queries/update-transact-sql?view=sql-server-ver17).
Do not assume every SQL implementation rewrites every BLOB in full or that a partial
update has no logging cost. PostgreSQL offers a
[bytea binary type](https://www.postgresql.org/docs/current/datatype-binary.html);
storage/update behavior must be measured for the selected engine and client.

PostgreSQL owns its WAL and physical checkpoints. Lattice owns its data format,
coherent native publication and child delivery. A child change stream is not
automatically a master recovery journal, and neither implies an additional local
file WAL. Any optional history needs bounded retention and safe reclamation.

### Costs and correctness boundaries

| Aspect | Possible later engine-owned files | Selected first PostgreSQL backend |
| --- | --- | --- |
| Resident reads | Local arena/index access | Same local path; no SQL call per ordinary read |
| Durable writes | File writes/flushes under the chosen protocol | Database commands, transaction commits and possibly network round trips; batching trades throughput against latency |
| Startup | Load files, recover, prepare indexes | Stream binary state from SQL, recover, prepare indexes; throughput and round trips must be measured |
| Memory | Arena plus recovery/checkpoint buffers | Same arena plus client/transfer buffers; SQL consumes its own cache/resources, possibly on another host |
| Operations | Own the file backup/storage/recovery workflow | Reuse configured SQL service backup/restore facilities; still validate engine recovery from restored bytes |
| Dependencies and cost | No SQL service; provision and operate appropriate storage | PostgreSQL service, driver, permissions and service charges for this Durable backend; provider-specific behavior and tests |

The SQL transaction boundary must align with engine acknowledgment and publication
of committed state. A lost connection after a server commit can leave the outcome
unknown to the client; recovery and operation identity need a defined contract.
SQL atomicity does not make RAM changes or replica application atomic with SQL
automatically. No root-document lock may be held across SQL/network I/O.

SQL-backed engine storage also does not automatically make unrelated business SQL
updates and an engine mutation one transaction. That integration is excluded from v1 by [Q09.3](external-transactions.md).
SQL service failover does not elect the engine's master; Q15 still owns one active
writer. Direct SQL edits to opaque engine tables cannot bypass that writer.

### Evidence and remaining validation

The initial prototype uses local PostgreSQL/Npgsql and engine-independent file
journals with explicit durable flushes. It creates and removes its own database;
it does not provision a service. Its fixed native roots and binary transfer tests
omit application indexes and other full-engine costs. Later comparisons must include
equivalent data, indexes, durability guarantees and mutation batches. Keep the
experiment scope separate from a universal SQL adapter or production implementation.

[Initial measured evidence](../design/prototype-evidence.md#postgresql-storage-and-streaming)
demonstrates batched change-row INSERTs and streamed loading. A subsequent
[unit comparison](../design/prototype-evidence.md#postgresql-current-state-units-and-journal-compaction)
measures UPDATE/UPSERT separately, including compression policy and paused journal
compaction. Capacity depends materially on representation; the initial append rate
cannot stand in for direct updates. The decision selects
PostgreSQL afterward and subsequently accepted
[durable storage acknowledgment](write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed).
The experiment does not establish production capacity or implement that contract.

The evaluation must cover thousands of small logical changes per
second, their impact on resident reads, and SQL streaming for binary state loading.
[Performance requirements](../design/performance-requirements.md) own the workload and
measurement details, including bounded batching, acknowledgment latency, writer
queue growth and streamed-load interference. Numeric SLOs remain Q10.1 work.

Measure resident query latency, durable write throughput/tail latency, data and
log bytes written, checkpoint work, full startup and peak RAM. Include frequent
small updates, variable-length data, checkpoint contention and realistic network
placement. Measure chunk sizes and batching rather than treating one naive BLOB
update loop as proof against SQL storage.

Also compare provisioning steps, operational ownership, retention configuration,
service/storage cost and a real backup/restore exercise. Restore into a new target,
open the engine and verify data/index results; exercise partial checkpoint writes,
commit-outcome uncertainty and child resynchronization after restoration. Provider
backup success alone is not the end-to-end recovery acceptance criterion.

Implement PostgreSQL persistence first while keeping persistence actions separate
from arena/query logic. Q09.5's MemoryOnly mode remains accepted in v1. A future
file backend does not justify implementing a plugin framework or speculative
universal storage interface now.
