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
| **Departure** | The three test projects of the engine reach inside it. `Moltaro.Lattice.Engine.Tests` tests internal types. `Moltaro.Lattice.PostgreSql.Tests` reads the provider's tables and arms fault points inside the engine. `Moltaro.Lattice.Cluster.Tests` arms fault points and damages a connection on purpose. Two programs that are never shipped have the same access: `Moltaro.Lattice.TestNode` arms fault points inside its own process, and `Moltaro.Lattice.Measurements` uses internal types of the engine and reads its counters. `Moltaro.Lattice.Engine.Tests` also addresses the reference fixture, an internal type of `Moltaro.Lattice.Measurements`, through friend access |
| **Why** | Layout sizes, ownership and reference-count audits, allocator behavior and injected failures have no public surface, and the release checks of the engine require all of them. The first engine mechanisms also exist before any public call can reach them, and their tests have to be written first. The reference fixture is what every measurement and every gate is run on; a test has to state that it produces the same entities on every run, and the fixture belongs to the program that measures, not to a test project |
| **Kept small by** | Each such test states an invariant written in a decision record or in the engine contract, never how the code happens to be built. Whatever can be stated through the public API is tested only there. A fault point is internal, does nothing unless a test arms it, and cannot be reached through the public API. Of the measurement program the tests address the reference fixture and nothing else |
| **Approved** | By the owner, 14 November 2026, with the plan of [Epic 1](epic-1-core.md); the reference fixture by the owner, 15 November 2026 |
| **Since** | Epic 1, sprint S01, which creates the first of the projects and puts the exception into the text of `AGENTS.md`; the other projects and the two programs come with the sprints that create them |
| **Until** | Permanent |

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
| **Rule** | Every wait has a timeout and honors cancellation; a timeout an application can observe is an option, never a constant (principle 9) |
| **Departure** | Waits of the engine of these kinds have no timeout. Waits for a step of a read: a read that finds every slot of the epoch table held waits for one (with the token of the call wherever the caller gave one; the synchronous forms have none); a read whose entity is between the stores of a write being published retries until the write has stored or the instance has faulted; a write that resolves a refusal of the arena waits for the reads that hold retired memory; a close waits for the reads in progress. Waits of a write, before its dispatch, for other writes (since S09, part 2): for the lock of the writes, which is held for work in memory only; behind the latest earlier write of its entity that has not completed; prepared, in the queue, while a group is in the persistence step; deferred while a refused write is resolved; and, when the arena refused it, for every write admitted before it and for the writes prepared and unpublished while it holds back the preparation of later ones. A write whose unique value is claimed, or about to be freed, by a prepared and unpublished write of another entity waits for that one write (since S16). A close waits for the writes admitted and for a persistence step in progress. Waits of the replication transport (since S10): a master's send to a child that does not read waits until the child takes the bytes, with no deadline until S33, which bounds delivery to a slow child; the close of the master ends it: since S12 a master that closes ends each connection in order — its replies, what the tail holds for that child, the frame Closed, the end of its sending side — and waits for the child to end its side, all connections together, for 5 s, after which it resets those still open. The 5 s are a constant in the code until S33, where the deadline of a send to a child that does not read becomes an option and bounds this wait too. Until then `ConnectionEndTests` asserts this wait as a range around the constant, 4.5 s to 9 s, and so depends on the load of the machine; it becomes deterministic with the option. After a load (since S11) the two tasks of a master for a child wait without a deadline: its sender for the next transaction of the tail, its receiver for the next Applied of the child; the end of the connection — the child's close or loss, the end of the other task — or the master's close ends each. A Ready child waits for the next frame of its stream without a deadline, and the end of the connection or its close ends that wait; a master that vanishes without closing the connection leaves the child Ready at its position and waiting, until S34. The sends of a child — its requests and its Applied, by its one sender since S12 — wait until the master has taken the bytes, which it does while it serves the child; the end of the link, the close of the child included, ends them, and between sends the sender waits for a call or for the follower to wake it. A write forwarded by a child (since S12) waits for its reply with no timeout and, once its first frame has begun, without the token of the call: the reply, the frame Closed, the end of the connection or the close of the child ends it, and a master that vanishes without closing the connection leaves it waiting, until S34. The receiver of a master for a child that received Closed keeps reading and dropping until the child ends its side, inside the 5 s of the close. A child whose arena refused a received change waits for the reads that hold retired memory, as a write does, with its close in the token. The internal switch of the tests that holds back a child's application holds it until it is disposed or the child closes. The pauses between the attempts of a child to reach its master (100 ms, doubling, at most 5 s) and between the attempts of a master to accept again after its listening socket failed, which its close ends, are constants in the code until S34, which builds reconnection and makes them options; `OpenTimeout` bounds the whole effort of a child |
| **Why** | A wait for a step of a read waits for engine code alone, which waits for nothing, does no I/O and runs no code of the application; a publication that fails between its stores does not leave a read retrying, because a read that retries checks the state of the instance and reports `Unavailable` once it is `Faulted`. A wait of a write for other writes ends when those writes end, and each of them ends — published, refused, cancelled before its dispatch or ended by a fault — within its own waits; a write that resolves a refusal waits only for writes that were in flight when it began, and no write admitted later prolongs it. A write that waits for a unique value waits for one holder, which waits only for its persistence step and never for its waiter, so there is no cycle; it holds nothing while it waits. Every such wait of a write but the one for the lock of the writes has the token of the write and is outside every lock; a token cancelled before the dispatch always ends the write as cancelled. The lock of the writes is held for as long as one write takes to be prepared and published in memory, milliseconds for an entity of 32 MiB, and it is not fair: a caller may be passed by others for about 100 ms, which the measurement of large writes in the document of Epic 1 states. These are waits of the kind taking a lock is, and the timeouts of principle 9 are for waits an application can make long. A timeout would turn a wait that always ends into a refusal under ordinary load, which the same principle forbids. The persistence step of MemoryOnly completes at once; the durable one gets its timeout with its sprint (S27). The two waits of the transport are for a peer and can be made long by it; they stand only until the sprints that bound them: an idle master has nothing to send and an idle child nothing to acknowledge, so silence on a stream is not a loss; a deadline on a send is the rule of a slow child, which is S33, and telling a lost peer from an idle one is reconnection, which is S34 |
| **Kept small by** | The rule that nothing waits and no code of the application runs inside the protection of a read, stated in the arena protocol, "Deferred reclamation", and held by tests. A write waits outside every lock. Each wait says in a comment why it ends, and the comment of the write coordinator lists them |
| **Approved** | By the planner, 16 November 2026, with the brief of S08, part 1; reported to the owner at the close of that sprint. The waits of a write restated by the planner on the same day with the brief of S09 and again after its review, and by the coordinator on 17 November 2026 for the write path of S09, part 2; the resolution of a refused write is the planner's reading of decision O20, reported to the owner, as the amendment of Q14 in the decision on write admission says. The waits of the transport by the planner, 17 November 2026, in the brief of S10 (point 10 and the decisions after the study, point 5); the pauses by the coordinator at the review of S10; the waits of a stream by the coordinator at the review of S11, 18 November 2026; the wait of a forwarded write for its reply and the constant of a master's close by the planner, 18 November 2026, in the brief of S12 (the decisions after its study, points 4 and 10); the wait for a unique value by the planner, 22 November 2026, in the brief of S16 |
| **Since** | Epic 1, sprint S08, part 1 |
| **Until** | Permanent for the waits of reads and writes. The sends of the transport and the constant of a master's close until S33; the waits for a frame of a stream, the wait of a forwarded write for its reply, the pauses of a child's attempts and of a master's accept until S34 |

## Approved, not in force yet

None.

## Ended

### X4. An internal entry to the engine for its first tests

| | |
| --- | --- |
| **Rule** | Tests use the public API |
| **Departure** | The tests of the engine, and the scenario tests of `Moltaro.Lattice.Tests` when they run against the engine, open a database through an internal entry of the engine assembly instead of `LatticeDatabase`, and from there use only the public interfaces. One helper of the test project does the opening, for the placeholder and for the engine in turn |
| **Why** | Until the engine can do everything the placeholder does, the public entry point still opens the placeholder. The alternative is a switch between two implementations behind the contract, which the project does not want |
| **Kept small by** | Only the way a database is opened differs; every operation after it goes through the public interfaces |
| **Approved** | By the owner, 14 November 2026, with the plan of [Epic 1](epic-1-core.md) |
| **Since** | Epic 1, sprint S06 |
| **Until** | Epic 1, sprint S12, which switched the entry point to the engine, deleted the placeholder and removed the internal entry |
| **Ended** | 18 November 2026, with Epic 1, sprint S12: `LatticeDatabase` opens the engine and every test opens its database through it. The tests of the engine that need an internal member of the instance cast what `LatticeDatabase.Create` returns, under X3 |
