# Epic 1: Core

**Status:** planned and reviewed · 14 November 2026. The owner's answers to the questions
of the plan and of its reviews are recorded below. Seven independent reviews followed one
another until the last found nothing that blocks. The owner approved the plan by merging
it into `main`. The sprints are being carried out; [Status](#status) says where each one
stands.

## Goal

Replace the placeholder behind the public contract with the real database engine, as the
first production milestone: durable in PostgreSQL, replicated to 5–10 children, within
its memory and latency targets. It is for applications that keep a read-dominated working
set in memory on every node and need writes that are durable, ordered and atomic.

Today the contract (`LatticeDatabase`, `ILatticeDatabase`, sets, records, write batches,
the compiled schema) is served by a simple managed in-memory implementation. It supports
MemoryOnly mode, scalar fields and a master with its children inside one process. It is
not the engine, and nothing in this plan treats its behavior as proof of the engine.

## Scope

The scope is not decided in this document. The engine design was accepted on 14 November
2026; this plan orders it and gives every part an owner.

| Document | What the plan takes from it |
| --- | --- |
| [Planning handoff](../../design/planning-handoff.md) | Required behavior, what is outside the milestone, the production gates |
| [Milestone 1 engine contract](../../design/milestone-1-engine-contract.md) | Component owners E01–E08, physical layouts, limits, the reference fixture, the verification table |
| [Decision register](../../design/questions.md) and [decision records](../../decisions/README.md) | What was agreed and why |
| [Performance requirements](../../design/performance-requirements.md), [prototype evidence](../../design/prototype-evidence.md) | The targets, and what the experiments did and did not prove |

**Included.** One database per instance; a master that is embedded or hosted; Durable
persistence through a public provider contract, in PostgreSQL and in SQL Server, and
explicit MemoryOnly; MemoryOnly children over TCP. The
accepted schema and type model with owned trees, List, Map, Set, Binary and references by
key. Typed access and access by name, owned copies and protected read-only views. Native
storage with bounded allocation, compaction and explicit memory-pressure outcomes. Primary
and declared secondary indexes, unique indexes, index search. Complete writes, explicit
partial updates, expected revisions, the atomic write batch. Operation identities and safe
retry. Journal, saved base, recovery, restore into a new history. Session consistency.
Explicit eager schema migration. Diagnostics. Every release check of the
[verification table](../../design/milestone-1-engine-contract.md#verification-handoff-and-closure).

**Outside this milestone.** A query language, its parser and planner; joins; automatic
index choice; indexes on collection members; snapshots across calls; external
transactions; lazy conversion of mixed schemas; generic continuation by the last key;
manual maintenance commands, CLI or GUI; a message broker. A partition layer and the
election or promotion of a master are not part of the design at all. Nothing else is left
out: an accepted feature that is hard is scheduled, not deferred.

**Not part of this epic.** The stores of [Epic 2](../epic-2-identity-storage.md). That package
adapts to the engine and does not set its scope. Its skipped tests, including Microsoft's
specification tests, become a check of the finished engine when Epic 2 runs.

## Sprints

### Rules for every sprint

- The [development process](../process.md) applies. On the full track one implementer puts
  the contract into code and writes the tests in commits that come before any of its
  implementation; the reviewer and the breaker give the independent view.
- A sprint on the full track has its own branch, `epic-1/sprint-NN-name`, cut from
  `main` and merged back when the sprint closes, as [branches and releases](../branches.md)
  says; the light sprint S01 uses a light-track branch. One sprint runs at a time. `main` stays green after every merge: what is not implemented refuses and
  its tests are skipped.
- Every document under **To read** is read at its baseline of 14 November 2026. If one
  changes, the coordinator stops and has this plan corrected.
- The briefs of S02 to S12 name their types and members exactly, under "Types and
  members"; the owner approves them with the plan, and the implementer of the sprint puts
  them into code as written. A later brief says what its contract must be able to do, and the
  planner writes its types and members before its stage begins: S13–S17, S18–S25,
  S26–S32, S33–S36, S37–S39. The owner approves the contract of each stage before its
  first sprint, as the [development process](../process.md#the-plan) says. For stage 5 he
  approves it in two parts (the owner, 23 November 2026): S18 and S19 together, which add
  no type and no member, so that S18 does not wait; then S20, S21 and S22 together,
  written and checked as one, before S20. S23 to S25 add none.
- A feature is delivered for entity classes and for records by name in the same sprint.
- From S12 on, a sprint that changes what is stored also delivers what a child needs:
  the transfer of the new structure in the initial load, its live application, and a
  test in which a child loads it and follows changes to it. From S30 on, the same holds
  for the saved base, the journal and recovery.
- A sprint that adds a public behavior adds or extends its sample.
- A timeout or a limit that an application can observe is an option with a default,
  never a constant in the code. It becomes an option in the sprint that owns the
  mechanism, with the starting value of the
  [engine contract](../../design/milestone-1-engine-contract.md#initial-bounded-settings),
  and all options are validated together at open.
- A departure from a rule of `AGENTS.md` is approved by the owner, entered in
  [exceptions to the rules](../exceptions.md) when it is approved and moved to **In force**
  there by the change that introduces it.
- The physical specification is not repeated here. A brief names the section that owns it.
- A verification sprint is on the full track: its implementer writes the scenarios and
  the harness in step 1 and then fixes what they expose.
- A test that only asserts that a feature is not implemented, such as
  `Search_is_not_implemented_yet`, is removed by the implementer in step 1 of the sprint
  that implements the feature. "Assertions unchanged" speaks of the tests of the
  behavior.
- An acceptance item that says what the code must not do — hold a lock across an `await`,
  perform I/O inside publication, run user code inside a gate — is checked by the two
  reviews of the sprint, and by a test where the brief names one.

### Dependency order

```text
S01 ground
 └─ S02 schema ── S03 schema of owned values and collections
     └─ S04 arena ── S05 root layout ── S06 one entity ── S07 key types
         └─ S08 enumeration, index growth ── S09 coordinator
             └─ S10 child loads ── S11 child follows
                 └─ S12 child writes, sessions; placeholder removed
                     └─ S13 text entries ── S14 exact search ── S15 ranges, binding
                         └─ S16 unique ── S17 write batch ── S18 owned values, lists
                             └─ S19 maps, sets ── S20 partial updates
                                 └─ S21 views by name ── S21I List traversal ── S22 typed views
                                     └─ S22C position-wait cancellation ── S23 compaction ── S23A segment evacuation
                                         └─ S24 memory budget ── S24A budget corrections ── S24R budget refactoring ── S24B budget by structure ── S24BR ── S25 memory gate ── S25L one lifecycle ── S26 operation identity
                                             └─ S27 provider ── S28 durable commit
                                                 └─ S29 storage failures ── S30 saved base
                                                     └─ S31 journal limits ── S31S SQL Server provider ── S32 recovery
                                                         └─ S33 bounded delivery
                                                             └─ S34 reconnect ── S34A advisory locks ── S35 cluster
                                                                 └─ S36 restore ── S37 ── S38
                                                                     └─ S39 ── S40 ── S41 ── S42
```

Why this order:

- **One vertical slice first.** S04–S12 take scalar entities from native memory to a
  child over TCP. It ends the life of the placeholder early; from then on every feature
  is tested only through the public API, on both roles.
- **The coordinator before anything that commits.** Its commit stage has a storage step
  from the start, which completes at once in MemoryOnly. Replication reads the published
  stream and does not care whether the commit was durable, so it can come before
  PostgreSQL without rework.
- **Text entries before secondary indexes.** An index shares text through stable text
  entries.
- **The full type model before persistence.** The journal, the applier
  and the operation fingerprint all encode ordered commands; built after S20 they see the
  whole vocabulary once. The wire format starts in S11 with complete writes and gains
  each command in the sprint that adds it. It also lets S25 measure the memory gate on one node before the
  second half of the epic.
- **Operation identities before PostgreSQL.** The durable commit transaction is defined
  by the receipt protocol: admission first, then effects and outcome together.
- **One provider builds the contract; the second proves it.** By the owner's decision of
  27 November 2026 ([Q09.6](../../decisions/persistence-backends.md#a-public-provider-contract-and-a-second-provider))
  the engine reaches durable storage through a public provider contract and keeps the
  format, the journal, coalescing, recovery and cleanup for itself.
  - S27 to S31 build that contract and its conformance tests with PostgreSQL, the
    production backend.
  - S31S builds the SQL Server provider against the contract of S27 to S31, once.
  - From S32 on, the shared tests of durable storage run with both providers, and S36
    and S37, which extend the contract, extend both providers. Kills, restarts and the
    backup with a server's own tools stay with PostgreSQL, the production backend.
- **Migration and diagnostics last among the features.** Migration needs the saved base
  and the reload of children; diagnostics report counters that exist only when their
  mechanisms do.
- After S17 nothing in the core blocks the MemoryOnly stages of Epic 2. Whether that
  epic starts before this one ends is the owner's decision and not part of this plan.

### Status

| Sprint | Result | Track | Status |
| --- | --- | --- | --- |
| [S01](sprints/s01.md) | Assemblies, package ownership, the default lane | Light | Done |
| [S02](sprints/s02.md) | Schema: identities, text policies, enums | Full | Done |
| [S03](sprints/s03.md) | Schema and values: owned types, collections, references | Full | Done |
| [S04](sprints/s04.md) | Arena | Full | Done |
| [S05](sprints/s05.md) | Root layout and values | Full | Done |
| [S06](sprints/s06.md) | Reads and writes of one entity | Full | Done |
| [S07](sprints/s07.md) | Every key type and declared value | Full | Done |
| [S08, part 1](sprints/s08.md) | The protection of reads: reads by key scale with threads | Full | Done |
| [S08, part 2](sprints/s08.md) | Enumeration under change and the read limits | Full | Done |
| [S09](sprints/s09.md) | Write coordinator and write limits | Full | Done |
| [S09, part 2](sprints/s09-part-2.md) | The write path after a proven source | Full | Done |
| [S10](sprints/s10.md) | A child loads the database | Full | Done |
| [S11](sprints/s11.md) | A child loads under writes and follows the master | Full | Done |
| [S12](sprints/s12.md) | Writes through a child, sessions; the placeholder is removed | Full | Done |
| [S13](sprints/s13.md) | Text entries and Dictionary strings | Full | Done |
| [After S13](sprints/after-s13.md) | The fixes after the outside review | Full | Done |
| S13R, part 1 | Refactoring that owes no measured figure | Full | Done |
| [S13R, part 2](sprints/s13r-part-2.md) | Refactoring that owes a measured figure | Full | Done |
| [S14](sprints/s14.md) | Secondary indexes and exact search | Full | Done |
| [S14, part 2](sprints/s14-part-2.md) | The cost of index upkeep and of a search | Full | Done |
| S14R | Refactoring after S14: structure only | Full | Done, in two parts, on 21 November 2026 ([refactoring backlog](../refactoring.md), its order) |
| [S15, part 1](sprints/s15.md) | The walk: leading parts, ranges, both directions, Offset and Limit, at most once | Full, critical | Done on 21 November 2026 |
| [S15, part 2](sprints/s15.md) | The binding rules | Full | Done on 21 November 2026 |
| S15R | Refactoring after S15 | Full | Done ([refactoring backlog](../refactoring.md), its order) |
| [S16](sprints/s16.md) | Unique indexes | Full | Done on 22 November 2026 |
| S16R | Refactoring after S16 | Full | Done on 22 November 2026 ([refactoring backlog](../refactoring.md), its order) |
| [S17, part 1](sprints/s17.md) | The reservation of a publication and a child's transaction checked on the state it leaves | Full, critical | Done on 22 November 2026 |
| [S17, part 2](sprints/s17.md) | A batch exposed whole, its commit on a master, unique values on the final state, the waits | Full, critical | Done on 23 November 2026 |
| [S17, part 3](sprints/s17.md) | A child applies a batch; a batch through a child | Full, critical | Done on 23 November 2026 |
| S17R | Refactoring after S17 | Full | Done on 23 November 2026 ([refactoring backlog](../refactoring.md), its order) |
| [S18](sprints/s18.md) | Owned values, Lists and references | Full, critical | Done on 24 November 2026, in three parts; its Measured line taken after the close |
| [S19](sprints/s19.md) | Maps and Sets | Full, critical | Done on 24 November 2026, in three parts |
| [S20](sprints/s20.md) | Partial updates | Full, critical | Done on 24 November 2026, Coordinator 11 |
| [S20I](sprints/s20i.md) | Investigate the Debug timeouts | Investigation | Done on 25 November 2026. Historical causes remain open |
| [S20R](sprints/s20r.md) | Packed Map scalar pairs in owned reads and whole writes | Refactoring | Done on 25 November 2026. [ARM64 measured with S21](../refactoring.md#r2-fixed-scalar-pair-comparison) |
| [S21](sprints/s21.md) | Protected views by name | Full, critical | Done on 25 November 2026, Coordinator 12 |
| [S21I](sprints/s21i.md) | Protected List traversal | Full, critical | Done on 25 November 2026 |
| [S22](sprints/s22.md) | Typed protected views | Full | Done on 25 November 2026 |
| [S22C](sprints/s22c.md) | Cancellation of a registered position wait | Full, critical | Both parts done on 25 November 2026 |
| [S23](sprints/s23.md) | Compaction and shrink | Full, critical | Done on 26 November 2026 |
| [S23A](sprints/s23a.md) | Evacuate nearly empty segments | Full, critical | Done on 26 November 2026 |
| [S24](sprints/s24.md) | The memory budget | Full, critical | Merged at `895651e3`. Acceptance reopened after independent reproduction. [Original measurements](sprints/s24.md#s24--the-memory-budget) |
| [S24A](sprints/s24a.md) | Budget ownership, withdrawal and dictionary growth | Full, critical | Done on 26 November 2026. [Brief and acceptance](sprints/s24a.md#s24a--budget-ownership-withdrawal-and-dictionary-growth) |
| [S24R](sprints/s24r.md) | Capacity wait and ownership cleanup | Refactoring | Done on 26 November 2026. [Brief and verification](sprints/s24r.md#s24r--capacity-wait-and-ownership-cleanup) |
| [S24B](sprints/s24b.md) | Budget accounting by structure | Full, critical | Not started. The owner's decision of 27 November 2026 |
| [S24BR](sprints/s24br.md) | Refactoring after S24B | Refactoring | Not started |
| [S25](sprints/s25.md) | Reference fixture, managed reference, the memory gate | Full | Not started |
| [S25L](sprints/s25l.md) | One lifecycle of the instance | Full, critical | Not started |
| [S26](sprints/s26.md) | Operation identity and safe retry | Full | Not started |
| [S27](sprints/s27.md) | Provider contract, PostgreSQL provider and Durable open | Full | Not started |
| [S28](sprints/s28.md) | Durable commit | Full | Not started |
| [S29](sprints/s29.md) | Storage failures and the loss of leadership | Full | Not started |
| [S30](sprints/s30.md) | Saved base and background application | Full | Not started |
| [S31](sprints/s31.md) | Journal limits, cleanup and receipt retirement | Full | Not started |
| [S31S](sprints/s31s.md) | SQL Server provider | Full | Not started. The owner's decision of 27 November 2026 |
| [S32](sprints/s32.md) | Recovery under failure | Full | Not started |
| [S33](sprints/s33.md) | Bounded delivery and slow children | Full | Not started |
| [S34](sprints/s34.md) | Reconnect, reload and a restarted master | Full | Not started |
| [S34A](sprints/s34a.md) | Advisory locks | Full | Not started |
| [S35](sprints/s35.md) | Cluster verification | Full | Not started |
| [S36](sprints/s36.md) | Restore and a new history | Full | Not started |
| [S37](sprints/s37.md) | Schema migration on the master | Full | Not started |
| [S38](sprints/s38.md) | Migration across the database | Full | Not started |
| [S39](sprints/s39.md) | Diagnostics | Full | Not started |
| [S40](sprints/s40.md) | Ordinary-load gates | Full | Not started |
| [S41](sprints/s41.md) | Burst and recovery profile | Full | Not started |
| [S42](sprints/s42.md) | Close | Full | Not started |

### Remarks left for the coordinator

The seventh review left four remarks that do not block. They are not corrected here, so
that the text the owner approves is the text that was reviewed; the coordinator settles
each when its sprint is prepared.

1. Exception X3 in the [exceptions register](../exceptions.md) says the measurement program
   "opens it through its internal entry", while S12 removes that entry and corrects only
   X4 in the register. S12 corrects the wording of X3 as well.
2. The brief of S12 does not say in one sentence that the move of `LatticeDatabase` and
   the deletion of the placeholder are made by the implementer in the implementation
   commit. It follows from the list of edits, and it is how the sprint is to be run: the
   contract commit changes only the documentation reference.
3. The block of S06 does not say who gives `Moltaro.Lattice.Tests` its reference to the
   engine assembly and its friend access. It is the test author, in the tests commit,
   with the helper that needs them.
4. In S12 the edit of the documentation of `ILatticeDatabase` is presented under the rule
   on edits that state nothing new, yet it is assigned one commit earlier than the commit
   that would break. It is harmless and keeps the roles apart.

The coordinator added the remark below while S01 was reviewed. It is settled in the same
way, when its sprint is prepared.

5. S01 created `Moltaro.Lattice.Engine` as a project that is not packed: it had no code,
   and the Publish workflow packs every packable project on a release tag. From S12 on
   the dependency injection package references the engine, and a package that depends on
   one that is never published cannot be restored; nothing in the build shows it. In the
   implementation commit of S12 the implementer makes the engine project packable and
   corrects the sentence of `README.md` that names the published packages. The brief of
   S12 does not list this edit; the coordinator enters it there when S12 is prepared.
6. The brief of S12 has the test author change "the address helpers of the two test
   projects". Since decision O13 there is one, in `Moltaro.Lattice.TestSupport`, and that
   one is changed.
7. `Moltaro.Lattice.TestSupport` holds two unrelated types at its root, which is fine for
   two files. The sprint that next adds shared code to it — S22 or S27 at the latest —
   gives the project folders by responsibility, as `AGENTS.md` asks of every assembly;
   its brief says so.
8. By the owner's answer to Q12 of the [open questions](../open-questions.md), S02 added a
   sentence "Not implemented yet" to the documentation of `LatticeIndex.IsUnique`, of
   `LatticeErrorEnum.UniqueConstraintViolation` and to the remarks of
   `ILatticeSet<TEntity>`. S16 implements unique indexes and removes the three sentences;
   the planner enters it in the brief of S16.
9. S04 gave `Moltaro.Lattice.Engine.Tests` a test collection for the tests that measure
   managed allocation: it runs alone and inside a region in which the runtime starts no
   garbage collection, because a collection caused by another test moves the counter of
   the measuring thread. Every later test that asserts an exact amount of managed
   allocation joins it. S06 and S07 assert the allocation of reads; if such a test lands
   in `Moltaro.Lattice.Tests`, the collection and its attribute move to
   `Moltaro.Lattice.TestSupport` in that sprint, as decision O13 says of code that two
   test projects need, and the brief given to the test author says so.

## Parts of this epic

- [Where the contract stands today](contract-today.md): what is in the contract and working after the last closed sprint, kept current by the coordinator.
- [Requirements](requirements.md): what the engine has to meet.
- [Acceptance](acceptance.md): how the milestone is accepted, with the measured results.
- [Open questions](decisions.md): the answers of the owner and of the reviews, and the choices made in the contracts.
- [Test lanes](test-lanes.md): the lanes of tests and measurements, and where each runs.
- [Stages](stages.md): the intro of each stage and the list of its sprints; the brief and the record of each sprint are in a file of its own under `sprints/`.
