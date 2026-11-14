# Handoff: first production milestone and technical sprints

**Baseline: 14 November 2026. Role: planner. Implementation is not authorized by this brief.**

Plan the first production milestone of Moltaro.Lattice and its technical sprints.
The engine's accepted design is now in this repository. The owner intends to use the
completed milestone in production, so persistence, recovery, memory safety and replica
consistency are release requirements, not work to postpone until after first use.

Do not restart the architecture discussion. Translate the accepted requirements into
a dependency-ordered implementation plan with tests and measurable release gates.
Raise a specific contradiction or missing guarantee if one is discovered; do not hide
it as an implementation detail. Routine file organization and measured tuning are
not reasons to reopen settled product choices.

## Read first

1. [AGENTS.md](../../AGENTS.md) and the [development process](../work/process.md).
2. The [repository README](../../README.md), current public types, samples and tests.
   The runnable managed store is a placeholder, not the native engine.
3. The [milestone 1 engine contract](milestone-1-engine-contract.md), including its
   component ownership, physical layouts, limits, fixture and verification table.
4. The [decision register](questions.md) and relevant [decision records](../decisions/README.md).
5. The [arena protocol](arena-protocol.md), [operation retry protocol](operation-retry-protocol.md),
   [session consistency](../decisions/session-consistency.md),
   [Unique indexes](../decisions/unique-indexes.md) and
   [index structures](../decisions/index-structures.md).
6. [Performance requirements](performance-requirements.md),
   [prototype evidence](prototype-evidence.md) and the relevant
   [experiment sources](../../src/Prototypes/README.md).
7. The existing [Epic 1 document](../work/epic-1-core.md). Reconcile its earlier
   status and scope with this baseline rather than treating its old wording as a
   newer architecture decision.

The [HTML architecture](index.html) is the visual overview; its [Markdown](architecture.md)
and SVG assets are maintained here. Historical discussion files in `questions/` retain
the alternatives and reasoning. They are not an additional queue of approvals.

## Authority and consumer boundary

The latest explicit owner decisions and their maintained contracts govern the engine.
The milestone contract fixes previously open physical mechanisms. Starting configuration
values are engineering baselines to verify, not claims of measured optimality. Earlier
discussion wording is read through the current register and later accepted decisions.

**Identity adapts to Lattice, not the reverse.** The owner has explicitly confirmed this
boundary, and the [Identity work document](../work/epic-2-identity-storage.md) was
updated to it on 14 November 2026. No consumer document is permission to remove declared
indexes, Unique indexes, collection support or atomic multi-type WriteBatch from the
accepted engine scope. Do not redesign that package in the core plan.
The earlier consumer compatibility analysis is retained as historical rationale.

Public facade signatures and placeholder behavior are evidence of what exists today;
they do not override accepted features that have not reached the API yet. Plan explicit
contract work for those gaps. Follow the repository's approval/review process for API
changes, without reopening the already accepted behavior.

## Required first-milestone behavior

- One database per instance, no partitions. Embedded and hosted modes use the same
  engine. Many callers may submit writes; only the configured master executes them.
- Durable PostgreSQL support is required before production. Explicit MemoryOnly remains
  available. Children are MemoryOnly and never connect to PostgreSQL.
- Schema-driven owned entity trees, accepted scalar/temporal/enum types, ordered Lists,
  Maps, Sets, Binary and references by key. References have no integrity enforcement.
  Preserve the accepted encoding, null, comparer and collection rules.
- Typed and schema-driven access to the same data. Ordinary reads return owned copies;
  short protected read-only views provide the separately specified no-copy path.
- Native segments, stable managed root-control entries, bounded allocation and incremental
  compaction, safe text sharing/reclamation and explicit memory-pressure outcomes.
- Arc UnorderedMapSlim for primary indexing and Arc OrderedMultiMap for secondary
  indexing. Declare indexes explicitly and select them explicitly in the facade.
  Named-index lookup never silently falls back to enumeration.
- Explicit single/composite `Unique` indexes. Validate the final operation/batch state
  at the master before effects commit; any-null keys are exempt. A conflict rejects
  the complete operation or WriteBatch. Atomic swaps and delete/reclaim are supported.
- Complete-object Save without diff, explicit partial updates, expected revisions and
  bounded atomic WriteBatch across entity types. One operation per type/key in a batch.
- Parallel preparation and ordered bounded commit/publication. Prepare resources before
  effects commit; no memory gate spans storage/network I/O. Faulted publication stops
  the instance until reopening/recovery rather than exposing partial success.
- PostgreSQL compact logical journal, independent binary base with 256-byte saved units,
  background coalescing, atomic base/cursor commits and bounded retention. Saved units
  are not the arena allocation size and are not a limit on entity size.
- Durable first admission, retained operation outcomes and bounded safe retry, with a
  15-minute default window. Include the admission transaction's cost in write measurements.
  A revision, an applied position and an operation identity are different things.
- Initial child load from master memory, locally rebuilt indexes, complete-transaction
  catch-up and readiness at a fixed position. Subsequent changes are pushed in order.
  Bounded RAM delivery history is released after required acknowledgments.
- A child-facing write succeeds only after durable master completion and local application
  on that calling child. Related requests propagate a minimum history/position and wait
  with a timeout on any child. Waiting does not freeze data or wait for every replica.
- One-arena full reload when retained history is unavailable, bounded read waiting,
  history mismatch handling, and local unconstrained reads during disconnection as specified.
- Stable schema identities and explicit eager migration with affected data paused.
  Diagnostic APIs, automatic maintenance and infrastructure-owned PostgreSQL backup/restore.

Use the detailed owning contracts for edge cases; this list is a planning checklist,
not a replacement specification.

## Outside this milestone

OQL/parser/planner, joins, automatic index choice, collection-member indexes,
cross-call database/graph snapshots, external transactions, partitioning, automatic
failover/election, lazy mixed-schema conversion, generic keyset continuation and manual
maintenance CLI/GUI/API commands. No message broker is part of the system.

The old MemoryOnly-first experiment is retained for historical hypotheses. It is not
the production milestone. Do not defer PostgreSQL or replication to a later production
release merely because an early technical sprint can test its component in isolation.

## Evidence and what it does not prove

The reference collection retains all twelve synthetic prototype projects, their
configuration, source and summarized results. Important combined checks include native
arena/index/string lifetimes, primary-load replay, WriteBatch plus PostgreSQL, the
256-byte saved-unit/applier extension and operation retry. The last bounded retry run
passed 13 protocol scenarios, nine Unique scenarios and two process-exit/recovery cases.

These checks establish particular mechanisms under stated fixture/resource limits.
They do not establish complete schema coverage, production multi-core correctness,
network-fault behavior, physical server-crash recovery or the full service objectives.
Schedule those missing checks explicitly. Do not copy prototype timings into release
claims or promote prototype code into the engine without contract tests and review.

Keep ordinary checks small. Large datasets and 5–10-child tests belong in explicit
bounded performance/integration runs, not a default workstation test sweep. Do not
launch heavy measurements simply to write this plan.

## Quantified production gates

The [performance requirements](performance-requirements.md) define accounting and
scenario boundaries; the [engine contract fixture](milestone-1-engine-contract.md#reproducible-first-fixture)
fixes the initial synthetic record shapes, indexes and operation mix.

| Gate | Accepted target |
| --- | --- |
| Reference roots | 121,000: 100,000 users, 10,000 groups, 1,000 roles, 10,000 settings |
| Deployment profile | 5–10 child replicas |
| Ordinary offered load | 10,000 local reads/s per ready child; 200 logical root changes/s total through the master |
| Ordinary small-read latency | p99 <=1 ms after the required position has already been applied |
| Ordinary child-facing write latency | p99 <=50 ms, including durable master work and application on the calling child |
| New-child readiness | <=10 seconds on the reference dataset under ordinary load |
| Retained storage memory | At least 25% less than an equivalent optimized managed reference, including comparable indexes and metadata |
| Ordinary/startup node peak | <=1.5 times the defined steady-state node memory; use the accepted accounting boundary |
| Separate peak profile | 10,000 changes/s for 30 seconds, then return to ordinary operation within 60 seconds at 200 changes/s |

Report offered, admitted, completed, refused and uncertain operations separately.
The burst is a capacity characterization, not a promise to accept unlimited work or
reuse ordinary p99 targets during overload. Hardware, network, absolute resource limits
and run budgets must be recorded; measurements without them are not comparable.

## Planning obligations

The design register has 49 accepted topic records and nine engineering areas with
specified implementation baselines. No known architecture selection is left awaiting
owner approval for this engine scope. This is permission to plan, not a claim that
implementation or production validation is complete.

Use the E01–E08 responsibility owners and the nine-row verification table to map every
required capability to a sprint and a release check. The difficult boundaries must be
visible: allocation/lifetime, prepared index growth, complete-batch publication,
foreground versus background durability, expiry/unknown outcomes, initial transfer
versus live replay, schema migration, and bounded memory during all of them.

Preserve the dependency-free public library boundary in the current repository when
planning the implementation assemblies. The selected Arc structures and PostgreSQL
driver must have explicit owners and dependency directions. Do not quietly replace
the accepted collections or add product dependencies during documentation work.

Distinguish a technical sprint from a production milestone: a sprint may finish an
isolated mechanism, while the milestone requires the integrated system and its gates.
Plan contract additions and tests ahead of implementation, as the development process
requires. Do not use temporary placeholder shortcuts as proof of engine completion.

## Deliverable for the planning agent

Update the existing Epic 1 working document using the repository's epic format. Provide
its goal, explicit scope, numbered requirements, acceptance gates, dependency order and
technical sprint briefs. Each brief needs its contract, documents to read, acceptance
tests and measurements, exclusions and full/light track. Include all required release
verification, rather than leaving it as an unowned final task.

Link requirements and sprint acceptance back to these durable decisions and evidence.
Maintain progress in the epic, while these references retain what was agreed and why.
Do not duplicate the physical specification inside every sprint or overwrite the
accepted baseline with a narrower plan. Plan review and owner approval precede execution.

The requested output is the plan and any concrete unresolved contradiction found while
preparing it. It is not production code, a new architecture questionnaire, an Identity
redesign, a benchmark campaign or a promise of a delivery date.
