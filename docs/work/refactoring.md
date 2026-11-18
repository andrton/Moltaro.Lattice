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

- **S13R**, after S13 and the fix package of the outside review, before S14: A7, A2, A1,
  A6, A9 in that order, by one implementer. Each is a commit of its own with the whole
  suite passing; after A2, A6 and A9 the write and the read by key are measured at 1 and
  16 threads with the allocation of each, so that a fall names its entry. Then the small
  entries in the same files, and the figure for the address validation of
  `Arena.Payload` and `Arena.Header`: no change of it without that figure.
- **S14R**, after S14: A3, A4 (in before S18), A5, A11, A12, A13.
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
| U2 | `Engine/Writes/PendingWrite.cs` 81–104; `WriteCoordinator.cs` 221–243, 429–498, 931–991 | Four intrusive lists through one record; `Next` serves three of them; one list has no name (`List`/`Unlist`/`Listed`); `Unlist` also starts another write's resolution | One small intrusive-queue type; a noun for the list of waiting writes; the side effect moved to the caller | taken (S13R) |
| U3 | `WriteCoordinator.cs` 111–150, 883–1112; `PendingWrite.cs` 117–119, 212–222 | The states of the resolution: `Reading` means waiting for readers to leave; `PendingWrite.Execute` is one of two things by a flag | With U1: its own type, states named for what is waited for | joined (S27), beyond what A9 takes |
| U4 | `Engine/Entities/EntityTable.cs` (917 lines) | Eight jobs: the layout, the seeded hashes of keys, a facade over the primary index, the read by key, the step of an enumeration, the reader's half of the stamp protocol, the wire format of keys (310–413), helpers for stored keys | The wire format of keys to `Replication/Transactions`; the hashes to a type of their own; the enumeration step beside `EntityPass` | taken (S13R) |
| U5 | `EntityTable.cs` 16–52, 801–916; `Engine/Writes/WriteExecutor.cs` 16–21, 631–731 | The stamp protocol of an entry is explained as differences from another project's lock, and its reader and writer halves are in two files; "0 means in progress" is a constant of one used by the other | One small type that holds mark, stamp and validation, with a description of ten lines that stands alone; the attribution in one line | taken (S13R): a self-standing description of the protocol only, no restructuring |
| U6 | `Engine/EngineDatabase.cs` (1,090 lines) | Seven jobs; the length comes from six test hooks with about 100 lines of comment (311–441) and from a header of 127 lines that documents the whole engine | The test hooks in a partial file; the open of a child (801–955) into `Engine/Loading`; the header reduced to the map of the parts and the lock order, the rest moved to the types that own it | taken (S14R) |
| U7 | `EngineDatabase.cs` 581–724 | The close is spread over several members; the reason for the order is given for one step only | A numbered list in `CloseAsync` in the order of the code | taken (S13R) |
| U8 | `Engine/Writes/WriteExecutor.cs` 590–629, and the facade over the retired memory, the epochs and the clock | Eight one-line members that pass a call on, each with a comment whose step numbers belong to another file | The private methods made internal with one comment; the pure pass-throughs removed | taken (S13R) |
| U9 | The write through a child, as a whole | It crosses about fourteen types in six folders; no place lists the chain | One numbered end-to-end list in the header of `Forwarder` | taken (S13R) |
| U10 | `Engine/Delivery/RecordedReplies.cs` 10–21; `Engine/Children/ChildConnection.cs` 35–40, 244–256; `WriteExecutor.cs` 554–559; `WriteCoordinator.cs` 177–189 | "A reply precedes its transaction" is explained four times in four wordings | One owner (`RecordedReplies`); the others name it in a line | taken (S13R) |
| U11 | `EngineDatabase.cs` 969–1038; `WriteCoordinator.cs` 266–267; `Engine/Following/Follower.cs` 127–141 | On a child a `WriteCoordinator` is built that admits no write, the real writers take the lock directly, and the sender of the forwarder is made inside the follower | One comment in `Build` on what is live on a child; the sender made where the forwarder is | taken (S14R) |
| U12 | `Engine/Children/ChildConnection.cs` 204–231; `Follower.cs` 172–206 | Three sources of cancellation and "ended in order" decided by the type of an inner exception; in the follower a bare `catch`, `LoseMaster` called twice, and an order of two calls that carries the meaning | A named local with its reason; comments at those lines instead of in the header | taken (S13R) |

### Names

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| N1 | "S" and "R" of a load, used before they are defined, with other names in the fields (`Start`/`End`, `readiness`, `CaptureStart.Sequence`) | One pair of names everywhere, for example `captureStart` and `readinessPosition` | taken (S13R) |
| N2 | `Protocol.EndsARoot` marks the last frame of any message | `EndsAMessage` | taken (S13R) |
| N3 | `Storage` (the arena and the root layout) beside "the storage step", `ICommitStorage`, `MemoryOnlyStorage` (durability) | Decide one word for each of the two | taken (S14R), with the files as they are touched |
| N4 | Two folders `Forwarding`; "Forwarded…" types in five places; `RecordedReplies` in `Delivery` | One home for what belongs to a forwarded write on each side | taken (S14R), with the files as they are touched |
| N5 | One word with several meanings: `Pass` (three), `Capture` (two), `Deliver` (two), "hold" (three); `Reading` and `ReadsHere` for "waiting for readers" | A word of its own for each meaning | taken (S14R), with the files as they are touched |
| N6 | Method names too general to search for or to tell apart in `WriteCoordinator`: `List`, `Outcome`, `TryLast`, `Offer`, `Drive`, `Finish`, `End`, `Complete`, `Succeed` | Names that say what is done to what | taken (S14R), with the files as they are touched |
| N7 | `MasterLink` lives in `Loading` and serves the child for its whole life; `SetOperations` is where a master and a child part, and its name does not say so | Move the first; say it in the header of the second | taken (S14R), with the files as they are touched |

### Comments

About 27 % of the lines are comments. They are accurate, and those at methods help — above
all the prefixes "Under the lock:", "After the lock:", "For the sender:". Two things work
against a reader who was not here:

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| C1 | About 380 lines in 94 files cite a sprint, a question, a "brief", a decision of the planner or an exception by its number (for example `WriteCoordinator.cs` 111–113: three citations and no content; `EngineDatabase.cs` 48, 70–72) | A comment states the rule and its reason in its own words. A reference to a decision record by its file is welcome; a sprint number, a date or "the planner's point 4" is not. A convention for `AGENTS.md`, then one pass over the engine | open: waits for the owner's answer on the convention for comments; then one commit of comments only at the end of S13R, in the files it touched |
| C2 | Class headers open with where a rule came from, and several are the length of a document (`WriteCoordinator.cs` 9–198, `EngineDatabase.cs` 27–153, `EntityTable.cs` 116 lines, `WriteExecutor.cs` 105) | A header says first what the type is for and its common path, in about fifteen lines; the history of measurements leaves the code for the document that owns it | open: waits for the owner's answer on the convention for comments; then one commit of comments only at the end of S13R, in the files it touched |
| C3 | Prose idiom that has to be learned: "the lock of the writes", "a hold", "the protection of reads", "the storage step" | A short glossary in one place that the headers can assume; or plainer words | taken (S14R), with the files as they are touched |
| C4 | `Engine/Loading/ReceivedChanges.cs` 10–65: the proof of the one rule and "what a read by key sees" are single paragraphs of eight and twenty lines | A line per case, as the header of `WriteExecutor` does (72–81) | open: waits for the owner's answer on the convention for comments; then one commit of comments only at the end of S13R, in the files it touched |

### Sound, leave as it is

`LatticeDatabase` and the folder `Sets`; `CloseBarrier`; `MemoryOnlyStorage` and
`ICommitStorage`; the code of `PositionClock`; `ForwardedCall`, `ChildSender` and
`PendingRequests`; `RootPlacement` and the body of `LoadFromMasterAsync`; the code of
`ReceivedChanges`; `RootWriter`, whose length is justified. Conventions to keep: one type
per file; the prefixes that state the locking context; the sections "The waits, and how
each ends" and "Who calls what"; attribution of adapted code with its commit; measured
figures beside a tuning constant.

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
| A1 | A child applies what it receives in three types of two folders: entering the barrier, the lock, try–drain–retry and the fault are written twice (`Engine/Loading/RootPlacement.cs` 90–112, `Engine/Following/Follower.cs` 329–387); the effects of a transaction are decoded inside `Follower` (421–467), far from their encoder; its constructor takes twelve parameters | `ReceivedChanges` owns the lock, the barrier and the fault, with two operations: place a root, apply a transaction. The decoding of effects moves beside `TransactionFormat`. `Follower` keeps receive, order check, suspension, apply, acknowledge. The three-step publication of several effects moves verbatim: it is reasoned, not tested, until S17 | M | A transaction is applied whole or not at all; a failed publication faults; the child scenarios of the measurement program | taken (S13R) |
| A2 | `Engine/Writes/WriteExecutor.cs`: thirteen members only pass a call on (244–276, 594–629), and the child's loader depends on the master's executor to reach the publication stores (643–731) | The five store sequences (install, mark and stamp of a replace and of a delete) move verbatim, with their comments, into one small sealed type that the master's publication and the child's application both call. No statement inside them moves: tests cannot see a reordering of those stores on x64 | M | The order of the stores and the fault points; the write scenario; the allocation tests of the writes | taken (S13R) |
| A3 | `Engine/EngineDatabase.cs`: twelve nullable fields set together and read with `!` about forty times; facade, life cycle, composition, the master's network, the child's load and test access in one file (with U6) | One immutable object of the parts, built by one method and published as one field; the body of the child's load into a type of `Engine/Loading`. The state transitions and the close stay | M | The order of refusals; the lock order; the close completing only after the arena is freed | taken (S14R) |
| A4 | Fourteen loops in six files walk the blocks a root owns and derive offset and kind again (`Storage/RootStorage.cs`, `RootWriter.cs`, `WriteExecutor.cs` 506–517, `Replication/Load/BlockExport.cs`, `BlockImport.cs`) | One enumerator in `Storage`, a ref struct with no allocation and no virtual call; free, publish, retire, the byte counts, export and import become its users | M | Counters equal the tally; no managed allocation; the write, load and root-bytes scenarios | taken (S14R); in before S18 |
| A5 | Three frame assemblers repeat one algorithm with the same checks and messages (`Replication/Load/RootAssembler.cs`, `Transactions/TransactionAssembler.cs`, `Forwarding/MessageAssembler.cs`) | One small struct for a body of an announced length; the three keep their headers | M | Every malformed frame refused as today, and the refusals for memory | taken (S14R) |
| A6 | About 300 of the 917 lines of `Engine/Entities/EntityTable.cs` are "the primary key of a type" and its wire format; its range check repeats `IntegerRange` of the core (with U4) | A sealed type for the keys of an entity type (kind, hash, equality, stored key) held in a readonly field; direct calls, no interface. The private copy of the range check goes | M | The read-by-key and enumeration scenarios; the allocation tests of a read by key | taken (S13R) |
| A7 | Four identical dictionaries from type id to table and three arrays of tables; `Engine/Children/ForwardedWrites.cs` exists only to carry three objects | One object for the tables of the database, built once; `ForwardedWrites` is deleted | S–M | An unknown or unstored type id refused as today | taken (S13R) |
| A8 | The envelope of a root on the wire (type id, last mutation) is read or written by hand in six places, and `Storage/RootWriter.cs` 493–525 writes a format of `Replication`; `ForwardEncoding.WriteRootLength` is called by nothing | One type for the envelope in `Replication/Load`; the export of `RootWriter` writes only what the block export writes | S | The bytes on the wire identical; the wire format stays 3 | taken (S13R) |
| A9 | `Engine/Writes/WriteCoordinator.cs`: four hand-linked lists, a block written twice (843–855, 1089–1101), and the pair "end, advance the chain" thirteen times (with U2) | A private struct for a queue of writes; one method for the pair; one for the block. No state, order or lock changes | S | A MemoryOnly write allocates nothing; the write scenarios | taken (S13R) |
| A10 | Comments (with C1 and C2): "brief of" 82 times, the planner 95, a date 65, a question id 67, a sprint number 237; the long class heads retell `docs/design/write-path.md` and `epoch-protection.md` | The convention of C1, applied to a file when an entry above touches it, never as one sweep; reasoning that exists nowhere else moves into `docs/design` in the same change | L in lines, none in behavior | — | open: waits for the owner's answer on the convention for comments; then one commit of comments only at the end of S13R, in the files it touched |
| A11 | `Storage`: the arena and the roots in one folder with `Values` between them, and `Values` uses `RootLayout` for three constants while the roots use `Values` | The roots into `Storage/Roots`; the three constants to `ValueBlocks`. Mechanical, touches about sixty files: scheduled alone | M | — | taken (S14R) |
| A12 | The child's side is spread over `Loading`, `Following`, `Forwarding` with `MasterLink` in `Loading`; the names do not say the side (with N4, N7) | One folder for the child's side with those three below it; the master's side stays `Children` and `Delivery`. After A1 and A3 | M | — | taken (S14R) |
| A13 | Members of the product that only tests use: the allocation that is live at once and `Arena.IsValid`, the unprepared mode of `RootWriter` and `ValueBlocks`, `IndexTable.cs` 312 | One allocation (prepared) and one constructor; the tests that want a live block publish it. About twenty test files change how they set up, not what they assert: the test author's work | M | The assertions of those tests | taken (S14R) |
| A14 | `Engine/Writes/RootValues.cs` is used by a read of a record and by the forwarder | It moves to `Engine/Entities` | S | — | taken (S13R) |
| A15 | Small duplicates: the strict UTF-8 decoder three times; a local `Invalid(string)` nine times; the message of a faulted database three times; a span copy loop twice that is `IBufferWriter.Write`; the wake pulse twice | One place each, in the type that owns the thing; messages unchanged | S | — | taken (S13R) |
| A16 | `EndsARoot` (N2) | `EndsABody`; the value on the wire unchanged | S | — | taken (S13R) |
| A17 | `src/Moltaro.Lattice/Storage/WriteKindEnum.cs`: one internal enum in a folder left from the placeholder, in a namespace the engine also uses | It moves to `Access`, where its users are; internal, so the contract does not change | S | — | taken (S13R) |

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

