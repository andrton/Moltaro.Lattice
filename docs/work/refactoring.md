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

## Review of understandability, 18 November 2026

A reader with the code alone and none of the documents explained the four main flows and
said where it got lost. Scores from 1 (reads like a book) to 5 (cannot be followed without
its authors): a write on a master 4; a read by key 2.5; the open of a child 3; following
and a write through a child 4.

### Structure

| # | Place | What is hard | The simpler form | Status |
| --- | --- | --- | --- | --- |
| U1 | `Engine/Writes/WriteCoordinator.cs` (1,591 lines) | Eight jobs in one type: admission, the order of one entity, the prepared queue and the group, the resolution of a write refused for memory, cancellation, the fan-out of a fault, the delivery of outcomes, list plumbing. One write runs through twelve methods that pass a mutable `ref Pass` whose fields are instructions for a later method (596–689, 754–794, 1131–1166, 1437–1472, 1528–1590) | The resolution of a refused write (883–1125, `_resolving`, the held list) as a type of its own. `Pass` declared at the top with, per field, who sets it and who uses it. A short method for the common path that reads straight through | open |
| U2 | `Engine/Writes/PendingWrite.cs` 81–104; `WriteCoordinator.cs` 221–243, 429–498, 931–991 | Four intrusive lists through one record; `Next` serves three of them; one list has no name (`List`/`Unlist`/`Listed`); `Unlist` also starts another write's resolution | One small intrusive-queue type; a noun for the list of waiting writes; the side effect moved to the caller | open |
| U3 | `WriteCoordinator.cs` 111–150, 883–1112; `PendingWrite.cs` 117–119, 212–222 | The states of the resolution: `Reading` means waiting for readers to leave; `PendingWrite.Execute` is one of two things by a flag | With U1: its own type, states named for what is waited for | open |
| U4 | `Engine/Entities/EntityTable.cs` (917 lines) | Eight jobs: the layout, the seeded hashes of keys, a facade over the primary index, the read by key, the step of an enumeration, the reader's half of the stamp protocol, the wire format of keys (310–413), helpers for stored keys | The wire format of keys to `Replication/Transactions`; the hashes to a type of their own; the enumeration step beside `EntityPass` | open |
| U5 | `EntityTable.cs` 16–52, 801–916; `Engine/Writes/WriteExecutor.cs` 16–21, 631–731 | The stamp protocol of an entry is explained as differences from another project's lock, and its reader and writer halves are in two files; "0 means in progress" is a constant of one used by the other | One small type that holds mark, stamp and validation, with a description of ten lines that stands alone; the attribution in one line | open |
| U6 | `Engine/EngineDatabase.cs` (1,090 lines) | Seven jobs; the length comes from six test hooks with about 100 lines of comment (311–441) and from a header of 127 lines that documents the whole engine | The test hooks in a partial file; the open of a child (801–955) into `Engine/Loading`; the header reduced to the map of the parts and the lock order, the rest moved to the types that own it | open |
| U7 | `EngineDatabase.cs` 581–724 | The close is spread over several members; the reason for the order is given for one step only | A numbered list in `CloseAsync` in the order of the code | open |
| U8 | `Engine/Writes/WriteExecutor.cs` 590–629, and the facade over the retired memory, the epochs and the clock | Eight one-line members that pass a call on, each with a comment whose step numbers belong to another file | The private methods made internal with one comment; the pure pass-throughs removed | open |
| U9 | The write through a child, as a whole | It crosses about fourteen types in six folders; no place lists the chain | One numbered end-to-end list in the header of `Forwarder` | open |
| U10 | `Engine/Delivery/RecordedReplies.cs` 10–21; `Engine/Children/ChildConnection.cs` 35–40, 244–256; `WriteExecutor.cs` 554–559; `WriteCoordinator.cs` 177–189 | "A reply precedes its transaction" is explained four times in four wordings | One owner (`RecordedReplies`); the others name it in a line | open |
| U11 | `EngineDatabase.cs` 969–1038; `WriteCoordinator.cs` 266–267; `Engine/Following/Follower.cs` 127–141 | On a child a `WriteCoordinator` is built that admits no write, the real writers take the lock directly, and the sender of the forwarder is made inside the follower | One comment in `Build` on what is live on a child; the sender made where the forwarder is | open |
| U12 | `Engine/Children/ChildConnection.cs` 204–231; `Follower.cs` 172–206 | Three sources of cancellation and "ended in order" decided by the type of an inner exception; in the follower a bare `catch`, `LoseMaster` called twice, and an order of two calls that carries the meaning | A named local with its reason; comments at those lines instead of in the header | open |

### Names

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| N1 | "S" and "R" of a load, used before they are defined, with other names in the fields (`Start`/`End`, `readiness`, `CaptureStart.Sequence`) | One pair of names everywhere, for example `captureStart` and `readinessPosition` | open |
| N2 | `Protocol.EndsARoot` marks the last frame of any message | `EndsAMessage` | open |
| N3 | `Storage` (the arena and the root layout) beside "the storage step", `ICommitStorage`, `MemoryOnlyStorage` (durability) | Decide one word for each of the two | open |
| N4 | Two folders `Forwarding`; "Forwarded…" types in five places; `RecordedReplies` in `Delivery` | One home for what belongs to a forwarded write on each side | open |
| N5 | One word with several meanings: `Pass` (three), `Capture` (two), `Deliver` (two), "hold" (three); `Reading` and `ReadsHere` for "waiting for readers" | A word of its own for each meaning | open |
| N6 | Method names too general to search for or to tell apart in `WriteCoordinator`: `List`, `Outcome`, `TryLast`, `Offer`, `Drive`, `Finish`, `End`, `Complete`, `Succeed` | Names that say what is done to what | open |
| N7 | `MasterLink` lives in `Loading` and serves the child for its whole life; `SetOperations` is where a master and a child part, and its name does not say so | Move the first; say it in the header of the second | open |

### Comments

About 27 % of the lines are comments. They are accurate, and those at methods help — above
all the prefixes "Under the lock:", "After the lock:", "For the sender:". Two things work
against a reader who was not here:

| # | What | The simpler form | Status |
| --- | --- | --- | --- |
| C1 | About 380 lines in 94 files cite a sprint, a question, a "brief", a decision of the planner or an exception by its number (for example `WriteCoordinator.cs` 111–113: three citations and no content; `EngineDatabase.cs` 48, 70–72) | A comment states the rule and its reason in its own words. A reference to a decision record by its file is welcome; a sprint number, a date or "the planner's point 4" is not. A convention for `AGENTS.md`, then one pass over the engine | open |
| C2 | Class headers open with where a rule came from, and several are the length of a document (`WriteCoordinator.cs` 9–198, `EngineDatabase.cs` 27–153, `EntityTable.cs` 116 lines, `WriteExecutor.cs` 105) | A header says first what the type is for and its common path, in about fifteen lines; the history of measurements leaves the code for the document that owns it | open |
| C3 | Prose idiom that has to be learned: "the lock of the writes", "a hold", "the protection of reads", "the storage step" | A short glossary in one place that the headers can assume; or plainer words | open |
| C4 | `Engine/Loading/ReceivedChanges.cs` 10–65: the proof of the one rule and "what a read by key sees" are single paragraphs of eight and twenty lines | A line per case, as the header of `WriteExecutor` does (72–81) | open |

### Sound, leave as it is

`LatticeDatabase` and the folder `Sets`; `CloseBarrier`; `MemoryOnlyStorage` and
`ICommitStorage`; the code of `PositionClock`; `ForwardedCall`, `ChildSender` and
`PendingRequests`; `RootPlacement` and the body of `LoadFromMasterAsync`; the code of
`ReceivedChanges`; `RootWriter`, whose length is justified. Conventions to keep: one type
per file; the prefixes that state the locking context; the sections "The waits, and how
each ends" and "Who calls what"; attribution of adapted code with its commit; measured
figures beside a tuning constant.

## Architect's review of stages 2 and 3, 18 November 2026

Running; its entries are added here when it returns.
