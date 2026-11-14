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
- **How it ends.** An answer comes from whoever the entry names as deciding. A rule of
  the public contract that no standing rule gives is the owner's; what follows from a
  rule that already stands the planner answers and reports to the owner. It is entered
  here with its date and with who gave it, and the
  rule it gives goes where it belongs: a decision record, the epic document, a brief,
  `AGENTS.md`. The entry then moves to **Answered** and says where the rule now lives.

## Open

### Q22. Does a record hold an owned value it is given, or a copy of it?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | The brief says the input of a List, a Map or a Set is copied. It did not say whether collections inside it are copied too, or whether a `LatticeOwnedValue` or a byte array that is set into a record is held as the same object or copied |
| **Until answered** | Settled by the planner and confirmed by the owner on 14 November 2026: a record rebuilds a collection it is given at every depth, and a write and a read validate and copy at every depth; the brief of S03 has the text. Under discussion with the owner: whether a `LatticeOwnedValue` and a byte array are held as given or copied. No test and no documentation states either |
| **Decides** | The owner, for S03 |

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

### Q14. A class can fail to bind with an exception that is not a `LatticeException`

| | |
| --- | --- |
| **Found** | 14 November 2026, review of Epic 1, sprint S02 |
| **Question** | Binding a class reports a class that does not agree with its entity type as `BindingFailed`. Two classes escaped that: one with a static property named like a field threw `ArgumentException` from `Set<TEntity>()`, and a derived class that hides a property with one of another type threw `AmbiguousMatchException`; both also from the typed members of a write batch. The code is older than S02. Are both `BindingFailed`, with the property named? |
| **Answer** | The owner, 14 November 2026, through the planner's session: yes. The property of a field is found as C# finds it by that name, on the most derived class that declares one; it has to be an instance property with get and set and of the type the field needs. A hiding property of that type binds; a static property, and a hiding property of another type, are `BindingFailed` with the property named |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md), which fixes it with the binding it extends |

### Q15. What is the element type of a collection of owned values in a bound class?

| | |
| --- | --- |
| **Found** | 14 November 2026, contract of Epic 1, sprint S03 |
| **Question** | The brief of S03 said a List is a `List<T>` where "T is the `ValueType` of the inner declaration", and that `ValueType` of an Owned position is `LatticeOwnedValue`. It also said that in a bound class an owned value is "the class named by `ClrType` of its owned type". For a collection of owned values in a bound class the two sentences gave different T. Which one binds, and what does a class do with an owned type that names no `ClrType`? |
| **Answer** | The planner, 14 November 2026, from the sentence of the brief on a bound class, and reported to the owner: `ValueType` says what a record holds. A bound class holds the same data in its own types, by one rule for every position: an owned value is the class of its owned type as a property, an element or a value. An owned type that names no `ClrType` has no form in a class, and neither has `LatticeOwnedValue`: both are `BindingFailed` |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md): the table "In a bound class" |

### Q16. May an owned type contain itself?

| | |
| --- | --- |
| **Found** | 14 November 2026, contract of Epic 1, sprint S03 |
| **Question** | Nothing said whether an owned type may have a field of its own type, directly or through a List, a Map or another owned type. Q02 excludes cycles of ownership between values; a type that refers to itself through a nullable field or a collection has none in its values, but its layout and both fingerprints would have to describe a type in terms of itself |
| **Answer** | The owner, 14 November 2026, through the planner's session: no. An owned type does not contain itself, directly or through another owned type, a List or a Map; such a declaration is `InvalidSchema` with the owned types on the way named, whether the field is nullable or not. A Reference is not containment. Allowing it would make every walk over values bear any depth and need a depth limit as a setting; the restriction can be lifted later without changing the meaning of a schema that compiles under it |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q17. The scope of an owned type's identity, name and class

| | |
| --- | --- |
| **Found** | 14 November 2026, contract of Epic 1, sprint S03 |
| **Question** | The brief gave an owned type an `Id`, a `Name` and an optional `ClrType` and did not say in which scope each is unique. May an owned type and an entity type have the same identity, the same name, or name the same .NET class? May two owned types name the same class? |
| **Answer** | The planner, 14 November 2026, from the scopes S02 gave enum types and entity types, and reported to the owner: owned types are a scope of their own for identities and for names, so an owned type may have the identity or the name of an entity type or an enum type. A .NET class is named by at most one type of the schema: two owned types, or an owned type and an entity type, that name the same class are `InvalidSchema` |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q18. A null key value for an index part behind an absent owned value

| | |
| --- | --- |
| **Found** | 14 November 2026, contract of Epic 1, sprint S03 |
| **Question** | The check of a search key refuses null for a part whose field is not nullable. By decision P9 a part that is a path through owned values is null when an owned value on the path is absent, even when the indexed field itself is not nullable. Does the check of S03 have to know this, or does it come with S18? |
| **Answer** | The planner, 14 November 2026, from decision P9 and the rule that a check needing no engine works from the start, and reported to the owner: the check of S03 accepts null for a part whose field is nullable or whose path has a nullable owned step, and refuses it for any other part with `ArgumentException`. That the entity is then found by a search for null stays with S18 |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q19. Which fields may be the last step of an index part?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | The brief of S03 accepted an index part "when … the last is a field an index may have", and S02 refuses a Binary field. May a part end at an Owned field, at a Reference, or at a List, a Map or a Set? |
| **Answer** | The planner, 14 November 2026, from Q03.4.2, confirmed by the owner: a part ends at a scalar that is not Binary, an Enum or a Reference, the types that have an equality of their own. A Reference is compared as the key of its target. A part that ends at an Owned field or a collection is `InvalidSchema` |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q20. Is an integer of another width accepted inside a collection of a record?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | A record accepts for an integer field "an integer of another width when it fits". Is a `List<int>` accepted for a List of Int64, and an `int` key for a Map of Int64 keys? The same for an integer inside a `LatticeOwnedValue` |
| **Answer** | The planner, 14 November 2026, from the purpose of the rule, confirmed by the owner: the element, key and value types of a collection are exact, and another one is `ArgumentException`. An integer of another width is accepted only where a value is given by itself: a field of a record, a field of a `LatticeOwnedValue`, a primary key, a part of a search key |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q21. Which comparer has a Map or a Set of references?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | A Reference declaration has no comparison of its own. When the key of its target is a String compared without case, does a Map or a Set of references ignore case as well, and does a fingerprint cover it? |
| **Answer** | The planner, 14 November 2026, from Q03.4.2, confirmed by the owner: a reference compares as the key of its target, comparison included, so such a Map or Set ignores case. No fingerprint gains anything: the layout fingerprint never covers a comparison, and the fingerprint of the schema already covers the comparison of the target's key and the identity of the target |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md); the consequence for a migration in the brief of S37 |

### Q23. Which error comes first for an object with an invalid nested value?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | Until S18 and S19 a write of an entity type with one of the new fields is refused with `NotSupportedException`. Through a set, does an invalid object or record get `ValidationFailed` or that refusal? |
| **Answer** | The planner, 14 November 2026, from the rule that a check needing no engine works from the start, confirmed by the owner: validation comes first, through a set as through a write batch. An invalid value at any depth is `ValidationFailed`, and only a valid object or record ends in `NotSupportedException` |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q24. Forms of the descriptor that the brief of S03 does not mention

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | An index part written as a path of one identity, `[[2]]`; an empty path; the same part twice in one index; an owned type without fields; an empty `ClrType` on an owned type; and the sentence on Map keys and Set elements, which could be read as excluding Enum and Reference |
| **Answer** | The planner, 14 November 2026, from what S02 does for the same things one level up and the owner's answer to Q9, confirmed by the owner: `[[2]]` and `[2]` are the same part; an empty path and the same part twice are `InvalidSchema`; an owned type declares at least one field, and its `ClrType` is not empty when given; a Map key and a Set element are a scalar that is not Binary, an Enum, or a Reference |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q25. Does validation also come before the refusals of S02?

| | |
| --- | --- |
| **Found** | 14 November 2026, documentation of Epic 1, sprint S03 |
| **Question** | By the answer to Q23 an invalid object or record of an entity type with an owned value, a collection or a reference is `ValidationFailed`, and only a valid one ends in `NotSupportedException`. Is the same true of what S02 has the placeholder refuse: a unique index, a comparison that ignores case, Dictionary storage, an enum field, a schema in UTF-16? |
| **Answer** | The planner, 14 November 2026, from what the code already does: yes, for every entity type. A write first checks its arguments and validates the entity against the schema, and only a write that passes ends in `NotSupportedException`. A check that belongs to what is not implemented is not made: whether an integer is a declared value of its enum type, and whether a unique index already holds the key |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md), with the sentence for the "Not implemented yet" parts of the public documentation |
