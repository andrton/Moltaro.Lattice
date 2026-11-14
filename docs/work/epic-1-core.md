# Epic 1: Core

**Status:** the engine design is accepted; the epic is ready to be planned and has no sprints yet · 14 November 2026

## Goal

Replace the placeholder behind the public contract with the real database engine, as the
first production milestone: durable, replicated, within its memory and latency targets.

Today the contract (`LatticeDatabase`, `ILatticeDatabase`, sets, records, write batches,
the compiled schema) is served by a simple managed in-memory implementation. It exists so
that the contract, the samples and the tests can run. It supports MemoryOnly mode, scalar
fields and a master with its children inside one process. It is not the engine.

## Where the scope comes from

The scope is not decided in this document. The engine design was accepted on 14 November
2026 and is kept in this repository:

| Document | What it gives the plan |
| --- | --- |
| [Planning handoff](../design/planning-handoff.md) | The brief for the planner: required behavior, what is outside the milestone, the production gates, the deliverable |
| [Milestone 1 engine contract](../design/milestone-1-engine-contract.md) | Component ownership, physical layouts, limits, the reference fixture and the verification table |
| [Decision register](../design/questions.md) and [decision records](../decisions/README.md) | What was agreed and why |
| [Performance requirements](../design/performance-requirements.md), [prototype evidence](../design/prototype-evidence.md) | The targets, and what the experiments did and did not prove |

The planner turns that into numbered requirements, acceptance gates and sprint briefs in
this file, as the [development process](process.md) describes. Until then the sections
below record only where the public contract stands against the accepted scope, so that the
plan starts from the present state.

## The contract against the accepted scope

**In the contract and working in the placeholder.** The engine has to keep these; their
tests exist and run.

- Opening and closing; master and child; MemoryOnly.
- The compiled schema: JSON descriptor, binary save and load, optional .NET class.
- Sets by class and by name; read, enumerate, insert, replace, upsert, delete; expected
  revisions.
- Positions and sessions; a write through a child completes when that child has applied
  it; waiting for a position with a timeout.

**In the contract, not implemented.** Entry points exist and refuse with
`NotSupportedException`; tests are written and skipped; samples are written.

| Feature | Contract | Tests waiting |
| --- | --- | --- |
| Search through a declared index | `set.Index(name).Find` / `FindAsync` | `IndexTests` |
| Unique indexes | `"Unique": true`, `LatticeIndex.IsUnique`, `UniqueConstraintViolation` | `UniqueIndexTests` |
| Atomic write batch across entity types | `ILatticeWriteBatch` | `WriteBatchTests` |
| Durable mode | `LatticePersistenceModeEnum.Durable`, the default | `OpeningAndClosingTests` |

**Accepted for the milestone, not in the contract yet.** Each needs contract work — public
types, a sample, tests — before its implementation, in the sprint that owns it.

- Owned entity trees and collections: List, Map, Set; references by key; the remaining
  accepted field types.
- Explicit partial updates.
- Protected read-only views, the no-copy read path.
- Operation identities, retained outcomes and safe retry of a write.
- The limits of a write batch and of an entity as options, and the resource errors.
- Schema migration; diagnostics.
- A network transport between master and children; today they meet inside one process.

The planner also decides where the engine's dependencies live. The core library has no
package dependencies today and the handoff asks to keep that boundary for the public
library, while the engine uses the selected index structures and a PostgreSQL driver.

## Consumers

[Epic 2](epic-2-identity-storage.md), the storage for ASP.NET Core Identity, is the first
consumer. It adapts to the engine and does not set its scope. It relies on index search,
unique indexes and the write batch, and its skipped tests become a check of the finished
engine.

## Open questions

None are recorded here. The design register reports no architecture choice awaiting
approval; a contradiction or a missing guarantee found while planning is raised as a
specific question, not settled inside a sprint.
