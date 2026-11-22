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
  code or the tests do until it is answered, and who has to answer it. An entry may be
  *taken*: the coordinator took the recommended answer and the work went on, as the
  [limits on questions](process.md#limits-on-review-and-on-questions) say. A taken
  answer counts as answered for an entry that names a sprint as its limit.
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

### Q31. Native bytes that no counter of the arena shows

| | |
| --- | --- |
| **Found** | 15 November 2026, implementation of Epic 1, sprint S04 |
| **Question** | A region is 64 KiB and serves one size class. A class that does not divide 64 KiB leaves a tail at the end of each of its regions, at most 2,304 bytes (3.5 %) for the 3,328-byte class; and the free slots of a region that still holds blocks are counted nowhere either. `ArenaCounters` tells live bytes, class slack, free regions and the reserve apart, and neither the tails nor those free slots are in any of them, so the counters do not add up to the native capacity. Do the memory budget (S24), the memory gate (S25) and diagnostics (S39) need them as counters of their own, and is 64 KiB the region size they are judged with? |
| **Until answered** | The arena of S04 has the counters its brief names and regions of 64 KiB, the implementer's choice. `Segments` times the segment size is the native capacity obtained; what the named counters do not cover is the tails and the free slots of regions in use |
| **Decides** | The planner, before the contracts of the stage with the memory budget (S24) |

### Q33. What an application sees when the table of root entries is full

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S04 |
| **Question** | The table of root entries ends at 2^31 − 4,096 slots, a limit of its structure and not an option. `RootTable.Add` then throws `InvalidOperationException` and leaves the table as it was. No brief says which error a write of an application gets when the engine reaches that limit, or a retired slot count that makes it nearer; the resource errors of the contract come with S08, S09, S17 and S24 |
| **Until answered** | The internal exception stands; no test can reach the limit, which needs about 96 GiB of root entries |
| **Decides** | The planner, with the resource errors of the memory budget (S24) |

### Q35. What the root layout of S05 leaves to later sprints

| | |
| --- | --- |
| **Found** | 15 November 2026, contract of Epic 1, sprint S05 |
| **Question** | Seven points of entries Q34 and of the reviews of S05 and S06 reach into later sprints and need the planner's word there. (1) S06: `RootLayout.Compile` refuses an entity type with a field it cannot store yet, and the engine contract wants an oversized fixed part reported before data is admitted, while the brief of S06 refuses at a write; when does the engine compile a layout, and when does an application see `InvalidSchema`? (2) S08 and S09: is the size of an entity, as an application's limit counts it, the fixed part plus the encoded bytes of its String and Binary values, and is `PreparedWrite.EncodedSize` the same number? (3) S10: a value in a root is stored in the byte order of the machine; which order have the blocks a child receives? (4) S20: an inline descriptor holds an offset and a length and no capacity; where does the capacity that a partial update needs live? (5) S37: every layout has `LayoutVersion` 1; how is a changed layout numbered? A root of another entity type passes the check of a reader as long as every layout has version 1. (6) S09: one String or Binary value holds at most 4,094 chunks of 65,504 bytes, 268,173,376 bytes, the bound of one directory block; when `MaxEntitySize` becomes an option, a larger setting still gets `WriteRejected` for such a value. Is that bound stated with the option, or is the directory to grow? (7) S18 and S19: one String or Binary value is bounded at 268,173,376 bytes of its stored encoding, and the documentation states the bound at the field type; does it hold for each element of a List, each key and value of a Map, each member of a Set and each field of an owned value? |
| **Until answered** | The readings of Q34 stand for S05. Point 1 is answered by the planner, 15 November 2026, and written into the brief of S06: the engine compiles the layout of every entity type when the database opens; a fixed part larger than one block fails the open with `InvalidSchema`; an entity type the engine cannot store yet gets no layout and does not fail the open. Points 2 and 6 are answered with the owner's decision O19 of 15 November 2026 and by the planner: an application has no limit on the size of an entity any more, and `PreparedWrite.EncodedSize` keeps its meaning for the accounting of queued input, worded with the contracts of S08 and S09; one String or Binary value holds at most 268,173,376 bytes as a fixed rule of the storage format, the directory does not grow in this milestone, and a value above it is `ValidationFailed` with the field named. Point 3 is answered by the planner, 15 November 2026, in the brief of S10: blocks travel in the byte order of the sending machine, the Hello carries the byte order, and a peer of another byte order is refused; no conversion is built |
| **Decides** | The planner: points 4, 5 and 7 before the contracts of their stages |

### Q40. What decision O18 asks of the stages whose contracts are not written yet

| | |
| --- | --- |
| **Found** | 15 November 2026, the owner's decision O18 on the limits of a write batch |
| **Question** | A batch has no limit of its own, and a bounded buffer never refuses one transaction for its own size. The briefs of S09 and S17 say what follows for the queued write input and for the batch. Three places wait for the contracts of their stages: the journal and the application of the base, where one transaction larger than a batch of base application is applied alone, within bounded memory; replication, where one transaction larger than the pending replication buffer is delivered, and a child that cannot hold it pauses as Q14 says and reloads by S34; and the hard limit of the journal as the one storage bound a batch can meet. |
| **Until answered** | The engine contract states the rule under its table of settings; nothing before S09 depends on it |
| **Decides** | The planner, with the contracts of the stages of S26 to S32 and of S33 to S36 |

### Q52. Does the write path need the parallel form?

| | |
| --- | --- |
| **Found** | 17 November 2026, the study of Epic 1, sprint S09, part 2 |
| **Question** | Writes are prepared one at a time under the lock of the writes, so several calling threads write less in total than one, and one write of a large entity holds the lock while it is encoded. The form that scales writers in established systems fixes the order first and lets each caller encode in parallel, which needs an arena that allocates on several threads. Does the write path need it? |
| **Until answered** | Writes are prepared one at a time under the lock of the writes. The owner chose on 17 November 2026, through the planner's session, not to build the parallel form now |
| **Decides** | The owner, on the planner's proposal, with the figures of S27 and with the measurement of S09, part 2 of what an entity of 32 MiB costs other writers. Measured on 17 November 2026: one write of 32 MiB holds the lock of the writes 2.6 to 2.7 ms (p99 5 to 7 ms); small writes of another caller beside a caller that writes such entities one after another fall to 1,500 to 4,300 a second with waits of up to 112 ms, against 0.57 to 0.91 million a second on S08 and 1.24 to 1.44 million on S09, which encoded outside its lock. The ordinary profile has no such entities. The figures and what was tried are in the [document of Epic 1](epic-1-core.md), "Measured results" |

### Q53. A fresh insert found by a search before a read by key finds it

| | |
| --- | --- |
| **Found** | 21 November 2026, the breaker of Epic 1, sprint S15, part 1, by reading, not probed |
| **Question** | `ChangePublisher.InstallNew` installs the secondary entries of a new entity first, and `Install` stores its stamp and marks it live before the entry of the primary index. For a few instructions a search can return a fresh insert at the position of its write while a read by key of it still finds nothing, at the position before. Each answer is true at its own position, and a read in a session waits on the clock and finds it. Is this acceptable, or must a new entity become visible to every kind of read at once? |
| **Until answered** | Each answer is true at the position it reports; nothing is changed |
| **Decides** | The planner, with S17, whose means of exposing a batch whole most likely closes it |

### Q54. What the binding of an index value does where the documents do not say

| | |
| --- | --- |
| **Found** | 21 November 2026, the coordinator's inventory for Epic 1, sprint S15, part 2 |
| **Question** | Three points that neither the engine contract nor Q07.3.6 decides. (1) Is a .NET enum value accepted for an Enum part? (2) Is an integer the enum type does not declare refused as a bound of a range as well as an exact value? (3) Is a null given as `string` a null with a type? |
| **Answer** | Taken by the planner, 21 November 2026, for the owner to read at the close of the sprint. (1) No: an Enum part is given as its integer; accepting the .NET enum would need a rule of agreement at every search and can be added later. (2) Yes: the contract rejects invalid enum and Flags bits for "lookup/bound parameters" alike. (3) Yes: Q07.3.6 checks a null by its declared type, and `string` is one; a null given as `object` has none. Taken by the coordinator, 21 November 2026, after the review of the sprint, from the same rule: a null given as any other class has a type no scalar field has and is refused for every part, as a `Nullable` without a value of such a type is |
| **What the code does** | As the brief of S15, part 2 says, and a null given as a reference type other than `string` and `object` is refused for every part (`IndexValue.Of`, the remarks of `IndexKey`) |
| **Where the rule lives** | The brief of S15, part 2 in the [document of Epic 1](epic-1-core.md) |

### Q55. Should a value written by name be bound as a part of a search key is?

| | |
| --- | --- |
| **Found** | 21 November 2026, the architect's review after Epic 1, sprint S15, part 2 |
| **Question** | A search binds a value by the allowlist of Q07.3.6: an integer of another width, any integer for a Decimal, an integer a Float32 or a Float64 holds exactly, a Float32 for a Float64 and a Float64 that converts back to itself. A value written by name is accepted only in the type of the field, or as an integer of another width that fits. So `record["Price"] = 5` is refused for a Decimal field, where `IndexKey.Equal(5)` finds the entity. Is the difference meant? |
| **Answer** | Decided by the owner, 21 November 2026, in the planner's session: one rule. A value given by itself in a write by name, the value of a field of a record or of an owned value, is bound by the allowlist of Q07.3.6, as a part of a search key is; the elements, keys and values of a collection stay as declared, and a primary key keeps its own rule, since a key is an integer, a String or a Guid. Not built yet: it is a task of S18, which works on the values written by name |
| **What the code does** | The narrower rule, as the remarks of `LatticeRecord` state it (`FieldValues.Normalize`) |
| **Where the rule lives** | The remarks of the indexer of `LatticeRecord`; `docs/decisions/queries.md`, Q07.3.6, for a search |

### Q56. Where the decision on unique indexes and the engine no longer say the same

| | |
| --- | --- |
| **Found** | 22 November 2026, the study of Epic 1, sprint S16 |
| **Question** | Two sentences of `docs/decisions/unique-indexes.md`. (1) Step 4 says "Remove old keys before installing final entries". Since S14 the engine installs the new entries before the root is stored and removes the old ones after the position; a reader sees no duplicate, because an entry is checked against its root, but inside the publication of a batch a unique tree may hold two entries of one key for a moment. (2) The record names `OrderedMultiMap` of a package; the engine has its own `OrderedTree`. Is the guarantee "readers cannot observe an intermediate duplicate" what the record means to keep, with the order of the two steps free? |
| **Answer** | Decided by the owner, 22 November 2026, in the planner's session: yes. The guarantee is the reader's, that no reader observes an intermediate duplicate; the order in which old keys are removed and final entries installed is not part of it. The two sentences of the decision record are corrected to say so; the engine does not change. The study of S17 takes the guarantee as it stands for the publication of a batch |
| **What the code does** | New entries before the store of the root, old ones after the position (`ChangePublisher`), since S14 |
| **Where the rule lives** | `docs/decisions/unique-indexes.md`, "Master validation and publication"; `docs/design/write-path.md` |

### Q57. Is the barrier of the decision on the write batch a guarantee or a means?

| | |
| --- | --- |
| **Found** | 22 November 2026, the study of Epic 1, sprint S17 |
| **Question** | `docs/decisions/document-model.md` says a batch is published "behind an exclusive database barrier", that every protected access cooperates with it and that "existing protected users drain before batch publication", and leaves the gate to engineering. The study proposes no barrier: every member is stored with its final stamp, the position is published last, and a read that meets a stamp above the position waits as it waits at the mark. A reader then sees all of a batch or none, a read of an entity that is not a member is never held, and nothing drains. Is the guarantee "all or none, and no position before all is visible", with the barrier as one way to keep it? |
| **Answer** | Decided by the owner, 22 November 2026, in the planner's session: yes. The guarantee is that a reader sees all of a batch or none of it and that no answer carries its position before all of it is visible; the barrier was a means. The engine keeps the guarantee with the protection of reads and the stamps it has, by publishing the position last; no read of an entity that is not a member is held, and nothing drains. The sentences of the decision record are corrected to say so |
| **What the code does** | Nothing yet: committing a batch is refused until S17 |
| **Where the rule lives** | `docs/decisions/document-model.md`, "Atomic WriteBatch within one database"; `docs/design/arena-protocol.md`, the write batch extension |

### Q58. Which member of a batch is the failing one when two of them claim one unique value

| | |
| --- | --- |
| **Found** | 22 November 2026, the study of Epic 1, sprint S17 |
| **Question** | `UniqueIndexTests` assert the number of the failing member against a published owner. For two members of one batch that claim the same value no test and no document names it |
| **Answer** | Taken by the planner, 22 November 2026, for the owner to read at the close of the sprint: the later member, in the first violated index by declaration, since the earlier member alone would have been accepted; against a published owner that no member frees, the member that claims |
| **What the code does** | As the brief of S17 says |
| **Where the rule lives** | The brief of S17 in the [document of Epic 1](epic-1-core.md) |

### Q59. Reading several entities linked by references as one consistent set

| | |
| --- | --- |
| **Found** | 22 November 2026, the owner, in the planner's session |
| **Question** | A read is coherent for one entity, and several reads are not a snapshot (principle 3). An application that reads a set of entities linked by references, not owned values, can therefore combine states of different positions, and with ranges it also meets phantoms. The case is rare. How does an application read such a set while nothing writes to it? |
| **Answer** | Decided by the owner, 22 November 2026: the engine does not grow a snapshot of several reads or a transaction of an application; that would cost reads or memory in its core. The application solves the case itself, and the database gives it a synchronization object for it: advisory locks, as in PostgreSQL, exclusive and shared, arbitrated by the master and asked for from any node (the sprint after S34 in the document of Epic 1). Two ways exist without it: what must be read as a whole is one entity with owned values; or a root entity whose revision every writer of the set changes in the same write batch, which a reader reads before and after the set and reads again when it changed (possible from S17 on, the read stays local) |
| **What the code does** | Nothing for it; each read is coherent by itself |
| **Where the rule lives** | `AGENTS.md`, principle 3; `docs/decisions/document-model.md`, where it defers locks held by an application beyond v1 and says an advisory lock alone does not freeze a graph, which the decision record of the new sprint amends |

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
| **Where the rule lives** | The XML documentation of the three members, since S02. S16 removed the sentences when it implemented unique indexes (2da3bf7): remark 8 for the coordinator in the [document of Epic 1](epic-1-core.md) |

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

### Q22. Does a record hold an owned value it is given, or a copy of it?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | The brief said the input of a List, a Map or a Set is copied. It did not say whether collections inside it are copied too, or whether a `LatticeOwnedValue` or a byte array that is set into a record is held as the same object or copied. The question was put on this code: `home["City"] = "Kyiv"; record["Home"] = home; home["City"] = "Lviv";` and then a write |
| **Answer** | The owner, 14 November 2026, through the planner's session: copied; Kyiv is written. A record copies every value that is set into it, at every depth, owned values and byte arrays included, and holds nothing of the caller's. What a record returns is its own and the same object every time, and a value inside a record is edited through what the record returns. Not chosen: a record that holds an owned value and a byte array as given, and a switch that leaves the choice to the caller. A bound class is not touched: its object holds what the application assigns, and the write copies it |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md); what a write and a read copy is asserted in S18, whose brief has the sentence |

### Q26. Does an owned value return its own values, as a record does?

| | |
| --- | --- |
| **Found** | 14 November 2026, tests of Epic 1, sprint S03 |
| **Question** | By the answer to Q22 what a record returns is its own and the same object every time. The brief did not say what a `LatticeOwnedValue` returns, nor what happens to an owned value that was made invalid through a collection it returned |
| **Answer** | The planner, 14 November 2026, as a consequence of the owner's answer to Q22, confirmed by the owner: a `LatticeOwnedValue` returns as a record does, its own collection, owned value or byte array, the same object every time. An owned value made invalid through a collection it returned is refused with `ArgumentException` and the field named when it is set into a record, into another owned value or into a collection, because setting it copies and checks what it holds |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md) |

### Q27. The schema names a type of the access layer

| | |
| --- | --- |
| **Found** | 14 November 2026, implementation of Epic 1, sprint S03 |
| **Question** | The brief of S03 puts `LatticeOwnedValue` into `Moltaro.Lattice.Access` and has `LatticeValueDeclaration.ValueType` of an Owned position report it. So a type of `Schema` names a type of `Access`, while `Access` is built on `Schema`, which principle 11 of `AGENTS.md` does not allow. Does `LatticeOwnedValue` belong to the namespace of the schema, or is this one reference an exception to record? |
| **Answer** | The owner, 14 November 2026, through the planner's session: the one reference stays and is recorded as an exception; no code and no contract changes. Not chosen: moving `LatticeOwnedValue` into the schema namespace, and taking `ValueType` out of the schema |
| **Where the rule lives** | Exception X5 in the [exceptions to the rules](exceptions.md) |

### Q28. Limits that the implementation of S03 set by itself

| | |
| --- | --- |
| **Found** | 14 November 2026, implementation of Epic 1, sprint S03 |
| **Question** | Three bounds were in the code and in no brief: declarations nested at most 64 deep, a saved form whose body is longer than an array can hold refused as damage, and no bound on a chain of owned types that each hold the next, where the walk over a value and the binding of a class recurse as deep as the chain. Principle 9 of `AGENTS.md` makes a limit an application can observe an option. Are these limits of the model, with these values, and is the depth of owned types bounded too? |
| **Answer** | The owner, 14 November 2026, through the planner's session: a declaration is at most 32 deep, counted through collections and owned types together; a deeper one is `InvalidSchema` with the field named, in a descriptor and in a saved schema. It is a fixed rule of the schema language and not an option: a schema is compiled by a static call outside any instance, and it has to mean the same on every node; principle 9 speaks of the limits of a running instance. 32 and not 64, because a descriptor cannot reach 64 and the bound could then not be tested exactly. The planner, on the second point: a body longer than one array can hold is damage, `InvalidSchema`, with no setting |
| **Where the rule lives** | The brief of S03 in the [document of Epic 1](epic-1-core.md); the number is stated in the documentation of `LatticeValueDeclaration`. Principle 9 of `AGENTS.md` now says which limits it speaks of: the coordinator worded that sentence from the owner's reasons above and reported it to the owner. The coordinator, after review round 2 of S03: a descriptor whose text is nested deeper than 128, far deeper than any descriptor of a schema can be, is refused as a whole, `InvalidSchema`, without a field named, so that a small text never takes long to refuse. This narrows "with the field named, in a descriptor" of the answer above to a declaration the text can hold, at most 124 deep; it is the coordinator's decision, reported to the owner with the close of S03 |

### Q30. What the members of the arena do where the brief of S04 does not say

| | |
| --- | --- |
| **Found** | 15 November 2026, contract of Epic 1, sprint S04 |
| **Question** | The brief of S04 names the types and members of the arena and leaves open what several of them do: when a root handle is current and where generations start; which state a new root entry has; what a new block holds; which exception each refusal is; what the constructor of `Arena` refuses and what the memory limit bounds; what each counter counts; in which order space is taken and when an emptied segment is freed. `RootHandle` had no constructor, so the test of an exhausted generation could not name its slot. Later sprints were touched too: what `DescriptorOffset` counts from, who moves the state of a root entry, what `LastMutationSequence` is |
| **Answer** | The planner, 15 November 2026, from the engine contract, the arena protocol and Q17, reported to the owner in the planner's session. The readings are written out in the brief of S04; `RootHandle` gets the constructor `RootHandle(uint generation, uint slot)`. Three of them are nearest to a choice: the payload of a new block is not zeroed, the memory limit is required, and an emptied segment beyond the reserve is freed inside `Free` until S23 brings the background release. Two things wait for the contracts of their stages and are the planner's to place: in S13 a shared-text block carries the handle of its text entry in `BlockHeader.Root`, told apart by `Kind`; in S20 a member of the arena changes the length of a live block and the counters with it |
| **Where the rule lives** | The briefs of S04, S05 and S06 in the [document of Epic 1](epic-1-core.md) |

### Q34. What the members of the root layout do where the brief of S05 does not say

| | |
| --- | --- |
| **Found** | 15 November 2026, contract of Epic 1, sprint S05 |
| **Question** | The brief of S05 names the types and members of the root layout and of the reference fixture and leaves open what the tests have to state: the formula of the fixed part, what `Compile` does with a field it cannot store yet, where a value of each size goes and what a directory holds, which member serves an Enum with an unsigned underlying type, the errors of a wrong field, of a value out of range and of reading a null, null against missing at `Complete`, the stored encoding of text and the byte order, what the size limit of an entity counts, the life of a writer, what a reader checks, what `LayoutVersion` is, and what of the fixture the engine contract does not give |
| **Answer** | The coordinator, 15 November 2026, from the engine contract and the decisions named in the brief; the planner's session could not be reached, and the readings are reported to the owner with the close of S05. They are written out in the brief of S05. Nearest to a choice: the fixed part is packed without padding; an entity is measured as its fixed part plus the encoded bytes of its String and Binary values, and one of exactly the limit is stored; a field is set once; `Compile` refuses an entity type with a field S05 cannot store with `NotSupportedException`; `LayoutVersion` is 1 |
| **Where the rule lives** | The brief of S05 in the [document of Epic 1](epic-1-core.md). What the readings leave to later sprints is entry Q35 |

### Q32. When does S06 release the entry of a deleted root?

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S04 |
| **Question** | The brief of S06 said, by the answer to Q30, "`Release` follows when the claims of S07 are gone". The brief of S07 does not mention claims; the first that does is S08. So the agents of S06 could not tell what S06 does with the entry of a root whose delete was published: release it at once, or keep it `Deleted` until S08 |
| **Answer** | The planner, 15 November 2026, from the milestone contract, "Reader protection, publication and compaction": the lookup claim of a read by key belongs to S06, because a read of S06 runs while a writer replaces or deletes. The entry of a deleted root is released when no claim is held on it: at once when there is none, otherwise by the reader that drops the last one. S08 adds the claims of an enumeration |
| **Amended** | 16 November 2026, with the owner's decision on deferred reclamation: there are no claims; the entry of a deleted root is retired and released once the reads that could see it have left. After the deletes have returned and a drain with no read in progress, the table holds no entry of a deleted entity |
| **Where the rule lives** | The brief of S06 in the [document of Epic 1](epic-1-core.md), "Types and members" and Acceptance |

### Q36. Exception X3 does not name the tests of the reference fixture

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S05 |
| **Question** | The brief of S05 makes `ReferenceFixture` an internal type of the measurement program and asks for a test of its digest. `Moltaro.Lattice.Engine.Tests` therefore references the program and has friend access to it. Exception X3 spoke of the test projects reaching into the engine and of the measurement program reaching into the engine; it did not say that a test project reaches into the measurement program. Is X3 worded to cover it, or does the fixture get another home? |
| **Answer** | The owner, 15 November 2026, through the planner's session: X3 is extended, and the fixture stays where the brief of S05 put it. The other option, moving the fixture into the shared test project, would have made the measurement program depend on a project meant for tests only |
| **Where the rule lives** | Exception X3 in the [exceptions to the rules](exceptions.md), mirrored in `AGENTS.md`, "Tests And Samples" |

### Q29. Does a property bind when its set accessor is not public?

| | |
| --- | --- |
| **Found** | 14 November 2026, documentation of Epic 1, sprint S03 |
| **Question** | By the answer to Q14 the property of a field is "an instance property with get and set". The binding requires the get accessor to be public and accepts any set accessor: private, protected or `init`. Is a set accessor that is not public accepted, which lets an application keep its entity classes closed for writing, or is a public set required? The review of S03 found two more edges of "the property C# finds by that name": a class that overrides only the getter of a virtual property is `BindingFailed`, although C# sees a get and a set there; and a public field that hides a property of the base class is not seen, so the property of the base class binds |
| **Answer** | The owner, 15 November 2026, through the planner's session: a property binds with a set accessor of any accessibility, and that becomes a promise of the contract. *Not chosen:* a public set, a shorter rule that would stop immutable classes and records with `init` from binding. The planner, as consequences of Q14: a class that overrides only the get accessor of a virtual property binds and is set through the accessor of its base class; a class in which a public field hides a property of its base class is `BindingFailed`. The generator of typed views (S22) reads and never sets, so nothing more follows for its contract |
| **Where the rule lives** | The remarks of `ILatticeDatabase.Set<TEntity>()` and the brief of S06 in the [document of Epic 1](epic-1-core.md), which carries the change under decision O16 |

### Q37. From which source the engine reads the memory available to the process

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S05 |
| **Question** | By decision O17 the default of the instance memory budget is half of the memory available to the process: the limit of its container, or the physical memory of the machine. The brief of S09 named `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` as the source. Measured on .NET 10: on a host without settings it reports the physical memory; in a container limited to 1 GiB it reports 805,306,368, which is 75% of the limit, the default heap limit of the garbage collector; with `DOTNET_GCHeapHardLimit` or `GCHeapHardLimitPercent` it reports what that setting gives. So the default budget in a 1 GiB container would be 384 MiB and not 512 MiB, and a GC heap setting would scale the budget of the native arena, which the decision on write admission says it is not. Does the engine read the limit of the container itself, or does the owner accept what the runtime reports and the three sentences of O17 say so? Two smaller points of the same texts: the sentences "No numeric defaults … are selected" and "No runtime setting or numerical memory default is chosen" of the decision on write admission stand above the owner's addition and read as contradicted; and `Validate` of S09 compares `MaxEntitySize` and `WriteQueueMemoryLimit` with `MemoryLimit` without saying that it is the limit in force when `MemoryLimit` is null |
| **Answer** | The owner, 15 November 2026, through the planner's session: the default of the instance budget is half of what the .NET runtime reports as available, `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`, and the texts say plainly what that figure is — the physical memory on a machine without limits, by default three quarters of the limit in a container, less when a heap hard limit of the collector is configured. *Not chosen:* keeping the decision exact with platform code of the project's own for cgroups and job objects. The default is a convenience; production, the gates and anyone who limits the heap of the collector set the limit explicitly. The two smaller points the planner answered on the same day: the two sentences of the decision on write admission point forward to the owner's addition, and `Validate` compares with the limit in force |
| **Where the rule lives** | Decision O17 and the brief of S09 in the [document of Epic 1](epic-1-core.md), the engine contract, "Initial bounded settings", and the decision on write admission |

### Q38. What the engine database does where the brief of S06 does not say

| | |
| --- | --- |
| **Found** | 15 November 2026, contract of Epic 1, sprint S06 |
| **Question** | The brief of S06 leaves open what the tests have to state: which of the tests of the three classes run on the engine; what `Create` does with options the engine does not serve; the states of an instance; what a position and a revision are; the limits before S09 makes them options; everything the engine refuses until a later sprint and at which call; how an enumeration behaves until S08; sessions and the write batch on a master; what "the returned copy" of a read is. Two statements of the Acceptance could not be read by a test: the count of the table of root entries, and the sequence of a position, which is internal to the core |
| **Answer** | The planner, 15 November 2026, from the engine contract, the retry protocol and the decision on mutation commands: the contract author's readings stand as proposed and are written out in the brief; `EngineCounters` gains `RootEntries` and `PublishedSequence`; what an application sees of a position is stated by a scenario test through the public API, and the numbers by a test of the engine under X3, with no friend access added. One point was not a gap but a disagreement between a test and an accepted decision and went to the owner as Q39 |
| **Where the rule lives** | The brief of S06 in the [document of Epic 1](epic-1-core.md), "Types and members" and Acceptance |

### Q39. Does a delete that deleted nothing move the position?

| | |
| --- | --- |
| **Found** | 15 November 2026, contract of Epic 1, sprint S06 |
| **Question** | `ConsistencyTests.Every_result_carries_a_position`, written for the placeholder before the design was transferred, asserts that a delete of an absent key returns the position the database had (`Assert.Equal(inserted.Position, nothingDeleted.Position)`), and the plan requires that test unchanged in S12. The decision on mutation commands ("assigns successful no-ops a retained result and stream position"), the engine contract ("receive a durable result and stream position") and the retry protocol say a successful no-op advances the position, and S11 has `EffectKindEnum.Unchanged`. It is a disagreement between a test of the contract and an accepted decision, which `AGENTS.md` makes a finding for the owner. The planner recommends the accepted decision: a no-op gets a position of its own and the revision of the entity stays, which changes one assertion of that test, by the test author, with the owner's word |
| **Answer** | The owner, 15 November 2026, through the planner's session: a write that changes nothing has a position of its own, as the accepted decision says. Every accepted operation has its place in the history, a retry returns its own position, and the retry protocol stands as it was checked on PostgreSQL; the cost is an empty entry in the journal and one changed assertion. *Not chosen:* the position stays and the retry protocol is reworked for a receipt without a position. The placeholder is left as it is until S12 removes it |
| **Where the rule lives** | The briefs of S06 and S12 in the [document of Epic 1](epic-1-core.md): the engine publishes such a write at a position of its own from S06 on, a test of it runs on the engine only until S12, and S12 changes the one assertion of `ConsistencyTests.Every_result_carries_a_position` and the documentation of `LatticeWriteResult.Position` |

### Q41. Where is text that is not well-formed refused?

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S06 |
| **Question** | The engine of S06 refused text that is not well-formed in its root writer, after the conditions of a write: with a stored entity and a lone surrogate in a field, an insert of the same key was `DuplicateKey`, a replace of an absent key `NotFound`, a wrong revision `RevisionConflict`, and only a write that passed them was `ValidationFailed`; for an entity type the engine does not store yet the write was `NotSupportedException` before any text was looked at. The placeholder accepted such text, and the public documentation, which lists what is `ValidationFailed` and says that validation comes first, did not mention it. Is it a validation of the contract, made by the core on both implementations, or a refusal of the engine alone? |
| **Answer** | The planner, 15 November 2026, from rules that stand: `AGENTS.md` puts a check that needs no engine into the contract; Q25 fixes the order — arguments, validation, then the conditions and the refusal of what is not implemented; Q04.1 and R10 say malformed Unicode is refused and never replaced. The validation of the core checks it at a write and at adding to a batch, at every depth, on both implementations; the root writer keeps its own refusal as the guard of the stored form; a key given as an argument that is not well-formed is `ArgumentException` |
| **Where the rule lives** | The brief of S06 in the [document of Epic 1](epic-1-core.md), "Types and members" and Acceptance, and the remarks of the public documentation that list `ValidationFailed` |

### Q43. A large value is copied once more than it has to be on its way into the engine

| | |
| --- | --- |
| **Found** | 15 November 2026, implementation of Epic 1, sprint S06 |
| **Question** | The engine copies a value once, into native memory. Before that the access layer of the core copies a Binary value again: by class the capture of an object clones the array, and by name the record copied it when it was set and the submission clones it a second time; a write of an entity with a 32 MiB Binary value allocates 32.5 MiB of managed memory that nothing keeps. Does the engine get a capture of its own without the copy, and what does the contract say of an input that is changed while its write runs? Two smaller observations of the review: an insert takes the catalog gate exclusively up to three times, and the encoding of the values runs under the lock of the writes |
| **Answer** | The planner, 15 November 2026, from the accepted decision "Stable write inputs until completion": an application keeps the input of a write unchanged until the write completes, so the engine takes the values as the caller gave them and copies each once, into the arena, with no managed copy of a Binary value on the way; an input changed while its write runs gives a stored value that mixes the two states, never a damaged database. The write batch keeps its copy until S17. The two observations become rules of S09, checked by its reviews: the values are encoded outside the lock that orders writes, and an insert takes the catalog gate exclusively once, at publication |
| **Where the rule lives** | The brief of S09 in the [document of Epic 1](epic-1-core.md), and from S09 the remarks of the write members |

### Q44. What does a running enumeration do after the database was closed?

| | |
| --- | --- |
| **Found** | 15 November 2026, tests of Epic 1, sprint S06 |
| **Question** | The contract says that an operation after a close is `ObjectDisposedException`, and says nothing of an enumeration that was started before the close and is continued after it. On the engine the next step is `ObjectDisposedException`; on the placeholder the enumeration goes on from what it took when it started. Which is the contract? |
| **Answer** | The planner, 15 November 2026: the next step fails with `ObjectDisposedException`, as every operation after a close does; an enumeration never goes on over a closed database. The placeholder is not changed, so the test runs on the engine only until S12 |
| **Where the rule lives** | The brief of S08 in the [document of Epic 1](epic-1-core.md) |

### Q42. Reads by key do not scale with threads

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S06 |
| **Question** | With 100,000 entities and no writer the engine of S06 reads about 2.3 million entities by key per second on one thread and 1.5 to 2.0 million in all on 8 to 16 threads. What bounds it is the shared acquisitions of the reader/writer gates: the counter of the close barrier, the catalog gate taken twice for a read, and the stripe of the root. It is the synchronization the engine contract prescribes as the initial one, and gate G2 asks for 10,000 reads per second, so no rule was broken. Does the scaling matter, and when is it dealt with? |
| **Answer** | The owner, 15 November 2026, through the planner's session: reads by key have to scale with threads, and the work is done in S08, where the gate of the index changes anyway, not judged at S25. That more threads read less in total is a very bad signal. The implementer of S08 studies in their source how open-source databases that read fast on many threads protect a read, and repeats a solution that is proven there; how many protections there are and how fine they are is decided by those systems and by measurement, not by a stated preference. The guarantees do not move |
| **Amended** | 16 November 2026, the owner: deferred reclamation by epochs, for reads and for the index, after a first implementation whose writer waited for the reads was stopped. The rule is in the arena protocol, "Deferred reclamation, 16 November 2026" |
| **Where the rule lives** | The brief of S08 in the [document of Epic 1](epic-1-core.md); S25 checks the read mix against what S08 reached |

### Q45. A write batch tells two spellings of one key apart

| | |
| --- | --- |
| **Found** | 15 November 2026, tests of Epic 1, sprint S07 |
| **Question** | A batch takes one operation per entity and refuses a second one for the same key when it is added. It told entities apart by the key as given, so for a String key compared without case "Main" and "MAIN" were accepted as two operations on one entity. Is the second operation refused at adding, under the comparison of the key? |
| **Answer** | The planner, 15 November 2026, from standing rules: yes. The schema is the authority and declares the comparison of the key; the contract says one operation per entity, not per spelling; and a check that needs no engine belongs to the contract. Two operations address one entity when their keys are equal as the schema compares them, an integer key by its value whatever its width |
| **Where the rule lives** | The remarks of `ILatticeWriteBatch`, and the brief of S07 in the [document of Epic 1](epic-1-core.md), which carries the change |

### Q46. Which keys are equal without case depends on the globalization mode of the process

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S07 |
| **Question** | A String key compared without case followed `StringComparison.OrdinalIgnoreCase` of .NET, and what .NET holds equal under it differs between ICU, NLS and invariant globalization and with the version of the Unicode data. Nothing recorded the mode, so two nodes, or a saved database and the process that opens it, could hold different entities equal. Is the mode fixed and checked, or does the engine bring a case mapping of its own? |
| **Answer** | The owner, 15 November 2026, through the planner's session: a case mapping of Lattice's own, a fixed versioned table in the core. *Not chosen:* recording the globalization mode and refusing another; leaving it until S10 |
| **Where the rule lives** | The decision on strings, Q04, the addition of 15 November 2026; the brief of S07 in the [document of Epic 1](epic-1-core.md) carries the work |

### Q47. Does the notice of the Unicode data travel inside the package?

| | |
| --- | --- |
| **Found** | 15 November 2026, review of Epic 1, sprint S07, round 2 |
| **Question** | The core library carries the case table, data derived from `UnicodeData.txt` of Unicode 16.0.0, and the Unicode licence asks that its notice appear with copies of the data. A package was built with `README.md` only. Is the notices file packed with the core library? |
| **Answer** | The owner, 16 November 2026, through the planner's session: yes. `THIRD-PARTY-NOTICES.md` is packed into the package of the core library beside `README.md`; the repository file stays |
| **Where the rule lives** | The build settings in `src`; carried by S08, part 1 |

### Q48. Can the selected map of the primary index be read while the writer changes it?

| | |
| --- | --- |
| **Found** | 16 November 2026, the planner, with the owner's decision on deferred reclamation; for Epic 1, sprint S08, part 1 |
| **Question** | The decision on index structures selected `UnorderedMapSlim` of `Arc.Collections` and foresaw readers on the old map only during growth. The owner's decision of 16 November 2026 asks for lookups during every change. Can that map be read safely while the one writer changes it? |
| **Answer** | No: its own remarks require external synchronization whenever a writer is present, and the planner read the code at the pinned commit to the same end. The owner, 16 November 2026, through the planner's session: the primary index becomes a hash table of the engine's own, modelled on the hash index of Garnet/Tsavorite; and where the engine can replace a package by its own structure written after an established open-source one, it does. Whether `Arc.Collections` leaves the engine altogether is settled with the ordered indexes of S14; until then the reference, the pin test of Q3 and the sentence of `AGENTS.md` stay |
| **Where the rule lives** | The decision on index structures, the addition of 16 November 2026; the brief of S08 in the [document of Epic 1](epic-1-core.md) |

### Q49. Does a partial update change a published root in place?

| | |
| --- | --- |
| **Found** | 16 November 2026, with the design of Epic 1, sprint S08, part 1 |
| **Question** | "Fixed-width fields may change in place under exclusive access" was an accepted decision. Reads now take no exclusion, so a reader would see a root half changed. Does a partial update copy the root block and publish it like a replace, or does a root carry a sequence that every read checks? |
| **Answer** | The owner, 16 November 2026, through the planner's session: a published root is never changed. A partial update builds a new root block with the changed fields, shares the value blocks it does not change, publishes it as a replace does and retires the old root block; a read checks nothing more. The owner's rule with it: when a design choice trades the cost of a read against the cost of a write, the read wins, as long as writes stay above their targets |
| **Where the rule lives** | The decision on memory and access, the addition of 16 November 2026; the arena protocol, "Deferred reclamation". The contract of S20 says who owns a shared value block and reports the cost |

### Q50. Keys chosen to share one hash make reads of them slow

| | |
| --- | --- |
| **Found** | 16 November 2026, review of Epic 1, sprint S08, part 1, round 2 |
| **Question** | The primary index hashed Int64 and Guid keys through a mixing function without a seed that can be inverted, so whoever chooses the keys could put many on one 32-bit hash: memory stayed bounded and every key was found, but a read of one of them walked the chain, 287 µs against 0.2 µs. Does the index get a seed of its own per instance? |
| **Answer** | The planner, 16 November 2026, from standing rules: yes. The cost is one word of state and one operation per hash; nothing a node shares depends on the hash; and "nobody outside chooses the keys" is something the database cannot know of an application. Each instance draws a seed when it opens; every hash of the primary index uses it, for every key type and both comparisons; the seed is never stored, sent or shown; the order of an enumeration is unspecified |
| **Where the rule lives** | The brief of S08, part 2 in the [document of Epic 1](epic-1-core.md), which carries the work |

### Q51. A read by name boxes a numeric key

| | |
| --- | --- |
| **Found** | 17 November 2026, an outside review of `main` after Epic 1, sprint S09 |
| **Question** | The members of `ILatticeSet` take a primary key as `object`, so a read by name of an integer or a Guid key allocates a box on every call: 2,400,000 bytes for 100,000 reads of absent Int64 keys, against 0 through a class. The brief of S06 had left the box out of the measured read as the caller's allocation. Is that the contract? |
| **Answer** | The planner, 17 November 2026, on the rules of `AGENTS.md`, reported to the owner: no. A signature that forces boxing per call on a read path is a defect of the contract, and access by name is not a second-class path. Every member by name that takes a primary key gets a form that takes it without a box; the shape — a generic key parameter, or overloads for the key families — is chosen by a figure in the contract step |
| **Where the rule lives** | The brief of S09, part 2 in the [document of Epic 1](epic-1-core.md), which carries the change; the sentence of the brief of S06 is corrected |
