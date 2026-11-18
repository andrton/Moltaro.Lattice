# Current work

The work is organized in epics. An epic is a result that is useful on its own; it is
delivered over several sprints.

| Epic | Result | Status |
| --- | --- | --- |
| [1. Core](epic-1-core.md) | The database engine behind the existing public contract, as the first production milestone | Planned from the accepted [engine design](../design/planning-handoff.md): 42 sprints with briefs. The owner answered its questions on 14 November 2026, and those of the first independent review; the owner answered those of the later reviews as well, and seven independent reviews followed; the last found nothing that blocks. The owner approved the plan by merging it. In progress: the epic document has the status of every sprint |
| [2. Storage for ASP.NET Core Identity](epic-2-identity-storage.md) | A package that keeps the users and roles of a standard ASP.NET Core Identity application in a Lattice database | The contract of the package and its tests exist; the stores wait for Epic 1 |
| [3. After the first milestone](epic-3-after-the-first-milestone.md) | Change subscription, the lookup of referring entities, and what the first milestone left out | Outline; opened 18 November 2026. Comes after Epic 2 |

The [refactoring backlog](refactoring.md) holds what the architect's review and the
reviews of understandability have named, until a refactoring sprint carries it out.

Epic 2 comes after Epic 1. It is the first real consumer of the database and adapts to it:
the scope of the engine is set by its accepted design, not by a consumer.

How an epic is planned, carried out sprint by sprint and reviewed is described in the
[development process](process.md). Where the commits go, what `main` refuses and how a
version is released is in [branches and releases](branches.md). Every place where the
project departs from a rule of `AGENTS.md` is recorded, with its reason, in
[exceptions to the rules](exceptions.md). A question that a sprint finds and that nothing
answers yet is written down in the [open questions](open-questions.md), and they are gone
through with the owner when an epic closes.

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
