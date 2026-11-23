# Database scope

[Decision index](README.md) · [Engine foundation](../design/engine-foundation.md) · [Question register](../design/questions.md)

## Q12: One database per engine instance

**Accepted on 13 November 2026:** remove the partition abstraction. One Lattice
engine instance opens one logical database, owning its schema, records, indexes,
string dictionary, native arena, resource budget and persistence/replication history.
This supersedes the earlier partition model and its lifecycle proposals.

There is no partition catalog, PartitionId, implicit/default partition, routing layer,
per-partition schema sharing, selection API or hidden compatibility wrapper. A native
segment is an internal allocation unit, not a separately addressable database scope.
The first production milestone still requires PostgreSQL persistence.

### Schema, identity and references

Within an open database, object lookup uses entity type and primary key. A type can
have many objects; related types such as User, Group and Role share the database and
can reference each other. The database owns its schema catalog. Separate Lattice
databases do not share a runtime schema catalog or mutable records automatically.

Typed references use the declared target type and its primary key in the same
database. They do not contain an extra routing identifier or native memory address.
References can form graphs and cycles without referential integrity, cascades or a
graph-wide snapshot. Missing non-null targets remain distinct from null references.
Map/Set reference equality follows the same target type/key rules. A reference must
not silently bind to another database when copied; exact SDK context handling is
engineering work. No built-in cross-database references are introduced.

### Memory, writes and durability

The database owns native segments and one bounded reserve of safely empty segments.
Compatible blocks from different records/types may share capacity. Preserve root
protection, stable control entries, relative addressing, allocation classes,
incremental compaction and deferred reclamation. There is no process-global allocator
or reserve shared between independently opened databases selected by this decision.

Plain remains the default string policy; opted-in Dictionary fields share one logical
string dictionary in the database. Entry counts and safe reclamation remain required.

The accepted bounded WriteBatch now groups objects in this database. Its atomic
publication makes no member visible to the database's local access paths before the
complete batch is installed (amended on 22 November 2026, the owner's answer to Q57 of the
open questions). Readers remain available during preparation/storage I/O; separate reads
and scans still have no shared snapshot. Ordinary operations retain per-root
coordination. No cross-database transaction or application-held long lock is added.

Durable success, compact journal/base recovery, operation-result retention and the
instance stop/reopen policy are unchanged. Durable identity/schema metadata must
survive reopen; memory addresses cannot identify persisted objects. The exact mapping
of a Lattice database to PostgreSQL tables/namespaces remains provider engineering,
not a requirement for a dedicated PostgreSQL server or physical database per instance.

### Replicas and independent databases

A child connects to its configured master and loads the whole database before Ready,
including local index construction and catch-up. There is no subset subscription or
selective partition loading. MemoryOnly children, one-arena reload, bounded read
waiting and stale local reads during disconnection retain their accepted contracts.

If an application needs another independent database, it opens another Lattice
instance with its own schema, state and storage identity. Hosting can be embedded or
standalone as already accepted. This does not introduce a multi-database management
service, a shared catalog or automatic cross-database coordination.

Q12 is closed. Former partition creation/removal/reference questions are removed,
not moved into a renamed internal layer. Physical encoding, safe lifetime protocols,
provider recovery, resource bounds and schema-migration mechanics remain in their
existing engineering work packages. No production partition format was implemented,
so this design change does not require a data migration or compatibility format.
