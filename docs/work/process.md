# Development process

How an epic goes from a plan to finished code when the work is done by agents. The rules
every agent follows in every task are in [AGENTS.md](../../AGENTS.md); this document is for
whoever plans an epic, coordinates its sprints or reviews a sprint.

## Two tracks

| Track | For | How |
| --- | --- | --- |
| **Full** | Changes to the core library and to the public contract | Everything below |
| **Light** | Documentation, samples, build files, small fixes that change no behavior | One agent, its own review, the usual checks |

The coordinator picks the track for each sprint and the plan records it. When in doubt, full.

## Roles

One agent holds one role in a sprint. The separation is the point: nobody checks their own work.

| Role | Does | Does not |
| --- | --- | --- |
| **Owner** (a person) | Approves the epic plan and every change of the contract; decides escalations; pushes | — |
| **Planner** | Writes the epic document: goal, scope, requirements, the contract, the sprints | Implement |
| **Coordinator** | Runs an approved epic sprint by sprint: assigns agents, runs the checks, reviews, decides disputes, keeps the epic document current | Write production code or tests |
| **Test author** | Writes the tests of a sprint from its brief and contract, before the implementation | Implement that sprint |
| **Implementer** | Makes the tests of the sprint pass | Change what the tests assert; change the contract |
| **Independent reviewer** | Reviews the result against the brief, with no part in producing it | Fix what it finds |

## The plan

The planner delivers an epic document in the form described in [README.md](README.md),
with one brief per sprint. A brief has to be enough for an agent that knows nothing else:

- **Goal** — one behavior, end to end, small enough for one agent and one review.
- **Contract** — the public types and members the sprint adds or changes.
- **To read** — the decision records and documents the sprint depends on, with their dates.
- **Acceptance** — the behaviors the tests have to state. For an engine sprint also what is
  measured: a benchmark, an allocation test, a memory figure.
- **Out of scope** — what the sprint deliberately leaves alone.
- **Track** — full or light.

Before any sprint starts:

1. An independent reviewer reviews the plan the way a sprint is reviewed. A wrong plan is
   the most expensive mistake, and it is the cheapest to catch here.
2. The owner approves the plan.

If a decision record the plan depends on changes while the epic runs, the coordinator
stops and has the plan corrected before the next sprint.

## A sprint on the full track

1. **Contract.** The public types of the sprint are added with their documentation; what
   is not implemented throws `NotSupportedException`. The build is green. One commit.
2. **Tests.** The test author writes the tests against the contract and marks those that
   cannot pass yet as skipped, with one shared reason. The sample of intended use is
   written too. The build is green and nothing fails. One commit.
3. **Implementation.** The implementer removes the skips and makes the tests pass. It may
   add tests. It does not change the assertions of the tests it was given, and it does not
   change the contract. If either looks wrong, it stops and reports to the coordinator.
4. **Verification.** The coordinator runs the checks itself: build, format, all tests, the
   samples. An agent's report that the checks passed is not a check.
5. **Review.** The coordinator reviews, and an independent reviewer reviews.
6. **Fixes.** The implementer fixes every P1, P2 and P3 finding. The next review covers
   only what changed and what was still open.
7. **Documents.** The coordinator compares `README.md`, the
   [architecture](../design/architecture.md) and the documents they point to with what
   the sprint delivered: what exists and what is planned, names, limits, described
   behavior. What no longer holds is corrected and committed with the sprint. A
   disagreement between the code and an accepted decision is not corrected in the text;
   it goes to the owner.
8. **Close.** With no P1, P2 or P3 open and the documents current, the coordinator marks
   the sprint done in the epic document. The next sprint starts, with another implementer.

When the last sprint of an epic closes, the same comparison is made once more over the
whole design reference, and what the epic delivered moves from its working document into
the product documentation.

A test that turns out to be wrong is changed by the test author, or by the coordinator,
with the reason in the commit message. Never by the implementer on its own.

## Review

**What the independent reviewer gets:** the sprint brief, the contract, `AGENTS.md` and the
diff. Not the implementer's explanation and not the coordinator's opinion: a reviewer that
is handed a conclusion confirms it.

**What is checked:**

- The behavior against the brief and the decision records it names.
- The architectural principles and the quality priorities of the zone, from `AGENTS.md`.
- That every behavior the brief states has a test, and that no test was weakened.
- That nothing outside the sprint's scope was changed.
- That the documentation the change touches says what the code now does.

**A finding states** where it is, which rule, decision or requirement it breaks, and what
goes wrong because of it: a concrete failure or a concrete cost. Then its severity:

| Severity | Meaning | Blocks |
| --- | --- | --- |
| **P1** | Wrong result; data lost, applied twice or returned stale; a guarantee of the contract broken; a security problem; the build or a test fails | Yes |
| **P2** | An accepted decision or an architectural principle is violated; a stated behavior has no test; a concurrency or lifetime hazard; the contract changed without approval | Yes |
| **P3** | It will cost later: duplication, unclear ownership or naming, missing or misleading public documentation, needless complexity, the quality priorities of the zone ignored | Yes |
| **P4** | A suggestion or a matter of taste | No; listed for the coordinator |

A finding that names no rule and no failure is P4. A reviewer does not widen the scope:
"while you are here" is not a finding.

**Disputes.** The implementer may contest a finding, with its reason. The coordinator
decides. What the coordinator cannot decide goes to the owner.

**Limit.** Three rounds of review and fixes. If P1, P2 or P3 findings are still open after
the third, the coordinator stops and escalates.

## Commits

- A sprint is a short series of commits in this order: contract, tests, implementation,
  fixes. They are not squashed, so that "tests first" stays visible in the history.
- The subject names the epic and the sprint: `Epic 2, sprint 3: tests for finding a user by email`.
- No agent attribution lines, as `AGENTS.md` says.
- Commits stay local. Nothing is pushed without the owner's instruction.
- One branch per epic.

## When to stop and ask the owner

- The contract has to change.
- A decision record contradicts the brief, or has changed.
- Three review rounds did not close the findings.
- A dispute the coordinator cannot settle.
- Anything destructive, or anything that leaves this repository.

## Where the state lives

In the epic document: the list of sprints with the status of each. The coordinator updates
it when a sprint closes. There are no separate reports, logs or handoff files; what a later
agent needs to know is in the brief of its sprint.
