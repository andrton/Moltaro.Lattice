# Exceptions to the rules

[AGENTS.md](../../AGENTS.md) owns the rules of the project. This document records every
place where the project departs from one of them: which rule, where, why, and until when.
It exists so that a departure is a decision somebody can find and question later, not
something a reader has to guess from the code.

## How an exception is recorded

- An exception is approved by the owner before it is used.
- It is entered under **Approved, not in force yet** when the owner approves it, moved to
  **In force** by the change that introduces the departure, and moved to **Ended** by the
  change that ends it.
- An entry names the rule in the words of `AGENTS.md`, the exact place of the departure,
  the reason the rule cannot be followed there, and what keeps the departure small.
- A temporary exception names the sprint or the event that ends it.

Some exceptions are also named in `AGENTS.md`, next to their rule, so that nobody takes
them for violations; this register gives each its reason and its bounds. A rule that is
replaced by another rule is reworded in `AGENTS.md` and is not recorded here.

## In force

### X1. The switch that holds back a child's replication

| | |
| --- | --- |
| **Rule** | Tests use the public API |
| **Departure** | The tests of `Moltaro.Lattice.Tests` call an internal member that suspends the application of changes on a child |
| **Why** | Waiting for a position can be tested deterministically only when the test decides when the child applies a change. Without the switch such a test would sleep and hope |
| **Kept small by** | One internal member, visible to the test project only; it does nothing but hold back application |
| **Since** | The first commit of the contract |
| **Until** | Permanent |

### X2. Prototypes pin their own package versions

| | |
| --- | --- |
| **Rule** | Package versions are managed centrally in `src/Directory.Packages.props` |
| **Departure** | Each project under `src/Prototypes` may have its own `Directory.Packages.props` and lockfile |
| **Why** | A prototype is evidence for a decision. Its measurements belong to the package versions it ran on, and moving it to a newer version would change what was measured |
| **Kept small by** | Prototypes are outside the product solution and are never shipped |
| **Since** | The transfer of the design reference, 14 November 2026 |
| **Until** | Permanent |

### X3. Tests of engine mechanisms

| | |
| --- | --- |
| **Rule** | Tests use the public API |
| **Departure** | The three test projects of the engine reach inside it. `Moltaro.Lattice.Engine.Tests` tests internal types. `Moltaro.Lattice.PostgreSql.Tests` reads the provider's tables and arms fault points inside the engine. `Moltaro.Lattice.Cluster.Tests` arms fault points and damages a connection on purpose. Two programs that are never shipped have the same access: `Moltaro.Lattice.TestNode` arms fault points inside its own process, and `Moltaro.Lattice.Measurements` uses internal types of the engine, opens it through its internal entry and reads its counters. `Moltaro.Lattice.Engine.Tests` also addresses the reference fixture, an internal type of `Moltaro.Lattice.Measurements`, through friend access |
| **Why** | Layout sizes, ownership and reference-count audits, allocator behavior and injected failures have no public surface, and the release checks of the engine require all of them. The first engine mechanisms also exist before any public call can reach them, and their tests have to be written first. The reference fixture is what every measurement and every gate is run on; a test has to state that it produces the same entities on every run, and the fixture belongs to the program that measures, not to a test project |
| **Kept small by** | Each such test states an invariant written in a decision record or in the engine contract, never how the code happens to be built. Whatever can be stated through the public API is tested only there. A fault point is internal, does nothing unless a test arms it, and cannot be reached through the public API. Of the measurement program the tests address the reference fixture and nothing else |
| **Approved** | By the owner, 14 November 2026, with the plan of [Epic 1](epic-1-core.md); the reference fixture by the owner, 15 November 2026 |
| **Since** | Epic 1, sprint S01, which creates the first of the projects and puts the exception into the text of `AGENTS.md`; the other projects and the two programs come with the sprints that create them |
| **Until** | Permanent |

### X4. An internal entry to the engine for its first tests

| | |
| --- | --- |
| **Rule** | Tests use the public API |
| **Departure** | The tests of the engine, and the scenario tests of `Moltaro.Lattice.Tests` when they run against the engine, open a database through an internal entry of the engine assembly instead of `LatticeDatabase`, and from there use only the public interfaces. One helper of the test project does the opening, for the placeholder and for the engine in turn |
| **Why** | Until the engine can do everything the placeholder does, the public entry point still opens the placeholder. The alternative is a switch between two implementations behind the contract, which the project does not want |
| **Kept small by** | Only the way a database is opened differs; every operation after it goes through the public interfaces |
| **Approved** | By the owner, 14 November 2026, with the plan of [Epic 1](epic-1-core.md) |
| **Since** | Epic 1, sprint S06 |
| **Until** | Epic 1, sprint S12, which switches the entry point to the engine, deletes the placeholder and removes the internal entry |

### X5. The schema names one type of the access namespace

| | |
| --- | --- |
| **Rule** | Inside an assembly folders depend on each other in one direction (principle 11) |
| **Departure** | `LatticeValueDeclaration` in `Moltaro.Lattice.Schema` names `LatticeOwnedValue` of `Moltaro.Lattice.Access` as the `ValueType` of an Owned position, while the access namespace is built on the schema |
| **Why** | `ValueType` says what a record holds at a position, and for an owned value that is `LatticeOwnedValue`. A value needs its type, and the type says what holds the value, so one of the two has to name the other. Moving `LatticeOwnedValue` into the schema namespace would take the checking and copying of record values with it and part it from `LatticeRecord`; taking `ValueType` out of the schema would change a public member that the binding, the validation and the documentation of two sprints stand on |
| **Kept small by** | One reference, a `typeof`, in one place, with a comment. The schema calls no member of the access namespace and holds no object of it |
| **Approved** | By the owner, 14 November 2026 |
| **Since** | Epic 1, sprint S03 |
| **Until** | Permanent, or until `ValueType` leaves the schema for another reason |

### X6. Waits inside the engine that have no timeout

| | |
| --- | --- |
| **Rule** | Every wait has a timeout and honors cancellation (principle 9) |
| **Departure** | Three kinds of waits of the engine have no timeout. Waits for a step of a read: a read that finds every slot of the epoch table held waits for one (with the token of the call wherever the caller gave one; the synchronous forms have none); a read whose entity is between the stores of a write being published retries until the write has stored or the instance has faulted; a write that resolves a refusal of the arena waits for the reads that hold retired memory; a close waits for the reads in progress. Waits of a write, before its dispatch, for other writes (since S09, part 2): for the lock of the writes, which is held for work in memory only; behind the latest earlier write of its entity that has not completed; prepared, in the queue, while a group is in the storage step; held back while a refused write is resolved; and, when the arena refused it, for every write admitted before it and for the writes prepared and unpublished while it holds back the preparation of later ones. A close waits for the writes admitted and for a storage step in progress. Waits of the replication transport (since S10): a master's send to a child that does not read waits until the child takes the bytes, with no deadline until S33, which bounds delivery to a slow child; the close of the master ends it by resetting the connection. A master waits for the end of the connection of a loaded child without a deadline: until S11 nothing travels on it, and the child's close, its loss or the master's close ends it; while it lasts the master refuses writes. The pauses between the attempts of a child to reach its master (100 ms, doubling, at most 5 s) are constants in the code until S34, which builds reconnection and makes them options; `OpenTimeout` bounds the whole effort |
| **Why** | A wait for a step of a read waits for engine code alone, which waits for nothing, does no I/O and runs no code of the application; a publication that fails between its stores does not leave a read retrying, because a read that retries checks the state of the instance and reports `Unavailable` once it is `Faulted`. A wait of a write for other writes ends when those writes end, and each of them ends — published, refused, cancelled before its dispatch or ended by a fault — within its own waits; a write that resolves a refusal waits only for writes that were in flight when it began, and no write admitted later prolongs it. Every such wait of a write but the one for the lock of the writes has the token of the write and is outside every lock; a token cancelled before the dispatch always ends the write as cancelled. The lock of the writes is held for as long as one write takes to be prepared and published in memory, milliseconds for an entity of 32 MiB, and it is not fair: a caller may be passed by others for about 100 ms, which the measurement of large writes in the document of Epic 1 states. These are waits of the kind taking a lock is, and the timeouts of principle 9 are for waits an application can make long. A timeout would turn a wait that always ends into a refusal under ordinary load, which the same principle forbids. The storage step of MemoryOnly completes at once; the durable one gets its timeout with its sprint (S27). The two waits of the transport are for a peer and can be made long by it; they stand only until the sprints that bound them, because a master of S10 has nothing else to do for a child than to load it, and a deadline on a send is the rule of a slow child, which is S33 |
| **Kept small by** | The rule that nothing waits and no code of the application runs inside the protection of a read, stated in the arena protocol, "Deferred reclamation", and held by tests. A write waits outside every lock. Each wait says in a comment why it ends, and the comment of the write coordinator lists them |
| **Approved** | By the planner, 16 November 2026, with the brief of S08, part 1; reported to the owner at the close of that sprint. The waits of a write restated by the planner on the same day with the brief of S09 and again after its review, and by the coordinator on 17 November 2026 for the write path of S09, part 2; the resolution of a refused write is the planner's reading of decision O20, reported to the owner, as the amendment of Q14 in the decision on write admission says. The waits of the transport by the planner, 17 November 2026, in the brief of S10 (point 10 and the decisions after the study, point 5); the pauses by the coordinator at the review of S10 |
| **Since** | Epic 1, sprint S08, part 1 |
| **Until** | Permanent for the waits of reads and writes. The waits of the transport until S33; the pauses of a child's attempts until S34 |

## Approved, not in force yet

None.

## Ended

None yet.
