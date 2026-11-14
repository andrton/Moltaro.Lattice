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
  sprint added in its report to the owner. When the coordinator prepares a sprint, it
  reads the open entries that name that sprint and does not start it before each of them
  is answered or moved to a later sprint by whoever decides it. Before the contracts of a
  stage are written, the planner goes through the open entries and takes up those the
  stage touches. When an epic closes, every entry is gone through with the owner: none
  stays open without a decision to carry it into the next epic.
- **How it ends.** An answer is the owner's. It is entered here with its date, and the
  rule it gives goes where it belongs: a decision record, the epic document, a brief,
  `AGENTS.md`. The entry then moves to **Answered** and says where the rule now lives.

## Open

### Q14. A class can fail to bind with an exception that is not a `LatticeException`

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | Binding a class reports a class that does not agree with its entity type as `BindingFailed`. Two classes escape that: one with a static property named like a field throws `ArgumentException` from `Set<TEntity>()`, and a derived class that hides a property with one of another type throws `AmbiguousMatchException`; both also from the typed members of a write batch. The code is older than S02. Are both `BindingFailed`, with the property named? |
| **Until answered** | The two exceptions escape as they are. The documentation of `ILatticeDatabase.Set<TEntity>()` names every cause that is reported as `BindingFailed`, and these two are not among them |
| **Decides** | The planner, for S03, which extends the binding of classes and states that "a class with inherited properties binds as before" |

## Answered

### Q1. A .NET enum that does not agree with the enum type of the schema

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 lets a class declare a property as a .NET enum and gives one rule, for two names of one value. It does not say what happens when the .NET enum has a name or a value the schema does not declare, or lacks one the schema declares |
| **Answer** | The owner, 14 November 2026: it is handled exactly as a schema that does not match. The binding is refused, as it is for a class that does not agree with its entity type; nothing is guessed, mapped or tolerated |
| **Where the rule lives** | Decision O15 and the brief of S02 in the [document of Epic 1](epic-1-core.md) |

### Q5. Does a layout fingerprint cover the identity of its entity type and its primary key?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 lists what the layout fingerprint of an entity type covers — the string encoding and, for every field, identity, type, nullability, string storage and the underlying type of an enum — and what it does not. The identity of the entity type and its primary key are in neither list |
| **Answer** | The owner, 14 November 2026, through the planner's session: the primary key is covered, the identity of the entity type is not. Which field is the key decides how an entity is found in the saved base, so a change of the key field has to be seen by a migration; the fingerprints of one entity type are compared across two versions of a schema, where its identity is the same, so two entity types with the same fields and key have the same layout fingerprint. The fingerprint of the schema does not change: it covers both already |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md), which extends the layout fingerprint anyway. S02 delivers the fingerprint without the key, as its brief lists it; the recorded layout value changes in S03; the recorded value of the schema fingerprint is recorded again there only if the extension of S03 changes it |

### Q7. Is a policy on a field of the wrong type an error of the descriptor?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The approved choice "the descriptor is closed" refuses a property the reader does not know. It does not speak of a property the reader knows in the wrong place: `Comparison` or `StringStorage` on a field that is not a String, `Enum` on a field that is not of type Enum |
| **Answer** | The owner, 14 November 2026: yes. Each is `InvalidSchema` with the field named; a misplaced policy is never ignored |
| **Where the rule lives** | "Choices made in the contracts of S02 to S12" and the brief of S02 in the [document of Epic 1](epic-1-core.md) |

### Q12. The documentation of unique indexes states the intended behavior as present

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | `LatticeIndex.IsUnique`, `LatticeErrorEnum.UniqueConstraintViolation` and the remarks of `ILatticeSet<TEntity>` describe a unique index as enforced. Until S16 every write of an entity type that declares one is refused with `NotSupportedException`, and no XML text said so; `ILatticeIndex` does say "Not implemented yet" for search |
| **Answer** | The owner, 14 November 2026, through the planner's session and then directly: yes, the sentence is added, and in the branch of S02 instead of a change of its own |
| **Where the rule lives** | The XML documentation of the three members, since S02. S16 removes the sentences when it implements unique indexes: remark 8 for the coordinator in the [document of Epic 1](epic-1-core.md) |

### Q8. Is a property given twice an error of the descriptor?

| | |
| --- | --- |
| **Found** | 14 November 2026, implementation of Epic 1, sprint S02 |
| **Question** | JSON lets an object name a property twice, and a reader then takes one of the two values silently. Neither the brief nor a decision says what the schema reader does |
| **Answer** | The owner, 14 November 2026: yes. It is `InvalidSchema` with the property named, at every level of the descriptor |
| **Where the rule lives** | "Choices made in the contracts of S02 to S12" and the brief of S02 in the [document of Epic 1](epic-1-core.md) |

### Q13. Where does a saved compiled schema end?

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | The fix that made the reader of a saved schema strict also decided two things nobody had asked for. `LatticeSchema.Load(path)` refuses a file that has data after the schema; before, such a file loaded. `LatticeSchema.Load(stream)` reads one schema and leaves the stream right after it, so the same bytes load through a stream. Are these the rules? |
| **Answer** | The owner, 14 November 2026: yes, a file with data after the schema is refused. Data after a schema in its own file means damage or two files joined, and it is not passed off as a success. A stream is the caller's: one schema is read from it and what follows is left to the caller |
| **Where the rule lives** | The documentation of `LatticeSchema.Load(string)` and `LatticeSchema.Load(Stream)`, the tests of `DamagedCompiledSchemaTests`, and the brief of S02 in the [document of Epic 1](epic-1-core.md) |

### Q9. May an enum type declare no members?

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | An enum type with `"Members": []` compiles. An ordinary enum type without members has no valid value, so a field of it that is not nullable can never be written; a Flags type without members accepts zero only. Q03.5 and the brief say nothing |
| **Answer** | The owner, 14 November 2026, through the planner's session: no. An enum type declares at least one member, a Flags type like an ordinary one; `"Members": []` is `InvalidSchema` with the enum type named. A Flags type whose only member is zero stays valid. The check is made in S03; S02 delivers enum types as its brief has them, and an empty list compiles until then |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q10. Which catalog versions are valid?

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | `"SchemaVersion": 0` and a negative one compile, as they did before S02. Since S02 the number is the catalog version, which the milestone contract calls monotonically increasing; nothing says where it starts or what a descriptor without it means |
| **Answer** | The owner, 14 November 2026, through the planner's session: a catalog version is a whole number from 1; zero or below is `InvalidSchema` with the property named. A descriptor without `SchemaVersion` has version 1, as before, and the property stays optional. "Monotonically increasing" means that the target schema of a migration has a greater version than the stored one |
| **Where the rule lives** | The range and the default: the brief of S03 in the [document of Epic 1](epic-1-core.md), which makes the check and words the documentation of `LatticeSchema.Version`. The rule that a version grows: the brief of S37 in the same document |

### Q11. Should a saved compiled schema carry a digest of itself?

| | |
| --- | --- |
| **Found** | 14 November 2026, fixes after the review of Epic 1, sprint S02 |
| **Question** | A truncated or malformed saved schema is refused. A byte change that still forms a valid schema — another name, another identity — loads as another schema, because the format carries no checksum. The fingerprint does not cover names, so it does not show such a change either |
| **Answer** | The owner, 14 November 2026, through the planner's session: yes. The binary form of a compiled schema ends with SHA-256 over every byte before it, and a load refuses a difference with `InvalidSchema` before it decodes anything of the body. It comes in S03 with the format version that sprint changes anyway; S02 delivers the form without a digest |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q6. The write batch does not document every way a class can fail to bind

| | |
| --- | --- |
| **Found** | 14 November 2026, implementation of Epic 1, sprint S02 |
| **Question** | The members of `ILatticeWriteBatch` that take an entity class documented `BindingFailed` as "the schema declares no entity type for the class". A batch binds the class as a set does, so a class that lacks a property, or whose .NET enum does not agree with the schema, fails there too |
| **Answer** | The owner, 14 November 2026, through the planner's session: the documentation is completed now, in the branch of S02, because S02 added a cause of `BindingFailed` and the document that owns a behavior is updated in the same change |
| **Where the rule lives** | The XML documentation of `ILatticeWriteBatch`, which points to `ILatticeDatabase.Set<TEntity>()`; that member names every cause, since S02 |

### Q3. Nothing guards the exact pin of the index package

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S01 |
| **Question** | `Arc.Collections` is pinned as `[2.2.0]`, but the test of the engine's dependencies compares the resolved version, so it stays green when the brackets are dropped and the package could then float for a user of the engine package. Should a test hold the exact pin? |
| **Answer** | The owner, 14 November 2026, through the planner's session: yes, a test holds the declaration itself, brackets included. It is added in S04, the first sprint that puts code into the engine |
| **Where the rule lives** | The brief of S04 in the [document of Epic 1](epic-1-core.md) |

### Q2. Is the squash enforced or only agreed?

| | |
| --- | --- |
| **Found** | 14 November 2026, review of the change that lets the coordinator merge |
| **Question** | [Branches and releases](branches.md) says every pull request is squashed, while the ruleset of `main` and the settings of the repository still admitted a merge commit. Should the ruleset allow the squash only? |
| **Answer** | The owner, 14 November 2026, through the planner's session: yes, the squash is enforced. The ruleset file allows the squash only, and the setting of the repository that admits merge commits is switched off |
| **Where the rule lives** | `.github/rulesets/main.json` and [branches and releases](branches.md), since S02. The owner applies the ruleset and the setting on GitHub; until then the coordinator keeps naming the squash explicitly |

### Q4. What does the Identity package validate in the new descriptor form?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S02 |
| **Question** | The brief of S02 says the Identity package's "descriptor and its validation follow the new form". It does not say whether the validation of an application's schema also compares the comparison and the string storage of a field, or the identities of the indexes, with what the package needs |
| **Answer** | The owner, 14 November 2026, through the planner's session: the validation compares, for every field of the default declaration, its type, its nullability and its string comparison. It compares neither the string storage nor an identity. The comparison decides which values are equal, so what an index finds and what a unique index refuses, and Identity tells some of its values apart by case; the storage changes memory and the cost of a write only; identities belong to the application's schema, and the stores address everything by name |
| **Where the rule lives** | [Epic 2](epic-2-identity-storage.md), "The schema belongs to the application". It is implemented in Epic 2; during Epic 1 the validation keeps comparing type and nullability |
