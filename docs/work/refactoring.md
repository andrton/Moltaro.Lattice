# Refactoring backlog

What the architect's review and the reviews of understandability have named, kept in one
place until a refactoring sprint carries it out. The [development process](process.md)
says how an entry gets here and how it leaves.

An entry changes no behavior and no assertion of a test. KISS holds for a refactoring as
for any change: an entry that adds a type or a level of indirection has to remove more
than it adds, and it is always a balance.

**Status of an entry:** *open* — not yet gone through by the planner and the coordinator;
*taken (S13R)* — goes into that refactoring sprint; *joined (Sxx)* — done with a sprint
that changes the same code; *refused* — with its reason; *done* — with the commit.

Line numbers are those of `main` at `e7b6e0e`, in `src/Moltaro.Lattice.Engine` unless a
path says otherwise.

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
  2. **S14, part 2** of the [epic](epic-1-core.md), which joins E1, E2, E4, E8 and the
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
  4. **S15, part 1,** with E6, G4, F5 and the read limits of F10.
  5. **S16,** with F6 and G2.
  6. **S17,** with F3, and G6 unless its study keeps the pool.
  7. **S23,** with F8.
  8. **S24,** with the memory limit of F10.
- Comments never ride in a commit that moves code.

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
| N3 | `Storage` (the arena and the root layout) beside "the storage step", `ICommitStorage`, `MemoryOnlyStorage` (durability) | Decide one word for each of the two | done in part (S14R, part 2): "storage" is the form of the entities in memory, and "persistence" what makes a write durable, the contract's own word (`LatticePersistenceModeEnum`): `IPersistence`, `MemoryOnlyPersistence` and the persistence step in the engine, the measurement program, `TestSupport` and `docs/design`. The word of the public contract for the durable store of a provider, "storage" (`LatticeDatabaseOptions` with its `StorageConnectionString`, and the documentation of four other public types), is decided with the contract of stage 6, before S26. Left, and taken (S15R): the message of `IndeterminateOutcome`, "The storage step took the write…", which applications see, and the names and comments of the tests that say "storage step", about a hundred places |
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
| D5 | `Engine/Delivery/TransactionEncoder.cs`; `Replication/Transactions/TransactionFormat.cs` | The envelopes of the effects of a transaction are written by hand in one folder and read in another, and each writer welds the header of the transaction to its one effect | `TransactionFormat` writes the three envelopes beside the code that reads them; the bytes are identical | joined (S17), which splits those methods |
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
[epic](epic-1-core.md).

## Architect's review after S14, 19 November 2026

Its judgement: the folders and the direction of dependencies are sound (`Storage/Ordered`
→ `Engine/Indexes` → `Entities` → `Sets`); the debt is in three places that S14, part 2,
S15 and S17 all change next — the reservation in `OrderedTree`, two forms of a key, and
the decision a write makes for each index, which nothing keeps. Read at `main` 53b404c,
whose line numbers these are. Gone through by the planner on the same day. An entry that
changes a measured figure is not refactoring and is joined to S14, part 2 of the
[epic](epic-1-core.md).

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| E1 | `Engine/Indexes/OrderedTree.cs` 26–44, 217–270, 426–542 | `Bound` is the target of every top-up and trim, while the real invariant, `Need`, is used only by a Debug assertion; the rule of growth, one level for each `LeastSeparators` inserts, is written three times | `Need` in closed form as the only target and the asserted invariant; one function for the growth; `Bound` gone. A reservation for each change is not plainer: a publication removes the old entries after all its inserts | `IndexPreparationTests`, `RefusedReservationTests`, the Debug assertion across the suite, and a test of the closed form against the loop | joined (S14, part 2), the first commit of its (b): it reserves fewer nodes, so it changes a figure and is not refactoring |
| E2 | `Engine/Indexes/SecondaryIndex.cs` (`Change` 201–225, called from four members; the scratch `_old` and `_new` 55–57); `Engine/Entities/PreparedChange.cs` | Both entries are rebuilt at every step, and the decision for each index is kept nowhere | The reservation records the decision with the prepared change, as a mask; the release, the installation, the removal and the five `Indexes.IsEmpty` guards of `ChangePublisher` read it | — | joined (S14, part 2), its (a) and (b): it changes figures |
| E3 | `Engine/Indexes/IndexOrder.cs` 81–110, 118–153, 157–201; `SecondaryIndex.HasKey` 96–133 | The comparison of one part — null, text, fixed — is written three times, and the check of a hint uses `TextEquals` while the tree orders by `CompareText`: two algorithms that must agree. A fourth loop, `IndexOrder.SameKey` on two roots, came with the change of each index decided once (S14, part 2) | The offset of a part in a search key compiled once into its `EntryPart`, and read by the constructor of `SearchKey`, `CompareKey` and `HasKey`, `FixedAt` taking the part; one `NullOrder` for the rule of nulls, written four times today. The four loops stay: they read different sources, an entry, a probe and a root, and a generic source of a part would cost more than it removes on the hottest path (G1 of the architect's review after S14, part 2) | index-search on one thread, the upkeep of an index against the bare tree, the 328 bytes of a search | done (S14R, part 1): the offset of each fixed part compiled into its `EntryPart`, one `NullOrder`, the four loops kept; `HasKey` joined (S14, part 2), its (d) |
| E4 | `Engine/Indexes/SearchKey.cs` 36, 48, 105–120 | The constructor demands the full key and copies the texts, so leading parts, bounds and a key to resume from have no form; the order of the identities is derived twice | The caller gives the bound parts and owns the texts as spans; `RootHandle` exposes its packed value once | `SearchAllocationTests`, `PausedSearchTests` | joined (S14, part 2), its (c) |
| E5 | `src/Moltaro.Lattice.Measurements/Scenarios/IndexSearchScenario.cs` 157–257 | B2 has grown to five copies: the window of the index scenarios calls a delegate for each step | At B2 | At B2 | joined to B2, which is taken now |
| E6 | `Engine/Entities/EntityTable.cs` 520–569, 666–693 (772 lines); `Engine/Entities/EntityPass.cs` 39–157 | The walk of S15 would land in the table | The read limits as one small type that `EntityPass` and `IndexSearch` both hold; one descent of a reader in `OrderedTree` that yields the leaf, the place and the fence in either direction — today `TryFindFirst` returns only an identity and descends twice | — | joined (S15) |
| E7 | `Storage/Ordered/NodeStorage.cs` 21–85; `IndexLayout.SharedTexts` 278–306 and `SharedTextSlotBytes` 311–334; `OrderedTree.PublishTexts` 964–978 | The walk over the shared keys of an entry is written several times | One member that yields the descriptors | — | done (S14R, part 1): `SharedTexts` yields the descriptors; the references of a text stay for each node (F8) |
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
| F3 | `src/Moltaro.Lattice.Engine/Engine/Indexes/OrderedTree.cs` (`TryReserve`, the managed room of the pool) | The pool is grown to the whole need (`Stack.EnsureCapacity`) before the arena is asked, so a need the memory limit cannot hold first takes a managed array of about eight bytes a node — some 17 GB at the largest need an `int` counts — and keeps it, since the pool never shrinks. The write is still refused, so this is a cost, not a wrong success; `main` does the same with `Bound`, which is larger. The writer's path is grown the same way before the arena is asked and stays too, a few KB beside the pool's tens of MB. No mode built today lets enough writes wait to reach it | A reservation that is refused leaves no managed room behind either, the pool's nor the path's: the managed room of the pool is taken in step with what the arena gives, and a pool that grew for writes that are gone gives it back | taken (S17, with the linear bound of the reservation) |
| F4 | `src/Moltaro.Lattice.Engine/Engine/Comparison/ValueComparer.cs`, its head | About sixty lines, against the twenty of the convention for the head of a type: equality, the reading of a text by pieces with its reasoning on surrogates, and the seeded hash, older than S14, part 2, which took out what it had copied from `docs/design/ordered-index.md`. It serves the primary index as well as the ordered one, and no design document has a section for the comparison of values, so the narrative has nowhere to go. Its paragraph on equality also says "No byte order of UTF-8 stands for UTF-16", which reads against the comparison of UTF-8 bytes with its one turn that the order now makes | A section on the comparison of values in a design document, and the head as what the type is for, what it guarantees and a pointer to that section | done (S14R, part 2), with G3: the head of `ValueComparer` in 17 lines; the comparison of a key in `docs/design/arena-protocol.md` |
| F5 | `src/Moltaro.Lattice.Engine/Engine/Entities/EntityTable.cs` (`TrySearch`, the condition that the end of a run is reported at the sequence before the step only when the sequence did not move) | No test holds the check. A mutant that drops it passes every test and every probe through the public API: a lazy `Find` drops the sequences of its steps, and `FindAsync` takes the largest of them, so no public path sees the sequence of the end step alone. A probe of the engine that takes the steps itself catches the mutant in every run, but it reaches the sequence only through private fields, and no member gives it. The check guards a replace or a delete of an entity after the identity, which publishes its position before it removes the old entry | The test, written with the first consumer of that sequence, the search resumed and paged, against the member that consumer needs; the probe kept with the outputs of the review of S14, part 2, outside the repository, is its starting point: 40 of 40 runs of one second caught the mutant, none of 55 failed on correct code, and the worst run caught it by 2 ends | taken (S15) |
| F6 | `src/Moltaro.Lattice.Engine/Storage/Ordered/IndexLayout.cs` (`SameEntry`), with `RootLayout.StoredFieldOf` | The decision whether a write changes an index looks up the stored field of each part four times on every write, through a call that is not inlined: two for whether it is null and two for its bytes. On the head of S14, part 2 this is about 15 ns an index on the workstation of the breakdown, the most of the 30 ns an index that a replace keeping every key costs on the rented machine, where the owner accepted 0.94 and 0.86 of the rate without indexes with three and six indexes | The stored fields of an index resolved once when its layout is compiled, so that the decision reads each part from a known offset, width and null bit, with the same rule as today and no second path beside it; then the line of index-writes measured again | taken (S16, after S14R has made the comparison of a part one) |
| F7 | `src/Moltaro.Lattice.Measurements/Scenarios/WriteCallersScenario.cs` and `ReadByKeyThreadsScenario.cs` | Both work on the users of the reference fixture, a type with three indexes, so neither measures a type without indexes, though the plan of the machine for S14, part 2 named them for it; that statement rested on the lines of `index-writes` with no index, one caller | A line of `write-callers` over a type without indexes, with its callers and its readers beside them, so that a start can state on many callers and readers that a type without indexes loses nothing | taken (the next sprint that starts the machine) |
| F8 | `src/Moltaro.Lattice.Engine/Storage/Ordered/NodeStorage.cs` (the references of a shared text) | Each node that holds an entry of a shared key takes a reference to its text entry, so every copy of a leaf takes one for each such entry and every free releases them. A variant that leaves the reference to the root of the entry, for each entry, measured on the rented machine with keys of 100 and 200 bytes: a replace that changes the key 1.45 to 1.51 times the head, an insert and a delete 1.9 to 2.1 with distinct heads, a search and a replace that keeps the key unchanged; its patch is kept with the outputs of the start of S14, part 2, outside the repository | The references of a shared text held for each entry. It changes who holds a reference to a shared text, a rule of the [decision on strings](../decisions/strings.md), and its safety rests on the order of retirement: the owner's question, which the planner brings | taken (S23, decided by the owner on 20 November 2026, as the planner relays; it returns earlier only if a target for the writes of long text keys is set and missed) |
| F9 | `src/Moltaro.Lattice.Measurements/Scenarios/IndexReadByKeyScenario.cs`, the line of 0 indexes, 1 thread, alone, before and after B2 | On the same engine the harness of B2 reads that line at 0.888 of the old one, in rounds of 0.94, 0.87 and 0.89, where its gauge is a single pair at 1.066; every other judged line agrees. Until the difference has a cause, the figures of that line recorded before B2 are not compared with figures after it | The cause found by reading what the old and the new harness do for that line: the window, the warm-up and what lies inside the timed region; if reading does not show it, that start carries a series of three builds for this line | taken (before the next start of the machine) |
| F10 | `src/Moltaro.Lattice.Samples/Limits/ReadLimitsSample.cs` and `MemoryLimitSample.cs` | Both samples show their limit held and never reached, so the block that catches the refusal never runs: a sample of a limit teaches half of it | The sample of the read limits shows a limit reached and the error an application sees, in S15, part 1, whose tests and sample extend those limits to a search; the sample of the memory limit shows a write refused for memory and what the application does next, in S24. Each still runs to the end: its catch block reports and the program goes on | taken (S15, part 1, the read limits; S24, the memory limit) |
| F11 | `src/Moltaro.Lattice.TestSupport/Replication/ClosedPort.cs` and `ListeningPort.cs`, with the tests that free a port for a master to open at, `OpenTimeoutTests.Child_started_before_its_master_becomes_ready_when_the_master_opens_within_its_OpenTimeout` among them | Between freeing the port and the master binding it, any socket can take the port. The test then fails loudly, never with a false pass: it did once, AddressAlreadyInUse, in a Release run of S14R while another agent ran the suite on the same workstation | A master that takes a listener the test already holds, so that no port is ever free between the two | taken (the first sprint that touches the opening of the listener of a master; earlier if it fails once more on a machine that is not loaded) |
| F12 | `src/Moltaro.Lattice.Engine/Engine/Writes/WriteExecutor.cs` (`SlotBytes`), with `Replication/Load/BlockImport.cs` (`Place`) | For a root a forwarded write places on the master, the encoded size counts no new text entry: `Place` discards the new bytes `TextTable.TryAcquire` reports, where a local write counts them (`RootWriter.PlaceText`). That size is `PreparedWrite.EncodedSize`, which bounds a commit group and feeds `LargestCommitGroupBytes`, so the same write may count less because a child sent it | Test first, by a test author: the same write, with a new text long enough to be a text entry, made on a master and forwarded by a child, counts the same in the size of its commit group, stated through `LargestCommitGroupBytes` and by the bound of a group where that can be shown deterministically. Before it is written, the decision on write limits is read for what the encoded size is defined to count; if it defines a forwarded root otherwise, the planner hears it first. If the test passes on `main`, the entry closes with the reason the sizes are equal; if it fails, the implementer fixes it, one reviewer | done (a light change of its own, 21 November 2026): the test failed as the entry said, the forwarded write counting 112 bytes, its root alone, where the same write made on the master counted 1,392 in UTF-8 and 2,160 in UTF-16, and two writes of 5 MiB sharing one group. `BlockImport.Place` now counts the slot bytes of what it places as `RootWriter` does, the new bytes of a text entry included, and `WriteExecutor.SlotBytes`, the walk that counted afterwards, is gone |

## Architect's review after S14, part 2, 20 November 2026

Read at `main` 22b8ee1. The sprint added no type and no folder, and the direction
`Storage/Ordered` → `Engine/Indexes` → `Entities` → `Sets` holds. Gone through by the
planner and the coordinator on the same day.

| # | Place | What is wrong | The simpler form | What must be shown unchanged | Status |
| --- | --- | --- | --- | --- | --- |
| G1 | `Engine/Indexes/IndexOrder.cs` (`CompareKey`, `HasKey`, `CompareKeys`, `SameKey` on two roots); `Engine/Indexes/SearchKey.cs` (the constructor, `FixedAt`); `Storage/Ordered/EntryPart.cs` | Where a fixed part lies in the scratch of a search key depends on the layout alone, yet three loops count it with a running offset; the P1 of S14, part 2 was one of them not stepping past a null part | The form of E3 | index-search on one thread; the upkeep of an index 1.27 times the bare tree; the 328 bytes of a search | done (S14R, part 1), as E3 |
| G2 | `Engine/Entities/ChangePublisher.cs` (`ReplacedBy`, the parameter `replaced` of `ChangeAt`, `ReleaseIndexes`, `RemoveOld`, `MarkReplace`, `StampDelete`, `Publish`); `Engine/Entities/PreparedChange.cs` (`Replaced`); `Engine/Writes/PreparedWrite.cs` (`Changes`) | The replaced root reaches the upkeep three ways by phase: from the root entry before the publication, from `change.Replaced` after `MarkReplace`, from `entry.Root` again for a delete | `TryReserve` records `Replaced` for a replace and a delete; `ChangeAt` takes the change alone; `ReplacedBy` and three parameters go; `Publish` and `PreparedWrite.Changes` take a `ReadOnlySpan` | The order of the stores and both fault points; a MemoryOnly write allocates nothing; index-writes with no index, the replace that keeps every key and write-callers, on the machine; no added call for a type without an index in the listing of `TryReserve` | taken (S16, its first structural commit, beside F6): it moves a load into the preparation of every write and owes figures of the machine, which S14R has no start for |
| G3 | `Engine/Entities/EntityTable.cs` (its head of 108 lines; the comment of `TrySearch`); `Engine/Entities/IndexSearch.cs` (its head); `docs/design/ordered-index.md`, "The search by a full key"; `Storage/Ordered/IndexLayout.cs` (`WriteEntry`) | Why a step may report the end at the sequence before it is argued three times in three wordings; the head of `EntityTable` has had no owner since A6; `WriteEntry` still says a reader checks a root with it | `ordered-index.md` owns the argument, and `TrySearch` keeps the condition and the one order it rests on in about five lines; the head of `EntityTable` says what the type is for and points onwards, its narrative of a read by key and of an enumeration moving to a design document in the same commit; the stale sentence goes | Nothing measured; comments only | done (S14R, part 2), with F4: "The reads of an entity table" in `docs/design/arena-protocol.md`; the heads of `EntityTable` and `IndexSearch` point to it and to `ordered-index.md`, which owns the argument of the early end; `TrySearch` keeps the condition of the early end and the one order it rests on; the stale sentence of `WriteEntry` removed |
| G4 | `Engine/Entities/IndexSearch.cs` (`TryReadNext`); `EntityTable.TrySearch` (`ref RootHandle from, ref ulong? endsAt`); `Engine/Sets/SetOperations.cs` | The state of a search is a struct of two private fields whose one member passes them by reference into the table: three hops and seven parameters for a step, and S15 adds the key to resume after, the direction, the bounds, Offset, Limit and the read limits | `TrySearch(index, key, ref IndexSearch search, ref copy, out sequence, token)`; `IndexSearch` is plain state and `TryReadNext` goes; `SetOperations.Next` keeps "moved past only after the build" by stepping a copy; `probe.After(identity).From` is `identity.Next` | The 328 and 320 bytes of a search; index-search | taken (S15, part 1, with E6, its first structural commit) |
| G5 | `Engine/Indexes/SecondaryIndex.cs` (`TryReserve`, `Release`); `ChangePublisher.cs` (`index.Tree.GiveBackReserve()`, `indexes[index].Tree.TopUp()`) | The counts a change needs of an index, inserts and removes, are written twice as two switches, and the publisher reaches into the tree for the top-up and the give-back | One private mapping to the pair of counts, used by both; `TopUp` and `GiveBackReserve` on `SecondaryIndex`, so that the publisher speaks to one type; S17 sums those counts over a batch | index-writes, every line; the Debug assertion of the reservation across the suite | done (S14R, part 1) |
| G6 | `Engine/Indexes/OrderedTree.cs` (1,215 lines): the pool, its counts and places, `TryReserve`, `TopUp`, `Release`, `GiveBackReserve`, `Trim`, `Need`, `Growth`, `StandingReserve`, `TakeNode`, `GiveBack` | One type holds the copy-on-write tree and the policy of its pool of nodes, interleaved, with separate invariants and separate sections of the design | A sealed type for the pool, given the height and the room of the root, from which the tree takes its nodes. It adds a type and removes none; it pays only because S17 then rewrites one small type with one invariant | The upkeep 1.27 times the bare tree; a publication allocates nothing; prepared bytes at most the standing reserve; the closed form against the loop | taken (S17, its first structural commit after its study), refused if the study keeps the pool as it is |
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

