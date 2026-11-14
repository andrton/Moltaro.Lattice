# Current work

The work is organized in epics. An epic is a result that is useful on its own; it is
delivered over several sprints.

| Epic | Result | Status |
| --- | --- | --- |
| [1. Core](epic-1-core.md) | The database engine behind the existing public contract, as the first production milestone | The [engine design](../design/planning-handoff.md) is accepted; ready to be planned, no sprints yet |
| [2. Storage for ASP.NET Core Identity](epic-2-identity-storage.md) | A package that keeps the users and roles of a standard ASP.NET Core Identity application in a Lattice database | The contract of the package and its tests exist; the stores wait for Epic 1 |

Epic 2 comes after Epic 1. It is the first real consumer of the database and adapts to it:
the scope of the engine is set by its accepted design, not by a consumer.

How an epic is planned, carried out sprint by sprint and reviewed is described in the
[development process](process.md).

## How an epic document is written

One file per epic, `epic-<number>-<short-name>.md`, with these parts:

1. **Goal** — the result and who it is for.
2. **Scope** — what is included and what is deliberately left out.
3. **Requirements** — numbered `R1`, `R2`, …; each says what is needed, what needs it and
   whether it exists today. Other documents refer to them as "Epic 2 R3".
4. **Acceptance** — how we know the epic is done.
5. **Open questions** — decisions that are not made yet, each with the options.
6. **Sprints** — the order of work, without dates. An epic that is ready to be carried out
   has one brief per sprint, as the [development process](process.md#the-plan) describes,
   and the status of each sprint. Before that, an outline of stages is enough.

A requirement level is one of:

- **Must** — the epic cannot be finished without it.
- **Should** — needed for a good result; a documented workaround is acceptable at first.
- **Release** — not needed to develop and test, but needed before anybody relies on it.

When an epic is finished, what it delivered is described in the product documentation, and
its working document is removed from this folder.
