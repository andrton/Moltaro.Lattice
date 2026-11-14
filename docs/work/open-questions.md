# Open questions

Questions that came up while a sprint was carried out and that no brief, decision record
or rule answers. Each is written down when it is found, so that it is not settled by
whoever happens to write the code, and not forgotten.

A question that blocks a sprint does not wait here: the coordinator stops and asks the
owner, as the [development process](process.md#when-to-stop-and-ask-the-owner) says. This
register is for what does not block: the sprint goes on, and the entry says what was done
in the meantime.

## How it is used

- **Who writes.** The coordinator, when an agent or a reviewer reports a gap, or when it
  finds one itself. An agent reports; it does not decide.
- **What an entry says.** Where the question was found, the question itself, what the
  code or the tests do until it is answered, and who has to answer it.
- **When it is looked at.** When a sprint closes, the coordinator names the entries the
  sprint added in its report to the owner. Before the contracts of a stage are written,
  the planner goes through the open entries and takes up those the stage touches. When an
  epic closes, every entry is gone through with the owner: none stays open without a
  decision to carry it into the next epic.
- **How it ends.** An answer is the owner's. It is entered here with its date, and the
  rule it gives goes where it belongs: a decision record, the epic document, a brief,
  `AGENTS.md`. The entry then moves to **Answered** and says where the rule now lives.

## Open

### Q2. Is the squash enforced or only agreed?

| | |
| --- | --- |
| **Found** | 14 November 2026, review of the change that lets the coordinator merge |
| **Question** | [Branches and releases](branches.md) says every pull request is squashed, while the ruleset of `main` and the settings of the repository still admit a merge commit. Should the ruleset allow the squash only? |
| **Until answered** | The coordinator merges with the squash named explicitly. A merge commit made by mistake could not be taken out of `main` |
| **Decides** | The owner: it is a change of `.github/rulesets/main.json` and of a repository setting, which the owner applies |

### Q3. Nothing guards the exact pin of the index package

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S01 |
| **Question** | `Arc.Collections` is pinned as `[2.2.0]`, but the test of the engine's dependencies compares the resolved version, so it stays green when the brackets are dropped and the package could then float for a user of the engine package. Should a test hold the exact pin? |
| **Until answered** | The pin is held by `src/Directory.Packages.props` and its comment |
| **Decides** | The planner, with the sprint that makes the engine a package (S12) |

### Q4. What does the Identity package validate in the new descriptor form?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 says the Identity package's "descriptor and its validation follow the new form". It does not say whether the validation of an application's schema also compares the comparison and the string storage of a field, or the identities of the indexes, with what the package needs |
| **Until answered** | The validation compares what it compared before, type and nullability; no test asserts more |
| **Decides** | The planner of [Epic 2](epic-2-identity-storage.md), which owns that package |

### Q5. Does a layout fingerprint cover the identity of its entity type and its primary key?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 lists what the layout fingerprint of an entity type covers — the string encoding and, for every field, identity, type, nullability, string storage and the underlying type of an enum — and what it does not. The identity of the entity type and its primary key are in neither list |
| **Until answered** | No test asserts either way; the code follows the list as written, so neither is covered |
| **Decides** | The planner, before the contracts of the stage with schema migration (S37), which is what the layout fingerprint is for |

### Q6. The write batch does not document every way a class can fail to bind

| | |
| --- | --- |
| **Found** | 14 November 2026, implementation of Epic 1, sprint S02 |
| **Question** | The members of `ILatticeWriteBatch` that take an entity class document `BindingFailed` as "the schema declares no entity type for the class". A batch binds the class as a set does, so a class that lacks a property, or whose .NET enum does not agree with the schema, fails there too. The text was incomplete before S02 and is outside its scope |
| **Until answered** | The behavior is right and tested through sets; only the documentation of the batch members says less than happens |
| **Decides** | The planner, with the sprint of the write batch (S17), which owns that contract |

## Answered

### Q1. A .NET enum that does not agree with the enum type of the schema

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 lets a class declare a property as a .NET enum and gives one rule, for two names of one value. It does not say what happens when the .NET enum has a name or a value the schema does not declare, or lacks one the schema declares |
| **Answer** | The owner, 14 November 2026: it is handled exactly as a schema that does not match. The binding is refused, as it is for a class that does not agree with its entity type; nothing is guessed, mapped or tolerated |
| **Where the rule lives** | Decision O15 and the brief of S02 in the [document of Epic 1](epic-1-core.md) |
