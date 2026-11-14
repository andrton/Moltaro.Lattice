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

## Approved, not in force yet

### X3. Tests of engine mechanisms

| | |
| --- | --- |
| **Rule** | Tests use the public API |
| **Departure** | The three test projects of the engine reach inside it. `Moltaro.Lattice.Engine.Tests` tests internal types. `Moltaro.Lattice.PostgreSql.Tests` reads the provider's tables and arms fault points inside the engine. `Moltaro.Lattice.Cluster.Tests` arms fault points and damages a connection on purpose. Two programs that are never shipped have the same access: `Moltaro.Lattice.TestNode` arms fault points inside its own process, and `Moltaro.Lattice.Measurements` uses internal types of the engine, opens it through its internal entry and reads its counters |
| **Why** | Layout sizes, ownership and reference-count audits, allocator behavior and injected failures have no public surface, and the release checks of the engine require all of them. The first engine mechanisms also exist before any public call can reach them, and their tests have to be written first |
| **Kept small by** | Each such test states an invariant written in a decision record or in the engine contract, never how the code happens to be built. Whatever can be stated through the public API is tested only there. A fault point is internal, does nothing unless a test arms it, and cannot be reached through the public API |
| **Approved** | By the owner, 14 November 2026, with the plan of [Epic 1](epic-1-core.md) |
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

## Ended

None yet.
