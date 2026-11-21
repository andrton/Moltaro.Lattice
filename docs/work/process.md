# Development process

How an epic goes from a plan to finished code when the work is done by agents. The rules
every agent follows in every task are in [AGENTS.md](../../AGENTS.md); this document is for
whoever plans an epic, coordinates its sprints or reviews a sprint.

## Two tracks

| Track | For | How |
| --- | --- | --- |
| **Full** | Changes to the core library and to the public contract | Everything below |
| **Light** | Documentation, samples, build files, small fixes that change no behavior | One agent, the usual checks, and an independent review |

The coordinator picks the track for each sprint and the plan records it. When in doubt, full.

Every sprint, on either track, is reviewed by an independent reviewer, and the review
repeats after the fixes until it finds nothing that blocks; see [Review](#review).

## Roles

The implementer produces; the reviewer and the breaker check, and neither has a part in
producing. Nobody checks their own work.

| Role | Does | Does not |
| --- | --- | --- |
| **Owner** (a person) | Approves the epic plan, the contract of each stage, every change of an Acceptance and, until the first release, a change of the contract that touches a guarantee written in a decision record (after it, every change of the contract); decides escalations; pushes and merges, or gives the coordinator a standing permission to | — |
| **Planner** | Writes the epic document: goal, scope, requirements, the sprints, and the contract of every sprint, stage by stage | Implement |
| **Coordinator** | Runs an approved epic sprint by sprint: assigns agents, runs the checks, reviews, decides disputes, keeps the epic document current; with the owner's standing permission, pushes and merges a closed sprint | Write production code or tests |
| **Implementer** | Puts the contract of the sprint into code exactly as the brief names it and writes its tests from the brief, in commits that come before any implementation; then makes those tests pass | Design the contract: a name or a signature the brief does not give goes back to the planner. Change what its tests assert once they are committed |
| **Independent reviewer** | Reviews the result against the brief, with no part in producing it | Fix what it finds |
| **Breaker** | Writes probe tests of its own against the guarantees the sprint states, with no part in producing it, and reports only what fails | Fix what it finds |

## Models and effort by role

Which model and effort each agent runs at (the owner, 19 and 21 November 2026). The
principle: the highest effort goes where the work is judgement and no later step catches
its mistake; work that is mechanical, or that an independent check follows, runs lower.

| Role or work | Model, effort |
| --- | --- |
| Breaker | Opus 5.5, xhigh |
| Independent reviewer of a sprint | Opus 5.5, xhigh |
| Implementer of a critical sprint of the engine (concurrency, storage, the lifetime of memory, replication, durability) | Opus 5.5, xhigh |
| Coordinator | Opus 5.5, xhigh |
| Implementer of any other sprint of the full track | Opus 5.5, high |
| Refactoring sprint: its implementer and its narrow reviewer | Opus 5.5, high |
| Light track: documents, samples, comments, small fixes | Opus 5.5, high |
| Preparing a measurement, variant patches, a comparison of listings | Opus 5.5, high |
| Architect's review, a study before a critical sprint, the planner | Fable |

The rows at high effort are a trial that begins with S15, part 2 of Epic 1: the record of
that sprint states the number of review rounds and of findings by severity beside those of
S15, part 1, and the owner decides from that whether the rows stay. An agent that stops on
a usage limit is not retried in a loop; the owner is told.

## New sessions at a sprint boundary

The coordinator and the planner are sessions, and a session reads its whole history again
at every turn. Both are therefore replaced when a sprint is closed.

- The change happens only at a boundary: the sprint is merged, no agent is running and no
  investigation is open. Unfinished work is not handed to another session.
- Before the merge the coordinator checks that every decision of the sprint is in the
  documents — the epic document, the briefs, `refactoring.md`, `open-questions.md`, this
  file — and names to the planner what could not be put there. After the merge it stops
  and does not start the next sprint.
- The outgoing planner writes one starting text, for the new planner, and gives it to the
  owner. It states the role, the state of `main`, the next work in order, what is owed to
  the owner, the mistakes not to repeat, and this rule.
- The new planner creates the new coordinator session, with the model and effort of the
  table above, gives it its starting text and tells the owner its name.
- The state lives in the repository. A starting text is not committed.

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

**The contract is written stage by stage.** An epic of many sprints is divided into
stages, and every sprint has its brief from the start. For the sprints of the first
stages, which the plan names, the brief gives the types and members exactly, with their
signatures. For a later stage
it says what the contract has to be able to do, and the planner writes the types and
members into those briefs before the stage begins, with what the earlier stages have
taught. The owner approves the contract of each stage before its first sprint starts. For a
sprint that begins with a study, its internal types and members are written in its
step 1, after the study; before the stage the owner approves its public contract
and what each sprint must be able to do. A
sprint never starts from a brief whose contract is not written out and approved: an
interface designed twenty sprints ahead would be changed anyway, and a change of the
contract follows the rule on a draft contract in
[Limits on review and on questions](#limits-on-review-and-on-questions).

For a sprint on an engine mechanism that has no public surface, the contract is the
internal types its tests address; they are named in the brief and put into code in the
same way.

Before any sprint starts:

1. An independent reviewer reviews the plan the way a sprint is reviewed, and once more
   after the corrections, by the [limits](#limits-on-review-and-on-questions) on the
   review of a document. A wrong plan is the most expensive mistake, and it is the
   cheapest to catch here.
2. The owner approves the plan, and with it the contracts of the first stages.

Before a later stage starts, the planner goes through the [open questions](open-questions.md)
and takes up those the stage touches. It then completes the contracts of its sprints, an
independent reviewer reviews them as the plan was reviewed, by the limits on the review
of a document, and the owner approves them. The coordinator does not start the first
sprint of a stage without that approval.

If a decision record the plan depends on changes while the epic runs, the coordinator
stops and has the plan corrected before the next sprint.

## A sprint on the full track

1. **Contract and tests.** The implementer adds the public types of the sprint with their
   documentation, exactly as the brief names them; what is not implemented throws
   `NotSupportedException`. It writes the tests against that contract from the brief and
   marks those that cannot pass yet as skipped, with one shared reason. The sample of
   intended use is written too. The build is green and nothing fails. These commits come
   before any implementation; the record of the sprint names the last of them.
2. **Implementation.** The same agent removes the skips and makes the tests pass. It may
   add tests. It does not change the assertions of the tests of step 1, and it does not
   change the contract. If either looks wrong, it stops and reports to the coordinator.
3. **Verification.** The coordinator runs the checks itself: build, format, all tests, the
   samples. An agent's report that the checks passed is not a check.
4. **Review.** The coordinator reviews, and an independent reviewer reviews; on a sprint
   that changes behavior the breaker works beside it.
5. **Fixes.** The implementer fixes every P1, P2 and P3 finding. The next review covers
   only what changed and what was still open. Review and fixes repeat within the
   [limits on the review of code](#limits-on-review-and-on-questions).
6. **Documents.** The coordinator compares `README.md`, the
   [architecture](../design/architecture.md) and the documents they point to with what
   the sprint delivered: what exists and what is planned, names, limits, described
   behavior. What no longer holds is corrected and committed with the sprint. A
   disagreement between the code and an accepted decision is not corrected in the text;
   it goes to the owner.
7. **Close.** With no P1, P2 or P3 open and the documents current, the coordinator marks
   the sprint done in the epic document. A finding is no longer open when an independent
   review found its fix sufficient, when the owner accepted it or moved it to a named
   sprint, as [Review](#review) describes, or when it is a P3 that the limits on review
   sent elsewhere: to the refactoring backlog after the third round of code, or, for a
   document, to the open questions. The next sprint starts, with another implementer.

**Small changes during a sprint.** A change that comes up while a sprint is open and is
small — an answer of the owner that touches a rule, a document, a file of the
repository's settings or the brief of a later sprint; a sentence of documentation — is
made in the branch of that sprint and merged with it. It gets no branch and no pull
request of its own. The brief of the sprint or the [open questions](open-questions.md)
name it with the answer that caused it, whoever holds the role for that kind of work
makes it, and it is reviewed with the sprint. The light track is for a change made while
no sprint is open, or one that cannot wait for the open sprint.

**A sentence of documentation needs no step of its own.** When a decision that is already
made changes a sentence of public documentation or a comment and nothing else of the
contract — no name, no signature, no behavior that was not decided — the implementer
writes the sentence in the commit that carries the change it describes, with the tests or
with the code. The brief or the register gives the wording, and the reviews check it. A
change of a name, a signature or a member remains a change of the contract, made in step 1
as the brief names it.

**A decision is written in one place.** A decision of the owner, or an answer of the
planner, is written out once: in the decision record or the list of decisions that owns
it, and in the brief of each sprint whose work it changes. Other documents that touch
the matter point to that place and do not restate it; a sentence elsewhere is changed
only when the decision makes it untrue. Tests are sized the same way: a rule that one
check enforces is stated by the smallest set of cases that tells a right implementation
from a wrong one, not once for every path that reaches the check.

When the last sprint of an epic closes, the same comparison is made once more over the
whole design reference, and what the epic delivered moves from its working document into
the product documentation.

**Questions that do not block.** A sprint can find a gap that no brief, decision record
or rule answers and that does not stop it. The agent that finds it reports it, with the
answer it recommends, and does not decide it. The coordinator takes the recommended
answer, or one of its own, and enters the question in the
[open questions](open-questions.md) as taken, with what the code now does; it names the
new entries in its report when the sprint closes, where the owner reads them, and goes
through all of them with the owner when the epic closes. An entry
that names a sprint as its limit is read when that sprint is prepared: the sprint does
not start before the entry is answered or moved to a later one. A question that does
block is not entered there: the coordinator stops and asks.

A test of step 1 that turns out to be wrong is changed only on the coordinator's decision,
with the reason in the commit message, and the reviewer is told which. Never by the
implementer on its own.

**Edits that state nothing new.** A sprint can force edits on what already exists: a
member that was renamed is used under its new name, a descriptor is written in a new
form, a helper names another class. Such an edit is made by the implementer in the
commit that would otherwise break the build or a test, in tests and samples as well, so
that the build stays green after every step. When the commit of the contract removes a
member in favour of another, the new one works at once for what already exists, so that
the tests that read it still pass; this is the one case in which a member of a contract
commit does more than refuse. Such an edit never changes what a test asserts. The brief
names each such edit, and the reviewers check that nothing else was changed under this
rule.

## Review

**What the independent reviewer gets:** the sprint brief, the contract, `AGENTS.md`, the
diff, and on the full track which commits are those of step 1. Not the implementer's
explanation and not the coordinator's opinion: a reviewer that is handed a conclusion
confirms it.

On every sprint of the full track that changes behavior, a second agent works beside the
reviewer as a breaker: it writes probe tests against the guarantees the sprint states and
reports only what fails. On a sprint that changes concurrency, storage, the lifetime of
memory, replication or durability the brief names its targets, in order. On any other it
takes the Acceptance of the brief as its list, within a bound the coordinator sets. A
refactoring sprint, which changes no behavior, and the light track have no breaker.

**What is checked:**

- **The tests against the Acceptance, sentence by sentence.** For every sentence of the
  Acceptance the reviewer names the test that holds it, or reports that none does. It
  does this from the brief and the tests of step 1, before it reads the implementation.
- **That no assertion moved after step 1,** on the full track. The reviewer compares the
  tests as they stood at the end of step 1 with the tests at the head. An assertion that
  the sprint's own commits changed, removed or loosened after step 1 without the
  coordinator's recorded decision is a P2; a change that came with a merge of `main` is
  not the sprint's.
- The behavior against the brief and the decision records it names.
- The architectural principles and the quality priorities of the zone, from `AGENTS.md`.
- That every behavior the brief states has a test, and that no test was weakened.
- That nothing outside the sprint's scope was changed. A small change that the owner
  decided while the sprint was open belongs to its scope when the brief or the
  [open questions](open-questions.md) name it; see "Small changes during a sprint".
- That the documentation the change touches says what the code now does.
- That every file the sprint added stands where `AGENTS.md` puts it — one type per file,
  a folder for one responsibility — in the tests and the samples as in the libraries. A
  folder that only collects files is a finding, whoever started it.
- **Claims are probed, not only read.** Every sentence of the change or of its documents
  that says something retains nothing or releases its memory is held by a test with weak
  references; every limit is probed at a boundary where a structure grows; everything
  called immutable or read-only is cast and written to. The reviewer writes such a probe
  itself where the sprint has none, and reports it.
- **Simplicity.** What in this sprint's change could be removed, merged or written more
  plainly without losing a guarantee or a measured figure: a mechanism without a test or
  a number that needs it; an option, a state or a branch nobody reaches; a type that only
  passes calls on; a method or a file that does several things; a comment that is needed
  because the code is not readable. Such a finding is a P3 with the simpler form named,
  not a remark of taste; the implementer takes it or says which guarantee or figure
  forbids it. From S14 of Epic 1 on, a comment that breaks the convention for comments
  of `AGENTS.md` in code the sprint adds or changes is a P3 as well. Each reviewer also
  names the one part of the change it found hardest to follow, and why; that sentence
  goes into the record of the sprint's review even when nothing is changed.

**A finding states** where it is, which rule, decision or requirement it breaks, and what
goes wrong because of it: a concrete failure or a concrete cost. Then its severity:

| Severity | Meaning | Blocks |
| --- | --- | --- |
| **P1** | Wrong result; data lost, applied twice or returned stale; a guarantee of the contract broken; a security problem; the build or a test fails | Yes |
| **P2** | An accepted decision or an architectural principle is violated; a stated behavior has no test; a concurrency or lifetime hazard; the contract changed without the approval it needs | Yes |
| **P3** | It will cost later: duplication, unclear ownership or naming, missing or misleading public documentation, needless complexity, the quality priorities of the zone ignored | Yes, within the [limits on review](#limits-on-review-and-on-questions) |
| **P4** | A suggestion or a matter of taste | No; listed for the coordinator |

A finding that names no rule and no failure is P4. A reviewer does not widen the scope:
"while you are here" is not a finding.

"Does not change the contract", said of the implementer, means its names, signatures and
behavior; a decided sentence of documentation is the implementer's to write, as "A
sentence of documentation needs no step of its own" says. "Every behavior the brief
states has a test" is ranked by what follows: a behavior the Acceptance states and no
test holds is P2; one the brief states elsewhere is P4.

A changed copy of the code that the tests do not notice is a finding of its own only when
the code is wrong, or when the change breaks a behavior that the Acceptance of the brief
states and no test holds it: then it is "a stated behavior has no test", P2. Where the
code is right and the behavior is not one the Acceptance states, it is P4. A review looks
for defects; it does not ask for a test of every line.

**Disputes.** The implementer may contest a finding, with its reason. The coordinator
decides. What the coordinator cannot decide goes to the owner.

**Rounds.** Review and fixes repeat within the
[limits on review](#limits-on-review-and-on-questions). A sprint or a plan is never closed
on its author's word that a finding was fixed: the fix is reviewed, and the fix of a P1 or
a P2 also after the last round. Only a P3 of a document, fixed in the same pass, needs no
further review.

A finding that comes back is another matter. When a finding is found again after its fix
was reviewed, the coordinator settles it itself first: it decides the dispute between the
implementer and the reviewer, or gives the fix to another implementer. The coordinator
stops and asks the owner only when the finding comes back once more after that, or when
it can be closed only by changing the acceptance of the brief, an accepted decision, or
the contract after the first release. That holds at any round: where the limits on review
would let the work go on, a finding that comes back still stops for the owner. The
coordinator reports what keeps coming back and what it tried, and the owner decides: the
requirement changes, or the finding is accepted, or it is moved to a named sprint. The
decision is recorded in the epic document, and in the [exceptions](exceptions.md) as well
when it departs from a rule of `AGENTS.md`. A sprint is never closed with an open P1 or
P2 on the coordinator's own word. The report of a closed sprint says how many rounds it
took.

## The architect's review

After every merged sprint the coordinator starts one more agent, in the role of the
architect of the system. It is not a reviewer of the sprint: the reviewers of a sprint
look for defects in a change, the architect looks at what the system has become. Its
concern is the cleanliness of the code and whether the system stays manageable and
extensible.

- **Who.** A separate agent that took no part in the sprint, on the strongest model in
  use, with the whole repository to read and nothing to write. Its brief says, every
  time, that KISS holds for a refactoring as for any change: its task is to remove
  technical debt where it can — to keep the code clean, understandable and easy to
  modify, and free of duplicates — and that this is always a balance. A refactoring that
  adds a type, an interface or a level of indirection has to remove more than it adds;
  one that only moves code around, or that a reader would not thank it for, is not
  proposed.
- **What it looks at.** The code the sprint touched and what that code leans on, two or
  three sprints back; once per stage, the whole engine. It asks: what could be removed,
  merged or written more plainly without losing a guarantee or a measured figure; where
  one type or one file has taken several responsibilities; where two places do the same
  thing; where the direction of dependencies between folders is bent; what a later sprint
  of the plan will find hard to extend, named with that sprint; what a reader without the
  history cannot follow, and why.
- **What it returns.** A short list, the most valuable first. Each entry names the place,
  what is wrong in one sentence, the simpler form, what the change costs — files, risk,
  the tests that cover it — and which guarantee or measurement must be shown unchanged.
  It also says what it looked at and found sound. It proposes no change of behavior and
  none of the public contract; such an idea is a question for the planner.
- **What becomes of it.** The planner goes through the list with the coordinator. Each
  entry is taken as a refactoring task, joined to a coming sprint that changes the same
  code, or refused with its reason written beside it. A taken entry is entered in the
  [refactoring backlog](refactoring.md) with its status. What would change a rule goes
  to the owner. What would change the scope follows the
  [limits on questions](#limits-on-review-and-on-questions): it waits for the owner only
  when it changes an Acceptance or a guarantee written in a decision record, or cannot be
  undone cheaply; otherwise it is taken and listed.
- **The refactoring sprint.** After a sprint is merged and the architect's list is gone
  through, the entries that were taken are carried out as one sprint of their own, before
  the next sprint of the plan starts; it is named after the sprint it follows (S13R after
  S13). Nothing else is in it: no new behavior, no fix of a defect. No assertion of a
  test changes and no test is removed; the whole suite and the format check pass before
  and after; where it touches a path that has a measured figure, the figure is measured
  again and does not fall. One reviewer answers one question: is the code simpler to
  read, and is everything it guaranteed still guaranteed. When the list has nothing
  taken, there is no refactoring sprint. An entry too large for one such sprint is
  planned by the planner as a sprint of the plan, with its place.
- **The measure** is not the number of lines. It is that a reader who knows the
  guarantees can follow the code, and that the next sprint of the plan can be built
  without untangling this one first.

## A study comes before a design

A sprint that builds a mechanism of the engine — storage, an index, the write path,
replication, recovery — begins with a study, before its internal contract is fixed and
before any code (the owner's rule of 17 November 2026). The agent reads the source, not
summaries, of two to four established systems that solve the same problem, at pinned
commits, the licence checked first by the rule on sources below. It reports, briefly: what
each guarantees, what it costs, what Lattice takes and why, what does not fit and why. The
planner and the coordinator read the report before the contract of step 1 is committed.
A design of our own is made only where nothing fits, and the report says so. The study is
bounded: it answers the questions the brief names and is not a survey. It is a step of
its sprint, not a sprint of its own.

## Code modelled on another project

When a sprint repeats a proven solution, its code is modelled only on sources under a
permissive licence (MIT, Apache-2.0, BSD). Sources under GPL or LGPL may be read about
in their papers and documentation to understand an idea; their code is not opened while
ours is written, and nothing is ported from it. A type that follows the code of a source
closely enough to be an adaptation of it says so in a comment at the head of its file —
which project, which file, which commit — and `THIRD-PARTY-NOTICES.md` gains the
project's copyright line and licence text in the same change. The pull request lists
what was adapted and what was only taken as an idea.

## Commits

- A sprint is a short series of commits in this order: contract, tests, implementation,
  fixes. On the branch they are not squashed, so that "tests first" stays visible in the
  pull request; the merge squashes them into one commit of `main`.
- The subject names the epic and the sprint: `Epic 2, sprint 3: tests for finding a user by email`.
- No agent attribution lines, as `AGENTS.md` says.
- Commits stay local. Nothing is pushed without the owner's instruction or standing
  permission.
- One branch per sprint, cut from `main` and merged back through a pull request when the
  sprint closes. [Branches and releases](branches.md) has the names, the merge rules and
  what `main` refuses.

## Limits on review and on questions

Rules of the owner of 21 November 2026.

- **Review of code has at most three rounds.** After the third, a P3 that is still open
  causes no further round: it goes to the refactoring backlog with a place. A P1 or a P2
  that is still open is fixed and its fix reviewed, because a wrong result is not merged;
  the coordinator tells the owner in a few lines what did not close in three rounds, and
  goes on.
- **Review of a document has at most two rounds** — a plan, a brief, an internal
  contract, a protocol, any design document. Only a P1 or a P2 blocks a document. A P3
  that costs a sentence is fixed in the same pass; any other goes to the open questions.
  A P3 never causes another round, and a round with no P1 and no P2 closes the review.
- **A document that grows under its fixes is over-built.** It gets no further round: it
  is cut to what its sprint builds, and what was cut moves to the brief of the sprint
  that builds it.
- **A design document covers what its sprint builds.** Behavior of a later sprint is
  written in that sprint's brief and proven by its tests, not described ahead in prose.
  The decision records are decisions, not designs ahead, and stay.
- **The first sprints of a plan make one scenario work end to end; hardening comes
  after.** The riskiest unknown is checked first. This is a rule for a plan or a stage
  that is written from now on; the order of a plan already under way is not rebuilt for
  it.
- **Until the first release the public contract is a draft.** A sprint that finds a name,
  a signature or the shape of a member should change makes the change, lists it for the
  owner in its record and goes on. A change that touches a guarantee written in a
  decision record waits for the owner, before and after a release. After the first
  release every change of the public contract waits for him.
- **A question blocks only when a wrong answer cannot be undone cheaply:** it is
  destructive, it leaves the repository, or it changes a released contract. For any
  other the recommended answer is taken, recorded in the open questions as taken, and
  the work goes on; the owner reads the list at the close of the sprint. A change of the
  Acceptance of a brief is the exception the owner kept: it still needs his word in the
  coordinator's session.
- **A run the owner does not watch has its goal stated as a result,** not as an activity
  — "the codec with its tests green", not "work on the codec". Its report says how far
  from that result the run ended.

## When to stop and ask the owner

- The contract has to change in a guarantee written in a decision record, or after the
  first release.
- The Acceptance of a brief has to change.
- A decision record contradicts the brief, or has changed.
- A finding keeps coming back after the coordinator has settled it once, as
  [Review](#review) describes.
- A dispute the coordinator cannot settle.
- Anything destructive, or anything that leaves this repository: a push, a pull request,
  a merge, a release tag. A standing permission of the owner, as
  [branches and releases](branches.md#who-does-what) describes it, covers the push, the
  pull request and the merge of a closed sprint while no decision of the owner is open;
  nothing covers a release tag.

A measured fall of performance is not by itself a reason to stop (the owner, 16 November
2026). First the cause is found, with a control or a profile. Then it is compared with
what established systems do for the same problem: is this what the problem costs
everywhere, or is something done wrong here. Then alternatives are tried, each kept only
if a figure justifies it. The owner is asked only when nothing helped, the fall cannot be
put down to what the problem costs in the industry, and a requirement of the project is
missed; the question then comes with the cause, the evidence, the alternatives tried with
their figures and the requirement that is missed. A fall that has a known cause and stays
within the requirements is reported at the close.

## Where the state lives

In the epic document: the list of sprints with the status of each. The coordinator updates
it when a sprint closes. What a later agent needs to know is in the brief of its sprint.
What nobody has answered yet is in the [open questions](open-questions.md), and so is
an answer the coordinator took, marked as taken. There are no other reports, logs or
handoff files.
