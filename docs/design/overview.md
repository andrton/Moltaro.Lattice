# Moltaro.Lattice

A schema-driven in-memory object database for .NET, designed for compact native
storage, low-allocation access, master-coordinated writes and local read replicas.

**Read the [architecture](architecture.md) first.** It is the short description of what
the database is. This page says the same in prose, with links into the decision records
for each statement; it adds no guarantee of its own.

**Status:** this collection describes the intended engine and isolated experiments,
not an implemented production engine. The [repository README](../../README.md) owns
the current status of the facade and the engine. The [first-milestone implementation contract](milestone-1-engine-contract.md)
fixes the physical baseline and the accepted unique-index contract.
PostgreSQL persistence is mandatory; milestone planning is tracked in
[Epic 1](../work/epic-1/README.md).

The [first production workload](performance-requirements.md#accepted-primary-workload-and-first-milestone-access-scope)
is users, groups, roles and settings with 5–10 child replicas. A separate scenario
tests 10,000 changes/s peak demand. OQL is outside this milestone; declared
single-field/composite indexes and [explicit facade selection](../decisions/index-access.md)
are required, without automatic index selection. These are
scope decisions, not claims of an implemented engine or measured capacity.

## Database model

One engine instance opens one database with its own schema, records, indexes, string
dictionary and arena. Lookup uses entity type and primary key. References and bounded
atomic WriteBatch stay inside the database. Native segments are internal allocation
units; there is no partition abstraction or shared catalog for several databases.

Many application threads can submit writes to the authoritative master. Independent
preparation is concurrent; [short bounded groups commit and publish in order](../decisions/write-acknowledgment.md#ordered-commit-and-publication).
A child loads the whole database before serving local reads. Durable masters use PostgreSQL;
explicit MemoryOnly remains available. Embedded and standalone hosting share the engine.

Children are MemoryOnly: they receive initial state and confirmed binary changes
only from the master and never connect to PostgreSQL. Master writes and child replay
share the arena application mechanism; Durable master writes add a storage commit
before local publication. The [accepted initial-loading protocol](../decisions/replication-and-hosting.md#initial-capture-and-readiness-handoff)
opens reads after full loading, local index construction and complete-transaction
catch-up through a fixed published master position.
Replication is served from master memory. A shared buffer retains changes only while
recipients still need them and releases them after acknowledgment, without an extra
history window. New children load full state; missing reconnect history requires
full reload. PostgreSQL remains the Durable master's recovery store, not a replica
delivery source.

A write forwarded through a child must wait for that child's coherent local
application before returning success. The master's durable completion rule is
unchanged. [Session consistency](../decisions/session-consistency.md) carries a
minimum applied history/position across related requests. Any serving pod waits for
that fixed target with a timeout. There is no all-replica completion barrier or
mandatory sticky routing; unconstrained reads retain local-state semantics.
An explicit asynchronous local-position wait can precede indexed reads; the same
dependency stays in their request context. Waiting does not freeze data.

The [first-release diagnostic contract](storage-and-maintenance.md#control-and-maintenance-tools-q18)
provides DI services and ready-made HTTP endpoints under an application-selected
base path. Lattice serves local status and forwards master-status requests from a
child through the internal protocol. No public master endpoint or separate management
host is required. The administrative API is diagnostic-only; manual maintenance
commands, a CLI and GUI are outside the first release. Automatic maintenance follows
the accepted engine policies. This API remains
design work, not implemented SDK functionality.

[Backup and restore execution](storage-and-maintenance.md#accepted-backup-and-restore-control)
belongs to infrastructure using PostgreSQL-native mechanisms. V1 has no API commands
to launch these jobs; verified engine recovery and diagnostic outcomes remain required.

The [historical MemoryOnly experiment](first-prototype.md) proposes a synthetic
users/groups/roles workload for measuring allocation, reads, updates and compaction.
Its hypotheses are useful for focused checks, not a production release plan.

## Documentation

- [Milestone 1 implementation contract](milestone-1-engine-contract.md): technical baseline, consumer compatibility and verification handoff.
- [Operation retry protocol](operation-retry-protocol.md): durable admission, results, expiry and recovery.
- [Engine foundation](engine-foundation.md): current physical design and completion criteria.
- [Arena protocol](arena-protocol.md): ownership, protection, publication and reclamation, with a bounded integrated experiment.
- [Database scope](../decisions/database-scope.md): ownership, identity and replica boundary.
- [Historical experiment](first-prototype.md): synthetic workload and measurement hypotheses.
- [Architecture](architecture.md): the short description; [index.html](index.html) is the same text with animated diagrams.
- [Decision register](questions.md): settled contracts and the engineering areas the milestone contract specifies.
- [Design decisions](../decisions/README.md): rationale and alternatives by topic.
- [Experiments](../../src/Prototypes/README.md): runnable standalone benchmarks and limitations.
- [Measurement evidence](prototype-evidence.md): historical synthetic results.
- [Public distribution](public-distribution.md): repository boundary and prerequisites.

Design decisions describe intended behavior; they do not establish implementation,
measured product performance or a release date.

## Working with these references

Prototype commands run from the repository's `src` directory.
Existing prototypes require the .NET 10 SDK; each prototype documents its own commands.
PostgreSQL-backed experiments require a local server. No company packages,
private datasets or private repositories are needed for the public experiments.

Rebuild the architecture HTML with PowerShell 7:

```powershell
pwsh -NoProfile -File docs/design/Build-Documentation.ps1
```

Keep credentials and generated output out of version control. Prototype run commands
do not launch the future production engine. Product commands remain in the main README.
