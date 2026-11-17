# Engine design reference

**Design baseline transferred on 14 November 2026.** This collection is the durable
reference for what was decided, why, what was tested and what still needs proof.
It is deliberately separate from active plans in `docs/work`.

Where to start depends on the question:

- **What is this database?** The [architecture](architecture.md), one page. The
  [design overview](overview.md) repeats it in prose with links into the decisions.
- **What exists today?** The repository's [main README](../../README.md): the public
  contract and the placeholder behind it.
- **What has to be built?** The [planning handoff](planning-handoff.md), then the
  [milestone 1 implementation contract](milestone-1-engine-contract.md).

## Reading status correctly

- Decision records own accepted product guarantees; the [register](questions.md) owns their status.
- The milestone contract specifies the physical implementation baseline and starting
  settings. Starting settings are not measured optimal values or new service guarantees.
- The illustrated architecture shows the selected first-milestone mechanisms, with
  interactive memory, write and replication illustrations. Historical comparisons
  remain in the decision/evidence records; the overview does not replace detailed contracts.
- `questions/` preserves topic discussions, alternatives and earlier open-mechanism notes.
  A question file is not, by its existence, an unresolved approval request. Read the
  current register and milestone contract before treating historical wording as open.
- Prototype reports preserve measurements, fixture sizes, units and limitations.
  Their source is retained under `src/Prototypes`; none is production engine code.
- OQL and the historical MemoryOnly-first proposal remain documented for future
  reference. They are not the scope of the first production milestone.

## Contents

The first milestone requires PostgreSQL persistence and one database per instance. The
consumer compatibility review inside the milestone contract is preserved as historical
context, not a dependency on a consumer's draft. The handoff separates specified
mechanisms, consumer decisions and release evidence; milestone and sprint planning is
tracked in [Epic 1](../work/epic-1-core.md).

| Document | Purpose |
| --- | --- |
| [Milestone 1 implementation contract](milestone-1-engine-contract.md) | Selected mechanisms, starting limits, deterministic fixture, consumer compatibility and release checks |
| [Operation retry protocol](operation-retry-protocol.md) | Concrete admission/result/retirement state machine, with bounded PostgreSQL evidence |
| [Engine foundation](engine-foundation.md) | Fixed requirements and the ownership overview. Its physical proposal is the earlier text; the milestone contract fixes the mechanisms it leaves open |
| [Arena protocol](arena-protocol.md) | Concrete bounded-experiment protocol for root/text protection, preparation, deletion, relocation and reclamation; production integration limits |
| [How reads are protected](epoch-protection.md) | An explanation for a reader, not a specification: epochs, memory that is freed later, and what an application can rely on; with an [animated version](epoch-protection.html) |
| [How a write is executed](write-path.md) | An explanation for a reader, not a specification: the lock of the writes, groups at the storage step, the order of writes to one entity and what it costs; with an [animated version](write-path.html) |
| [Architecture](index.html) | Illustrated design; generated from [Markdown](architecture.md) |
| [Decision register](questions.md) | Settled contracts and the specified engineering areas; nothing in it awaits a decision |
| [Decision records](../decisions/README.md) | Detailed rationale and alternatives, grouped by responsibility |
| [Database scope](../decisions/database-scope.md) | Accepted one database per instance, its own schema/state and full replicas; no partition abstraction |
| [Persisted base layout](questions/persisted-base-layout.md) | Accepted independent binary base and arena reconstruction; rationale and remaining encoding work |
| [Shared arena application](../decisions/replication-and-hosting.md#shared-arena-application-on-masters-and-replicas) | Master/child reuse of mutation logic, master-only durability and replica readiness |
| [Native segment ownership](../decisions/physical-storage.md#database-owned-native-segments) | Accepted database-dedicated active segments and common bounded empty reserve |
| [Root lookup during relocation](questions/root-location.md) | Accepted stable root control entries; safe acquisition and reuse engineering |
| [Physical index structures](../decisions/index-structures.md) | ArcSlim selected for the primary index; Arc OrderedMultiMap accepted for ordered secondary indexes; fixed-size key copies and shared protected text accepted; preparation and remaining engine integration |
| [Mutation preparation](questions/mutation-preparation.md) | Accepted targeted partial updates and full Save replacement without diff |
| [Write coordination and group commit](questions/master-write-order.md) | Accepted per-object coordination and internal storage batching; the milestone contract specifies the scheduler |
| [Explicit database WriteBatch](questions/write-batch.md) | Accepted atomic writes in one database, without a limit of their own since 15 November 2026; publication/read boundary and costs |
| [Publication-failure recovery](questions/publication-failure.md) | Accepted instance-wide stop and recovery through close/reopen |
| [Operation outcomes](questions/operation-outcomes.md) | Accepted operation identity, compact retained results and bounded safe resubmission |
| [Performance requirements](performance-requirements.md) | Accepted workload and service objectives, with measurement definitions and limits |
| [Prototype evidence](prototype-evidence.md) | Historical synthetic measurements and their limitations |
| [Experiments](../../src/Prototypes/README.md) | Actual runnable projects and commands |
| [Historical MemoryOnly proposal](first-prototype.md) | Isolated experiment scope; superseded as the production milestone plan |
| [Storage and maintenance](storage-and-maintenance.md) | Lifecycle and operational requirements; owns the diagnostics scope (Q18). Its open-choice notes predate the milestone contract |
| [Technical references](references.md) | Public sources supporting the design |
| [Example schema](examples/settings.schema.example.json) | Illustrative descriptor in the form decided on 14 November 2026 (`EntityTypes`, an `Id` on every index); the contract reads that form since Epic 1, sprint S02 |
| [Public distribution](public-distribution.md) | Public source boundary, excluded private material and experiment prerequisites |

Decision records own product contracts; the register owns their status. The architecture
summarizes the design. The milestone contract resolves previously open physical
mechanisms; the foundation retains the connected ownership overview and evidence.
An accepted or specified design is not evidence that a feature exists.
Experiment defaults are not automatically production defaults or release commitments.

The HTML builder includes architecture.md and the SVG assets only. It does not combine
the discussion records into a single page. From the project root, with PowerShell 7:

```powershell
pwsh -NoProfile -File docs/design/Build-Documentation.ps1
```

Experiment commands run from `src`, as documented in their READMEs. The architecture
builder runs from the repository root. Neither command set needs the former workspace.
