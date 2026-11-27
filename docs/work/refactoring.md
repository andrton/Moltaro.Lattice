# Refactoring backlog

What the architect's review and the reviews of understandability have named, and the P3
findings a review left after its last round, kept in one place until a refactoring sprint
carries it out. The [development process](process.md)
says how an entry gets here and how it leaves.

An entry changes no behavior and no assertion of a test. KISS holds for a refactoring as
for any change: an entry that adds a type or a level of indirection has to remove more
than it adds, and it is always a balance.

**Status of an entry:** *open* — not yet gone through by the planner and the coordinator;
*taken (S13R)* — goes into that refactoring sprint; *joined (Sxx)* — done with a sprint
that changes the same code; *refused* — with its reason; *done* — with the commit.

Line numbers are those of `main` at `e7b6e0e`, in `src/Moltaro.Lattice.Engine` unless a
path says otherwise.

## After S24

- **Return unused root pages in one pass** *(joined S24A)*. The failed preparation
  path searched the free list for each returned new page. It now uses
  `RootTable.WithdrawUnusedPages`. The root page and root slot witnesses keep
  issued generations, the surviving directory and the release boundary.
- **Register the follower's memory wait once** *(done in S24R)*. The ready child
  arms one signal under the budget lock after rollback. Capacity returned after
  registration signals the child, and the paced retry covers capacity returned
  before registration. See the [S24R verification](epic-1/sprints/s24r.md#s24r--capacity-wait-and-ownership-cleanup).
- **Share the lifetime of one charged owner** *(refused in S24R)*. The stack-only
  scope lets a caller's exception filter observe the outstanding charge before it
  unwinds on a construction or registration failure. The existing catch releases
  before rethrow. A typed cached factory preserves that catch but allocated 88 B
  on its first use, including a refused reservation. The existing array factory
  constructs directly, and lease factories that own several objects unwind in
  their own order. Sharing only the remaining cold paths does not justify adding
  the scope or a distinct factory protocol.
- **Release parked bucket generations in one loop** *(done in S24R)*. Both
  publication and withdrawal use the bounded release walk. Withdrawal keeps the
  layout selected for restoration and returns the charge for that layout's
  history wrapper when it reinstalls the layout. The [S24R work counts](epic-1/sprints/s24r.md#s24r--capacity-wait-and-ownership-cleanup)
  report the equivalent work on the same input.
- **Measure workspace retirement with a durable queue** *(joined S27)*.
  `WriteExecutor.FinishWorkspace` and `ReleaseUnusedWorkspace` can scan the
  outstanding borrowers for each ordered publication. Quantify the work at
  increasing queue depth with delayed persistence and check exact physical
  capacity against equivalent runs with only the surviving owners. The
  ordinary single-member MemoryOnly path and immediate group do not show
  that cost.

## Order

Gone through by the planner and the coordinator on 18 November 2026.

- **S13R is carried out in two parts**, because the owner's machine could give no
  measurement on 18 November 2026. Part 1, what owes no figure, is done and merged: A7,
  A1, A8, A14 to A17, N1, N2, U2, U5, U7 to U10, U12, the one walk of B1 and the
  comments. Part 2, each commit with its figure, measured on a rented machine on 19
  November 2026, is done: A2 in the cut of D1 with D2, D3 and D4; A6; T1 with B3. A9
  and B2 were not done in part 2 and are taken into S14R, B2 before the scenarios of S15
  are written. The address validation of the arena was not changed, so its figure is
  not owed. The first plan follows.
- **S13R**, after S13 and the fix package of the outside review, before S14: A7, A2, A1,
  A6, A9 in that order, by one implementer. Each is a commit of its own with the whole
  suite passing; after A2, A6 and A9 the write and the read by key are measured at 1 and
  16 threads with the allocation of each, so that a fall names its entry. Then the small
  entries in the same files, and the figure for the address validation of
  `Arena.Payload` and `Arena.Header`: no change of it without that figure.
  T1 of the review of S13 goes with them, after A9, with its figures, and B3 with T1;
  the one walk of B1 is among the small entries; B2 is a commit of its own that waits
  for its figures.
- **After S14,** set by the planner on 19 November 2026 after the architect's review after
  S14; it replaces the order for S14R set on 18 November:
  1. **B2,** done on 20 November 2026 (merged to `main` as 19d3f6b); its series is the first item
     of the plan of the start of the machine for S14, part 2.
  2. **S14, part 2** of the [epic](epic-1/README.md), which joins E1, E2, E4, E8 and the
     half of E3 that is `HasKey`.
  3. **S14R,** structure only: I1 to I4 of the review of S14; the loops of E3, with I1;
     E7; E9; A9; M1; E10; F1; F4; and what was taken into it before — A3 with B4, A4 with B1 (in
     before S18), A5, A11, A12, A13, U6, U11, and N3 to N7 and C3 with the files as they
     are touched. With them, from the architect's review after S14, part 2: G1 first, as
     the form of E3; G5; G7 with F1, I3 and I1; and G3 with F4 in the commit of comments.
     Before its structural commits, a test of the engine by the test author states that
     the read by key depends on the order in which the fields are declared
     (`EntityTable._indexes`), and it passes on `main` as it is. One entry a commit, G1
     first, the commits of comments last; one narrow reviewer with one question: is any
     behavior or assertion changed. It has no start of the machine: what it must show
     unchanged in figures is checked on the workstation by direction and listing, and
     decided at the start of S15, part 1, whose builds are `main` before S14R, `main`
     after it and the head. It is merged in two parts. Part 1, the guard test and
     F1, G7, G1, I1, E9, G5, I4, E10, E7, I3 and A13 but its unprepared mode, holds
     what S15, part 1 stands on. Part 2 is the unprepared mode of A13 with its tests first,
     A9, A3 with B4, U6 and U11, A4 with B1, A5, A11, A12 with N4 and N7, M1, and last the
     commits of comments: G3 with F4, N3, N5, N6 and C3. Both parts are done; the rest of
     N3 is taken into S15R. The reviewer of part 2 found hardest to follow, in
     `WriteCoordinator`, that whether the next write of a chain
     starts after a write ends is decided in four ways: most ends call
     `DecideFailureAndAdvanceChain`, `Prepare` advances when `TryQueue` refuses,
     `Resolve` advances by the state of a write, and the withdrawal of a chained write
     only decides, since `Unchain` has relinked the chain; a question for the architect
     after S15, part 1, with the flag `topsUp` of `TryQueue`, which has one caller that
     tops up.
  4. **S15, part 1,** with E6, G4, F5 and the read limits of F10. Done on 21 November 2026,
     with three starts of the machine; F13 and F14 were found there.
  4a. **S15, part 2,** the binding rules, with the architect's review after S15, part 1
     started before it and one more after it; then **S15R** with the rest of N3 and F13.
     **S15R,** set by the planner on 21 November 2026 after the architect's review after
     S15, part 1, with what the architect's review after part 2 added on the same day: H1;
     H4; H3; the candidates 1 to 4 of F13; H2; J1; J2; the comments (H5, the rest of N3, the
     comments that say "the one writer", and J3). It starts after part 2 is merged and after
     the architect's review after part 2 is gone through, whose taken entries join it. No
     start of the machine of its own. Done on 22 November 2026, in one round of
     review; H1, H3, the candidates 1 to 3 of F13 and J1 get lines in the start of
     S16.
  5. **S16,** with F6 and G2. Done on 22 November 2026; K1 was found in its start of the
     machine.
  5a. **S16R,** set by the planner on 22 November 2026 after the architect's review after
     S16: the documents (the answer to Q56, L5, the section of that review), L2, L3, L6,
     and last the listings of K1 written into its entry. No assertion changes; one narrow
     reviewer, no breaker, no start of the machine of its own: the figure of L2 and the
     cause of K1 are owed to the start of S17. Done on 22 November 2026, in two rounds of
     review.
  6. **S17,** with F3, L1, and G6 unless its study keeps the pool. Done on 23 November 2026,
     in three parts, with the tighter bound of the reservation and the write path after it;
     the start of the machine before its close closed K1 and found K2, which goes to the
     architect's review after S17.
  6a. **S17R,** set by the planner on 23 November 2026 after the architect's review after
     S17: W1 with the plain form of `NodePool.LeftAfterMove`; W3; W2; W4; and last W5, the
     commit of comments and documents. One entry a commit; one narrow reviewer, no breaker,
     no start of the machine of its own. Each entry on a measured path is checked by the JIT
     listings of the callers it names, on the workstation; its figures are owed to the start
     of S18, on `index-writes` with 3 and 6 indexes, `unique-writes` lines 1 to 3 and
     `following-children`. Done on 23 November 2026, in one round of review; its record is
     in the [epic](epic-1/README.md).
  6b. **S18R,** set by the planner on 24 November 2026 after the architect's review after
     S18: Z3, Z8, Z6, Z7, Z2 (a) to (d), Z4, Z1, and Z5 last. The next work, in order (the
     planner, 24 November 2026): the fix of the state of an instance behind its fault, form
     (A), test first (the record of S18 in the epic); `kd` of K2; S18R; the subsumed index
     tests as a change of the light track; then S19, its first structural commit first. The
     section of that review says the rest.
  6c. **After S19,** no separate refactoring sprint. The planner and coordinator joined
     each entry to the sprint that changes its path. See the
     [architect's review after S19](#architects-review-after-s19-24-november-2026).
  6d. **S20I** is done on 25 November 2026. See the
     [clock correction and historical Debug controls](epic-1/sprints/s20i.md#s20i--investigate-the-debug-timeouts).
  6e. **S20R** took the bounded R2 work on packed Map copies and whole writes
     after S20I and before S21. See the [record](epic-1/sprints/s20r.md#s20r--refactor-packed-map-scalar-pairs).
     The common S21 ARM64 measurement queue takes R2 and the two R1 experiments
     after their x64 preflights.
  7. **S23,** with F8 and V2.
  8. **S24,** with the memory limit of F10.
- Comments never ride in a commit that moves code.

**Found by the first start of S15, part 1, 21 November 2026, for every refactoring
sprint.** E7 of S14R, part 1 cost an insert and a delete of an index with a String part 4
to 8 %, because a `stackalloc` beside a loop took `NodeStorage.Retire` out of tiering and
out of the inlining into its callers, which no reading of the source shows. So a change of
a refactoring sprint on the read or the write path is checked by the JIT listing of its
callers, not only of the changed method, and a refactoring sprint that touches those paths
gets a line in the next start of the machine. A change that makes
`EntityTable.Step` shorter or longer is checked by the listing of `Step` itself as well:
it is compiled at full optimization on its first call, its budget for inlining follows its
size, and S15R saw it stop inlining two members after it shrank.

## Review of understandability, 18 November 2026

A reader with the code alone and none of the documents explained the four main flows and
said where it got lost. Scores from 1 (reads like a book) to 5 (cannot be followed without
its authors): a write on a master 4; a read by key 2.5; the open of a child 3; following
and a write through a child 4.

### Structure

| # | Place | What is hard | The simpler form | Status |
| --- | --- | --- | --- | --- |
| U1 | `Engine/Writes/WriteCoordinator.cs` (1,591 lines) | Eight jobs in one type: admission, the order of one entity, the prepared queue and the group, the resolution of a write refused for memory, cancellation, the fan-out of a fault, the delivery of outcomes, list plumbing. One write runs through twelve methods that pass a mutable `ref Pass` whose fields are instructions for a later method (596–689, 754–794, 1131–1166, 1437–1472, 1528–1590) | The resolution of a refused write (883–1125, `_resolving`, the held list) as a type of its own. `Pass` declared at the top with, per field, who sets it and who uses it. A short method for the common path that reads straight through | joined (S27), beyond what A9 takes |
| U2 | `Engine/Writes/PendingWrite.cs` 81–104; `WriteCoordinator.cs` 221–243, 429–498, 931–991 | Four intrusive lists through one record; `Next` serves three of them; one list has no name (`List`/`Unlist`/`Listed`); `Unlist` also starts another write's resolution | One small intrusive-queue type; a noun for the list of waiting writes; the side effect moved to the caller | done (b61c1d8) for the names and the side effect; the type of the queue is joined to A9 |
| U3 | `WriteCoordinator.cs` 111–150, 883–1112; `PendingWrite.cs` 117–119, 212–222 | The states of the resolution: `Reading` means waiting for readers to leave; `PendingWrite.Execute` is one of two things by a flag | With U1: its own type, states named for what is waited for | joined (S27), beyond what A9 takes |
| U4 | `Engine/Entities/EntityTable.cs` (917 lines) | Eight jobs: the layout, the seeded hashes of keys, a facade over the primary index, the read by key, the step of an enumeration, the reader's half of the stamp protocol, the wire format of keys (310–413), helpers for stored keys | The wire format of keys to `Replication/Transactions`; the hashes to a type of their own; the enumeration step beside `EntityPass` | joined to A6: the form of a key on the wire needs the key type and the seeded hash, which live in `Engine`, and `Replication` depends on nothing there |
| U5 | `EntityTable.cs` 16–52, 801–916; `Engine/Writes/WriteExecutor.cs` 16–21, 631–731 | The stamp protocol of an entry is explained as differences from another project's lock, and its reader and writer halves are in two files; "0 means in progress" is a constant of one used by the other | One small type that holds mark, stamp and validation, with a description of ten lines that stands alone; the attribution in one line | done (ae446eb); what differs from a sequence lock moved to `docs/design/arena-protocol.md`, "The stamp of a root entry" |
| U6 | `Engine/EngineDatabase.cs` (1,090 lines) | Seven jobs; the length comes from six test hooks with about 100 lines of comment (311–441) and from a header of 127 lines that documents the whole engine | The test hooks in a partial file; the open of a child (801–955) into `Engine/Loading`; the header reduced to the map of the parts and the lock order, the rest moved to the types that own it | done (S14R, part 2): the test hooks in the partial file `EngineDatabase.TestAccess.cs`, the body of a child's load in `Engine/Child/Loading`, and the head reduced to the map of the parts and the lock order |
| U7 | `EngineDatabase.cs` 581–724 | The close is spread over several members; the reason for the order is given for one step only | A numbered list in `CloseAsync` in the order of the code | done (0894add) |
| U8 | `Engine/Writes/WriteExecutor.cs` 590–629, and the facade over the retired memory, the epochs and the clock | Eight one-line members that pass a call on, each with a comment whose step numbers belong to another file | The private methods made internal with one comment; the pure pass-throughs removed | done (af2bc52). The price is constructors of 10 parameters (`WriteCoordinator`) and 12 (`ReceivedChanges`); the reviewer kept the form because a reader sees the real target of a call. The 12 do not outlive A2 and A3 with B4 |
| U9 | The write through a child, as a whole | It crosses about fourteen types in six folders; no place lists the chain | One numbered end-to-end list in the header of `Forwarder` | done (3af4fa6) |
| U10 | `Engine/Delivery/RecordedReplies.cs` 10–21; `Engine/Children/ChildConnection.cs` 35–40, 244–256; `WriteExecutor.cs` 554–559; `WriteCoordinator.cs` 177–189 | "A reply precedes its transaction" is explained four times in four wordings | One owner (`RecordedReplies`); the others name it in a line | done (a853d23) |
| U11 | `EngineDatabase.cs` 969–1038; `WriteCoordinator.cs` 266–267; `Engine/Following/Follower.cs` 127–141 | On a child a `WriteCoordinator` is built that admits no write, the real writers take the lock directly, and the sender of the forwarder is made inside the follower | One comment in `Build` on what is live on a child; the sender made where the forwarder is | done (S14R, part 2): the sender of a child's writes made beside the forwarder, and the head of `EngineDatabase` says what is live on a child |
| U12 | `Engine/Children/ChildConnection.cs` 204–231; `Follower.cs` 172–206 | Three sources of cancellation and "ended in order" decided by the type of an inner exception; in the follower a bare `catch`, `LoseMaster` called twice, and an order of two calls that carries the meaning | A named local with its reason; comments at those lines instead of in the header | done (c4a82a4) |

### Names

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| N1 | "S" and "R" of a load, used before they are defined, with other names in the fields (`Start`/`End`, `readiness`, `CaptureStart.Sequence`) | One pair of names everywhere, for example `captureStart` and `readinessPosition` | done (f5bd4d6) |
| N2 | `Protocol.EndsARoot` marks the last frame of any message | `EndsAMessage` | done (70b7798), as `EndsABody` |
| N3 | `Storage` (the arena and the root layout) beside "the storage step", `ICommitStorage`, `MemoryOnlyStorage` (durability) | Decide one word for each of the two | done in part (S14R, part 2): "storage" is the form of the entities in memory, and "persistence" what makes a write durable, the contract's own word (`LatticePersistenceModeEnum`): `IPersistence`, `MemoryOnlyPersistence` and the persistence step in the engine, the measurement program, `TestSupport` and `docs/design`. The word of the public contract for the durable store of a provider, "storage" (`LatticeDatabaseOptions` with its `StorageConnectionString`, and the documentation of four other public types), is decided with the contract of stage 6, before S26. The rest done (S15R, 61b647e, 5f96523, bee8693): the message of `IndeterminateOutcome` says "persistence step", and so do the comments and the names of the tests. Left: about fifteen string literals of tests that say "storage step", assertion messages, the text of an injected `IOException` and a value `TallyTests` stores, since they are data of a test; for the next sprint that touches those tests |
| N4 | Two folders `Forwarding`; "Forwarded…" types in five places; `RecordedReplies` in `Delivery` | One home for what belongs to a forwarded write on each side | done (S14R, part 2), with A12: what belongs to a forwarded write on the child's side is in `Engine/Child/Forwarding` |
| N5 | One word with several meanings: `Pass` (three), `Capture` (two), `Deliver` (two), "hold" (three); `Reading` and `ReadsHere` for "waiting for readers" | A word of its own for each meaning | done (S14R, part 2): the decisions of a hold are `WriteCoordinator.Hold`, a fault point is reached by `ReachFaultPoint` and released bytes advance by `AdvanceReleased`, so that a pass is only that of an enumeration; the values of a write are `ValuesForWrite`, so that a capture is only the master's for a load; an outcome another thread decided is given by `GiveOutcome`, so that delivery is only to the children; the writes held back while a refused write is resolved are deferred, and the switches the tests arm are the read, growth and wait switches (`ArmReadSwitch`, `ArmGrowthSwitch`, `ArmWaitSwitch`), so that a hold is only one of the lock of the writes; the verb keeps its plain sense in the names of test doubles, as the Words say; and the wait for readers is `WaitingForReads` |
| N6 | Method names too general to search for or to tell apart in `WriteCoordinator`: `List`, `Outcome`, `TryLast`, `Offer`, `Drive`, `Finish`, `End`, `Complete`, `Succeed` | Names that say what is done to what | done (S14R, part 2): `ReturnOwnOutcome`, `TryPrepareResolved`, `OfferWaitSwitch`, `StartReadyAndCommit`, `CarryOutDecisions`, `DecideFailure`, `DecideFailureAndAdvanceChain`, `MarkCompleted` and `DecideSuccess`; `List` went with A9 |
| N7 | `MasterLink` lives in `Loading` and serves the child for its whole life; `SetOperations` is where a master and a child part, and its name does not say so | Move the first; say it in the header of the second | done (S14R, part 2), with A12: `MasterLink` in `Engine/Child`, and the head of `SetOperations` says it is where a master and a child part |

### Comments

About 27 % of the lines are comments. They are accurate, and those at methods help — above
all the prefixes "Under the lock:", "After the lock:", "For the sender:". Two things work
against a reader who was not here:

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| C1 | About 380 lines in 94 files cite a sprint, a question, a "brief", a decision of the planner or an exception by its number (for example `WriteCoordinator.cs` 111–113: three citations and no content; `EngineDatabase.cs` 48, 70–72) | A comment states the rule and its reason in its own words. A reference to a decision record by its file is welcome; a sprint number, a date or "the planner's point 4" is not. A convention for `AGENTS.md`, then one pass over the engine | done (6376dfd) for the files S13R touched and the files of S13; the other files as later sprints touch them, never one sweep |
| C2 | Class headers open with where a rule came from, and several are the length of a document (`WriteCoordinator.cs` 9–198, `EngineDatabase.cs` 27–153, `EntityTable.cs` 116 lines, `WriteExecutor.cs` 105) | A header says first what the type is for and its common path, in about fifteen lines; the history of measurements leaves the code for the document that owns it | partly done (6376dfd): the heads open with what the type is for and one block of history moved to `docs/design/write-path.md`, but they are not short — `WriteCoordinator` 170 lines, `EngineDatabase` 124, `EntityTable` 101, `WriteExecutor` 89. The rest goes with the entries that restructure each type: U6 and A3 (`EngineDatabase`), A9 and U1 (`WriteCoordinator`), A6 (`EntityTable`), A2 (`WriteExecutor`) |
| C3 | Prose idiom that has to be learned: "the lock of the writes", "a hold", "the protection of reads", "the storage step" | A short glossary in one place that the headers can assume; or plainer words | done (S14R, part 2): the Words of `docs/design/write-path.md`, which the README of `docs/design` names as the vocabulary of the engine |
| C4 | `Engine/Loading/ReceivedChanges.cs` 10–65: the proof of the one rule and "what a read by key sees" are single paragraphs of eight and twenty lines | A line per case, as the header of `WriteExecutor` does (72–81) | done (6376dfd) |

### Sound, leave as it is

`LatticeDatabase` and the folder `Sets`; `CloseBarrier`; `MemoryOnlyStorage` and
`ICommitStorage`; the code of `PositionClock`; `ForwardedCall`, `ChildSender` and
`PendingRequests`; `RootPlacement` and the body of `LoadFromMasterAsync`; the code of
`ReceivedChanges`; `RootWriter`, whose length is justified. Conventions to keep: one type
per file; the prefixes that state the locking context; the sections "The waits, and how
each ends" and "Who calls what"; attribution of adapted code with its commit; measured
figures beside a tuning constant.

## Review of S13, 18 November 2026

What the two reviewers of S13 named under the question of simplicity. Line numbers are
those of the branch of S13 at its merge.

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| T1 | `Storage/Text/TextSlot.cs` (`State`), `TextTable.cs` (`Publish`, the branch of `TryAcquire` that publishes on a hit, the parameter `prepared`), `ValueBlocks.PublishText`, the `Text` branch of `RootStorage.Publish` | The state Prepared or Published of an entry is kept in the slot beside what the arena already knows of the entry's block, and the same field is the link of the free list; a reader has to hold four places together to see that an entry is published before a read reaches it. Both reviewers named it the hardest part of S13 to follow | `Publish` asks `Arena.IsPrepared(address)` and the slot keeps no state (the other form named by the review, an entry allocated live always, changes what `PreparedBytes` counts and is carried to S24). The free-list link moves into a field a free slot does not use and the slot is 24 bytes instead of 32. Shown unchanged: the tally tests, `PreparedBytes` and `LiveBytes` in the released-write tests, memory for each distinct entry (51.4 bytes of table today), the writes with a Dictionary text | done (808b619): `TextSlot` is 24 bytes and keeps no state, the arena says whether the entry's block is prepared (`Arena.IsPrepared`) |

## Architect's review after the fixes of S13 and part 1 of S13R, 18 November 2026

Read at 8717b13, nothing run. Its judgement: part 1 did what it claimed and the fix
package added no structure of its own; one seam remains, and S14 lands on it. Gone
through by the planner and the coordinator on the same day.

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| D1 | `Engine/Writes/WriteExecutor.cs` (`Release`, the reservations of the three preparations, `PublishInsert`, `ReplaceRoot`, `DeleteRoot`, the five stores), `Engine/Writes/PreparedWrite.cs`; `Engine/Loading/ReceivedChanges.cs` (`TryPrepareWrite`, `PrepareDelete`, `Publish`, `Abandon`, `GiveBack`, the private `Change` and `ChangeKindEnum`) | "The effects of a write are installed" is one place for the stores only: a prepared change is recorded in two types, reserved in two places and given back in four, and its kind is switched on in seven. S14 would write the reservation, the release and the record of index entries twice | A2 in a wider cut: one struct for a prepared change (kind, table, key, handle, root, replaced root) and one small type that reserves, releases, and makes the stores before the position and after it. The type is sealed and called directly, no interface, and takes the prepared changes of one publication as a span — a transaction on a child has several today, a master passes one — so the three steps exist once and the batch of S17 writes only its loop. `Loading` no longer depends on `WriteExecutor`. Shown unchanged: the order of the stores and both fault points, a MemoryOnly write allocates nothing, the write of one caller and of sixteen, the child follow figure | done (670617c), as A2: one struct `PreparedChange` with `ChangeKindEnum`, and the sealed `ChangePublisher` that reserves, releases and publishes over a span. Then 70fcc30, babd343, 0d63331 and f4ae9f3 removed what the first cut cost on a write: the change is written straight into the record of the write, not built aside and copied |
| D2 | `Engine/Loading/ReceivedChanges.cs` (`Place` with `PlaceUnderTheLock`, `Apply`, the field of the coordinator); `WriteExecutor.ReadsToWaitFor` | One rule, "refused for memory: drain, retry", in two shapes; a pass-through that survived U8, with a flag that repeats what `RetiredMemory.ReadsToWaitFor` already says; the whole coordinator taken for one test switch | `Place` takes the shape of `Apply` over a `TryPlace`; both callers ask the retired memory; the switch moves to `ReadEpochs`, beside the hold of a read it already carries for tests | done (55b48a7), except where the switch lives: not in `ReadEpochs` but in `RetiredMemory` (4b784ef), because in `ReadEpochs` it kept a reference that hollowed the weak-reference probe of "a closed database retains nothing" |
| D3 | `Replication/Load/BlockImport.cs` (`Check`), and the schema passed through `WriteExecutor`, `ReceivedChanges` and `ForwardedWrites` for it | A check that the layout is one of the schema: a lookup by name for every received root, an exception no test asserts | Remove the check and its field — on one condition, which the reviewer of part 2 shows by reading: the layout that reaches the check is always taken from the node's own schema and never selected by received bytes. If a received byte can select it, the check is part of what a node checks of a peer: it stays, gets a test, and the decision on trust between nodes names it | done (5a88dd4); the reviewer showed the condition by reading: the layout that reaches the check is always taken from the node's own schema, never selected by received bytes |
| D4 | `Engine/Writes/WriteExecutor.cs`, `Engine/Loading/ReceivedChanges.cs`, `Engine/Protection/RetiredMemory.cs` (`AfterWrite`) | Whether a publication retired nothing is worked out by the callers from the kind of each write, for a write on a master and for a transaction on a child, although the retired memory sees every addition itself; S14 changes which kinds retire | `RetiredMemory` notes whether anything was retired since the previous publication; `AfterWrite` takes no argument. The unit is the publication of one position, on a master and on a child. Shown unchanged: the moves of the epoch for each 10,000 writes recorded with the fixes after S13 | done (42b6317) |
| D5 | `Engine/Delivery/TransactionEncoder.cs`; `Replication/Transactions/TransactionFormat.cs` | The envelopes of the effects of a transaction are written by hand in one folder and read in another, and each writer welds the header of the transaction to its one effect | `TransactionFormat` writes the three envelopes beside the code that reads them; the bytes are identical | joined (S17), which split the header from its effect; the rest done (S17R) as W4 of the architect's review after S17 |
| D6 | `Engine/Loading/RootPlacement.cs` (a repeated test for a layout); comments in `WriteCoordinator`, `EngineDatabase`, `ReceivedChanges` and the head of `Follower` that still send the reader to `Follower` for what `ReceivedChanges` does since A1; citations of briefs left in `Engine/Writes` (`PreparedWrite`, `CommitGroup`, `ICommitStorage`, `ForwardedOrigin`, `MemoryOnlyStorage`, `EngineFaultPointEnum`) | Small leftovers | Corrected as the files are touched; the implementer of S14 is told | done (S14R, part 2): the citations of briefs in `Engine/Writes` and in the persistence types removed; no comment sends the reader to `Follower` for what `ReceivedChanges` does, and `RootPlacement` tests a layout once |

**The order before the implementation of S14,** which its study and its contract step do
not wait for: part 1 merged and the branch of S14 rebased on it; D1 as A2, with D2, D3
and D4; A6, because S14 gives `EntityTable` its secondary indexes and an entry unique by
the key of the index and the identity of the entity needs the key of a type that A6
takes out; T1 with B3 only if the study of S14 chooses one shared text entry for an
index and a root. A9 and B2 do not touch what S14 builds on. All of these owe figures and
wait for a quiet machine; whether A2 and A6 may be merged with their figures owed, if no
machine comes, is a question for the owner and is not decided.

What part 2 of S13R did to this order: D1 as A2 with D2, D3 and D4, then A6, then T1
with B3 are done with their figures, measured on a rented machine on 19 November 2026,
so the question for the owner did not arise. A9 and B2 are taken into S14R.

## Found while measuring part 2 of S13R, 19 November 2026

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| M1 | `src/Moltaro.Lattice.Measurements/Scenarios`: `RemoteChild.cs`, `RemoteForwarder.cs` | The two put a child into a process of its own, for the scenarios following-children and forwarding-costs, and start `Environment.ProcessPath` as that second process, so those scenarios fail when the program is started as `dotnet <dll>` | Start the second process the way the first was started; two lines | done (S14R, part 2): the second process started as the first was started |

## Review of S14, 19 November 2026

What the two reviewers of S14 named under the question of simplicity. Every entry is
structure only and changes no behavior.

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| I1 | `Engine/Indexes/OrderedTree.cs` | The binary search over the entries of a node is written four times, and the descent from the root to a leaf twice | One of each, used by every caller | done (S14R, part 1): two binary searches, one for each kind of probe, a search key and an entry, where there were four, and one descent of a reader where there were two; no interface, since a shared body would need one. With G7, `WithoutChild` is the one copy of an inner node less a child |
| I2 | `Engine/Indexes/SearchKey.cs` | `SearchKey` holds the key of a search, an `IndexKey`, by value | The key not copied into it | joined (S14, part 2), with E4, which gives the form |
| I3 | The index types, `Storage/Ordered` and `Engine/Indexes`; `IndexLayout.ReadValues`, `TextOf` and `ScalarOf`, added by the architect's review after S14 | Members that nothing calls, and members that only tests call | The dead ones removed; the test-only ones as A13 treats its members | done (S14R, part 1): the dead members removed, `IndexNode.FreeBytes` among them; of the members only tests read, `IndexLayout.EntryLength` and `OrderedTree.Count` and `Bytes` removed as A13 treats them, and `Height` kept, since the writer computes the need from it and a test holds that it is the height of the nodes |
| I4 | `Engine/Entities/ChangePublisher.cs`, `Publish` | The parameter `armedFault` hands a fault of a test into every publication: a child always passes null, and the other fault points are reached through `WriteExecutor.Pass` | The fault point reached without a parameter of the publication | done (S14R, part 1): the publication in two halves, the executor passing its fault point through `Pass` between them; the publisher carries no hook of a test |

The costs the reviews found — the pool of nodes topped up and trimmed on every write, both
entries built three times for each index, what a search allocates, `CompareText` — are
not refactoring: they change measured figures and belong to S14, part 2 of the
[epic](epic-1/README.md).

## Architect's review after S14, 19 November 2026

Its judgement: the folders and the direction of dependencies are sound (`Storage/Ordered`
→ `Engine/Indexes` → `Entities` → `Sets`); the debt is in three places that S14, part 2,
S15 and S17 all change next — the reservation in `OrderedTree`, two forms of a key, and
the decision a write makes for each index, which nothing keeps. Read at `main` 53b404c,
whose line numbers these are. Gone through by the planner on the same day. An entry that
changes a measured figure is not refactoring and is joined to S14, part 2 of the
[epic](epic-1/README.md).

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| E1 | `Engine/Indexes/OrderedTree.cs` 26–44, 217–270, 426–542 | `Bound` is the target of every top-up and trim, while the real invariant, `Need`, is used only by a Debug assertion; the rule of growth, one level for each `LeastSeparators` inserts, is written three times | `Need` in closed form as the only target and the asserted invariant; one function for the growth; `Bound` gone. A reservation for each change is not plainer: a publication removes the old entries after all its inserts | `IndexPreparationTests`, `RefusedReservationTests`, the Debug assertion across the suite, and a test of the closed form against the loop | joined (S14, part 2), the first commit of its (b): it reserves fewer nodes, so it changes a figure and is not refactoring |
| E2 | `Engine/Indexes/SecondaryIndex.cs` (`Change` 201–225, called from four members; the scratch `_old` and `_new` 55–57); `Engine/Entities/PreparedChange.cs` | Both entries are rebuilt at every step, and the decision for each index is kept nowhere | The reservation records the decision with the prepared change, as a mask; the release, the installation, the removal and the five `Indexes.IsEmpty` guards of `ChangePublisher` read it | — | joined (S14, part 2), its (a) and (b): it changes figures |
| E3 | `Engine/Indexes/IndexOrder.cs` 81–110, 118–153, 157–201; `SecondaryIndex.HasKey` 96–133 | The comparison of one part — null, text, fixed — is written three times, and the check of a hint uses `TextEquals` while the tree orders by `CompareText`: two algorithms that must agree. A fourth loop, `IndexOrder.SameKey` on two roots, came with the change of each index decided once (S14, part 2) | The offset of a part in a search key compiled once into its `EntryPart`, and read by the constructor of `SearchKey`, `CompareKey` and `HasKey`, `FixedAt` taking the part; one `NullOrder` for the rule of nulls, written four times today. The four loops stay: they read different sources, an entry, a probe and a root, and a generic source of a part would cost more than it removes on the hottest path (G1 of the architect's review after S14, part 2) | index-search on one thread, the upkeep of an index against the bare tree, the 328 bytes of a search | done (S14R, part 1): the offset of each fixed part compiled into its `EntryPart`, one `NullOrder`, the four loops kept; `HasKey` joined (S14, part 2), its (d) |
| E4 | `Engine/Indexes/SearchKey.cs` 36, 48, 105–120 | The constructor demands the full key and copies the texts, so leading parts, bounds and a key to resume from have no form; the order of the identities is derived twice | The caller gives the bound parts and owns the texts as spans; `RootHandle` exposes its packed value once | `SearchAllocationTests`, `PausedSearchTests` | joined (S14, part 2), its (c) |
| E5 | `src/Moltaro.Lattice.Measurements/Scenarios/IndexSearchScenario.cs` 157–257 | B2 has grown to five copies: the window of the index scenarios calls a delegate for each step | At B2 | At B2 | joined to B2, which is taken now |
| E6 | `Engine/Entities/EntityTable.cs` 520–569, 666–693 (772 lines); `Engine/Entities/EntityPass.cs` 39–157 | The walk of S15 would land in the table | The read limits as one small type that `EntityPass` and `IndexSearch` both hold; one descent of a reader in `OrderedTree` that yields the leaf, the place and the fence in either direction — today `TryFindFirst` returns only an identity and descends twice | — | done (S15, part 1): the read limits in `ReadLimits`, which `EntityPass` and `IndexSearch` hold by value; one descent of a reader, `OrderedTree.Seek` by a search key or by a whole entry in either direction, which yields the leaf, the place and the fence on the side of travel (`TreePlace`), and `TryFindFirst` is gone |
| E7 | `Storage/Ordered/NodeStorage.cs` 21–85; `IndexLayout.SharedTexts` 278–306 and `SharedTextSlotBytes` 311–334; `OrderedTree.PublishTexts` 964–978 | The walk over the shared keys of an entry is written several times | One member that yields the descriptors | — | done (S14R, part 1): `SharedTexts` yielded the descriptors; the references of a text stay for each node (F8). As merged it cost an insert and a delete of an index with a String part 4 to 8 %, found by the first start of the rented machine for S15, part 1, which carried the build before S14R: the `stackalloc` beside the loop of `NodeStorage.Retire` compiled it without tiering and kept it from being inlined into the retirement of a node, about 54 calls more on each insert and each delete, since a replace retires no node. Of two fixes measured on the second start, V1, the sum of the slot bytes back, came to 0.96 to 0.99 of the build before S14R on the insert-and-delete lines of `write-callers`, and V2, the shared keys of an entry walked by the ref struct `SharedKeys` with no `stackalloc`, to 1.01 to 1.08, with nothing lost on `index-long-text`; two lines whose rounds scatter for every build are not judged, readers of the other database beside one caller that inserts and deletes, and an insert and a delete with a common head of a long key. V2 is taken by the rule written before the start, in the brief of S15, part 1 (`docs/work/epic-1/sprints/s15.md`), and `SharedTexts` goes with it. V2 also makes a replace that changes a long text key and an insert and a delete with distinct heads 8 to 14 % faster on that machine, as measured on those lines only. Left from its review: `SharedKeys` repeats the `HasText` check its callers make, it has an unused `using`, and a few of its comments could read better, for the next refactoring sprint; the `using` is gone (S15R, 61b647e), the check is left, since it is on the write path, where E7 shows that a small change moves what the JIT makes; and no test holds the text bytes `NodeStorage.Retire` reports, a gap older than the fix, for the next sprint that touches the retirement of a node |
| E8 | `Engine/Sets/SetIndex.cs` 63–82, 103–108, and `Engine/EngineDatabase.cs` 573–595 (`WaitForPositionAsync`, both overloads, added by the planner on 20 November 2026), against `SetOperations.ReadAsync` 62–74 | A fast path written by hand on which a failure is thrown at the call, while after a wait it comes through the task | Not refactoring, a defect of consistency: the rule `ReadAsync` keeps, a caller's mistake thrown at the call and what the database decides through the task | — | joined (S14, part 2), its (c), test first |
| E9 | `Storage/Values/StoredScalars.cs` 96–140 | It takes the contract's `IndexValue`, and only `SearchKey` uses it | The switch moves beside `SearchKey` | `IndexPartValueTests` | done (S14R, part 1): the switch is `SearchKey.WriteFixed` |
| E10 | `Engine/Writes/PreparedWrite.cs` (`WriteChange`, the comment at `SetNoChange`); `Engine/Writes/WriteExecutor.cs` (`TryPrepareDelete`); `Engine/Entities/ChangePublisher.cs` (the loop of `Publish`) | Two P4 of the review of the merge in S14 and two leftovers of the listings of the JIT: nothing asserts that the record `WriteChange` writes changes no entity yet; the comment at `SetNoChange` is inexact; `refusal = null` in `TryPrepareDelete` is redundant; the loop of `Publish` has a check of bounds the build before S14 did not have | The assertion; the comment corrected; the assignment removed; the check gone, as before S14 | — | done (S14R, part 1), but for the check of bounds in the loop of `Publish`: it is there before S14 too, from the `Enqueue` of the retired list, so that premise did not hold and it stays |

It adds to three entries above: U6 with A3, I3, and B2, of which E5 is the fifth copy.

## Found while coordinating S14, part 2, 20 November 2026

Taken by the planner and the coordinator on the same day.

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| F1 | `src/Moltaro.Lattice.Engine.Tests/Engine/Indexes` | 22 files lie flat in one folder that holds several areas: refusals and the reservation, the upkeep, the search, the order of text, nodes and their memory, seeds and contradictions | Sub-folders by area, those S14, part 2 opens for its new tests — `Refusals`, `Reservation`, `Upkeep`, `Search` — and what the rest needs; the helpers the whole folder shares stay at its root. No assertion changes | done (S14R, part 1): sub-folders `Primary`, `Tree`, `Memory`, `Order`, `Search`, `Reservation`, `Refusals` and `Upkeep`, the shared helpers at the root |
| F2 | `src/Moltaro.Lattice.Tests/Database/OpeningAndClosingTests.cs` (`Closing_a_child_ends_its_wait_for_the_master`), with `src/Moltaro.Lattice.TestSupport/Replication/MasterAndChild.cs` (`NewAddress`) as it uses it | Two weaknesses that can be read in the test. `NewAddress` lets its socket go before the address is used, so any socket can take the port in between, a master of a test that runs at the same time among them, and the child then reaches it. And the test waits by a sleep of 50 ms for its child to be in its wait for the master, where a deterministic way is wanted. The test failed once in a whole Debug run, "Expected: Connecting, Actual: Closed", and not in 30 runs of its project on the head of S14, part 2 and 30 on `main`: which of the two, if either, caused it is not shown | The test holds its address for as long as it uses it, with a socket of its own bound to it, so that no master can take the port; and it knows from that socket, not from a sleep, that the child is in its wait, for example one that listens, accepts the child's attempt and ends it, so that the child waits to try again. `NewAddress` stays for its other users, which need a free address before their master opens, as its comment says. Every assertion unchanged: a child that waits for its master ends that wait when it is closed | done: the test holds its port with `ListeningPort` of `src/Moltaro.Lattice.TestSupport/Replication`, which accepts the child's attempt to reach its master and ends it, so that the test knows without a sleep that the child is in its wait; `MasterAndChildTests.Child_opened_before_its_master_waits_for_it` and `Opening_a_child_can_be_cancelled` take the same type, and `ListenUriTests` as well, so `NewAddress` is gone |
| F3 | `src/Moltaro.Lattice.Engine/Engine/Indexes/OrderedTree.cs` (`TryReserve`, the managed room of the pool) | The pool is grown to the whole need (`Stack.EnsureCapacity`) before the arena is asked, so a need the memory limit cannot hold first takes a managed array of about eight bytes a node — some 17 GB at the largest need an `int` counts — and keeps it, since the pool never shrinks. The write is still refused, so this is a cost, not a wrong success; `main` does the same with `Bound`, which is larger. The writer's path is grown the same way before the arena is asked and stays too, a few KB beside the pool's tens of MB. No mode built today lets enough writes wait to reach it | A reservation that is refused leaves no managed room behind either, the pool's nor the path's: the managed room of the pool is taken in step with what the arena gives, and a pool that grew for writes that are gone gives it back | done (S17, part 1): the reserved nodes are linked through their own payload (`Indexes.NodePool`), so the pool takes no managed room; a reservation whose nodes do not fit under the memory limit is refused before it takes one, and the path of the writer grows only once the arena gave the nodes. The places of the retired list stay managed room, reserved once the nodes are had |
| F4 | `src/Moltaro.Lattice.Engine/Engine/Comparison/ValueComparer.cs`, its head | About sixty lines, against the twenty of the convention for the head of a type: equality, the reading of a text by pieces with its reasoning on surrogates, and the seeded hash, older than S14, part 2, which took out what it had copied from `docs/design/ordered-index.md`. It serves the primary index as well as the ordered one, and no design document has a section for the comparison of values, so the narrative has nowhere to go. Its paragraph on equality also says "No byte order of UTF-8 stands for UTF-16", which reads against the comparison of UTF-8 bytes with its one turn that the order now makes | A section on the comparison of values in a design document, and the head as what the type is for, what it guarantees and a pointer to that section | done (S14R, part 2), with G3: the head of `ValueComparer` in 17 lines; the comparison of a key in `docs/design/arena-protocol.md` |
| F5 | `src/Moltaro.Lattice.Engine/Engine/Entities/EntityTable.cs` (`TrySearch`, the condition that the end of a run is reported at the sequence before the step only when the sequence did not move) | No test holds the check. A mutant that drops it passes every test and every probe through the public API: a lazy `Find` drops the sequences of its steps, and `FindAsync` takes the largest of them, so no public path sees the sequence of the end step alone. A probe of the engine that takes the steps itself catches the mutant in every run, but it reaches the sequence only through private fields, and no member gives it. The check guards a replace or a delete of an entity after the identity, which publishes its position before it removes the old entry | The test, written with the first consumer of that sequence, the search resumed and paged, against the member that consumer needs; the probe kept with the outputs of the review of S14, part 2, outside the repository, is its starting point: 40 of 40 runs of one second caught the mutant, none of 55 failed on correct code, and the worst run caught it by 2 ends | removed with its subject (S15, part 1): the implementer and the breaker of S15, part 1 each answered on 21 November 2026 that nothing relies on an end at exactly the reported sequence, the owner removed the sentence of the Acceptance that asked for the test, and the end a step that copies an entity sees is reported at the sequence of that step, without the check; the probes of the breaker of S14, part 2 and of S15, part 1 are kept with their outputs, outside the repository, as the evidence |
| F6 | `src/Moltaro.Lattice.Engine/Storage/Ordered/IndexLayout.cs` (`SameEntry`), with `RootLayout.StoredFieldOf` | The decision whether a write changes an index looks up the stored field of each part four times on every write, through a call that is not inlined: two for whether it is null and two for its bytes. On the head of S14, part 2 this is about 15 ns an index on the workstation of the breakdown, the most of the 30 ns an index that a replace keeping every key costs on the rented machine, where the owner accepted 0.94 and 0.86 of the rate without indexes with three and six indexes | The stored fields of an index resolved once when its layout is compiled, so that the decision reads each part from a known offset, width and null bit, with the same rule as today and no second path beside it; then the line of index-writes measured again | done (S16, e79a710): `EntryPart.Stored` holds the stored field, resolved once with the layout of the index. In the start of S16 the head over 57be01b runs at 1.02, 1.06 and 1.10 on a replace that keeps every key with 1, 3 and 6 indexes, one caller, the gauge 0.995 to 1.01: 0.94, 0.98 and 0.93 of the rate without indexes, where the owner had accepted 0.94 and 0.86. G2 and the implementation are measured with it, in one build |
| F7 | `src/Moltaro.Lattice.Measurements/Scenarios/WriteCallersScenario.cs` and `ReadByKeyThreadsScenario.cs` | Both work on the users of the reference fixture, a type with three indexes, so neither measures a type without indexes, though the plan of the machine for S14, part 2 named them for it; that statement rested on the lines of `index-writes` with no index, one caller | A line of `write-callers` over a type without indexes, with its callers and its readers beside them, so that a start can state on many callers and readers that a type without indexes loses nothing | done (S15, part 1): the line of `write-callers` over users without indexes, compared on the first start of S15, part 1 with the line added to the older builds as a patch; a type without indexes loses nothing, neither to S14R nor to S15, part 1 |
| F8 | `src/Moltaro.Lattice.Engine/Storage/Ordered/NodeStorage.cs` (the references of a shared text) | Each node that holds an entry of a shared key takes a reference to its text entry, so every copy of a leaf takes one for each such entry and every free releases them. A variant that leaves the reference to the root of the entry, for each entry, measured on the rented machine with keys of 100 and 200 bytes: a replace that changes the key 1.45 to 1.51 times the head, an insert and a delete 1.9 to 2.1 with distinct heads, a search and a replace that keeps the key unchanged; its patch is kept with the outputs of the start of S14, part 2, outside the repository | The references of a shared text held for each entry. It changes who holds a reference to a shared text, a rule of the [decision on strings](../decisions/strings.md), and its safety rests on the order of retirement: the owner's question, which the planner brings | S23 kept per-node ownership as required by Q04.4. A physical text move preserves the stored reference count. The root-only variant remains an experiment. Reconsidering ownership requires a separate accepted decision. See the [index lifetime rule](../design/ordered-index.md#text-references) and the [S23 indexed-text probe](epic-1/sprints/s23.md#final-arm64-acceptance-evidence). |
| F9 | `src/Moltaro.Lattice.Measurements/Scenarios/IndexReadByKeyScenario.cs`, the line of 0 indexes, 1 thread, alone, before and after B2 | On the same engine the harness of B2 reads that line at 0.888 of the old one, in rounds of 0.94, 0.87 and 0.89, where its gauge is a single pair at 1.066; every other judged line agrees. Until the difference has a cause, the figures of that line recorded before B2 are not compared with figures after it | The cause found by reading what the old and the new harness do for that line: the window, the warm-up and what lies inside the timed region; if reading does not show it, that start carries a series of three builds for this line | investigation complete. The [matched ARM64 comparison](#f9-index-read-by-key-driver-comparison) did not reproduce the fall. The historical cause remains unresolved and the pre-B2 line remains incomparable with later figures |
| F10 | `src/Moltaro.Lattice.Samples/Limits/ReadLimitsSample.cs` and `MemoryLimitSample.cs` | Both samples show their limit held and never reached, so the block that catches the refusal never runs: a sample of a limit teaches half of it | The sample of the read limits shows a limit reached and the error an application sees, in S15, part 1, whose tests and sample extend those limits to a search; the sample of the memory limit shows a write refused for memory and what the application does next, in S24. Each still runs to the end: its catch block reports and the program goes on | done in part (S15, part 1): the read limits of a search, `ReadMemoryLimit` counting what the search remembers from the moment it is remembered and every entity of an answer by everything stored for it, `ReadTimeLimit` the engine time of its steps, the token looked at before a limit at every place; taken (S24), the memory limit |
| F11 | `src/Moltaro.Lattice.TestSupport/Replication/ClosedPort.cs` and `ListeningPort.cs`, with the tests that free a port for a master to open at, `OpenTimeoutTests.Child_started_before_its_master_becomes_ready_when_the_master_opens_within_its_OpenTimeout` among them | Between freeing the port and the master binding it, any socket can take the port. The test then fails loudly, never with a false pass: it did once, AddressAlreadyInUse, in a Release run of S14R while another agent ran the suite on the same workstation | A master that takes a listener the test already holds, so that no port is ever free between the two | taken (the first sprint that touches the opening of the listener of a master; earlier if it fails once more on a machine that is not loaded) |
| F12 | `src/Moltaro.Lattice.Engine/Engine/Writes/WriteExecutor.cs` (`SlotBytes`), with `Replication/Load/BlockImport.cs` (`Place`) | For a root a forwarded write places on the master, the encoded size counts no new text entry: `Place` discards the new bytes `TextTable.TryAcquire` reports, where a local write counts them (`RootWriter.PlaceText`). That size is `PreparedWrite.EncodedSize`, which bounds a commit group and feeds `LargestCommitGroupBytes`, so the same write may count less because a child sent it | Test first, by a test author: the same write, with a new text long enough to be a text entry, made on a master and forwarded by a child, counts the same in the size of its commit group, stated through `LargestCommitGroupBytes` and by the bound of a group where that can be shown deterministically. Before it is written, the decision on write limits is read for what the encoded size is defined to count; if it defines a forwarded root otherwise, the planner hears it first. If the test passes on `main`, the entry closes with the reason the sizes are equal; if it fails, the implementer fixes it, one reviewer | done (a light change of its own, 21 November 2026): the test failed as the entry said, the forwarded write counting 112 bytes, its root alone, where the same write made on the master counted 1,392 in UTF-8 and 2,160 in UTF-16, and two writes of 5 MiB sharing one group. `BlockImport.Place` now counts the slot bytes of what it places as `RootWriter` does, the new bytes of a text entry included, and `WriteExecutor.SlotBytes`, the walk that counted afterwards, is gone |
| F13 | `src/Moltaro.Lattice.Engine/Engine/Entities/EntityTable.cs` (`TrySearch`, `Step`, `StepWithoutWalk`), `IndexSearch.cs`, `ReadLimits.cs`, `FirstKey.cs` | The exact search pays for the walk: 3 to 4 % on an Int64 key, about 10 % on a text key of 8 bytes, 4 to 5 % on a text of 100 bytes and 13 to 17 % on the first of many, one thread alone, against 5cc64c8 in the third start of S15, part 1. What S15, part 1 added to every search: the read limits, at most once, the kept first key. Three variants tried on the machine, a smaller search object, a lean step for a full key and a step split from its loop, gained nothing outside the gauge | Look for the part of this that an exact search of one entity does not need, checked by JIT listings and counts, not by the machine; if something is found, it gets a line in the next start of the machine | — | taken (S15R): the candidates 1 to 4 of the architect's review after S15, part 1, checked by listing and counts in that order, one commit each, each kept only if the listing shows fewer calls or regions on the step of an exact search; candidate 1 only if the errors a closed or a faulted database gives are the same in every test that states them. Candidates 5 and 6 are not taken: 5 was measured (lean, split) and gave nothing; 6 is P1 and P2 of that review. Done (S15R): candidate 1 (5f3b658), a step with nothing to walk looks at the state of the protection without taking a slot, 8 calls to 6 and 2 regions to 1; candidate 2 (30b9cf8), the read limits return their failure, one round of an exact step 4 calls to 2 and 3 regions to none; candidate 3 (4d70e3b), the order of a search key against an entry is inlined into the walk and the descent. Candidate 4 is not done: inlining `TryNext`, `IsOfSearch` and `FollowsMore` used the inline budget of `Step`, and seven calls the step inlined before became calls. Counted by the JIT listing of a Release driver with tiered PGO off, on the workstation. The line of `index-search` in the start of S16, against 074f783, three rounds: an Int64 key through the class 1.05 on one thread alone and 1.08 on eight, the gauge 1.00 on both; by name 1.02 and 1.00, the gauge 1.01 and 1.05; the first of 16 keys 1.01 and 1.03 on one thread, the gauge 1.04 and 1.00. The exact search gains about 5 % through the class and loses nothing elsewhere |
| F14 | `EntityTable.StepWithoutWalk`, group 1 of the fixes of the fall of the exact search in S15, part 1 (the step with nothing to walk as a method of its own, one walk and one scratch in the frame of a step) | It adds one method; its gain was +2 to +6 % on the one-entity lines in the second start of S15, part 1, at the edge of that start's gauge of 3 to 4 %, and no later start separated it | Keep or remove: the architect's review decides, one method against a gain at the edge of the gauge | — | done — kept (the architect's review after S15, part 1): folding it back is not simpler, with a nullable index and two early returns again and a search key written for a step with nothing to walk, and it would put the cheap step into a method that does not tier; no measurement is owed |

## Architect's review after S14, part 2, 20 November 2026

Read at `main` 22b8ee1. The sprint added no type and no folder, and the direction
`Storage/Ordered` → `Engine/Indexes` → `Entities` → `Sets` holds. Gone through by the
planner and the coordinator on the same day.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| G1 | `Engine/Indexes/IndexOrder.cs` (`CompareKey`, `HasKey`, `CompareKeys`, `SameKey` on two roots); `Engine/Indexes/SearchKey.cs` (the constructor, `FixedAt`); `Storage/Ordered/EntryPart.cs` | Where a fixed part lies in the scratch of a search key depends on the layout alone, yet three loops count it with a running offset; the P1 of S14, part 2 was one of them not stepping past a null part | The form of E3 | index-search on one thread; the upkeep of an index 1.27 times the bare tree; the 328 bytes of a search | done (S14R, part 1), as E3 |
| G2 | `Engine/Entities/ChangePublisher.cs` (`ReplacedBy`, the parameter `replaced` of `ChangeAt`, `ReleaseIndexes`, `RemoveOld`, `MarkReplace`, `StampDelete`, `Publish`); `Engine/Entities/PreparedChange.cs` (`Replaced`); `Engine/Writes/PreparedWrite.cs` (`Changes`) | The replaced root reaches the upkeep three ways by phase: from the root entry before the publication, from `change.Replaced` after `MarkReplace`, from `entry.Root` again for a delete | `TryReserve` records `Replaced` for a replace and a delete; `ChangeAt` takes the change alone; `ReplacedBy` and three parameters go; `Publish` and `PreparedWrite.Changes` take a `ReadOnlySpan` | The order of the stores and both fault points; a MemoryOnly write allocates nothing; index-writes with no index, the replace that keeps every key and write-callers, on the machine; no added call for a type without an index in the listing of `TryReserve` | done (S16, 138215c), with the lookup (`EntityTable.TryFind`) giving the replaced root, which the preparation writes with the change: the reservation reading it would have added a call that is not inlined to every replace and delete. `TryReserveWithIndexes` makes 15 calls instead of 20; for a type without an index the listing of `TryReserve` makes the 5 calls it made. In the start of S16 a type without an index runs at 0.996 to 1.013 of 57be01b, the gauge 0.97 to 1.02; the fixture's users run at 1.02 to 1.06 on a replace and at 0.984 on an insert and a delete with one caller (K1) |
| G3 | `Engine/Entities/EntityTable.cs` (its head of 108 lines; the comment of `TrySearch`); `Engine/Entities/IndexSearch.cs` (its head); `docs/design/ordered-index.md`, "The search by a full key"; `Storage/Ordered/IndexLayout.cs` (`WriteEntry`) | Why a step may report the end at the sequence before it is argued three times in three wordings; the head of `EntityTable` has had no owner since A6; `WriteEntry` still says a reader checks a root with it | `ordered-index.md` owns the argument, and `TrySearch` keeps the condition and the one order it rests on in about five lines; the head of `EntityTable` says what the type is for and points onwards, its narrative of a read by key and of an enumeration moving to a design document in the same commit; the stale sentence goes | Nothing measured; comments only | done (S14R, part 2), with F4: "The reads of an entity table" in `docs/design/arena-protocol.md`; the heads of `EntityTable` and `IndexSearch` point to it and to `ordered-index.md`, which owns the argument of the early end; `TrySearch` keeps the condition of the early end and the one order it rests on; the stale sentence of `WriteEntry` removed |
| G4 | `Engine/Entities/IndexSearch.cs` (`TryReadNext`); `EntityTable.TrySearch` (`ref RootHandle from, ref ulong? endsAt`); `Engine/Sets/SetOperations.cs` | The state of a search is a struct of two private fields whose one member passes them by reference into the table: three hops and seven parameters for a step, and S15 adds the key to resume after, the direction, the bounds, Offset, Limit and the read limits | `TrySearch(index, key, ref IndexSearch search, ref copy, out sequence, token)`; `IndexSearch` is plain state and `TryReadNext` goes; `SetOperations.Next` keeps "moved past only after the build" by stepping a copy; `probe.After(identity).From` is `identity.Next` | The 328 and 320 bytes of a search; index-search | done (S15, part 1), with E6: `IndexSearch` is plain state, and a step gives the place past the entity it returns as out values, which `SetOperations.Next` takes with what the step staged only once the entity is built, so a build that throws leaves the search before it and keeps the time spent and a failure recorded; this replaced, after the first start of the machine, both the copy of the brief and the form that put the place back |
| G5 | `Engine/Indexes/SecondaryIndex.cs` (`TryReserve`, `Release`); `ChangePublisher.cs` (`index.Tree.GiveBackReserve()`, `indexes[index].Tree.TopUp()`) | The counts a change needs of an index, inserts and removes, are written twice as two switches, and the publisher reaches into the tree for the top-up and the give-back | One private mapping to the pair of counts, used by both; `TopUp` and `GiveBackReserve` on `SecondaryIndex`, so that the publisher speaks to one type; S17 sums those counts over a batch | index-writes, every line; the Debug assertion of the reservation across the suite | done (S14R, part 1) |
| G6 | `Engine/Indexes/OrderedTree.cs` (1,215 lines): the pool, its counts and places, `TryReserve`, `TopUp`, `Release`, `GiveBackReserve`, `Trim`, `Need`, `Growth`, `StandingReserve`, `TakeNode`, `GiveBack` | One type holds the copy-on-write tree and the policy of its pool of nodes, interleaved, with separate invariants and separate sections of the design | A sealed type for the pool, given the height and the room of the root, from which the tree takes its nodes. It adds a type and removes none; it pays only because S17 then rewrites one small type with one invariant | The upkeep 1.27 times the bare tree; a publication allocates nothing; prepared bytes at most the standing reserve; the closed form against the loop | done (S17, part 1): `Indexes.NodePool`, owned by the tree, with the ledger of the linear bound; `Need` and `Growth` moved to it |
| G7 | The tests of `Engine/Indexes`; `IndexNode.FreeBytes`; `OrderedTree.Height`, `Count` and `Bytes`; `IndexLayout.EntryLength`; `OrderedTree.ReplaceChildPair` and `RemoveChild`; the constant `Bound` of the tests | `EntryOf` written five times in two forms; the tests of the primary index among those of the ordered one; a constant of one minute written 62 times in two test projects; a member called by nothing, and members read only by tests whose comments say otherwise; the copy of an inner node less one separator written twice | With F1: one `EntryOf` at the root of the folder and a sub-folder `Primary`. One constant of the timeout in `Moltaro.Lattice.TestSupport`. With I3, by the rule of A13 for members of the product that only tests use: `IndexNode.FreeBytes` removed; `IndexLayout.EntryLength` moved to the tests; `OrderedTree.Height`, `Count` and `Bytes` removed, with the two counts the tree keeps for them on every publication, the tests reading what they need by the walk they already have (`EngineDatabase.IndexNodes`); the height the engine itself uses for the need stays. With I1: one method for the copy of an inner node less a separator, only if it reads plainer | The assertions of the tests | done (S14R, part 1): one `EntryOf` in two plain forms, `TestWait.Bound` in TestSupport, the members of I3, `WithoutChild` of I1 |

Its questions went to the study of S15, part 1 (the search key written at every step; the
identity of an entry as the handle of the root) and to the study of S16 (a unique conflict
as a third outcome of `ChangePublisher.TryReserve`). Found sound: the two masks of
`PreparedChange` with their shared last bit; the four small loops of `ChangePublisher`
over `ChangeAt`; the order of the upkeep in `Publish`; `Need` in closed form with the loop
kept in the tests; the top-up after the needs of every index, and drain, give back and the
last try; `EditedLeaf.CopyTo`; `IndexMatches`; `SetIndex`; `SearchKey` as a ref struct;
`CompareUtf8Ordinal`; the path of one number; the new sub-folders of the tests.

Sound, leave as it is: `IndexNode`, `NodeWriter`, `NodeHeader`; publishing a node by one
release store and retiring after it; `EditedLeaf` and `GrownInner` as views; entries as
hints checked against the root; the order of upkeep in `ChangePublisher.Publish`;
`IndexLayout.WriteEntry` copying fixed parts as they lie in the root; `SetOperations.Next`
written twice, because merging them needs an interface that is no boundary; `TryCopy`
beside `TryCopyFound`, on which the figure of the read by key depends.

## Architect's review after S15, part 2, 21 November 2026

Read at `main` 074f783. The sprint landed where it belongs: binding is one type of the
core, `IndexValue`; `IndexCriteria` shrank; `SearchKey.WriteFixed` needed no change; no
folder and no dependency moved, and nothing needs a new type. Gone through by the planner
and the coordinator on the same day. Found sound and left: `IndexValue` as one type with
`Pairs`, `TryConvert`, `TryConvertInteger` and the type of a null in its padding; the
split of `IndexCriteria.Prepare`; `KeyArgument` beside `IndexValue.Of`, since merging them
would cost a read by key a call; the two oracles of the tests of `ValueBinding`, which
repeat about 25 lines and get a shared place if a third is written. Not examined:
`docs/design` and the README beyond names, the samples, the child's side, and the places
of H1, H3 and F13.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| J1 | `src/Moltaro.Lattice/Access/Values/IndexValue.cs` (`Of`, `OfOther`) | Reference types and value types go through one interleaved path, and a null is made in four places: the branch of a string, the test of a null reference, `Is` and `OfOther`. A string is taken ahead of `OfObject`, which takes it as well | One branch for every reference type, the first in `Of`: a null reference becomes the null of its kind, of String for a string, without a type for an object, of a type no field has otherwise, and any other reference goes to `OfObject`. After it only value types remain, the chain of `Is` and `OfOther`, so a null is made in three places, one for each kind of .NET null | Making a key boxes and allocates nothing, in a build the JIT does not optimize as well (`SearchAllocationTests`, `ConvertedValueAllocationTests`, `KeySizeTests`); the tests of `Indexes/ValueBinding`; the JIT listing of `IndexKey.Equal<string>`, `Equal<long>`, `Equal<object>` and `IndexBound.Inclusive<string>` with their callers, in which the string form shows no added call; a line of `index-search` with a text key in the start of S16 | done (S15R, d708fb8), the reference branch makes the String value itself; the four listings make the same calls, and the shared `Of` no longer calls the cast helper for a string. The line of `index-search` with a text key in the start of S16, against 074f783, three rounds: text of 8 bytes 1.00 through the class and 1.01 by name on one thread, 1.05 and 1.07 on eight; of 100 bytes 1.01 and 1.01, 1.02 and 1.02; the gauge 0.96 to 1.03. Nothing is lost |
| J2 | `IndexValue.BindOther`, `src/Moltaro.Lattice/Access/Values/FieldValues.cs` (`Normalize`) | The refusal of a value that cannot be used for a field is written character for character in two files, the head of `IndexValue` promises that they agree, and no test holds either text | One member of `FieldValues` that makes that `ArgumentException` from the field, the value and the name of the parameter, called by both. The messages of a null, of an enum value and of text that is not well-formed have other endings and stay | Both messages byte for byte, and `ParamName`: a test that pins them is committed first | done (S15R): the tests 3b7c884, the member `FieldValues.CannotBeUsed` f24d491 |
| J3 | Comments: the head of `IndexValue`, 33 lines, whose fourth paragraph repeats the remarks of `IndexKey`; `FieldValues.cs`, which cites the planner and a question; `Engine/Indexes/PrimaryIndex.cs`, which cites a review round, a sprint and a dated decision | The convention for comments of `AGENTS.md` | The head of `IndexValue` keeps what the type is for, that nothing is boxed, and the bits; of `Bind` it says in two or three lines that it applies the remarks of `IndexKey` and Q07.3.6 of `docs/decisions/queries.md`, and adds what they do not say: a converted value is written into the bits, and the check of an enum value uses the words of a write. The other two state their rule in their own words | Nothing measured; comments only | done (S15R, 61b647e). Comments of the same kind in files S15R does not touch stay with C1 |

Left as they are, from the review of S15, part 2 and from this one:

- `IndexValue.TryConvert` is right only for a pair `Pairs` allows. It is private, its one
  caller is three lines above it and its comment says so; making it stand alone would
  repeat `Pairs`.
- `TextPolicyTests` repeats the equality that `TextKeySearchTests` owns. Removing it
  removes assertions, and its head already names the owner.
- `IndexBound.Inclusive<string>(null!)` is refused in the words of a bound without a
  value, which is not its cause. A message is behavior; it is changed when someone meets
  it, in a sprint of the plan.
- `BindingSearch.ExpectRefusedAsync` accepts any exception derived from
  `ArgumentException`. Tightening it changes an assertion, so it is not refactoring; it
  is a note for the test author of S16 in the document of Epic 1.

## Architect's review after S15, part 1, 21 November 2026

Read at `main` 5f1907d. Folders and the direction of dependencies hold; the sprint's new
types have one job each. The debt is in two places, `EntityTable.Step` and the chain
handling of `WriteCoordinator`; nothing needs a new type. Gone through by the planner and
the coordinator on the same day. Found sound and left: the walk inside `EntityTable` (to
be looked at again when S16 or S17 grow it), `OrderedTree.Seek` written twice,
`ReadLimits` and `WalkProgress`, `StepWithoutWalk` (F14, kept). Not examined:
`docs/design` beyond names, the measurement scenarios beyond the loop of `index-search`,
the child's side.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| H1 | `Engine/Writes/WriteCoordinator.cs` (`MarkCompleted`, `DecideFailureAndAdvanceChain`, `Prepare`, `Resolve`, `PublishGroup`, `Withdraw`, `TryQueue` and its flag `topsUp`) | That the end of a write starts the next write of its chain is one rule written four ways: eleven ends call `DecideFailureAndAdvanceChain`, `Prepare` advances when `TryQueue` refuses, `Resolve` advances by the state of a write, and `PublishGroup` calls `DecideSuccess` and `AdvanceChain` side by side; the withdrawal of a chained write only decides, since `Unchain` has relinked the chain. S16 makes a write wait behind a prepared write of another entity and S17 makes one write a member of several chains, and each would have to be taught all four | `MarkCompleted` calls `AdvanceChain`, which does nothing for a write with nothing behind it and clears both links before it makes the later write ready, so a second call does nothing; `DecideFailureAndAdvanceChain` goes, its callers call `DecideFailure`; the three explicit advances and the test of the state in `Resolve` go; `Withdraw` keeps its form with one comment. `TryQueue` loses `topsUp`: `Prepare` tops up when `TryQueue` queues, in the same hold, after the write is appended, since `TopUp` reads nothing of that queue | The order of a chain; nothing applied twice, nothing lost; the order of the ready list of a hold; the tests of the coordinator in `Moltaro.Lattice.Engine.Tests` and the write tests; the JIT listing of `PublishGroup` and `Start` on the workstation; `write-callers` in the start of S16, whose builds are `main` before S15R, `main` after S15R and the head, with a gauge | done (S15R, 08520e3): `MarkCompleted` ends with the advance of the chain, and `DecideFailureAndAdvanceChain` and `topsUp` are gone. Without PGO `PublishGroup` calls `MarkCompleted` once for each write it publishes instead of inlining it, about two calls more for each write; with PGO it makes the same calls. A line of `write-callers` in the start of S16: against 074f783, four rounds, 0.96 to 1.03 alone and beside 16 readers, the gauge 0.97 to 1.03; nothing is lost |
| H2 | `src/Moltaro.Lattice.Tests/Indexes`, its root | About twenty files lie flat beside the sub-folder `Walk`, in several areas: the declaration of an index, the arguments of a key, the exact search, unique indexes | Sub-folders by area, for example `Declaration`, `Keys`, `Search` and `Unique` beside `Walk` and `ValueBinding`; `Account`, `Member` and `MemberSchema`, and a helper that two folders use, stay at the root; `Walk/RangeEnd.cs`, which `ValueBinding` uses as well, moves there beside `TypedValues` | The count of tests and of skips | done (S15R, db09634), with the same count of tests and skips; `IndexTests`, the overview of the area, stays at the root |
| H3 | `Engine/Entities/EntityTable.cs` (`Step`, `StandAt`) | What a round ended for is kept in three locals, one of them derivable, and written from two places at once: the branch that needs room sets `makesProgress` and the length of the entry and then calls `StandAt`, which updates the same two by reference; the length of the detached entry is computed twice; detaching the entry and writing the identity is written twice; the bound of a key repeats the rule of `ReturnedKeys.HasRoomFor` | `makesProgress` goes, decided after the protection from whether the search has a `WalkProgress` and the round asked for room; `StandAt` returns the length of the entry it could not stand at, 0 when it stood, with no flags by reference; one member of `SecondaryIndex` detaches at an identity and replaces the two pairs | The pinned bytes of a search and of a walk; the tests of `Engine/Indexes/Search` and of `Indexes/Walk`; the JIT listing of the callers of `Step` | done (S15R, 2357720), with `SecondaryIndex.DetachAt`. The shorter `Step` stopped inlining `ReadLimits.EndStep` and `ReturnedKeys.HasRoomFor`, which a listing of `Step` itself showed; both are marked for inlining, and the steps and their callers make the calls they made before. Left: the length of a detached entry is computed in the step and again in `StandAt`, and the bound of a text key repeats the rule of `ReturnedKeys.HasRoomFor`; the entry gave no form for either. The figures of the start of S16 are those of F13, whose step this is |
| H4 | `Engine/Sets/SetIndex.cs`, `Engine/Sets/IndexMatches.cs`, `Engine/Sets/SetOperations.cs` (`Options`) | Both owners of a search build it with the same expression, whether the index is given and the key is full, and `SetOperations.Options` exists only to hand them the options, where an enumeration has `StartPass` for the same job | `SetOperations.StartSearch(index, key)` and `StartSearch(index, key, reverse, offset)`; `Options` goes, and `Sets` no longer asks `IndexWalk` whether a key is full | The pinned bytes of the search object | done (S15R): `SetOperations.StartSearch(index, key)` and `StartSearch(index, key, reverse, offset)`, and `Options` is gone |
| H5 | Comments in files S15, part 1 touched: `Engine/Entities/EntityPass.cs`, `src/Moltaro.Lattice/LatticeDatabaseOptions.cs`, `src/Moltaro.Lattice.Measurements/Scenarios/UserDatabase.cs` and `WriteCallersScenario.cs`, which cite a brief or a sprint; `Engine/Entities/IndexSearch.cs`, whose head tells the history of its size | The convention for comments of `AGENTS.md` | Each comment states its rule in its own words; the head of `IndexSearch` states its bytes and their composition | Nothing measured; comments only | taken (S15R), in the commit of comments, the last, with the rest of N3; never with a move. With it, the comments of the engine that call the thread holding the lock of the writes "the one writer" (`PrimaryIndex.cs`, `RetiredMemory.cs`, `IndexTable.cs`, `EngineCounters.cs`, `ReadEpochs.cs`) say "the publishing thread" or "the thread that holds the lock of the writes", whichever the place means; `docs/design/epoch-protection.md`, whose "One writer beside 20 readers" names one writing thread of a scenario, stays. Done (S15R, 61b647e). Left: "the writer", without "one", in those files and in `SecondaryIndex.cs`, for the thread that holds the lock of the writes; it goes when a sprint touches those comments, by C1 |
| P1 | `Engine/Entities/IndexSearch.cs`, `FirstKey.cs` | The kept first key is an object of its own | The key inline in the search, a hash and two words, the search from 56 to 80 bytes | — | refused: it changes the pinned bytes, what counts against `ReadMemoryLimit` and the size of every search, against the condition of the internal contract of S15, part 1 that a search that finds one entity or none grows only by the read limits and one reference; a refactoring sprint changes no assertion, and no figure shows a gain (a6 moved nothing). It comes back only with a figure of F13 that puts the loss of the first of many on the allocation of `FirstKey`, as a change of a sprint of the plan |
| P2 | `Engine/Entities/FirstKey.cs` | The kept first key carries a hash the second step can compute from its words | `FirstKey` without its hash, 40 to 32 bytes | — | refused, for the reason of P1 |
| P3 | `Engine/Sets/SetIndex.cs` (`FindAll`) | `FindAll` takes one more step after an entity whose step saw the end, which only looks at the token, the limits and the state | Stop when the search has seen its end | — | refused: an application would no longer see a cancellation, a close or a time limit that arrives between the last entity and that step, for a step whose cost nobody measured. It comes back to the planner only if the count of F13 shows that step to be a material part of the loss of an exact `FindAsync` |

## Architect's review after S16, 22 November 2026

Read at `main` 5d27900, nothing run. S16 landed in the right folders and added no
dependency. The debt is in one place: the check of a unique value is welded into the
reservation of one change, and S17 has to undo exactly that; nothing else needs a new type.
Gone through by the planner and the coordinator on the same day. Found sound and left: G2
as it landed, the replaced root written with the change; `ReservationEnum` and
`PreparationEnum`; `PreparedWrites` as one enumerator over the queue and the group;
`ReleaseWaitersOf` behind its test of an empty list; `SecondaryIndex.TryFindOwner` and
`OrderedTree.HoldsAKeyTwice`; `IndexSearch.EndsAtItsEntity`, decided once;
`PendingRequests` looking a call up before it removes it, so that a refused reply cannot
leave a call without its reply; `UniqueSchema.cs` at the root of the engine tests, used by
three folders; the comments of the files of S16, which cite no sprint and no brief. Not
examined: `docs/design` and the README; the samples and `UniqueWritesScenario`; most of
the new tests beyond their helpers; H1, H3, J1 and J2 beyond their rows; the exact layout
of `EntryPart` and `PreparedChange`, whose sizes it gave from their fields.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| L1 | `Engine/Entities/ChangePublisher.cs` (`TryReserve`, `TryReserveWithIndexes`, `TryReserveChecked`, `ClaimsSameKey`, `Frees`); `Engine/Entities/PreparedChange.cs` (`TakenIndex`, `TakenBy`); `Engine/Writes/PreparedWrite.cs` (`Taken`); `Engine/Writes/WriteCoordinator.cs` (the decision on a taken value); `Engine/Child/Loading/ReceivedChanges.cs` (the refusal of a taken value) | Each change is checked against the published tree before the other changes of its publication are known; what follows differs by caller, the master scanning the prepared writes and the child throwing; `TryReserveChecked` repeats the loop of the reservation with its handling of a failure; the result travels in two fields of every change | Three steps over the span of one publication: every change decided first (its masks); one member of `ChangePublisher` finds a taken value on the state the publication leaves, where an owner that a change of the span frees is not taken and two changes that claim one value are; then each index reserves once for the summed counts (G5). `TakenIndex` and `TakenBy` go and the change is 96 bytes again; so do the flag of a check, the second loop and the unused index of `ClaimsSameKey` | The lines 1 to 3 of `unique-writes`, a refused insert still refused before any node is reserved; the 5 calls of `TryReserve` for a type without an index; `index-writes`; the tests of `Engine/Writes/ValueWaits` and of `Indexes/Unique` | done (S17, part 1), with G6 and F3: `ChangePublisher.TryReserve` over a span, `Entities.TakenValue`, and a child's transaction checked on the state it leaves. It is not a refactoring: the path of a type without a unique index becomes decide-then-reserve, so it is the work of S17, with its tests and its figures |
| L2 | `Storage/Ordered/IndexLayout.cs` (`WriteEntry`); `Engine/Indexes/IndexOrder.cs` (the comparisons of parts); `Storage/Ordered/EntryPart.cs` | F6 is half done: these still read a part through `part.Field` and the lookup `StoredFieldOf`, while `SameEntry` and `HasNull` read `part.Stored`; `EntryPart` carries the field, its type and its width twice, by its fields from about 32 to about 64 bytes | Every reader through `part.Stored`; `EntryPart.Field` goes, and the overloads of `RootReader` that take a `LatticeField` if nothing else uses them | The sizes of `EntryPart` before and after; the listings of `EntityTable.Step` and of the callers of `InstallNew` and `RemoveOld` with no added call; the pinned bytes of a search and of a write; every test | done (S16R, e5d8d5e, with 4ecf3a2 after its review): every reader of a part reads `part.Stored`; `EntryPart.Field` and `RootReader.StoredBytes(LatticeField)` are gone, `RootReader.ReadText(in StoredField)` is added, and `EntryPart.Type` is read from `Stored`. `EntryPart` is 48 bytes, against 56 at the head of S16 and 32 before S16, by `Unsafe.SizeOf` on a Release build: it holds two references, the text comparison and the field, and the place of the part both in an entry and in a root. By reading, not measured, 32 would need the comparison taken from the field, a dependent load on every comparison of text, or narrower fields. In the JIT listing of a Release driver with tiered PGO off, on the workstation, `EntityTable.Step` (67 calls), `StepWithoutWalk` and the publication of an insert and a delete make the calls they made; the lookups that went take 1 to 4 calls each out of `WriteEntry`, `WriteText`, `HasKey`, `CompareRootText` and `SameKey`; the loop of `CompareKeys` steps by 48 bytes, and reading the type through `Stored` adds one instruction to a read of the type in `CompareKeys` and in `CompareKey`. The pinned bytes of a search and of a write are unchanged by construction, since no type of a search or of the record of a write changed, and the tests that pin them pass. Its figure is owed to the start of the machine of S17 (`index-writes`, `index-search`, `write-callers`), with the build before and the build after this commit among the builds; if a read line falls there outside the gauge, the commit is reverted before anything else is tried |
| L3 | `src/Moltaro.Lattice.Engine.Tests/Engine/Writes/ValueWaits` (`OpenAsync` with a held persistence step in `ClaimedValueTests`, `FreedValueTests`, `WaiterTests` and `WaitersAndChainsTests`; `Mebibyte` twice); `Engine/Indexes/Unique` and `Engine/Replication/Unique` (the leaves of an index read five times: `LeavesOf` three times, `TreeOf`, `Tree.Of`); `src/Moltaro.Lattice.Tests/Indexes/Unique` (`Profile` three times) | Helpers of the tests written several times | One helper in `ValueWaits`; one helper for the leaves of an index beside `Engine/UniqueSchema.cs`; `UniqueSchemas.Profile`. `ExpectOneValueAsync`, written twice, stays: the two differ in what they assert | The assertions and the count of tests | done (S16R, 0a61cbf): `HeldMaster` in `ValueWaits`, `IndexTree` beside `UniqueSchema.cs`, `UniqueSchemas.Profile`; the same tests, the same assertions |
| L4 | `Engine/Entities/EntityTable.cs` (`search.Progress = null; _epochs.ThrowForState();`, written twice) | One rule in two places, whose other two places `IndexSearch` already owns (`CheckRound`, `CheckMemory`) | A member of `IndexSearch` that does not return, called at both places. Both are cold blocks, but the change shrinks the code of `StepWithoutWalk` and of the entry into the protection | The listings of `Step`, `StepWithoutWalk` and `SetOperations.Next` | done (S18, `IndexSearch.RefuseForState`, 41b96de) |
| L5 | This backlog, the row of H4 | It said "taken (S15R)" for an entry that is done | The status corrected | — | done (S16R) |
| L6 | `src/Moltaro.Lattice.Engine.Tests/Engine/Replication` (`LoopbackLink.RunAsync`), `ReplicationNodes.OpenChildAsync`, and the bounds of the waits of `MalformedTransactionTests` | A failure of a socket in the setup of the link is swallowed as "the link ended", so the open of the child later fails with a message that does not name it; the open of a child does not go through `ListenFailure.ExplainAsync`; a bare bound says only that it timed out. This is all the architect found that could have made the one failure of `MalformedTransactionTests.Delete_whose_integer_key_is_outside…` in S16 unnamed: no assertion of the test depends on time, and no port is freed and taken again | A failure of the setup of the link rethrown as `InvalidOperationException` with `ListenFailure.Describe`, failing `DisposeAsync` as a failed callback does; the open of a child explained; each bound named | The assertions of the replication tests | done (S16R, c0cf571, with 8a6136a after its review): `LoopbackLink.RunAsync` rethrows a failure of the accept or of the connect before the test ends the link, `ReplicationNodes.OpenChildAsync` goes through `ListenFailure.ExplainAsync`, and `BoundedWait` names what a bounded wait waited for |

Left as they are, from this review:

- `ForwardEncoding.NameIndex` copies the properties of an exception by hand to give it
  the name of an index. An internal setter of `LatticeException.IndexName` would save the
  copy, on a path of refusals only, and a settable member on an exception is worse than
  the copy: refused.
- `ReadTimeInsideTheEngineTests.Time_limit_is_the_sum_of_the_steps_of_a_pass_and_fails_it_though_no_step_reaches_it`
  cannot be deterministic on the clock of the machine: a stall counts as engine time, and
  the test controls only the lower bound of a step. Making it so changes the engine and
  how the test judges, which is not refactoring: a task of S18 in the
  [document of Epic 1](epic-1/README.md). A form that only widens its constants is not
  taken. Until then the test stays as it is, is not skipped and not loosened, and a
  failure of it under load is kept with its output and is not a finding of the sprint
  that met it.
- `MalformedTransactionTests` also passes when the connection dies before the forged
  frame reaches the child. Closing that is a new assertion, not refactoring: the test
  author of S17 writes it.

Its questions for S17 went into the study of that sprint: one write a member of several
chains, where a pending write has one pair of links and there are now two ways to wait for
another write, the chain and the wait for the holder of a value; one change for each
record of a write; an insert, which is installed live with its final stamp before the
position; a search that ends at its entity meeting two entries of one value during a swap;
a child that would refuse a valid swap.

## Found while measuring S16, 22 November 2026

| # | Place | What is wrong | The simpler form | What it must keep | Status |
| --- | --- | --- | --- | --- | --- |
| K1 | The preparation and the upkeep of an insert and a delete of a type with indexes and none of them unique: `Engine/Entities/ChangePublisher.cs` (`TryReserve`, `TryReserveWithIndexes`), `Engine/Entities/EntityTable.cs` (`TryFind`), `Engine/Writes/PreparedWrite.cs`, `PendingWrite.cs` | In the start of S16 an insert and a delete by name of the fixture's users, one caller, `write-callers`, run at 0.984 of 57be01b, the gauge 0.990, and every run of the two builds of the head is below the runs of the three builds before: 157.7 k to 163.8 k a second against 162.5 k to 166.6 k. What is specific to the users is three text indexes, ByLogin, ByEmail and ByRegionAndLogin, none of them unique, so the check of a unique value does not run. The same sprint makes the inserts and deletes of `index-writes`, Int64 indexes through the class, 7 % faster. A control on the workstation, the builds step by step, scattered too much to show a cause. The candidates are the 16 bytes added to the record of a write, the load G2 moves into the lookup, and the split of `TryReserve` | Not known until the cause is: the line on the machine, the builds of the sprint step by step | Every guarantee of S16; the size of the record a write needs | closed on 23 November 2026, below. Counted by the JIT listing of a Release driver over the public API, with `DOTNET_JitDisasm` and tiered PGO off, on the workstation (S16R): the users of the fixture inserted and deleted by name, at 57be01b and at 5d27900. (1) `ChangePublisher.TryReserveWithIndexes` makes 16 calls in 972 bytes instead of 20 in 1,264, its frame 144 bytes in both; `TryReserve` makes 5 calls in both, 226 bytes instead of 222; `InstallNew` 3 calls in 157 bytes instead of 4 in 194 and `RemoveOld` 5 calls in both, 218 bytes instead of 216, each with a frame 8 bytes larger; `StampDelete` and `Record` are inlined in both and `ChangeAt` is a call in both, so no call stopped being inlined. (2) The descent of an insert (`OrderedTree.Insert`, `Descend`) and `IndexOrder.CompareKeys` and `CompareTexts` make the same calls in the same bytes, but that `CompareKeys` steps over the parts by 56 bytes instead of 32: `EntryPart` grew from 32 to 56 bytes by the stored field at its end, and no offset moved. (3) `WriteExecutor.TryPrepare` makes the same 37 calls, 1,655 bytes instead of 1,622, its frame 280 bytes instead of 264: the replaced root `EntityTable.TryFind` gives lies in the frame as an out value and goes to `TryPrepareDelete` as one more argument, which puts one more of its arguments on the stack, and the key the preparation copies lies 8 bytes later in the record of the write; `TryPrepareInsert` and `TryPrepareDelete` are calls in both, 21 and 14 calls, 802 and 534 bytes instead of 785 and 527. (4) Not identical, as expected: `EntityTable.TryFind` makes 6 calls in both, 666 bytes instead of 639; `WriteCoordinator.Prepare` 5 calls in both, 213 bytes instead of 206; `TryPrepareTwice` inlines the check of a taken value, 8 calls in 415 bytes instead of 4 in 191, of which only the test of `HasUniqueIndex` runs for these users, and `TryReserveWithIndexes` reaches `TryReserveChecked` only for a type with a unique index; `MarkCompleted` 7 calls in 233 bytes instead of 6 in 208, the new call `ReleaseWaitersOf` behind the test of an empty list. No call is added on the path of these users, and the listings name no cause. After L2 (e5d8d5e) the listings of (1) are those of 5d27900, and `CompareKeys` steps by 48 bytes, its offsets 8 bytes lower. The cause is decided in the start of the machine of S17 by the builds 57be01b, e79a710, 138215c, 2da3bf7 and 5d27900, with L2. In the start of S17 (the rented ARM64 machine, five rounds) no step of S16 separates from the gauge, and S16 as a whole runs at 0.962 of 57be01b, below 1 in every round; the five trees ran adjacent and in one order, which the next start rotates. With part 1 of S17, which costs 2 to 6.5 % on writes that reserve in a secondary index, the entry is the path of such a write as a whole (the planner, 23 November 2026): listings at 57be01b, 5d27900, b9a9074 and ed81c76, kept outside the repository, count what it pays. They were taken again on 23 November 2026 at every step of S16 (57be01b, e79a710, 138215c, 2da3bf7, 5d27900) and at b9a9074, each driver built alone and its engine compared with the build's, since a driver whose folder held the output of another build could resolve that build's engine: the counts (1) to (4) stand, and the growth now shows step by step. F6 (e79a710) changes none of them. G2 (138215c) makes `TryReserveWithIndexes` 15 calls in 920 bytes, `InstallNew` 3 calls in 157, the frames of `InstallNew` and `RemoveOld` 8 bytes larger, `TryPrepare` 1,655 bytes with its frame of 280, `TryFind` 666 bytes and `TryPrepareDelete` 540. The unique indexes (2da3bf7) make `TryReserveWithIndexes` 16 calls in 972 bytes, `TryReserve` 226, `TryPrepareInsert` 802, `TryPrepareDelete` 534, `Prepare` 213, `TryPrepareTwice` 8 calls in 393 and `MarkCompleted` 7 calls in 233; the last commit of the sprint (5d27900) makes `TryPrepareTwice` 415 bytes. Counted by the rule of the record of S17, part 2 in `epic-1/sprints/s17.md`, an insert into one Int64 index makes 21 + n calls at every step of S16 and at b9a9074, n being the nodes refilled, and a delete from G2 on makes two fewer than before G2, the two calls of `RootTable.Entry`, whose root the lookup now gives; so S16 adds only cold code to a write into an Int64 index, and part 1 raises the insert to 34 + 3n. It closes when a change brings the lines back within the gauge of 57be01b, or when the count shows what the two sprints bought and the planner accepts it with the figure. Closed on 23 November 2026, by that rule, in the start of the machine before the close of S17, with its trees rotated: the head of part 3 against 57be01b 0.990, within the gauge, none called (every ratio in the record of that start in `epic-1/sprints/s17.md`); the 0.962 of the start of part 2, with the trees adjacent in one order, did not reproduce. The steps of S16 were not separated, because the listings count them and the entry closes on the head against 57be01b. What the path of a write with several indexes still pays after S17 is K2 |

## Found while building S17, part 3, 23 November 2026

| # | Place | What is wrong | The simpler form | What it must keep | Status |
| --- | --- | --- | --- | --- | --- |
| V1 | `Replication/Transactions/TransactionAssembler.cs` (`_large`), `Replication/Load/TransferBuffer.cs` (`Clear` keeps its pieces) | A transaction of several frames is collected in one buffer of the connection that grows to the largest transaction once and keeps its pieces, so after a transaction of B bytes a child keeps B managed bytes, outside its memory budget, for the life of its connection. A write batch has no limit of its own, so neither has that room: measured on the workstation, a batch of the fixture's users through a child left in the child's buffer 196,608 bytes for 1,000 users (a request of 172,006 bytes), 1,638,400 for 10,000 (1,720,006) and 15,925,248 for 100,000 (17,200,006); a batch committed on the master leaves the same in every child | The buffer returns to its ordinary size after a transaction above a threshold, the simple form to try first | A transaction of one frame handed out where it lies; an ordinary stream that allocates nothing more; the check of every frame | joined (S33, the bounded delivery of R30) |
| V2 | `Storage/Text/TextTable.cs` (`_slots`, `_buckets`) | The master's text table keeps the managed capacity a preparation grew it to. Found by the breaker of S17, part 3: its managed bytes go from 416 to 10,624 after 200 new texts and stay there, after a batch that is refused as after one that is committed and deleted again, while its entries, its texts and the bytes of the arena are back as they were. No guarantee of S17 addresses it; a write batch has no limit of its own, so neither has that room | The slots and the buckets return to the room the live texts need when they fall well below it, with the shrink of S23 | A write whose text is in the table allocates nothing; the table is used only by writes, under the lock of the writes | joined (S23) |

## Found while measuring S17, 23 November 2026

| # | Place | What is wrong | The simpler form | What it must keep | Status |
| --- | --- | --- | --- | --- | --- |
| K2 | The write path of a type with several indexes after S17: `Engine/Entities/ChangePublisher.cs` (`TryReserve`, `TryReserveWithIndexes`, `TopUpIndexes`), `Engine/Indexes/NodePool.cs` (`TryReserve`, `TopUp`, `LeftAfterMove`, `Trim`), `Engine/Indexes/OrderedTree.cs` (`TryReserve`, `AfterPublication`) | S17 costs a write that reserves in a secondary index. By listing, an insert into one Int64 index makes 25 + 2n calls at the head where it made 21 + n at b9a9074, n being the nodes refilled, the same on x64 and on ARM64 call for call. By ratio, the head against b9a9074 on `index-writes` down to 0.910; the ratios of part 1, of the bound by levels with what it bought, and of the write path, which bought nothing this method can see, are in the table of falls of the record of the start of the machine before the close of S17 in `epic-1/sprints/s17.md`. The second line: part 2's insert that grows the primary index, 2 to 4 % by the start of part 2, owed, since no series of the last start grew the primary index; the next start carries `read-by-key-threads` for ed81c76, 3e24ce4 and the head, the trees rotated. The third line: part 3's `unique-writes` line, 0.953 against f6d9cf3, not explained by its listing, run again in the next start with the machine's listings (the same record) | Not known. The question to the architect's review after S17: what of the cost per index and per reservation can go without giving up the bound by levels or the check on the final state | The bound by levels: a batch whose take fits is not refused for its bound, and a refused or withdrawn reservation gives back exactly what it took; the check of unique values on the state a publication leaves; every guarantee of S17; no added load on a read | closed on 24 November 2026, after the start of the machine for S18 (its record in the [epic](epic-1/README.md)): the first line by `kd`, taken as a change of the light track of its own (four lines of `index-writes` 1.017 to 1.060, none falling), and applied as recorded below; `kbc` refused (3.1 to 5.4 % on six lines); the re-inlining of `NodePool.AfterPublication` not needed; the second line closed, part 2's growing insert not standing with the trees rotated; the third line closed, part 3's line not reproducing rotated. The reported fall while the primary index grows was carried as K3 |
| K3 | The writer that inserts while the primary index grows, `read-by-key-threads` | 1.3 to 2.9 % slower at 6102bbe than at ed81c76 on four lines, called at three or four of five rounds, never at five; the same against 3e24ce4, so it arrives after part 2 of S17: in part 3 of S17, S17R or S18. No target is missed (the targets of writes G3 and G6) | The workstation listings at ed81c76 and 6102bbe and the rotated ARM64 run across 3e24ce4, 6b968b3, 8dc0e16, 6a4fb98, 6f50194 and 6102bbe | Every guarantee of S17 and S18 | closed in the ARM64 follow-up; the earlier fall did not repeat in five rounds across six trees |
| K4 | A replace that keeps all values, `full-form-users` | The ordinary and middle forms change ByLogin uniqueness and Dictionary storage of Region together. The combined cost repeated on ARM64. | Compare the four combinations on one release tree using the existing unchanged-replace series. | The check of unique values, Dictionary sharing and identical users and index shapes. | closed; [ARM64 factorial](#k4-unchanged-replace-factorial) |

**K2, `kd`** (24 November 2026, light track, coordinator 9). The measured patch was
carried unchanged to the current engine as `82b7e48`. It skips divisions in `Need`,
`Bounds` and `ProgressOf` when the result is already determined. The independent review
checked the cumulative ledger arithmetic, the hardest part of the change, including
rewrites and overlapping keys during publication. The bound is recorded in
[the index design](../design/ordered-index.md#the-reservation). The focused Debug tests
passed all 88 cases. The coordinator's Release suite passed 3,291 tests with 129 skipped.
Build, format and all 16 samples passed. The applied patch and the gauge ran against its
parent on the rented machine. [ARM64 follow-up](epic-1/sprints/s18r-arm64-follow-up.md#arm64-follow-up-after-s18r)

**K3 workstation listings** (24 November 2026). The x64 Release driver used the
fixture's insert by name with 1,000 places remaining, the same preparation as the timed
scenario. At `ed81c76` and `6102bbe` the primary index grew from 4,457,536 to 8,913,984
bytes. `PrimaryIndex.Grow` has the same instructions and 581 bytes at both ends.
`TryPrepareInsert` has 21 and 23 call sites, and `PublishBeforePosition` has 7 and 12.
The rotated run across the six trees is in the
[ARM64 follow-up](epic-1/sprints/s18r-arm64-follow-up.md#arm64-follow-up-after-s18r).

## Architect's review after S18, 24 November 2026

Read at `main` 6102bbe, nothing run. S18 landed in the right folders and the direction of
dependencies holds: `Storage` uses nothing of `Engine` or `Replication` in code, `Roots`
uses `Values` and not the reverse, and `ExportedValues` in `Storage.Roots` follows that
order. The debt is of one kind: a tree is descended by hand in eleven places of the
product, two of them the export's; `NestedValues` holds two jobs; and the rules of the
stored form of a tree live in five heads of types with no design section that owns them.
Gone through by the planner and the coordinator on the same day. Found sound and left: one
stored form for a root and a record with `SharedPlaces`; `StructureWalk` with struct
visitors; `ValueHolder` as the one placement of bytes; `PackedSource` apart from
`ValueSource` with its figure; `IRecordSource` with two real sources; `RecordBlocks`; the
check of `BlockImport` before its placement, one `CheckStructure` for a root and a record,
the rule of inline values on their locations; the rule of pending descriptors as a design;
the allowlist through one member of `IndexValue`; `ValueBound` in one place; the second pass
of `ClassCopier` with its figure; `ReadSettings`. Left with their reasons: the three loops
over `RecordsPerChunk` (two lines each over a fixed rule of the format); `RootWriter.Start`
beside `NestedValues.StartRecord` and the typed switch of `RootValues` beside the boxed one
of `NestedValues` (merging puts a chain of type tests on every scalar field of every write);
the three visitors of `RootStorage` with the same arms (on the publication and the free of
every write; S19 decides with its kinds); `ValueHolder`'s `Arena` and `Layout`, null in two
of three uses. Not examined: `docs/design` and the README beyond names, the samples, the
scenarios, most tests of S18 beyond the audit and the model.

**S18R, the order** (set by the planner on 24 November 2026): Z3, Z8, Z6, Z7, Z2 (a) to (d),
Z4, Z1, and Z5 last. One commit per entry and one narrow independent review under the
[refactoring track](process.md#the-architects-review). Z1 and Z4 lie on measured paths.
Their workstation listings are in the [S18R close-out](epic-1/sprints/s18r.md#s18r). The
`following-children` series is in the
[ARM64 follow-up](epic-1/sprints/s18r-arm64-follow-up.md#arm64-follow-up-after-s18r).

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| Z1 | `Replication/Load/BlockExport.cs` (`RecordsLength`, `WriteRecords`, `RecordDescriptor`, `RecordOffset`); `Storage/Roots/StructureWalk.cs` (`WalkBlock`), `IStructureVisitor.cs` | The export repeats the walk's descent in two recursions, because it needs a block before its records and the walk has no such step (Y2) | One member on `IStructureVisitor`, called by `WalkBlock` before the records of a block; the export's two recursions become two small visitors through `StructureWalk.WalkValue`; the root level of the export as it is; the other visitors, the audit's included, get an empty member written out, not a default member of the interface, which would box a struct | The bytes of the wire, and `Length` equal to what `Write` writes, by a comparison of the old and the new export over trees with both forms of a List at depth, and the suites of children and forwarding. The listings of `StructureWalk.Walk` over the publication, the free and the retirement with `ChangePublisher.Install` and `RetiredMemory.Free`, and of `BlockExport.Length` and `Write` of a scalar root with `TransactionEncoder` and `Capture`, which must be identical; `following-children` measured | done (S18R) |
| Z2 | `Storage/Roots/NestedValues.cs`, `ListRecords.cs`, `PackedOwnedValues.cs`, `RootReader.cs` | One class converts the form of a record and also places, links, fails and frees the blocks of a List of records, and its sizing must agree with its writing with nothing to check it | (a) a Debug assertion after each fill that a block not refused ends at its last inline value; (b) the rule of inline values once, in one private member; (c) the read side takes a structure's own bytes and the payload of its block, as `BlockImport.CheckStructure` does; (d) `StartRecord` in one frame of offsets. (e), the placement of a List of records as a type of its own, refused: it adds a type and removes no line; open to the study of S19 if its placement needs it | Every guarantee of S18; the listings of `RootValues.Write` (frame 328) and `BlockImport.PlaceValues` | (a) to (d) done (S18R); (e) refused |
| Z3 | `Moltaro.Lattice.Engine.Tests/Engine/Indexes/Reservation/LevelBoundTests.cs`, `ManagedAllocationCollection.cs` | A test measures the allocation of its thread outside the collection made for that, so a garbage collection inside its window moves the counter: the cause of the failure seen once in S18, part 1 | The one fact in a class of its own in the same folder, in `ManagedAllocationCollection`; not the whole class, whose theories build large trees | No assertion changed | done (S18R) |
| Z4 | `Replication/Load/BlockImport.cs` (the head, `LeavePending`), `ReceivedRecords.cs`; the comments of `StructureWalk`, `RootStorage` and `NestedValues` that point to it | The rule that a descriptor of kind Null with a length names nothing yet lives in the import and is relied on by `Storage` | The rule named once in `Storage` (`ValueBlocks`: write a pending descriptor, ask whether one is pending) and stated in the head of `StructureWalk`; the import keeps only which received descriptors become pending | The tests of loading, forwarding and the audit; the listings of `BlockImport.Check`, `Place`, `PlaceValues` and a child's `TryApply`; `following-children` measured | done (S18R) |
| Z5 | The heads of `StructureLayout`, `RootLayout`, `BlockImport`, `BlockExport` and `NestedValues`; `docs/design/arena-protocol.md`; stale comments | The stored form of a tree is told in five heads, each pointing to the others, and some comments are stale or cite working documents | One section of `arena-protocol.md` (structures and records, the frames of offsets, the wire in preorder, the rules of the check, pending descriptors), each head to about twenty lines pointing to it; `RootDescriptors` (users it no longer has), `IStructureVisitor` after Z1, `RootReader` ("holds no String or Binary value"), `DescriptorKindEnum` ("in the root block"; a brief cited), `StoredTypeEnum`, `ClassBinder`, `SetOperations`, the heads of tests S18 touched that cite briefs; and where the stored form says bytes under a null bit are zero, that they are zero as a writer of this node writes them, a received root may hold any bytes there and nothing reads them (`docs/decisions/replication-and-hosting.md`, "Trust between the nodes of one database") | Comments and documents only | done (S18R), last |
| Z6 | `Moltaro.Lattice.Tests/Indexes/Model/Unstored` (`UnstoredPartSearchModelTests`, `UnstoredSchema`) | The names say the parts are not stored, and since S18 they are (Y1) | The folder `ReferencesAndPaths`, `ReferenceAndPathSearchModelTests`, `ReferenceAndPathSchema`; the heads lose "the part the engine stored last" | A move: the count of tests and of skips | done (S18R) |
| Z7 | `Access/Binding/ClassBinder.cs` | Whether the record holds a value as the property holds it is asked twice, as a test of types for a List and as a test of a delegate at run time in `ArrayOf` | One test, made when the class is bound, for both; `ArrayOf` loses its test of the delegate | The tests of binding and of copies | done (S18R) |
| Z8 | `Engine/EngineDatabase.TestAccess.cs` (`Counters`, `TextReferences`, `IndexNodes`, `ReservedNodeBytes`, `ForEachLiveRoot`, `DrainRetired`) | Six members repeat one frame: the state checked, the barrier, the parts, the lock of the writes | One private member that takes a function of the parts | Tests only | done (S18R) |

**The tests of the search by index that the model subsumes**, decided by the planner on 24
November 2026: not part of S18R, since a refactoring sprint removes no test, but a change of
the light track of its own after it. They go now, in a commit of their own whose
message names, for each assertion removed, the test of the model that holds it, the narrow
reviewer checking that list: `RangeOfEveryFamilyTests.Range_with_inclusive_and_exclusive_bounds_at_the_ends_of_the_type_...`
(15 cases), `EnumAndFlagsTests.Enum_and_Flags_parts_are_in_numeric_order_...` and
`FloatingPointKeyTests.Whole_index_of_floating_point_values_...` (2 cases). They go after an
addition to the model, made first as a commit of tests: `FloatingPointKeyTests.Exact_search_...`
(the model searches one of 0 and -0, not each) and `FloatingPointKeyTests...as_an_inclusive_or_exclusive_bound...`
(the model bounds on -infinity and the zeros only by chance). They stay:
`OneValueEveryWayTests`, its second theory (it also reads each entity by key through a class
and by name for every scalar type at its ends); `IndexPartValueTests`, whole (values of each
type given boxed, both spellings of one key, an Enum over Int32); and
`RangeOfEveryFamilyTests.Bound_given_in_another_type_...` (`AllowlistTests` holds one foreign
bound, forward only). A test with one assertion the reviewer finds without a holder stays.
It needs no word of the owner: a reviewed commit that removes a test is brought back by a
revert, and the rule that a deletion waits for him is about what cannot be brought back;
the change is reported to him in its record.

**The index-test cleanup** (24 November 2026, light track, coordinator 9). The first
commit, `660e53b`, added nullable Float32 and Float64 model rows with both infinities,
distinct NaNs, both zeros and null stored together. `7b7cc4b` then removed six legacy
floating-point rows and named the holder of each assertion. The model checks exact
searches and all four one-sided bounds for each special value, and walks the whole
index in both directions. `IndexModel.Expect` checks the returned identities.
`IndexModel.ExpectStored` compares the raw floating-point bits for each identity,
including the zero sign and each NaN representation. These cases run by name and
through a class, on the master and on a child.

The planner kept all 15 rows of `RangeOfEveryFamilyTests` for the joint behavior of
two bounds at stored extrema and the intermediate value. The numeric-order method of
`EnumAndFlagsTests` also stays for the bounded range through unnamed combinations of
Flags, including `(1, 5]`. The remaining bound, boxed-value and read-by-key cases kept
their coverage. The focused model and value-binding run went from 224 to 220 passing
rows, two added and six removed.

Independent review took one round. The hardest part was tracing exact searches and
bounds through their separate binders to the checks of identities and stored bits.
The full Windows x64 Release suite passed 3,287 tests with 129 skipped. Build, format
and all 16 samples passed.

Y1 and Y2 of "Found while building S18" are closed into Z6 and Z1.

## Architect's review after S18R, 24 November 2026

Read at `main` `5635f74`. The shared descent serves both the wire order and the
ownership walks. Storage owns the pending descriptor rule, class binding chooses the
List conversion once, and test access keeps the same order for readiness, validation
and the writes lock. The planner joined all three entries below to the S19 changes
that already touch these paths. The first structural commit and the Acceptance of S19
stand as written in its brief.

| Place | Simpler form | What it must keep | Status |
| --- | --- | --- | --- |
| `RootReader.Counting` | Count each record block payload once in `EnterRecordBlock`. Count the directory of a chunked List once in `Visit`. Do this with `EntityBytes` for Map and Set. | The byte sums for an S18 tree and both forms of Map and Set against the independent audit, including `ReadMemoryLimit`. Keep `index-search` on the scalar schema as the control and compare a search with nested records and an active memory limit in the S19 measurements. | done in S19. The ownership audit and read-limit boundary passed. The nested search with an active limit was 0.9892 of the base median, within its gauge. |
| `ClassField.CopyNested`, `RootValues.Value`, `RootReader` | Use one internal read path through `NestedValues.Read` for owned values, List, Map and Set as collection reads arrive. | The same values and independent copies by name and through a class, including null and empty collections. Keep property conversion in the same stage and compare the scalar read listings. | done in S19. Both API suites passed. The x64 scalar class copier was 1,398 bytes against 1,516 in the base listing. |
| `BlockExport.Write`, `WritingRecords.EnterRecordBlock` | Share the rule that prepares an outgoing descriptor in the root and in a record when the wire uses the collection's entries descriptor. | Byte-for-byte compatibility for the existing forms, their inline offsets and the output order. A Map or Set sends its entries without local addresses or the lookup table. | done in S19. Twelve full UTF-8 and UTF-16 bodies matched byte for byte, and collection transfer passed with local lookup rebuilt on receipt. |

The [S19 historical comparison](epic-1/sprints/s19.md#s19-arm64-results) also measured the
two S18 receiving series that were still owed. With two following children, the first
repeat was 0.9618 of the base write rate. For an ordinary 100-byte forwarded replace,
the second repeat's write p50 was 1.0751 of base. Both met the rotated rule in the
named repeat. The listings identify extra checks and a further placement call level
without separating their costs. See the [review after S19](#s18-receiving-lines).

## Architect's review after S19, 24 November 2026

Read at `main` `7c6e09af`. The planner and coordinator joined the five entries below to
the sprints that already change their paths. None requires a separate S19R. The shared
publication and position path, the storage dependency direction, the checked wire and
resident key sources, and the shared comparer, hash and probe are sound. The audit keeps
its own checks of ownership, descriptor offsets and lookup shape.

| Place | Simpler form and reason | What it must keep | Status |
| --- | --- | --- | --- |
| `RootStorage`, `StructureWalk`, `LookupBlocks` | Keep one rule for walking the physical form when Q49 changes what a descriptor owns. Share classification where it replaces repeated code and keep lifecycle actions explicit. Bring the List-only comments up to date with the arena protocol. | One walk per lifecycle action, holder offsets, children before owner, and no early or double release. Check a protected old root, ownership and collection byte audits, rollback and child publication. Compare the scalar write listings and fixture bytes. | joined (S20), which changes ownership |
| `NestedValues`, `ValueHolder`, record and collection sources | Use the existing record and collection entry sources. Reuse the resident placement tail and rollback where a new source would otherwise repeat them. Splitting `NestedValues` by size alone still adds a type without removing work (Z2e). | Null and empty states, comparer, uniqueness and order, compact and hashed forms, and rollback of descriptors, counts and the inline boundary. Check the collection form, validation and receiving tests, bytes and the write lock interval. | joined (S20), which adds a source over stored blocks. Z2e remains refused. The earlier `Admit` entry remains one item. |
| `RootReader.FindEntry`, `CollectionKeys`, `CollectionLookup` | Reuse the local lookup for nested protected views when there is a second caller. | Use the actual holder payload under the same protection, with the same comparer, seed and scoped ordinal. Check both local forms and zero managed allocations for the lookup. | joined (S21), which adds the view that S22 uses |
| `CollectionLookup.IsChunked`, `LookupBlocks` | Take physical shape from the allocation once spare capacity or shrink lets count and shape differ. Today a new hashed lookup is built for its count and an unchanged lookup is reused whole. | Slot and holder backlinks and offsets, hash and ordinal slots, children before owner and coherent reads during movement. Audit after churn and compare bytes at the shape thresholds. | joined (S23), when relocation and capacity can change shape independently |
| `BlockImport`, `SkippedCollections`, `CollectionRecords` | Inventory owners, limits and lifetimes of import scratch, temporary pools and lookup blocks before allocating the budget. The native lookup already belongs to the arena and is counted there once. | Distinguish instance retention from temporary peak managed memory, clear wire slices before returning arrays, and keep malformed and duplicate refusal before publication. | joined (S24), which assigns the memory budget |

### K4 unchanged replace

The `e96650c` base and gauge compared Plain Region with no ByLogin mark against
Dictionary Region with the mark. The full form also adds Groups. The results give no
separate cost for either switch. On an unchanged key `SetsValue` skips the unique owner
lookup, so the combined figure does not identify an owner lookup cost.

S20 prepares the existing unchanged-replace series in all four combinations on one
fixed Release tree, with the same generated users, digest, indexes and inputs. Rotate
the four forms and balance the class and by-name row order in each. Check the schemas,
trace and rotation on x64. In the first suitable independent rental, use the same ARM64
platform for the four forms and report each switch with the other switch off and on,
and their interaction, for both API forms. The receiving queue is the S21 protected
view comparison of Replace during a held view and Replace without a view, if that queue
runs on the same ARM64 platform with five minutes available for K4. Otherwise carry it
to the first equivalent S22 queue. There is no separate rental for K4.

The Windows x64 preflight at `7c6e09af` passed the four compiled schemas, both
unchanged-replace paths and their complete input traces. The same seed 42 gives all
forms the same 100,000 users. Each warm-up visits the once-ordered fixture and each
timed row visits it ten times. The plan balances all 32 visits across form, order
position and first API. One timed smoke round exercised all eight rows. Its timings
verify the runner only. The checked kit and preflight archive are kept outside Git.
The ARM64 runner estimates three minutes timed and five minutes with build and checks.
The fixed S19 tree and balanced write-only series isolate the costs there. The earlier
`e96650c` combined ratio is historical context for this comparison.

### S18 receiving lines

Keep the [two historical comparisons](epic-1/sprints/s19.md#s19-arm64-results) separate. The
first in-process two-child repeat reached 0.9618 of base writes/s. The second ordinary
100-byte forwarded replace repeat reached 1.0751 of base p50. Each met the rotating
rule in that repeat. The old listing identifies extra checks and another placement
call level. Delivery, scheduling and application also enter each end-to-end result.
The forwarder already checked the input before admission and again when placing the
retained raw input on the historical base. Dropping a check or retaining a checked
tree through queueing would alter the validation boundary or add state without an
isolated figure, so neither change is taken.

Keep ordinary replace in the S20 control. If a new signal warrants attribution,
estimate focused comparisons separately. Use a no-child master control beside the
first two-child row and master-write application latency beside the forwarded p50.
For an attribution to S18, use exact `6f50194`, its independent gauge and `6102bbe`
with behavior-equivalent check or placement controls and their ARM64 hot listings.

The S19 `PublisherScratchTests` timeout happened after both publication holds were
released. The master had committed the forwarded 5,000-member batch, and the child
missed the ten-second local application wait. The isolated base and head reruns and
subsequent full runs passed. Keep the unchanged test in the S20 publication checks and
report any recurrence or evidence that explains it. The coordinator's three isolated
Release runs on the S20 baseline passed as well. The later S20 Debug recurrence and
two separate outer waits go to [S20I](epic-1/sprints/s20i.md#s20i--investigate-the-debug-timeouts)
by the owner's disposition. S20 keeps the unchanged test in its final publication check.

## Found while measuring S19, 24 November 2026

**Open, scalar `index-search` writer.** Beside eight typed readers taking the first
match of shared Int64 keys, the writer fell from 273,181 to 264,344 writes/s on
ARM64. The reader change was within its gauge. An x64 control restoring the old
ownership walk gave no consistent recovery within workstation variation. Inlining
of `WalkStructure` remains a hypothesis. [Measurements](epic-1/sprints/s19.md#s19-arm64-results)

Use the first independently justified rental. S20 checks publication locally and in
ARM64 ordering CI, and its encoded size is deterministic. The receiving candidate is
the S21 held-view Replace comparison on the same platform with room for this control,
then the first suitable S22 protected-view comparison. No rental is booked. The original
ARM64 archive has timings but no hot listing. The saved tiered x64 listing comes from
a serial driver and does not establish the call active in the ARM64 timing window.

In a separate diagnostic invocation, identify the code tier and the ownership call
active in the original concurrent scenario under its original timing and warm-up. Use
five rotated rounds of that writer and reader series, `e863f688`, an independent build
of it for the gauge, and `3066ea63`. Add a scratch `3066ea63` with `AggressiveInlining`
on `WalkStructure` only when the original base and head have that hot call difference
and the hint changes it in the scratch without another hot-path change. Recheck the
active code if the row is extracted for timing. A gain or fall requires a median
outside the gauge band and at least three paired rounds on that side. Attribute the
fall to the call only if the variant recovers to the gauge and the hot listings match
the timed code. Otherwise the five rounds check the effect and the hypothesis stays
open. There is no separate rental for this control.

## Found while reviewing S19 after its merge, 24 November 2026

Read at `main` `7c6e09af` by an independent review of the merged sprint. The coordinator
checked the three remarks against the source and the archived ARM64 reports. The
thresholds in the S20 Acceptance stay in force.

| # | Place | What is wrong | The simpler form | What it must keep | Status |
| --- | --- | --- | --- | --- | --- |
| R1 | `src/Moltaro.Lattice.Engine/Storage/Roots/CollectionLookup.cs` (`MostCompactEntries`) | The [S19 lookup results](epic-1/sprints/s19.md#s19-arm64-results) compare compact at 8 entries and hashed at 9. Shape and count change together. The reported clock frequency gives a nominal 1 ns tick, and many percentiles have a visible 40 ns step. The first small hit rows have long tails of unknown cause. | Compare both forms at the same count, key type and hit or miss pattern for 4, 8, 9 and 16 entries. Warm each form, time a batch of probes together, and record native bytes and creation cost with the read results. Put the figure beside the constant when it supports a decision. | The S20 transitions remain compact to hashed from 8 to 9 and hashed to compact from 5 to 4. A new threshold needs a separate decision. | [measured with S21](#r1-matched-lookup-and-rotated-write-intervals), thresholds kept |
| R1, write-lock series | S19 `map-write-lock` | At 100,000 entries the admission interval starts at about 41 ms and ends at 17 to 19 ms in each run, with lookup around 11 ms. All three runs use the same size order. The run does not establish steady performance by size or identify which work caused the change. | Use a separate series with each size warmed and size order rotated. Keep lookup time beside admission and whole-write time, and keep raw rounds in order. | Equivalent inputs, placement and instrumentation across sizes. Establish a cause before attributing the change. | [measured with S21](#r1-matched-lookup-and-rotated-write-intervals), order effects remain |
| R2 | `src/Moltaro.Lattice.Engine/Storage/Roots/PackedMap.cs` | A packed Map boxes its key and value in the owned copy and whole write paths. The S19 `79,112 B` figure is the entire Int64 Map owned read plus lookup at 1,000 entries. The protected lookup allocates zero. The whole replacement figure includes validation and placement as well as boxing. | Use the existing type-bound approach of `PackedSet` for the fixed pair where it fits. Measure the same owned read plus lookup and whole write before and after. | Independent owned copies with declared types, key spelling and comparison, the existing checks of null, duplicate and bound, and no reflection on the call path. Map enumeration order is unspecified. | done in [S20R](epic-1/sprints/s20r.md#s20r--refactor-packed-map-scalar-pairs). [ARM64 measured with S21](#r2-fixed-scalar-pair-comparison) |
| R3 | `src/Moltaro.Lattice.Engine/Storage/CollectionWriteTimings.cs` and the `LATTICE_COLLECTION_TIMINGS` sections in `WriteCoordinator.cs`, `NestedValues.cs` and `CollectionLookup.cs` | The default build excludes the cells. The source still places measurement code in the write path. The comment describes the special synchronous build that reads the cells on its calling thread. | After the affected-head control, remove the conditional product insertions and the obsolete scenario wiring from the current build in one commit. Exact lock timing needed by a later study belongs in its isolated measurement build. | The historical `3066ea63` build and archive remain reproducible. The default build has no timing cells or calls. | done in S20: affected-head control at `0f988e6`, removed in `c2aa7c5` |

Both R1 preflights passed in Windows Git Bash in S20, on `3066ea63` with the isolated
measurement variants. The lookup smoke matched both resident forms and both key types
at 4, 8, 9 and 16 entries. It checked paired digests, hit and miss traces and zero
managed allocation in 64 probe windows. The write smoke checked nested intervals and
rotation on 1,000, 2,000 and 4,000 entries. The portable kit and full logs are archived
outside Git. The ARM64 estimates are 30 to 65 seconds for lookup and 50 to 80 seconds
for write lock, plus builds. The S21 queue reserves two minutes for each. It uses the
predeclared paired and stationarity checks. The x64 results verify the recipe. Access
to the rental follows the existing checked-queue and owner activation procedure.

## S21 ARM64 follow-up

The five queue rows ran on the same ARM64 platform. The repaired R1 and K4
kits kept the original sources by Git identity after line-ending normalization
and used the same patches and rotation plans as the x64 preflight. See the
[run record](epic-1/sprints/s21.md#arm64-results). The independent measurement review
checked the raw and paired results.

### R1 matched lookup and rotated write intervals

The lookup compared the two shapes at equal counts in eight balanced rounds.
The table reports median ratios of hashed to compact time, and the native
retention of each shape above the same empty Map. Creation times include the
whole replacement of a prebuilt input.

| Entries | Key | Hit time | Miss time | Creation time | Compact / hashed bytes |
| ---: | --- | ---: | ---: | ---: | ---: |
| 4 | Int64 | 2.425 | 1.357 | 2.564 | 64 / 192 |
| 4 | Text | 0.847 | 0.322 | 1.269 | 176 / 272 |
| 8 | Int64 | 0.755 | 0.228 | 1.752 | 160 / 304 |
| 8 | Text | 0.483 | 0.145 | 0.742 | 320 / 464 |
| 9 | Int64 | 1.491 | 0.482 | 0.991 | 176 / 336 |
| 9 | Text | 0.462 | 0.159 | 1.143 | 368 / 528 |
| 16 | Int64 | 0.574 | 0.132 | 0.759 | 288 / 528 |
| 16 | Text | 0.579 | 0.087 | 0.676 | 608 / 848 |

All probes had zero managed allocation. At eight entries the Int64 hit pairs
spanned 0.473 to 1.654 and Text hit pairs 0.451 to 2.123. Both favored the hashed
shape in six of eight pairs, with the direction changing by count position.
Several miss rows favored the hashed shape in every pair. Every matched hashed
shape retained more bytes. The order sensitivity and the different hit, miss
and creation costs give no single threshold change. The S20 transitions stay
8 to 9 and 5 to 4.

The write interval series warmed each size for 12 replacements before its
seven samples and rotated every size into each order position twice. The
median of the six round medians at 100,000 entries was 26.068 ms for the whole
call, 17.586 ms for admission and 10.962 ms for lookup. Late versus early
admission ratios inside the measured windows ranged from 0.933 to 1.028.
The historical large drop did not recur within those warmed windows. The
first warm replacement still took 84.555 to 89.389 ms of admission in three
orders. At 1,000 entries the two first-position whole-call medians were
1.497 and 1.481 ms, and the other four were 0.266 to 0.294 ms. Those order
effects prevent a steady comparison across sizes. The series establishes
where the time changed and leaves the cause open.

### R2 fixed scalar pair comparison

The six rotated rounds compare `6f10cbd8`, its independent gauge and
`61e7c438`. All ten Int64 owned lookup and whole replacement rows improved
under the predeclared paired rule. The other 21 rows stayed inside their gauge,
including all Text and protected lookup controls. Digests and the native live
and slack bytes matched. Protected lookup allocated zero. The diagnostic
listings show the typed `PackedMap<long, long>` reads and writes at the head.

| Int64 row | Base median | Head median | Paired time ratio | Base / head managed bytes |
| --- | ---: | ---: | ---: | ---: |
| Owned Get plus hit, 1,000 entries | 47.658 µs | 11.331 µs | 0.243 | 79,112 / 31,112 |
| Owned Get plus miss, 1,000 entries | 48.683 µs | 11.269 µs | 0.233 | 79,112 / 31,112 |
| Whole Replace, 1,000 entries | 217.030 µs | 148.863 µs | 0.702 | 198,584 / 150,520 |
| Whole Replace, 100,000 entries | 28.383 ms | 21.293 ms | 0.753 | about 18,065,435 / 13,265,371 |

These are figures for the complete operations. Whole Replace includes validation
and placement, and the owned read includes the dictionary and lookup. The
ARM64 comparison confirms the allocation reduction and closes the measurement
owed by S20R.

### K4 unchanged Replace factorial

The four schema variants ran from `7c6e09af` with the same fixture and balanced
orders. The table gives the paired median ratios for enabling unique ByLogin
(U) and Dictionary Region (D) while the other switch is off (0) or on (1).

| API | Percentile | U with D0 | U with D1 | D with U0 | D with U1 | Interaction |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| Class | p50 | 1.059 | 1.039 | 1.118 | 1.081 | 0.995 |
| By name | p50 | 1.029 | 1.054 | 1.088 | 1.114 | 1.024 |
| Class | p95 | 1.044 | 1.041 | 1.065 | 1.051 | 0.988 |
| By name | p95 | 1.011 | 1.053 | 1.069 | 1.114 | 1.041 |
| Class | p99 | 1.203 | 1.975 | 1.478 | 2.323 | 1.464 |
| By name | p99 | 1.034 | 2.349 | 1.324 | 2.988 | 2.183 |

The Dictionary p50 ratios exceeded one in all eight pairs for each condition.
The p99 interaction ranged from 0.536 to 2.113 through the class and 0.813
to 2.705 by name. That spread leaves the magnitude of a tail interaction
uncertain. The unchanged-key path skips the unique owner lookup, so the
ratios apply to the enabled schema forms. The K4 measurement is closed.

The original concurrent scalar writer and reader line remains open. The S21
listings cover the S21 comparison. The historical scalar line still needs its
hot ownership call identified under its own workload in the next suitable
queue.

## Architect's review after S20 and S20I, 25 November 2026

The independent review read `main` at `6f10cbd8`. It covered the packed collections,
their write and lookup callers, and the position-clock correction. The planner and
coordinator assigned its findings as follows.

| Place | Finding and disposition |
| --- | --- |
| `PackedMap`, `StructureLayout`, `MapForm`, `NestedValues.PlaceCollection` | R2 takes the fixed scalar pair through the typed scalar routines and the existing Map factory. The nullable value keeps its bitmap and slot. A fully fixed owned value keeps record placement and copying. The existing collection owner still checks uniqueness and rolls back refused placement. |
| `RootReader.FindEntry`, `CollectionKeys`, `CollectionLookup` | The S21 study checks the existing path from the owning record's payload and arena seed to the entry ordinal, and how stamp validation covers a hit or a miss under the viewer's epoch. The work belongs to the protected view. A separate sparse writer or owned copy rewrite would duplicate paths that serve the existing behavior. |
| `PositionClock` (`_targets`, `_byTarget`, `Publish`, `Wake`) | Assess one pending-target index in S34, with Deadline and ChildDeadline checks, the clock-waiter allocation and contention scenarios, and the hot Publish listing and ARM64 measurements. Keep first reach recorded in publication and wait completions outside both locks. |

## Architect's review after S21I, 25 November 2026

The independent review read merged `main` at `4ee9d4a2` and covered S21I, S21 and
S20R. The planner and coordinator joined the findings below to the sprints that
touch the same paths. No separate refactoring sprint is needed.

| Place | Finding and disposition |
| --- | --- |
| `ClassSet`, `SetOperations`, `EntityTable.TryView` | Join to S22. Use the existing stamped protected read with a generated projection bound once to the actual schema. Keep one epoch, the same hit and miss positions, session ordering, exception cleanup and zero allocations for scalar reads. `TryRead` and `TryCopy` already share `TakeEntry`. Keep their measured front ends. |
| `SchemaJsonReader`, `SchemaBuilder`, `ClassBinder`, `EntityBinding` | Join to the S22 generator proof. The actual analyzer and runtime class binding must apply the same schema and class agreement rules, including inherited members from a referenced assembly. Separate agreement checks from construction of runtime delegates only where the two callers require it. The compiler dependency belongs to the generator. |
| `ResidentViews`, `CollectionKeys` | Joined to the [S23 resident collection work](#s23-resident-collection-shape). |

The S34 position-clock item and the concurrent scalar writer item keep their existing
dispositions. The scalar writer check requires the original hot listing in a suitable
independently justified ARM64 queue.

## Architect's review after S22, 25 November 2026

The independent architect reviewed merged S22 at `159d3fee`, including the typed
generator, shared protected-read path and the collection storage it reads. The stamped
lookup shared with by-name views, the cold projection binding outside the epoch and the
separation of compiler dependencies from the runtime are sound.

### S23 resident collection shape

Use one rule in `ResidentViews`, `CollectionKeys` and `CollectionLookup` to open a
resident collection descriptor or packed block. Join this to the
[`CollectionLookup.IsChunked` and `LookupBlocks` item](#architects-review-after-s19-24-november-2026).
Get the physical form from its placement and keep the logical element count separate.
Exercise two collections with the same count and different physical forms. Preserve
holder backlinks, offsets, ordinals, coherent held views, copied enumerators, split
records and native byte spans. Repeat the existing allocation and timing comparisons
for the paths this work changes, using the S22 protected scalar retiming if it applies.
Keep the packed List cursor and the owned Map scalar specialization for their
distinct traversal patterns.

### Generator findings

- `RoslynClassMetadata` revisits accessor facts during inference and validation.
  The accessor lookup already uses one algorithm. Property selection and the diagnostic
  for missing implementation metadata serve different callers. Declined for S22R.
- `ProjectionBuilder` creates an empty projection list for validation-only descendants
  during the consumer build. The traversal still validates the descendants. The review
  did not establish a build cost or a simpler form.
  Declined for S22R. Revisit either finding if a consumer build measures the cost.

The proposed S34 pending-target index and S24 budget accounting keep their placements.
The existing full-root rollback defect was corrected in
[S23D](epic-1/sprints/s24.md#s24--the-memory-budget).

## Found while building S18, 24 November 2026

| # | Place | What is wrong | The simpler form | What it must keep | Status |
| --- | --- | --- | --- | --- | --- |
| Y1 | `src/Moltaro.Lattice.Tests/Indexes/Model/Unstored` (`UnstoredPartSearchModelTests`, `UnstoredSchema`) | The names say the parts are not stored, and since S18 they are | The folder and the two types named for what they test, a Reference part and a part on a path into an owned value | A move: no assertion changes | taken (S18R) as Z6 of the architect's review after S18 |
| Y2 | `Storage/Roots/BlockExport` (`RecordsLength`, `WriteRecords`) beside `StructureWalk` | The export descends a tree on its own, since the wire is in preorder and the walk visits the children first; S19 has to extend both | A pre-order step in the walk that serves the export, or the two kept apart with the reason that stands in the head of `BlockExport` | The bytes of the wire; the listing of the export of a scalar root | taken (S18R) as Z1 of the architect's review after S18 |

## Architect's review after S17, 23 November 2026

Read at `main` 6b968b3, nothing run. S17 landed in the right folders and the direction of
dependencies holds: `Entities` and `Indexes` use nothing of `Writes` or `Child`, and each
new type has one job (`NodePool`, `PublicationKeys`, `UniqueClaims`, `BatchMember`). The
debt is small: what the pool is handed at every call, the give-back of changes that were
never reserved, and D5, which S17 was to finish and did not. Gone through by the planner
and the coordinator on the same day. Found sound and left: the ledger as a value that
`Undo` restores; `Bounds` pure and static; the three steps of `TryReserveWithIndexes`;
the publication in two calls with its order of stores; `TryPrepare` and `TryPrepareBatch`
of the coordinator, alike on purpose so that the single path stays inlined; `Forwarder`
and `RequestReceiver`, each sharing what the two kinds share; `RootValues.Export` beside
`Forwarder.Request`; the two test hooks on a publication; the test folders of S17. Not
examined: `docs/design` beyond the sections it needed, the samples, the scenarios of
batches, most new tests beyond their heads. `WriteCoordinator` (2,031 lines) takes S20,
S24 and S26 before U1 (S27); that changes no order.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| W1 | `Engine/Indexes/NodePool.cs` (`TryReserve`, `TopUp`, `Release`, `GiveBackReserve`, `AfterPublication`, `StandingReserve`, `LeftAfterMove`, `MoveBase`, `Targets`, `Trim`, `AssertReserved`); `Engine/Indexes/OrderedTree.cs` (`TryReserve`, `TopUp`, `Release`, `GiveBackReserve`, `GrowPath`, `Room`, `AfterPublication`) | Twelve members of the pool take the height and the room of the root, and the tree reads its root node three times a write to supply them, though both change only in a publication. In `LeftAfterMove` the local `moves` first means that something was published since the base and then that the base moves | The tree tells the pool both once, in `AfterPublication`; the pool keeps them and the standing reserve counted from them; the signatures lose the two arguments, four members of the tree stop calling `Room`, and `TryReserve` tests the length of the path before it calls `GrowPath`. `LeftAfterMove` in its plain form, one meaning for `moves`, the same result in every case | The pool holds the lesser bound and no more than the larger of it and the reserve; a refused or withdrawn reservation gives back what it took: `Reservation`, `Refusals` and `Batches/Memory` of the engine tests and the Debug assertion of the pool. By the listing of the head on the machine it removes six of the 25 calls of an insert into one Int64 index. The listings of `TryReserveWithIndexes`, `TopUpIndexes`, `OrderedTree.Insert` and `Remove`, `NodePool.TryReserve` and `Trim`; the figure owed to the start of S18. As done, an insert makes three calls fewer, not six: `NodePool.AfterPublication` is no longer inlined into the tree's and computes the standing reserve, so one call is added and one moves | done (S17R) |
| W2 | `Engine/Writes/WriteExecutor.cs` (`GiveBack`); `Engine/Child/Loading/ReceivedChanges.cs` (`GiveBack`, `TryPlace`, `TryApply`, `TryPrepareWrite`, `TryPrepareDelete`); `Engine/Entities/ChangePublisher.cs` (`Release`) | The give-back of changes never reserved (free the placed root, release the root entry of an insert, in reverse) is written in the executor and in the child; on the child four try/catch blocks call it inside the preparation and `TryApply` calls it again around them | One `ChangePublisher.GiveBack` beside `Release`; `TryApply` and `TryPlace` give back once, in one catch and on a false; the preparations lose three of their four try/catch blocks, the fourth keeping only the root entry not yet in the list | A transaction is applied whole or not at all; a refused batch or transaction leaves every counter as it was: `MalformedTransactionTests`, `KeyNamedOnceTests`, `Replication/Batches`, `RefusedBatchTests`, the refusals of a load, the tally tests. The listing of `TryApply`; `following-children` owed to the start of S18 | done (S17R) |
| W3 | `Engine/Entities/ChangePublisher.cs` (`FindTaken`, `FindTakenChecked`, the `FindTaken` of one index) | Three methods named for one check; `FindTakenChecked` holds two algorithms, the descents of one change and the loop over the unique indexes of several, and the rule of which conflict is reported is explained at the gate, not where it is implemented | The gate chooses; one method for one change, one for several, and the check of one index with a name of its own; the comment of the rule moves to the method for several. The local `found`, the 16 bytes the frame of the single path grew in S17, leaves that path | `unique-writes` lines 1 to 3; a change that can set no unique value makes no call for the check. The listing of `TryReserveWithIndexes`; the figure owed to the start of S18 | done (S17R) |
| W4 | `Engine/Delivery/TransactionEncoder.cs`; `Replication/Transactions/TransactionFormat.cs` (its head, `ReadEffect`) | D5 half done: the three envelopes of an effect are still written by hand in `Delivery` and read in `Replication/Transactions`, and the head of the format does not name the member a root carries since wire format 5 | Three small writers in `TransactionFormat` beside `ReadEffect`; the encoder keeps only where the bytes go | The bytes identical, wire format 5: `Replication/Transactions` of the engine tests and the following tests. The listings of `WriteRoot` and `WriteDelete`; `following-children` owed to the start of S18 | done (S17R) |
| W5 | Comments and documents | Comments that cite a sprint, a brief, a working document or an open question in files S17 added or touched (`ChangePublisher.cs`, `RecordValues.cs`, `RootTable.cs`, `OrderedTree.cs`, about fifteen heads of tests of batches and `ReadsBesideBatchesScenario`); the head of `FailedLevelGrowthTests`, hard to read; the comment of `KeptRoom`, "two loads and a test", which no listing backs; the example of a composite index in `README.md`, whose field ids the schema shown does not declare; the members of `ILatticeWriteBatch` that add an operation, which do not name the `InvalidOperationException` of a batch already submitted; `Program.cs` of the samples, which still marks `WriteBatchSample` and `UniqueIndexSample` as planned; the rule of which member a refused batch names (Q58), which an application sees and which lives in no decision record and in no remark of the contract | The rule and its reason in their own words; the head of `FailedLevelGrowthTests` in three sentences (what holds, why the two arrays stay of one length, what only a Debug build catches); the comment of `KeptRoom` says what is tested, without a count; the README names the fields; one line on each adding member; the two samples not marked planned if they run to the end; the rule of Q58 in two sentences in the remarks of `ILatticeWriteBatch.CommitAsync` and of `LatticeException.OperationIndex`, and in one sentence of `docs/decisions/mutation-commands.md` with the owner's reading | Comments and documents only | done (S17R) |

The P4s of the reviews of S17, gone through with it:

- The arguments of `Admit`, shared by a single write and a batch, and the forwarded
  admission that serves both kinds: joined (S20), where a partial update brings a third
  kind of input and the inputs of a write become one value passed by reference, with the
  figure of a single write.
- `WriteCoordinator._group` keeps the records of the last group until the next forms:
  joined (S28), where with a durable step every write waits and the group is cleared when
  its outcomes are decided, with a probe.
- The claims of a unique index gathered and sorted in `ChangePublisher` and again in
  `UniqueClaims`: joined (S28), beside the cost of the first question already carried there.
- `HeldPersistence` written twice, in the engine tests and in the measurement program:
  joined (S27), the first sprint that changes `IPersistence`.
- The test of a master that faults during a forwarded batch does not check that a later
  write is `Unavailable`: refused, since it follows from the master being faulted, which
  the test asserts, and `MasterFaultTests` states it for a write after the fault.
- About 90 ns a transaction for clearing the kept set of a child after a large one:
  refused as refactoring; `HashSet.Clear` clears the whole bucket array,
  `following-children` shows nothing, and the form that removes it adds state for a figure
  nobody measured. If it is measured, its place is S33 with V1.

K2, gone through with it. Of the 25 calls of an insert into one Int64 index at the head:
(a) W1 removes six. (b) `ChangeAt` split, the decision taken again for an index at place 63
or above in a method of its own and the three tests of the masks inlined into `CountsOf`,
`InstallNew`, `RemoveOld` and `SetsValue`: two calls fewer for an insert, one more in the
publication, and one fewer an index for a replace that keeps every key, which made no call
of `ChangeAt` before S17. (c) `NodePool.Targets` gives nothing without counting the need
when nothing waits. (d) The arithmetic of the pool, about thirteen integer divisions an
insert into one index by reading: `Bounds` can compare before it divides, `Need` has no
growth while the inserts fit the room, and a node far from full has no progress; each
exact, each a branch added for speed. (e) An allocation that returns the payload of the
node it gives, n calls fewer, which touches the address validation of `Arena.Payload`.
(f) For a batch, `CountsOf` walks every change once for each index in the reservation and
again in the top-up, and `TablesOf` is scanned three times. Since the write path removed
9 + n calls and the machine saw nothing, (b) and (c) are one variant build in the start of
S18, and (d) a second on `index-writes` with 3 and 6 indexes; (e) and (f) are recorded and
not run. A single write that goes round the ledger is not proposed: it would be a second
rule of reservation and would change what is taken at the memory limit. The second line of
K2, part 2's insert that grows the primary index: no cause in the code; it is run with the
trees rotated first, and a sample profile of the writer at ed81c76 and 3e24ce4 is taken
only if the fall stands. The third line, part 3's `unique-writes` line: the 139 bytes gone
from `ClassSet<T>.Save` are the stub of `Forwarder.WriteAsync`, on the branch of a child,
which a master does not take; on the machine every body on the path of that line is listed
at f6d9cf3 and at the head and compared for identity, then the line runs under `perf stat`
for both, and equal instructions with different cycles is placement and leaves K2.

The two timing tests that failed on a loaded workstation, `ReadTimeInsideTheEngineTests`
and `CancelledStepTests`, ran past the one-minute bound of the tests, after which a held
read leaves its slot; neither failure was the engine's. Both are made deterministic by the
replaceable source of the timestamp in S18, as its brief says; that is not refactoring,
since it needs a source the engine does not have.

## Architect's review after S13, 18 November 2026

At `main` 851d432, read only. Its judgement: S13 landed cleanly — the text mechanism sits
in `Storage`, and the rule of counts is implemented for a descriptor in any place, not
for a root. Gone through by the planner and the coordinator on the same day.

| # | Place | What is wrong | The simpler form | Status |
| --- | --- | --- | --- | --- |
| B1 | `Storage/RootStorage.cs` (`FreeValue`, the branch in `Publish`, `TextBytes`, `Retire`); the guards `Kind is Block or Chunked` in `Storage/Values/ValueBlocks.cs` (`Free`, `Publish`, `Retire`, `Adopt`, `SlotBytes`); `Engine/Protection/RetiredMemory.cs` (`RetireBlocks`) | What one descriptor holds outside its block is decided in three forms and five guards, and retiring a root walks its descriptors twice | The operations for one descriptor stand side by side in one place above `Values` and `Text`, taking the arena and the descriptor; the enumerator of A4 makes the operations of a root short loops over them. Shown unchanged: counters equal the tally, the bound on dead text behind retired roots, writes with a Dictionary text, the time of a drain | done (S14R, part 2), with A4: `RootStorage` decides in one place what one descriptor holds outside its block; the merge of `TextBytes` into `Retire` was done before (3031e2b) |
| B2 | `src/Moltaro.Lattice.Measurements/Scenarios`: `ReadByKeyThreadsScenario.cs`, `WriteCallersScenario.cs`, `ReadWindow.cs`, `DictionaryWritesScenario.cs`, and since S14 `IndexSearchScenario.cs` 157–257, the window of the index scenarios (E5) | The thread harness "warm-up, window, phases" exists five times, differing in the body of one step, and the copy of the index scenarios calls a delegate for each step; every sprint with a new read path adds one | One window type generic over the step as a struct, no call for each step; each scenario keeps its report lines. Proof: the listing of the hot loop of each scenario before and after, and one short series on the workstation at a quiet moment — the old and the new harness on the same engine, three rounds rotating, with a pair of the base against itself as the gauge. If the workstation is never quiet enough for the gauge to lie inside ±6 %, the listing stands alone and the series is the first item of the start of the machine of S14, part 2 | done (e8c5c0b, 75fd5e1, f438791, 097d700, 281807e, 8567acd): one `StepWindow` whose step is a struct, used by read-by-key-threads, write-callers and the child scenarios that run its series, large-writes, the index scenarios and the Dictionary reads and writes; `ReadWindow` is gone. The JIT listings are the proof: no call added to any step and the delegate call of the index reads gone, and every report line keeps its name, order and shape. Its short series, old against new beside two builds of main, is the first item of the plan of the start of the machine for S14, part 2, because the workstation was not quiet |
| B3 | `Storage/Text/TextTable.cs` (`new RootHandle(0, number)`, `Header(address).Root.Slot`), `Storage/BlockHeader.cs` (the comment), `ValueBlocks.TryStoreText` | "The header of a block of shared text names the slot of its entry" is written through one type, read through another member and explained in a third file | `ValueBlocks.TryStoreText` takes the slot as a number and writes the header; `ValueBlocks.TextSlotOf` beside it reads it | done (808b619), with T1 |
| B4 | `Engine/EngineDatabase.cs`: the close sets fourteen fields to null one by one, and members reach the parts by two idioms, `_x!` inside the barrier and `Part(_x)` outside it | A part added later that is not added to that list breaks "a closed database retains nothing" without a failing test until the weak-reference test is extended | One object that holds the parts of an open database in one field; the close clears that field, and a member reads it once and reports `ObjectDisposedException` when it is null. Shown unchanged: the tests of what a closed instance holds, the read by key (the sets keep their own references and the read path gains no indirection) | done (S14R, part 2), with A3: the close clears one field |

What S13 did to entries above: T1 is carried out in the form that asks
`Arena.IsPrepared` — allocating an entry live always changes what `PreparedBytes` counts
and is carried to S24 as a question of accounting. A13 gains the branch of `TextTable`
for a write that is not prepared, which no product path reaches; T1 does not polish it.
A11 states the order it leaves: `Roots` → `Text` → `Values` → (`Arena`, `Hashing`,
`Allocation`); the cycle between the folders that a reviewer of S13 saw is A11 and one
constructor line of `Arena`. The comment commit of S13R covers the files of S13, which
cite the points of a decision and a brief, and adds to `LiesInChunks` that the
descriptor of a collection is not for it.

Sound, leave as it is: the core of `TextTable`; `TextPieces` beside `ValueSource`, which
are not duplicates — merging them adds a copy to a write that hits; `IndexTable` shared
from `Storage/Hashing`; `RootWriter.PlaceText` beside `PlaceValue`; the check-then-place
split of `BlockImport`; the tests of S13 in their folders.

Refused: a text table owned by the database instead of the arena — both reviewers found
it no plainer: every free of a block has the arena at hand and has to drop the block's
references, and the other form adds a parameter to about ten signatures.

## Architect's review of stages 2 and 3, 18 November 2026

The first review in the role of the architect, over the whole engine. Its judgement: the
mechanisms are sound and plain where it matters — the arena, the stamp of an entry, the
epochs, the index table and the frame codec are each one job in one type. The debt is in
three places: several jobs crammed into one file; the master and the child each with an
implementation of their own of "a prepared change", which every sprint from S13 to S19
would have to extend twice; and the comments. Over-engineering is limited: the waiting
machinery of the write coordinator is reached in production only through a write refused
for memory until the durable storage step exists; it was built ahead on a decision and is
tested, so it stays.

| # | What | The simpler form | Cost | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| A1 | A child applies what it receives in three types of two folders: entering the barrier, the lock, try–drain–retry and the fault are written twice (`Engine/Loading/RootPlacement.cs` 90–112, `Engine/Following/Follower.cs` 329–387); the effects of a transaction are decoded inside `Follower` (421–467), far from their encoder; its constructor takes twelve parameters | `ReceivedChanges` owns the lock, the barrier and the fault, with two operations: place a root, apply a transaction. The decoding of effects moves beside `TransactionFormat`. `Follower` keeps receive, order check, suspension, apply, acknowledge. The three-step publication of several effects moves verbatim: it is reasoned, not tested, until S17 | M | A transaction is applied whole or not at all; a failed publication faults; the child scenarios of the measurement program | done (778d2a3); its figure, the child scenarios against 645fa43, was measured on 19 November 2026 and shows no fall (the measured results of the epic) |
| A2 | `Engine/Writes/WriteExecutor.cs`: thirteen members only pass a call on (244–276, 594–629), and the child's loader depends on the master's executor to reach the publication stores (643–731) | The five store sequences (install, mark and stamp of a replace and of a delete) move verbatim, with their comments, into one small sealed type that the master's publication and the child's application both call. No statement inside them moves: tests cannot see a reordering of those stores on x64 | M | The order of the stores and the fault points; the write scenario; the allocation tests of the writes | done (670617c), in the wider cut of D1 |
| A3 | `Engine/EngineDatabase.cs`: twelve nullable fields set together and read with `!` about forty times; facade, life cycle, composition, the master's network, the child's load and test access in one file (with U6) | One immutable object of the parts, built by one method and published as one field; the body of the child's load into a type of `Engine/Loading`. The state transitions and the close stay | M | The order of refusals; the lock order; the close completing only after the arena is freed | done (S14R, part 2): `DatabaseParts`, immutable once made, published as one field and read once through `Part`; the body of a child's load in `Engine/Child/Loading`. One difference in a race only, accepted by the planner on 21 November 2026 on the condition that every member reads the field once: `Schema`, `Position`, `Set` by name and `CreateWriteBatch`, read by a member that found the state Ready just before a close that then ran to its end, report `ObjectDisposedException`, as every member through `Part` already did, instead of answering from what the close kept |
| A4 | Fourteen loops in six files walk the blocks a root owns and derive offset and kind again (`Storage/RootStorage.cs`, `RootWriter.cs`, `WriteExecutor.cs` 506–517, `Replication/Load/BlockExport.cs`, `BlockImport.cs`) | One enumerator in `Storage`, a ref struct with no allocation and no virtual call; free, publish, retire, the byte counts, export and import become its users | M | Counters equal the tally; no managed allocation; the write, load and root-bytes scenarios | done (S14R, part 2): `RootDescriptors`, a ref struct, used by free, publish, retire, the byte counts, export and import |
| A5 | Three frame assemblers repeat one algorithm with the same checks and messages (`Replication/Load/RootAssembler.cs`, `Transactions/TransactionAssembler.cs`, `Forwarding/MessageAssembler.cs`) | One small struct for a body of an announced length; the three keep their headers | M | Every malformed frame refused as today, and the refusals for memory | done (S14R, part 2): `AnnouncedBody`. The old and the new assemblers were fed the same 200,000 random sequences each, well-formed and malformed, in a comparison not kept: every malformed frame is refused at the same frame with the same code, and the refusals for memory are equal. The messages of the body checks now share one wording, and a Blocks frame marked as the end of a root it does not complete is refused by the check of the flag instead of the check of the count, with the same code; the planner found that no change of behavior |
| A6 | About 300 of the 917 lines of `Engine/Entities/EntityTable.cs` are "the primary key of a type" and its wire format; its range check repeats `IntegerRange` of the core (with U4) | A sealed type for the keys of an entity type (kind, hash, equality, stored key) held in a readonly field; direct calls, no interface. The private copy of the range check goes | M | The read-by-key and enumeration scenarios; the allocation tests of a read by key | done (37071e5, 0151c9f), with one difference: the keys of an entity type are a readonly struct `PrimaryKeys` of 24 bytes held by value in the table, not a sealed class in a readonly field, because a class added dependent loads to a read by key; the comparison stays in `EntityTable.KeyEquals` |
| A7 | Four identical dictionaries from type id to table and three arrays of tables; `Engine/Children/ForwardedWrites.cs` exists only to carry three objects | One object for the tables of the database, built once; `ForwardedWrites` is deleted | S–M | An unknown or unstored type id refused as today | done (5656ad0), except that `ForwardedWrites` stays: deleting it threads four parameters through `ChildListener` and `ChildConnection`, which only pass them on, and that costs more than the type. Refused in that part |
| A8 | The envelope of a root on the wire (type id, last mutation) is read or written by hand in six places, and `Storage/RootWriter.cs` 493–525 writes a format of `Replication`; `ForwardEncoding.WriteRootLength` is called by nothing | One type for the envelope in `Replication/Load`; the export of `RootWriter` writes only what the block export writes | S | The bytes on the wire identical; the wire format stays 3 | done (72ee354); the bodies on the wire are identical, and the cut of a forwarded request into frames may differ for a root that starts in the last 24 bytes of a frame, which every receiver takes |
| A9 | `Engine/Writes/WriteCoordinator.cs`: four hand-linked lists, a block written twice (843–855, 1089–1101), and the pair "end, advance the chain" thirteen times (with U2) | A private struct for a queue of writes; one method for the pair; one for the block. No state, order or lock changes | S | A MemoryOnly write allocates nothing; the write scenarios | done (S14R, part 2): one queue of writes, one end that starts the next of the chain, one queueing of a prepared write |
| A10 | Comments (with C1 and C2): "brief of" 82 times, the planner 95, a date 65, a question id 67, a sprint number 237; the long class heads retell `docs/design/write-path.md` and `epoch-protection.md` | The convention of C1, applied to a file when an entry above touches it, never as one sweep; reasoning that exists nowhere else moves into `docs/design` in the same change | L in lines, none in behavior | — | done (6376dfd) for the files S13R touched and the files of S13; the other files as later sprints touch them, never one sweep |
| A11 | `Storage`: the arena and the roots in one folder with `Values` between them, and `Values` uses `RootLayout` for three constants while the roots use `Values` | The roots into `Storage/Roots`; the three constants to `ValueBlocks`. Mechanical, touches about sixty files: scheduled alone | M | — | done (S14R, part 2): the roots in `Storage/Roots`, the three constants in `ValueBlocks` |
| A12 | The child's side is spread over `Loading`, `Following`, `Forwarding` with `MasterLink` in `Loading`; the names do not say the side (with N4, N7) | One folder for the child's side with those three below it; the master's side stays `Children` and `Delivery`. After A1 and A3 | M | — | done (S14R, part 2): `Engine/Child` with `Loading`, `Following` and `Forwarding` below it; the master's side stays `Children` and `Delivery` |
| A13 | Members of the product that only tests use: the allocation that is live at once and `Arena.IsValid`, the unprepared mode of `RootWriter` and `ValueBlocks`, `IndexTable.cs` 312 | One allocation (prepared) and one constructor; the tests that want a live block publish it. About twenty test files change how they set up, not what they assert: the test author's work | M | The assertions of those tests | done in part (S14R, part 1): `Arena.IsValid` and the short `IndexTable.TryFindNext` removed. The unprepared mode of `RootWriter`, `ValueBlocks` and `TextTable` and the live `Arena.TryAllocate` are used by the measurement program too, which has the access of the tests: taken (S14R, part 2), the tests and the three scenarios first; the rest done (S14R, part 2): the tests, `TestSupport` and the scenarios write prepared first, then `Arena.TryAllocate`, the three-argument `RootWriter` and the flags prepared removed. On the planner's word, 15 tests that read the bytes a writer leaves are restated through `StoredBytes.In`, which counts prepared bytes too. Two mutants were run and not kept. M1, a writer whose Abandon or failed Complete leaves its first value of blocks, fails the two cases of `CompleteFailureTests.…frees_the_value_blocks_written_before_it`, `WriterLifeTests.After_a_failed_Complete…` and `Abandon_frees_everything_the_writer_allocated`, `PreparedRootTests.Abandoned_or_refused…` and the two cases of `WriteAllocationTests.Writing_abandoning_and_freeing…`, which reads `StoredBytes.In` and so counts the prepared bytes as well, its text unchanged; M2, a failed store of a chunked value that frees its chunks but leaves its directory, fails the two of `ArenaMemoryTests`, the three of `SetFailureTests` with failing requests 1 to 3, the three of `ValueBoundTests` and `PreparedRootTests.Abandoned_or_refused…`. Four pass both, because their writer holds no block when it fails, as with the live writer: `CompleteFailureTests.…of_inline_values…`, `ArenaMemoryTests.An_arena_with_no_room_left…`, `NullAndMissingTests.A_field_that_is_not_nullable…` and `SetFailureTests` with failing request 0. The reviewer ran both mutants again at the base and at the head: the same tests fail them |
| A14 | `Engine/Writes/RootValues.cs` is used by a read of a record and by the forwarder | It moves to `Engine/Entities` | S | — | done (54935c9) |
| A15 | Small duplicates: the strict UTF-8 decoder three times; a local `Invalid(string)` nine times; the message of a faulted database three times; a span copy loop twice that is `IBufferWriter.Write`; the wake pulse twice | One place each, in the type that owns the thing; messages unchanged | S | — | done (08cddfa), except the wake pulse: no type owns it, and a type of its own for two copies of ten lines does not pay. Refused in that part |
| A16 | `EndsARoot` (N2) | `EndsABody`; the value on the wire unchanged | S | — | done (70b7798) |
| A17 | `src/Moltaro.Lattice/Storage/WriteKindEnum.cs`: one internal enum in a folder left from the placeholder, in a namespace the engine also uses | It moves to `Access`, where its users are; internal, so the contract does not change | S | — | done (94d896e) |

### What the coming sprints will find hard

- **S14, S17:** "an entity and all its index entries in one publication" and "prepared
  before the commit" have to be added on the master and on the child; A1 and A2 make that
  one place each, and a batch then reuses the three steps the child already has instead
  of a third copy. A6 first, because the entity table gets a second kind of index.
- **S16:** a write that waits behind a write holding its unique value is a second reason
  to chain, and a pending write has one pair of chain links. A9 only; the chain model is
  for that sprint's study, and its brief should say so.
- **S18, S19, S23:** blocks will own blocks, so every walk of A4 becomes recursive, and
  compaction has to find every holder of an address; A4 is the important preparation.
- **S24:** memory is counted in several places and summed in one method of
  `EngineDatabase`; "try, drain, retry, wait for the reads, refuse" exists three times.
  A3 and A1.
- **S13 to S19:** "an entity type the engine cannot store yet" is checked 52 times in 14
  files. Right today; deleted, not kept, when the last type becomes storable.

### Sound, leave as it is

`Arena` with `Storage/Allocation`; the stamp of an entry — do not restructure it;
`Engine/Protection`; `Engine/Indexes`; `PositionClock`; the struct copies behind
`IRootCopy`; `SetOperations`; the frame codec, the connection and the protocol;
`ICommitStorage`, a real boundary; the test hooks as a mechanism. No test names the write
coordinator, the executor, the follower, the received changes, the placement, the capture,
the connections, the assemblers or the block import — which is what makes A1, A2, A5, A7
and A9 cheap.

### Not examined by this review

The text of most comments against the code; many types by their signatures only; the
content of the tests (coverage is judged by folders and by which types the tests name);
the measured figures themselves. A refactoring sprint verifies each before it relies on it.

## Architect's review after S23, 26 November 2026

Reviewed `a17137b6` and the test-only recovery at `b53fb21a`. The counted
nonblocking maintenance admission, raw epoch retirement after address
publication and separate bounded and synchronous cleanup paths preserve their
responsibilities. No S23R is needed before S23A.

The owner requires a separate closure before S24. These entries were open
at S23A runtime `5014a6c8` and the [S23AR record](#s23ar-verification) shows
their disposition.

- **Text detachment** (*done in S23AR, E01/E03*). The decrement and detachment were
  duplicated in [TextTable.Release and ReleaseStep](../../src/Moltaro.Lattice.Engine/Storage/Text/TextTable.cs#L149).
  Keep immediate and resumable native and locator return separate. Preserve the
  lifetime tests and matched ARM64 mixed replacement and allocations, using the
  anchored and disjoint dictionary controls.
- **Repeated edge geometry** (*done in S23AR, E01/E03*). Lookup offset arithmetic was repeated
  in [CollectionLookup](../../src/Moltaro.Lattice.Engine/Storage/Roots/CollectionLookup.cs#L160),
  [RelocationBlock](../../src/Moltaro.Lattice.Engine/Engine/Maintenance/RelocationBlock.cs#L48)
  and [CompoundRelocation](../../src/Moltaro.Lattice.Engine/Engine/Maintenance/CompoundRelocation.cs).
  Move repeated record-directory and lookup geometry to the layout owners. Keep
  placement, relocation validation and reclamation walks separate. Preserve
  aligned layouts, wire bytes and matched read, write, allocation and turn
  measurements.
- **Protected export test setup** (*done in S23AR, E03*). S23A reused
  [PausedExportBuffer](../../src/Moltaro.Lattice.Engine.Tests/Engine/Views/PausedExportBuffer.cs#L18)
  and added a pause after the old chunk is acquired. The normal protected export
  sequence was repeated in
  [AutomaticCompoundRelocationTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Protection/AutomaticCompoundRelocationTests.cs#L564),
  [CompoundCollectionRelocationTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Protection/CompoundCollectionRelocationTests.cs#L992),
  [FixedRootRelocationTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Protection/FixedRootRelocationTests.cs#L652),
  [OwnedValueRelocationTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Protection/OwnedValueRelocationTests.cs),
  [CompoundRelocationTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Protection/CompoundRelocationTests.cs)
  and [RelocatedCollectionMatrixTests](../../src/Moltaro.Lattice.Engine.Tests/Engine/Collections/RelocatedCollectionMatrixTests.cs).
  Share only the equivalent capture setup. Keep each case's timing cuts, data,
  revision and physical placement assertions local.

The [final cleanup investigation](epic-1/sprints/s23a.md#pre-s24-close-cleanup-investigation)
has a separate pre-S24 closure under E07 because its outcome may change behavior.

### S23AR verification

The refactoring shares the final-reference decrement and detachment in
`TextTable`; each caller still owns its original native and locator return
schedule. Record directory and lookup offset rules sit with the physical
layout owners. The relocation walks retain their validation and publication
order. `Arena.Retire` and the live path of `Arena.Free` share the live-slot
accounting updates. The relocation tests keep their wire-byte checks. The
ARM64 geometry comparison verified data, held views, the independent native
ledger and the one-segment standing reserve.

The test fixtures share an unpaused protected export and read-only segment
and allocator inspection. The original length checks, pause points and
physical-placement assertions stay with their cases. The full Windows x64
Release solution at `382ecf1b` passed 4,667 tests with 129 expected skips,
format and all 18 samples. The later read-only ledger wrapper at `5e12b72d`
passed 248 focused tests and rejects an attempt to modify the live ledger.

An independent ARM64 replay checked all 72 text windows from `4d8941e8`,
a separately built copy of the parent, and `de4fcaf2`. Each window had
524,288 public replacements. There was no directional change under the
six-rotation rule, and both measured thread allocation rows stayed at
152 B per replacement.

| Text workload | Median paired candidate / parent | Gauge |
| --- | ---: | ---: |
| Mixed, default tiering | 0.9986 | 1.0300 |
| Mixed, FullOpts | 0.9956 | 1.0435 |
| Anchored | 1.0067 | 1.0326 |
| Disjoint | 0.9927 | 1.0329 |

The ARM64 geometry and arena accounting comparison used `de4fcaf2`, a
separate build of that parent, and the exact product candidate `aaf43b25`.
It completed three balanced rotations, with matching retained values and
native ledger across master and child. Wide moves returned 4 MiB on each role
and the no-room control preserved its native memory. Borrowed Map reads
allocated 0 B in all rows. The 58 eligible comparisons gave no directional
change under the paired rule. Default wide turns varied with process state.
A separate FullOpts control used the same binaries and matched the wide move
and return on both roles. The warmed repeat's median elapsed time was
0.281 to 0.283 ms on master and 0.374 to 0.292 ms on child. The paired
candidate-to-parent ratios were 1.015 and 0.990, within the gauge.
Cold wide turns had wider variation and do not establish equivalence.
An independent replay recomputed all 70 timing rows from the two geometry
archives. A Windows x64 FullOpts listing of the ordinary disjoint replacement
path shows calls to the shared helpers from both arena live-slot transitions
and from the immediate text release path. The paired ARM64 measurements record
the cost observed under their respective workloads.

The ARM64 archive SHA-256 values, in order, are
`2cbc4a622b8de556c0458196cdee33872a17d030b8ce2ebc374183de06fcfd6e`,
`ba99524206a52935128aadbd82d46fda84fe586c00a9fb67a2046ad84e55e17a` and
`0ba044f40eabebbee8f2879fc11a40b7ab97a0664f6567fb8a3440799b6261a2`.
Raw measurements stay outside Git.

The integrated refactoring over `f9f80c99` has eleven changed engine files.
Each matches its respective source blob on both sides of the ARM64
comparison. The integrated test setup also keeps the exact native address and
the raw header witness from the evacuation fixture correction.

### Child preparation rollback exits

The review of `ReceivedChanges.TryPlace` and `TryApply` considered merging
the early refusal and exception cleanup paths. The explicit exits are kept.
A shared finally would track success across each preparation to choose the
rollback, including attempts that go straight on to index top-up and
publication. The two supported exits each return prepared resources once.
After [S23F](epic-1/sprints/s23a.md#s23f-child-scratch-after-publishing-or-returning-a-batch),
returning scratch does not allocate. Root handle release uses the free
chain, arena release uses the already allocated segment tables and text
release queues later capacity work. The real three-attempt native refusal
and the matched mixed-batch original-error checks verify these paths.
Review the exits again when a reservation change changes this flow.

### F9 index-read-by-key driver comparison

The old driver at `f3d767f0` and the B2 driver at `19d3f6b5` use byte-identical
engine and contract builds. Both target windows warmed for 0.5 s and then
counted reads for 2 s. The earlier `NoInlining` control did not recover the
historical 0.888 ratio.

On ARM64 with .NET 10.0.15, the historical driver runs retained the earlier
six-index windows. The solo controls used the same three fixed index seeds
for both drivers and skipped the earlier windows. Each stage used six
alternating ABBA and BAAB blocks, with one process per sample. The ratio is
the mean of the two B2 rates divided by the mean of the two old rates in
each block. All 48 target windows reported 88 B per read.

| Stage | Median B2 / old | Paired range | Within-driver bounds |
| --- | ---: | ---: | ---: |
| Historical sequence | 1.00115 | 0.94827–1.08361 | 0.87008–1.14932 |
| Fixed-seed solo | 1.00379 | 0.97584–1.07641 | 0.90761–1.10180 |

An independent review recomputed the raw rows. Both sets of pairs were
inside their repeat bounds and had effects in both directions. The
historical fall did not reproduce. The earlier absolute rates for this
row stay incomparable and the cause of that result remains unresolved.
The raw archive SHA-256 is
`b2d72b0dac61e38826a5e5d11c2f385383178551b529358ba55dbdd1c1f8bbc9`.
