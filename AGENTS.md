# Repository Guidelines

`AGENTS.md` is the one instruction file for coding agents and a short statement of how
this project is built. Shared rules live here; `CLAUDE.md` only points to this file.

## Project

Moltaro.Lattice is a schema-driven in-memory object database for .NET: compact storage,
low-allocation access, one master that executes writes, local read replicas, PostgreSQL
persistence.

What exists today is the **public contract** and a **placeholder** behind it: a simple
managed in-memory implementation that lets the contract, the samples and the tests run.
The placeholder is not the engine. The engine, its storage, indexes and persistence are
designed separately; their decision records in `docs/decisions` are the authority for
what the database guarantees. The physical baseline, evidence and planning handoff
are in `docs/design`. These describe intended behavior, not delivered engine features.

This repository is going to be public. Nothing specific to a private product goes into it.

## Layout

| Path | Contents |
| --- | --- |
| `src` | The solution, every project and the build settings. Run `dotnet` from here |
| `src/Moltaro.Lattice` | The core library. No package dependencies |
| `src/Moltaro.Lattice.DependencyInjection` | Registration in a container; a thin separate package |
| `src/Moltaro.Lattice.AspNet.Identity` | Storage for ASP.NET Core Identity; a separate package, contract only so far |
| `src/Moltaro.Lattice.Samples` | Runnable samples of intended use |
| `src/*.Tests` | Tests, one project per library |
| `docs` | Product documentation; `docs/work` holds the epics in progress |
| `docs/design`, `docs/decisions` | Durable engine design reference, accepted decisions, evidence and planning input |
| `src/Prototypes` | Isolated synthetic experiments; not part of the product solution or shipped packages. A prototype may pin its own package versions |

The repository root holds only repository-level files. Code never lives there.

## Keep It Simple

KISS is the base rule for design, code, tests and documentation.

- Start from the simplest thing that fully solves today's case. Prefer one plain rule or
  an existing mechanism over a new one.
- Build what is needed now. What is not needed yet is not built; at most it is noted as
  planned in the document that owns it.
- No speculative abstractions, options, switches, retries, fallbacks or defensive checks
  "just in case".
- When something is awkward to use, fix its cause. Do not wrap it: no adapter or facade
  class around a core type to work around what the core lacks.

Simple is not the goal by itself. Do not make something simpler by dropping a guarantee,
blurring a boundary or pushing the difficulty onto the caller. The target is the simplest
design that keeps every accepted guarantee. When simple and correct disagree, correct
wins. When simple and fast disagree, measure before choosing.

## Architectural Principles

These hold across the contract, the placeholder and the future engine.

1. **The schema is the authority.** Entity types, fields, keys, indexes and the optional
   .NET class of an entity type are declared in the schema. It is compiled once and can be
   saved and loaded. Nothing is discovered by searching types at call time: a class is
   matched to its entity type once, and calls afterwards use what that produced.
2. **Two equal ways to the same data.** Every data operation exists for entity classes
   and for records addressed by name, member for member, with the same rules. A .NET class
   is optional; access by name is not a second-class path.
3. **Reads return owned copies.** Nothing the caller holds changes when the database
   changes, and editing a copy changes nothing until it is written. A read is coherent for
   one entity; several reads are not a snapshot.
4. **One master executes writes; children follow.** Application code uses a master and a
   child through the same interface and does not branch on the role.
5. **Atomicity is explicit.** One write is atomic for one entity. Several writes are
   atomic only when they are submitted together as one write batch, which is bounded,
   may address several entity types and stays inside one database. Separate calls are
   never atomic together, there is no transaction that spans application code, and
   nothing spans two databases or an external one.
6. **Three identities stay separate.** A revision identifies one state of one entity, a
   position says how far the database history has been applied, an operation id identifies
   a submitted write. None stands in for another.
7. **Never a stale or approximate success.** An operation that depends on a position waits
   for it or fails. An index search uses the named index or fails; it does not read
   everything instead. A requested mode is honored or refused, never quietly replaced.
8. **Explicit over implicit.** The caller names the index, selects MemoryOnly, passes the
   session. Defaults are the safe choice. An expected outcome, such as an absent key, is a
   result; a failed condition is an error with a code.
9. **Waiting is finite and outside locks.** No lock is held across I/O or an `await`.
   Every wait has a timeout and honors cancellation. A type that takes more than one lock
   documents the order.
10. **One instance, one database.** Instances are independent; an application may have
    several. No static mutable state, except where a comment says why.
11. **Dependencies point one way.** Folders are namespaces, and each is one
    responsibility: `Schema`, then `Storage`, then `Access` and `Replication`, then
    `Engine`; the root namespace holds the entry point, options and errors. The core
    depends on nothing outside .NET. An integration is its own thin assembly, named for
    what it is for.
12. **Performance is a design goal, proven by measurement.** Avoid needless copies and
    allocations on read paths, but justify added complexity with a number. The
    placeholder's speed is not a target.

## Quality Priorities

When two good things pull apart, the zone of the code decides which one wins. Read each
row left to right: an earlier quality is not traded for a later one.

| Zone | Priorities, in order |
| --- | --- |
| Engine: storage, indexes, the read path, applying replication | Correctness → memory and speed → readability → flexibility |
| Public contract | Correctness → clarity of use → stability → speed |
| Integrations, samples, tests | Readability → simplicity; speed does not matter |

- **Correctness is first everywhere.** For a database it means: nothing lost, nothing
  applied twice, nothing stale returned as current.
- **Memory is a quality of its own**, next to speed. Compact storage is why the project exists.
- **A performance claim needs a measurement.** "Faster" or "fewer allocations" is accepted
  with a number from a benchmark or an allocation test, not by argument. Without one, the
  simpler code stays.
- The contract must not make the engine slow: a signature that forces boxing, copying or
  an allocation per call on a read path is a defect of the contract.

## Design Patterns

Patterns are vocabulary for explaining a decision, not a requirement to meet. No pattern
is applied because it exists.

- Put logic where the data it needs lives.
- Introduce an interface only at a real boundary: the public contract, a storage provider,
  a replication transport. One implementation behind a private interface is not a boundary.
- In the engine's hot paths, design from the data: plain structures, spans, no virtual
  dispatch and no allocation per entity. Object-oriented patterns that cost a call or an
  object per item do not belong there.
- Elsewhere a known pattern is welcome when it removes real duplication or names a real
  variation. Say which problem it solves; if that cannot be said in a sentence, leave it out.

## The Contract Comes First

A feature starts as contract: public types with documentation, a sample of its use, and
tests that state its behavior. Implementation follows, possibly much later and elsewhere.

- Respect the stage that was agreed. "In the facade, not implemented yet" means exactly
  that: the entry points throw `NotSupportedException`, the tests that need the feature
  are skipped with a shared reason, the sample is written and reports where it stops. Do
  not add stand-in logic to the placeholder unless asked.
- Checks that need no engine belong to the contract and work from the start: resolving a
  declared name, validating an argument against the schema.
- When the implementation arrives it has to pass the tests that were written for the
  contract. Do not weaken or delete a test to get a green run.
- Nothing has been released. There are no compatibility shims: when something is replaced,
  remove the old code, tests and documentation in the same change.

## Code Style

Language and build:

- C# on .NET 10, nullable enabled, warnings are errors. `dotnet format --verify-no-changes`
  stays clean.
- Package versions are managed centrally in `src/Directory.Packages.props`; a
  `PackageReference` has no `Version`.

Organization:

- One type per file, named after the type. The generic form of a type that also has a
  non-generic one goes to `Name.Generic.cs`.
- A folder is a namespace and one responsibility. No `Internal`, `Helpers`, `Utils` or
  other folders that collect unrelated types.
- `internal` by default. A type is public only when it is part of the contract.

Naming:

- Public types start with `Lattice`; interfaces with `ILattice`. Small values written
  inside every call, such as `IndexKey` and `IndexBound`, stay short.
- Every enum type ends with `Enum`.
- Asynchronous methods end with `Async` and take a `CancellationToken` last.
- Private fields are `_camelCase`.
- Name things for what they do for the user, not for the framework mechanism inside.
- Words: a stored object is an *entity*; a replica is a *child*; the per-type access
  object is a *set*.

Shape of the public API:

- Something that answers at once from local memory is synchronous. Something that can
  wait is asynchronous. A read therefore has two forms: a synchronous one without a
  session, an asynchronous one that takes it.
- `LatticeException` with a `LatticeErrorEnum` code reports what the database decided.
  `ArgumentException` and its relatives report a caller's mistake.
  `ObjectDisposedException` follows a close. `NotSupportedException` marks what is not
  implemented.
- Use ordinary results for expected outcomes; reserve exceptions for failures.
- Every public member has XML documentation: what it does, what it guarantees, what it
  does not, and which errors it reports. Plain sentences, no restating of the signature.

Writing code:

- Write for a reader. Descriptive names, one statement per line, blank lines between
  steps; no nested ternaries or dense chains.
- Keep a method focused, without splitting a simple operation across needless helpers.
- Comments are in English and explain why: an invariant, an ordering requirement, who
  owns mutable data, what a lock protects. They do not repeat the code.
- Distinguish "not loaded", "confirmed empty" and "failed". Never return an invented empty
  result in place of a failure.
- No reflection on a call path. No duplicated checks. No redundant models.

## Tests And Samples

- A public behavior has a scenario test that reads like its use. A test name is a
  sentence stating the behavior.
- Tests use the public API. The one exception is the internal switch that holds back a
  child's replication, so that waiting can be tested deterministically.
- A child may lag. A test waits through a session or a position; it does not assume, and
  it does not sleep where a deterministic way exists.
- A feature that is not implemented has its tests written and marked
  `[Fact(Skip = ...)]` with one shared reason. Skipping is never used for a flaky test.
- Tests come first and from someone else. On the full track of the
  [development process](docs/work/process.md), the tests of a sprint are written and
  committed before its implementation, by a different agent than the one who implements
  it. The implementer removes the skip and makes them pass; it does not change what they
  assert. A test that looks wrong is reported, not edited.
- The samples project shows intended use and runs to the end with `dotnet run`. A sample
  of an unimplemented feature is still written; the program reports where it stopped.
- Code shown in `README.md` must compile against the current contract.

From `src`:

```bash
dotnet build
dotnet test
dotnet format --verify-no-changes
dotnet run --project Moltaro.Lattice.Samples
```

## Documentation

- Everything in the repository is written in English: documentation, code comments, XML
  documentation, test names and commit messages. This holds whatever language the task
  was given in.
- A document lives next to the code it changes. Product documentation is in `docs`;
  working documents, the epics, are in `docs/work`. The plan for using this database
  inside another product belongs to that product's repository.
- A decision is not an implementation. Say plainly what exists and what is planned.
- When behavior changes, update the document that owns it in the same change.
- The architecture documents describe what is there. When a larger block of work is
  finished — a sprint, an epic, a change of the public contract — compare `README.md`,
  `docs/design/architecture.md` and the documents they point to with what now exists.
  Correct every statement of status, every name, limit and described behavior that no
  longer holds, in the same change, and rebuild `docs/design/index.html` when its source
  changed. Do it at once, not as a later cleanup: a reader must be able to trust them.
- That check never changes a guarantee. When the code and an accepted decision disagree,
  it is a finding to report, not a sentence to edit.
- Requirements that come from a particular consumer are written in general terms.

## Working Rules

- The user's instructions define the task. Among repository documents this file owns the
  global rules. Code and tests show what the code does, not that it is intended.
- Before designing a change to the contract, read the decision record that covers it.
- Make routine, reversible choices within the task. Ask only when a missing decision
  changes behavior, the contract or the scope. Say what was left out.
- Review the whole affected flow before finishing: contract, placeholder, tests, samples,
  `README.md`, `docs`.
- Keep plans, progress and findings in the conversation. Do not create report, audit or
  plan files unless the user asks for that document.
- Never revert the user's changes or unrelated work.
- Do not commit or push unless asked. Stage only the files of the task. No secrets,
  tokens, machine paths, build output or run output in Git.
- Commit messages carry no agent attribution: no `Co-Authored-By` line and no "generated
  with" line. This overrides the default of any tool.
- Planning an epic, coordinating its sprints or reviewing a sprint follows the
  [development process](docs/work/process.md). Read it before taking one of those roles.
- Finish by reporting what changed, which checks ran and what is not done. When the task
  leaves changes in Git, suggest a commit message.
