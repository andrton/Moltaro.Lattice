# Mutation commands

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md)
now specifies the mechanisms and starting values described as open, partially accepted or
illustrative below, and the [decision register](../design/questions.md) owns the current
status. The earlier notes are kept as the decision's history; they do not reopen what was
specified. "v1" below is wider than the first production milestone, whose scope is in
the [planning handoff](../design/planning-handoff.md).

Accepted design contracts, rationale and alternatives. No production mutation API
or general document replay engine is implemented by this record.

## Q13: How can a document be updated?

**Partially accepted on 13 November 2026:** option B for v1. Support
whole-document replacement and explicit assignment of one or several scalar fields
within one owned document. A command publishes all its changes atomically with the
affected indexes; unmentioned fields retain their values at execution time.

The subsequent decision selects collection replacement/element commands and the
ordinary unchanged-result versus invalid-position rules below. Partial updates now
also require existing targets and explicit initialization. Complete-document writes
now use explicit Insert, Replace and Upsert modes. Delete returns deleted or already
absent without an absence exception. Optional revision checks for Replace, partial
updates and Delete are also accepted below. Primary keys are immutable for each
root's lifetime, with no rename operation in v1. V1 assigns ready numeric values;
dedicated arithmetic commands are deferred. Save submits a complete final document;
partial updates use a separate explicit API, without automatic change tracking in v1.
Mutable write inputs remain unchanged until asynchronous completion, including
failure/cancellation completion. Operation identity, retained results and safe
same-token resubmission within a bounded window are accepted below. Repeated scalar
targets in one explicit partial mutation are rejected before commit, as specified
below. Remaining failure rules stay open. A field update does not make a public
borrowed view writable.

Q01 now accepts a bounded [atomic WriteBatch](document-model.md#atomic-writebatch-within-one-database)
that groups supported mutations in one database. The single-command contracts below
remain the baseline; independent calls do not acquire batch atomicity automatically.
[Complete-object batch saving uses Upsert](#atomic-batch-save-with-upsert-semantics):
create absent objects and fully replace existing ones across entity types, with one
transaction boundary for the whole submitted batch.
[Repeated commands for the same root inside a batch](#repeated-roots-inside-an-explicit-writebatch)
are rejected before commit: an explicit WriteBatch admits at most one operation
per entity type/primary-key identity. This rule is accepted below.

[Question register](../design/questions.md) · [Document atomicity](document-model.md#q01-what-must-change-together) · [Durable completion](write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed)

## Application editing and submission boundary

Ordinary ID reads return caller-owned values. Changing such a copy, including its
owned nested collections, does not write through to the arena. A complete value
submitted later contains its final state; repeated local assignments are not a
sequence of database mutations. Public borrowed views remain read-only.

The accepted complete and partial mutation capabilities describe work submitted to
the writer, not per-setter calls from application code. The later
[Save decision](#full-document-save-and-explicit-partial-updates-accepted-for-v1)
selects complete final values and a separate explicit partial-update API, without
automatic change tracking in v1. Engine-controlled atomic publication is separate
from the caller's editing lifetime.
[Input remains unchanged until asynchronous completion](#stable-write-inputs-until-completion-accepted-for-v1),
including failure/cancellation completion; source buffers cannot be returned early.

## Concrete example

A schema-bound document contains a name, settings and a numeric threshold. Only
Threshold changes. With replacement, the caller supplies the complete new document.
With an explicit field update, the caller supplies the document key, the schema
field to change and its new value. The engine owns validation, ordering, journal
encoding and coherent publication. A public borrowed view remains read-only.

## Option A: whole-document replacement only — not selected

The mutation surface accepts complete document values. It has fewer command shapes
and a simpler initial validation/replay path. Small documents or applications that
already produce complete new values may find this sufficient.

For a small change to a large document, the caller must still supply the rest of it.
That can require reading/materializing unchanged data and larger queued/encoded
payloads. Internal comparison could reduce persisted output, but would introduce
its own work and complexity; replacement alone does not provide that optimization.
Repeated read-modify-replace can also overwrite intervening changes from another
submitted mutation. Master coordination does not make an earlier caller-side
read current.

Ordinary reads keep their existing path. Replacement may create more write-side
allocation, copying and validation, and therefore more contention for reads. It is
not a measured claim that every replacement is slower than a field update.

## Option B: replacement plus explicit field assignments — accepted

Keep complete replacement and also accept a command assigning one or several named
scalar fields within one owned document. Fields are identified through the schema,
not by exposing raw writable addresses or running arbitrary application callbacks
inside the writer. Both generated and schema-driven access must be able to describe
the same command; SDK signatures and the serialized format are not selected here.

One command preserves the accepted root-level atomicity: readers see all of its
field changes together. Unmentioned fields keep their values at execution time.
An unconditional assignment can still overwrite an earlier assignment. The later
[revision-check decision](#optional-revision-checks-accepted-for-v1) adds an optional
root-wide precondition without changing the unconditional form.

For sparse updates, the caller need not copy an entire document. Smaller commands
can reduce queue memory, encoding and journal traffic. Updating indexed fields still
requires coherent index maintenance; variable-sized strings can still allocate and
adjust dictionary references. Neither storage durability nor native lifetime rules
are bypassed. PostgreSQL saved-unit updates retain their 256-byte baseline: a tiny
field assignment does not imply an equally tiny physical SQL/WAL write.

Costs: an additional validated command form, field resolution, preparation and replay
rules, and recovery/atomicity checks for multiple assignments. Full replacement and
field assignment must share the same writer/publication pipeline. This is more work
than A, but it directly serves the stated frequent-small-update workload.

## Costs and limits of the accepted choice

| Aspect | A: replacement only | B: replacement and explicit assignments |
| --- | --- | --- |
| Sparse writes | Supplies all document values; may require a preceding copy/read | Supplies the key, target fields and new values; less unchanged input |
| Ordinary reads | Same local read contract; heavier writes may contend | Same contract; smaller commands may reduce contention |
| Temporary RAM | Potentially retains full replacement payloads | Sparse commands can use less queue/staging memory; not allocation-free |
| Journal and saved state | Full input unless an additional diff mechanism is built | Can encode explicit changes; affected saved units and metadata still need updates |
| Dense replacement | Natural and simple | Still available; a long field list need not be used |
| Complexity | Fewer command forms | Adds schema-bound assignments and their validation/replay tests |

These are expected workload-dependent effects, not new benchmark results. Existing
fixed-size field-assignment prototypes do not implement a general document mutation
API or measure it against complete document replacement.

The decision selects **B** for v1 to support sparse updates without making complete
replacement inconvenient. Commands still use schema validation, the single ordered
writer, coherent root/index publication and the selected completion guarantee.
Durable writes wait for journal commit and publication; MemoryOnly promises atomic
local publication without durable recovery. No root gate spans storage I/O.

<a id="collection-mutations-accepted-for-v1"></a>

## Collection mutations — accepted for v1

**Accepted on 13 November 2026:** option B. Support replacement of an
entire collection field and explicit List, Map and Set element commands. A bounded
mutation may combine them with scalar assignments inside one owned document, execute
in submitted order and publish atomically. Public views remain read-only.

[Q03.3](schema-and-types.md#q03.3-should-the-schema-support-list-map-and-set) continues
to own collection kinds, types and order. The subsequent
[operation-outcome decision](#collection-operation-outcomes-accepted-for-v1) settles
duplicate Set additions, absent Map/Set removals and invalid List positions.
The later [target requirement](#partial-update-target-requirements-accepted-for-v1)
requires existing documents/parents and non-null collections for element commands.

### Collection example

A document contains a List of measurements. One new measurement arrives. Replacing
that collection requires supplying the complete resulting sequence; an append command
supplies the owning document key, collection field and new element. A Map entry or
Set membership can similarly change without supplying all other entries.

Neither alternative changes the accepted full-document replacement capability.
Both add the ability to replace a whole collection field while leaving unrelated
fields alone. Collection-field replacement is accepted by this decision in addition
to the earlier scalar-assignment contract.

### Collection option A: replace entire collection fields only — not selected

Allow the caller to supply a complete new List, Map or Set value for a field. To add
or remove one item, it first constructs the complete resulting collection, then
submits the replacement through the master writer.

Advantages: fewer command forms and simpler initial validation and replay. This is
useful for small collections, rare changes, imports or algorithms that already
produce complete new collections. The engine preserves List input order and validates
Map/Set uniqueness under the eventual input rules.

Costs: a small edit to a large collection can require a large caller-owned copy,
validation pass and pending payload. Internal diffing could reduce persistent output,
but adds its own cost and is not supplied by this option. A read-modify-replace based
on an old copy can overwrite intervening collection changes. Ordinary read semantics
stay the same; copying and publication work can compete with readers.

### Collection option B: replacement plus explicit element commands — accepted

Keep whole-collection replacement, and also offer the following bounded command set.
Names describe capabilities, not finalized SDK method signatures.

| Collection | Accepted capabilities |
| --- | --- |
| List | Append an element, insert at a position, replace at a position, remove at a position |
| Map | Set a value by key (add or replace), remove by key |
| Set | Add a member, remove a member |

Commands go through the master writer. Public arena views remain read-only. A
bounded mutation may combine these operations and scalar assignments within one
owned document. Operations execute in their submitted order against that document;
publication and related indexes remain atomic for the complete mutation. A failed
mutation must not expose a partially edited collection or part of its scalar changes.
This does not create a transaction across multiple documents.

List positions refer to the state when the master executes the command;
within a command, later positions see the effects of earlier edits. Positions are
not stable element identities from an earlier caller snapshot. For example, removing
position 2 after another command inserted at position 0 may target a different item
than the caller originally read. The later revision-check decision lets the caller
reject an intervening root change explicitly; commands without that condition still
use execution-time positions. Preserve the order of remaining List elements;
never introduce automatic sorting. Map/Set still have no enumeration-order guarantee.

Advantages: sparse edits can send and retain much smaller payloads, avoid building
a complete caller-side collection, and encode less unchanged input into the journal.
This suits long lists, frequently changing maps and membership sets. Full replacement
remains useful when most of a collection changes.

Costs: more validation, ordered replay, allocation/reclamation and failure cases.
A small command is not a promise of constant-time work or allocation-free execution:
List insertion/removal may move many elements, growth may reallocate, and Map/Set
storage may resize or rebuild. The physical layout remains Q16/Q17 work. Saved-unit
updates retain their 256-byte baseline plus metadata; no byte-for-byte physical SQL
write-size promise follows from a small command. Coalescing must preserve operation
order and result, rather than blindly keeping only the last collection operation.

### Collection read, write and memory tradeoffs

| Aspect | A: whole collection field replacement | B: replacement plus element commands |
| --- | --- | --- |
| Sparse update input | Supplies every resulting element | Supplies the changed element/key/position |
| Write work | Whole input validation/copying unless optimized internally | Usually less unchanged input; shifts, resizing and validation can still dominate |
| Temporary RAM | May retain a full new collection | Smaller command payload; temporary native growth and publication state still matter |
| Ordinary reads | Same local access and root atomicity | Same contract; lower input work may reduce contention, costly edits can still block briefly |
| Dense changes | Simple complete replacement | Complete replacement remains available |
| Complexity | Fewer command forms and failure cases | More command and replay rules, including positional changes |

These are expected effects, not new benchmark results. No collection-edit prototype
has established throughput, memory or pause guarantees for either option.

### Collection scope and remaining semantics

The decision selects **B** for v1. This accepts the capability set and per-document
ordered atomicity, not full compatibility with mutable .NET collection classes.
Supported key/element/value types and equality stay with the schema contract.
The operation-outcome decision below now settles absent Map/Set removals, duplicate
Set additions and invalid List positions. Partial updates require existing parents
and non-null target collections as recorded below. The accepted
[collection input contract](schema-and-types.md#collection-input-contract) preserves
existing Map-key spelling on set-by-key. The Set no-op preserves its existing member.

This decision does not add sorting commands, arbitrary predicates/callbacks, structural
search-and-edit, key renaming, nested element field paths or cross-document commands.
A List item or Map value can be replaced as a value; finer edits need a separate
contract. Unknown commit outcomes do not authorize blindly repeating collection
commands: an append or positional removal can have a different result on repetition.

<a id="collection-operation-outcomes-accepted-for-v1"></a>

## Collection operation outcomes — accepted for v1

**Accepted on 13 November 2026:** option A. Adding an already-present
Set member and removing an absent Map key or Set member return normal unchanged
results. Invalid List positions fail the complete mutation. These outcomes apply
to a valid operation on an existing, non-null target collection.

The collection command set and ordered root-level atomicity remain unchanged.
This is a design contract, not an implemented or benchmarked mutation API.

### Scope of the accepted outcomes

The owning document and target collection already exist, the collection is non-null,
and supplied values satisfy the schema. Missing documents/parents, null collections,
and malformed values must not be silently treated as empty collections or successful
no-ops. Bulk replacement follows the accepted [collection input contract](schema-and-types.md#collection-input-contract):
reject duplicate Map keys atomically; coalesce duplicate Set members.

Map set-by-key already means add or replace under the accepted capability contract.
List permits duplicate values. Neither option changes those rules. Equality follows
the declared collection contract, including field-specific string comparison; no new
caller-supplied comparer or full-document equality rule is introduced here.

### Outcome option A: ordinary unchanged results; invalid positions fail — accepted

| Situation | Accepted result |
| --- | --- |
| Add an already-present Set member | Leave the existing member unchanged; report not added |
| Remove an absent Set member | Leave the collection unchanged; report not removed |
| Remove an absent Map key | Leave the collection unchanged; report not removed |
| Insert into a List | Accept positions from 0 through Count, including the end |
| Replace or remove a List item | Accept positions from 0 through Count minus 1 |
| Use a List position outside the applicable range | Fail the entire mutation explicitly; do not clamp or ignore the index |

For the Set additions and Map/Set removals above, expose the membership-change
outcome, such as added/removed versus unchanged, without using exceptions for
ordinary absence.
Exact result types and whether a multi-operation result uses a compact status list
remain SDK engineering work. Do not imply that Map set compares complete old/new
values or reports a value-level Changed flag; that equality/optimization is separate.

A no-op preserves the existing element, including its original string representation.
For example, Set add under ignore-case equality does not replace an equal member's
spelling merely because the supplied text differs. Q03.4.2 now also preserves the
existing Map key's spelling during set-by-key. Full replacement uses its validated
input, without merging the previous collection's key text.

All checks run against the ordered execution state. Later operations in one command
see earlier edits, so List bounds use that current length. Results are not committed
until the complete mutation succeeds. If one operation has an invalid position or
another actual validation failure, none of the command's edits become visible or
recover as a successful partial mutation. Earlier provisional add/remove outcomes
must not be reported as independently committed work.

Advantages: callers can request membership changes without a separate existence
read that could become stale. Repeated ordinary absence returns a normal result,
with less exception construction/unwinding when it is common. Invalid positional
access still exposes an error rather than silently losing the caller's intent.

Costs: applications that require an element to have existed must inspect the result.
A normal unchanged result can hide an incorrect assumption if the caller ignores it.
The engine must report compact operation outcomes and preserve command atomicity.
This is a predictable outcome policy, not a promise of higher storage throughput.

### Outcome option B: strict duplicate and missing-target failures — not selected

Treat adding an existing Set member, removing an absent Set member and removing an
absent Map key as explicit errors that fail the whole mutation. Invalid List bounds
also fail; the valid ranges are the same as in A. Map set-by-key still adds or
replaces, and List duplicates remain valid as already accepted.

Advantages: an incorrect existence assumption is immediately visible without the
caller checking an unchanged result. There are fewer successful outcome variants.
This can suit application commands whose meaning requires a transition to occur.

Costs: harmless repeated removal/addition attempts also fail. A caller may need to
handle expected exceptions; checking beforehand adds a read and does not guarantee
the state will remain unchanged before execution. Frequent exceptions add transient
allocation and CPU work, and one missing member rejects other edits in the command.
Both variants still require existence/bounds checks and atomic preparation/replay.

### Outcome performance and implementation comparison

| Aspect | A: unchanged is a normal result | B: strict failure |
| --- | --- | --- |
| Actual element changes | Same logical lookup, validation and mutation work | Same work |
| Repeated absence/membership | Normal compact result; no exception for that condition | Exception construction and failure handling when the condition occurs |
| Ordinary reads | Unchanged | Unchanged |
| Stored RAM | No extra per-element history or state required | No intrinsic stored-memory saving |
| Temporary RAM | Small outcome state within the bounded command budget | Potential exception objects on frequent expected failures |
| Complexity | Distinguish unchanged from errors; callers inspect outcomes | Simpler success shape, but callers handle more exceptions |

No new measurements establish a speed or memory ratio. The choice is primarily
about application behavior; normal successful writes do not become intrinsically
faster merely by choosing a different absence policy.

### Outcome boundaries and remaining work

The decision selects **A** for v1. The rule distinguishes a valid operation whose
membership is already as requested from an invalid List address. It does not add a
second strictness mode, automatic retries or a broad ignore-errors flag.

The [milestone contract](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics)
now assigns successful no-ops a retained result and stream position while preserving
unchanged root revisions; this choice does not permit early success for a
command that changes data. Existing durable-completion and uncertain-commit rules
remain in force. Normal no-op outcomes do not make an entire command safe to repeat:
appends, positional edits or intervening changes can alter the effect of replay.
The target-requirement decision below rejects missing roots/parents for partial
updates and null collections for element commands. The complete-document modes are
accepted below, as are ordinary deleted/already-absent outcomes and optional revision
conditions. Q03.4.2 settles bulk duplicates and retained Map-key spelling; other
condition forms remain separate.

<a id="partial-update-target-requirements-accepted-for-v1"></a>

## Partial-update target requirements — accepted for v1

**Accepted on 13 November 2026:** option A. A partial update requires
an existing root document and all intermediate owned parents. Collection element
commands also require a non-null target collection. Missing targets fail the complete
mutation; initialization is explicit. No missing document or owned path is created
implicitly by a field assignment or element command.

The earlier unchanged collection outcomes apply only within an existing collection.
Explicit scalar assignment or whole-collection replacement can initialize a nullable
field when its owner exists. Reading through an allowed null parent still returns
null under Q07.3.6.

### Missing-target examples

- A scalar assignment targets a document key that is not present.
- An assignment targets Options.Threshold while the nullable owned Options is null.
- A List append or Map/Set element command targets a nullable collection that is null.

These are distinct from assigning a nullable scalar that currently contains null,
or explicitly replacing a collection field whose owning parent exists. A field can
be assigned a valid value without previously holding a non-null value itself.
An invalid schema field or incompatible supplied value remains an error in both
options. Neither option follows a non-owning reference to create another document.

### Missing-target option A: require existing targets; initialize explicitly — accepted

A partial update requires its root document and all intermediate owned parents to
exist. Collection element commands additionally require a non-null collection.
If a required target is absent when the master executes the operation, fail
the whole mutation explicitly. Do not fabricate a root, invent required field
values, convert null to empty, or silently ignore the update.

This applies to element removal as well as addition. Removing an absent key from an
existing Map remains the accepted normal not-removed result. Removing through a
null Map or an absent document is a different failure. An empty but non-null
collection is a valid target and follows the already accepted bounds/membership rules.

Creation and initialization use explicit complete, schema-valid values. A caller
can provide a new document intentionally through the subsequently accepted
[Insert or Upsert modes](#complete-document-write-modes-accepted-for-v1). Replace
requires an existing document. These full-document modes do not make a partial
assignment an upsert or allow it to construct a missing owned path.

For an existing document, explicit whole-collection replacement can initialize a
nullable collection field when its parent exists. An earlier collection replacement
in the same accepted ordered mutation can establish the target for a later element
operation; validation must respect that order. This need not require two database
calls. An absent owned parent can be supplied as part of a complete valid document
replacement; no new nested-object construction command is implied here.

Advantages: predictable update scope, clear diagnosis of stale/mistyped identities,
and no unexpected recreation of a deleted document. The writer needs existence and
schema checks without a second implicit-construction policy. Required fields are
supplied intentionally, and normal updates allocate no extra parent just to navigate.

Costs: callers must initialize the intended structure explicitly and distinguish
not-found/path failures from unchanged membership results. Supplying a complete
replacement to initialize a complex parent can require more caller-side copying.
This is a usability tradeoff, not a claim that every explicit creation is faster.

### Missing-target option B: initialize during partial updates — not selected

Allow value-producing commands to construct missing roots/owned parents and treat
a null collection as initially empty, when enough initialization data is available
to produce a valid document. All non-nullable required values still need legitimate
inputs or separately approved schema defaults; arbitrary zero/empty values are not
automatically valid. With insufficient data, fail the complete mutation.

Under this alternative, removal through a missing root/path or null collection
would return unchanged without creating a container. That broader no-op rule belongs
only to this unselected alternative, not the accepted existing-collection rule.
List bounds still apply after any valid initialization: an empty List permits
insertion at position 0, but replacement/removal cannot synthesize missing elements.

Advantages: convenient first-write creation for simple configuration documents;
callers may avoid building and submitting an entire structure themselves. Legitimate
initialization can happen in the same atomic mutation as the requested field change.

Costs: extra initialization rules, payloads/default requirements, allocation,
validation, replay and error cases. A delayed partial update can recreate a previously
deleted target. The schema currently has no accepted general default/factory policy;
this option cannot silently introduce one. Complete required inputs are still needed,
so the convenience is limited for complex documents. Initialization can write more
than the field mentioned by the original update.

### Missing-target read, write and memory tradeoffs

| Aspect | A: explicit initialization | B: implicit initialization when valid |
| --- | --- | --- |
| Ordinary reads | Unchanged | Unchanged |
| Updates to existing complete paths | Existence/schema checks and normal mutation work | Similar checks and normal mutation work; no intrinsic speed advantage |
| Missing target | Explicit failure; caller chooses initialization | Potential allocation, initialization and validation; fails if required values cannot be supplied |
| Stored RAM | Only explicitly supplied data is created | Newly constructed data consumes memory; ordinary existing records need not be larger |
| Temporary RAM | Initialization may need a complete caller-owned replacement | Engine constructs missing data; required initialization inputs still consume memory |
| Complexity | Fewer implicit behaviors and simpler failure explanation | Additional creation/no-op branches, required-value rules and recovery cases |

No benchmark establishes a speed or memory ratio for these alternatives. The decision selects **A** for v1: update and creation intent remain explicit and schema
requirements stay predictable. B is not selected or promised as a v2 feature.

### Missing-target boundaries and remaining work

Checks use the state at ordered execution, including earlier operations in the same
command. Any actual validation failure preserves root-level atomicity; no partial
changes become visible. Target validation alone does not protect a previous
caller-side read; the later revision-check decision supplies that optional condition.

Reading through an allowed null parent still returns null under Q07.3.6. That read
rule does not determine write-side construction. Null and empty remain distinct;
collection equality and unchanged outcomes keep their accepted contracts.

This subsection settles missing-target behavior for partial updates. The following
complete-document and deletion decisions below supply Insert/Replace/Upsert and
ordinary deleted/already-absent semantics. Primary keys are immutable under the
later decision. Q03.4.2 now settles Map-key spelling and bulk-input duplicates.
Automatic retry behavior and exact API/encoding details remain separate.

<a id="complete-document-write-modes-accepted-for-v1"></a>

## Complete-document write modes — accepted for v1

**Accepted on 13 November 2026:** option A. Support Insert, Replace and
Upsert through a shared write pipeline. Insert requires an absent key, Replace
requires an existing key, and Upsert explicitly permits either creation or complete
replacement. Resolve the condition at execution time in the master writer.

All three modes take complete schema-valid document values. This does not change
[partial-update target requirements](#partial-update-target-requirements-accepted-for-v1)
or introduce implicit field merging or three independent engines. The later revision
check adds an optional condition to Replace. This is an accepted design; exact SDK
signatures and implementation remain open.

### Atomic batch save with Upsert semantics

**Accepted clarification for v1 on 13 November 2026:** ordinary complete-object
batch saving is an atomic batch Upsert. A batch can contain different entity types
in the same database, such as users, groups, roles and settings.

For each supplied object, resolve identity by entity type and primary key on the
master at execution time. If absent, create it; if present, replace its complete
owned value with the supplied final state. The caller need not separate new and
existing objects or issue preliminary existence reads. Full Save keeps its accepted
complete-value semantics without automatic diff or implicit field merging.

Adding an object to the batch does not commit it independently. Validate and
prepare the whole bounded batch, then commit and publish it through the accepted
WriteBatch transaction boundary. A definitive validation/admission/rollback failure
commits none of its data changes. Existing unknown-outcome and post-commit recovery
rules still apply. This reuses the accepted journal/publication pipeline, rather
than selecting a different PostgreSQL storage strategy.

Explicit Insert, Replace, Delete and partial-update operations retain their accepted
contracts when requested; the ordinary batch-save path uses Upsert. This clarification
does not select public SDK names or a global default for every standalone write API.
The later [duplicate-identity decision](#repeated-roots-inside-an-explicit-writebatch)
rejects repeated submissions of the same type/key inside one explicit WriteBatch
before commit. Multi-type support, atomicity and Upsert semantics are unchanged.

### Write-mode option A: explicit Insert, Replace and Upsert — accepted

Expose three intentions through one ordered write pipeline. These names are
conceptual operations, not finalized SDK signatures or a requirement for three
independent implementations.

| Mode | Key absent at execution | Key present at execution |
| --- | --- | --- |
| Insert | Create the document from the supplied value | Fail with an explicit already-exists outcome |
| Replace | Fail with an explicit not-found outcome | Replace the complete document value |
| Upsert | Create the document from the supplied value | Replace the complete document value |

All modes require a complete schema-valid document value. Replacement means the
whole owned document, including its nested data and collections; it is not a merge
that preserves fields absent from the supplied value. Field omission/default decoding
rules remain schema/API work. References retain their key semantics and do not cause
creation or replacement of target documents. Primary-key renaming is excluded in v1.

Resolve existence and apply the requested condition inside the single master writer
at execution time. The caller need not first read to determine which path is valid.
Concurrent callers can submit requests, but while an earlier inserted document
remains present, another Insert for the same key must fail. A failed
existence condition changes no document or indexes and produces no successful
partial mutation. Exact exception/result names remain API work.

Upsert explicitly authorizes creation and full replacement; it does not weaken the
accepted partial-update policy. A stale complete value can overwrite newer contents,
and a delayed Upsert can recreate a deleted key. Unconditional Replace checks
existence only; it does not detect changes since an earlier read or distinguish
deletion/recreation. The later revision-check decision adds an optional condition to
Replace. Upsert remains unconditional, and no mode introduces automatic retry.

Advantages: callers can state whether they intend creation, replacement or either.
Initialization/import jobs can use Upsert, while workflows that must detect an
unexpected existing/missing key use Insert or Replace. Conditions run at the write
boundary, avoiding an extra application existence read and its race with changes.

Costs: three documented modes, explicit existence failures and focused tests of
each transition. This adds a condition to a shared pipeline rather than three arenas,
indexes, journals or copies of each document. Correct insertion/replacement,
allocation/reclamation and publication are required in either alternative.

### Write-mode option B: a single Put/Upsert mode — not selected

Provide only the add-or-replace behavior for complete document values. An absent key
is created; an existing key is fully replaced. Keep the same schema validation,
root atomicity, durability and partial-update contracts.

Advantages: a smaller initial public surface and one normal successful mode. It is
convenient when data import or configuration refresh always means make this key hold
the supplied complete value, regardless of its previous presence.

Costs: callers cannot atomically require creation-only or replacement-only through
this mode. A preliminary read can become stale before the writer executes. Adding
that requirement later needs an explicit mode or conditional-write contract. A bad
key or stale complete value can silently create an unintended document or replace
an existing one. This is the same intentional risk of A's Upsert, but B provides no
strict Insert/Replace choice for those workflows.

### Write-mode speed, memory and complexity

| Aspect | A: three explicit modes | B: only Put/Upsert |
| --- | --- | --- |
| Ordinary reads | Same arena/index path | Same path |
| Complete write work | Primary lookup plus the selected existence condition, validation and publication | Primary lookup to distinguish create/replace, validation and publication |
| Caller-side existence checks | Unnecessary for create-only/replace-only intent | A read cannot provide an atomic existence precondition |
| Stored RAM | Same data and indexes; no per-document history required by the modes | No intrinsic stored-memory saving |
| Temporary RAM | Driven by supplied document and prepared mutation; small command-mode metadata | Similar document/preparation payload |
| Complexity | More public outcomes and transition tests, one shared engine path | Smaller surface, fewer mode-specific failure cases |

The existence condition uses the engine's primary-key lookup; this design adds no
separate SQL round trip for each mode. Actual durability still waits for PostgreSQL
in Durable mode. No benchmark establishes a throughput difference, and the fixed-field
storage prototype does not implement or validate these full-document operations.

The decision selects **A** for v1. The existence condition gives the application control
over creation versus replacement without a separate read. Ordinary reads and the
document representation need not become more expensive.

### Write-mode boundaries and remaining work

Each ordinary mutation publishes its root and affected indexes coherently. Explicit
WriteBatch publishes the complete group so that no member is visible in its new state
before all of it is, and a read that begins once its node has reached the position of the
group sees all of it; a search or a scan that runs across the publication may combine
states from before and after it, as the decision on the document model says (amended on
22 and 23 November 2026, the owner's answers to Q57 of the open questions and on the
Acceptance of S17). Durable completion requires
committed journal state and native publication; MemoryOnly requires local atomic
publication without promising recovery. No root gate spans I/O.
Unknown commit outcomes do not authorize blindly repeating any of these commands:
even full replacement can overwrite intervening work on a later attempt.

The later deletion and revision decisions settle ordinary Delete outcomes and
optional revision conditions. The primary-key decision below excludes renaming in
v1. Q03.4.2 settles bulk-input duplicates. Q01 separately accepts same-database
WriteBatch; full-document modes alone do not create a multi-root transaction. Nested
patch details, exact SDK/transport shapes and revision representation remain
engineering work. Repeated-root batch rejection is accepted below.
This decision does not turn the PostgreSQL provider's internal UPSERT statements
into the public document contract.

<a id="document-deletion-outcomes-accepted-for-v1"></a>

## Document deletion outcomes — accepted for v1

**Accepted on 13 November 2026:** option A. Delete by primary key returns
an ordinary deleted or already-absent result according to state at ordered execution.
Absence alone does not throw. A real deletion follows root/index atomicity, durability
and safe reclamation. Existing key-reference rules remain unchanged.

This is an accepted design, not an implemented or benchmarked deletion API.

### Deletion behavior and existing contracts

Submit a valid document key to the master writer. Determine existence at execution
time through the primary-key lookup. When present, remove the root, its exclusively
owned nested data/collections and corresponding index entries coherently. Other
independently stored documents are unaffected. Exact method/result type names remain
SDK work; neither alternative adds predicate deletion or a multi-document transaction.

Q02.1 already selects key references without referential integrity. Incoming
references do not block deletion, are not searched for or cleared automatically,
and do not cause cascade deletion. The stored keys can remain unresolved after the
target disappears. If that key is later recreated, key references resolve to the new
document under the existing identity contract. These rules apply to both options.

Logical removal and physical memory reclamation are separate. Release owned storage
and decrement stored string references through the existing lifetime/ownership rules;
shared strings still used elsewhere remain alive. Reuse memory only when protected
readers and other engine users can no longer access it. Caller-owned copies remain
valid. Returning memory to the OS or compacting native segments is Q17 engineering,
not a promise that a successful Delete immediately lowers process working set.

A real Durable deletion succeeds only after its journal commit and coherent native/
index publication. Recovery and ordered replica application must preserve the deletion.
No root gate spans I/O; no child-acknowledgment barrier is introduced. Replicas may
still lag; Q08.1.2 permits local reads without immediate freshness. Missing keys, invalid input,
unavailable/failed state and an uncertain commit are different outcomes; errors must
not be disguised as confirmed absence.

### Delete option A: deleted or already absent — accepted

| State at execution | Accepted result |
| --- | --- |
| Document exists | Delete it and report deleted |
| Document is absent | Leave data unchanged and report not deleted / already absent |

An absent key is a normal outcome, not an exception. The caller can distinguish a
performed deletion from an already-absent target, for example through a Boolean or
compact result value; the exact SDK shape is not selected. No separate application
existence read is required, and such a read would not replace execution-time checking.

Advantages: cleanup and synchronization code can request absence directly. Repeated
removal of an unchanged absent key is cheap to handle without constructing an
exception. This is consistent with the accepted missing Map/Set removal behavior,
while still addressing an independent root rather than a collection member.

Costs: applications requiring the document to have existed must inspect the result.
Ignoring not-deleted can hide a wrong key or a stale expectation. The result confirms
state at this command's execution; a later Insert or Upsert can create that key again.

### Delete option B: fail when the document is absent — not selected

Delete an existing document normally; return an explicit not-found failure when the
key is absent. An absent-key failure changes no data. Reference, ownership, index,
durability and reclamation rules remain identical to A.

Advantages: an incorrect assumption that the document exists is visible immediately
through a failed call. The successful outcome has one shape: a document was deleted.

Costs: harmless repeated deletion also becomes a failure. Callers that accept absence
must handle that failure; checking first adds work and can race with changes. Frequent
expected not-found exceptions add transient allocation and CPU work. The engine still
needs the same primary lookup and real-deletion machinery.

### Delete speed, memory and complexity

| Aspect | A: normal absent result | B: not-found failure |
| --- | --- | --- |
| Deleting an existing document | Same lookup, journal, index and ownership work | Same work |
| Missing key | Compact unchanged result | Failure/exception handling |
| Ordinary reads | Same local read contract | Same contract |
| Stored RAM | Same logical removal and safe reclamation | No intrinsic memory saving |
| Temporary RAM | Small outcome state; real deletion may require bounded preparation | Similar preparation plus exception allocation for missing keys |
| Complexity | Two ordinary results; callers inspect them | Simpler success shape, more expected failures in callers |

Deletion cost can depend on document size, owned blocks, shared-string references and
indexes. Neither option promises constant-time deletion or immediate physical memory
release. No benchmark establishes a speed/memory ratio; the prototype does not test
a general document-delete implementation.

The decision selects **A** for v1. Applications can request removal directly and
distinguish whether anything was removed, without exceptions for an ordinary absent
key. This does not select a second strictness mode.

### Delete boundaries and remaining work

The shared all-no-op sequence/journal accounting follows the milestone contract; this
choice does not weaken durable completion for real changes. An unknown commit
outcome is not an already-absent result and does not authorize blind retry: another
command could recreate the key before a repeated Delete. The later revision check
provides an optional guard but does not establish which operation committed after
an uncertain response. Automatic retry and permanent tombstones are not selected.

Bulk deletion and exact API/transport shapes remain separate. The later primary-key
decision confirms that deletion and creation under another key are separate writes.
Root deletion does not relax the accepted partial-update rule: trying to patch a
missing root is still a failure. The accepted conditional Delete reports a conflict
if its root is absent or its revision differs; unconditional Delete keeps the
ordinary already-absent result.

<a id="optional-revision-checks-accepted-for-v1"></a>

## Optional revision checks — accepted for v1

**Accepted on 13 November 2026:** option A. Replace, partial scalar/
collection updates and Delete may require the revision observed by the caller.
Check it against the existing root inside ordered execution. A missing root or a
mismatched revision produces a conflict and changes nothing. Unconditional forms
remain available; ordinary Delete still reports already absent for a missing key.

The revision covers the complete owned document and is captured coherently with
read data. Keep current metadata, without retaining historical document copies.
Insert keeps its existence condition and Upsert remains unconditional. Exact token
representation and SDK signatures remain engineering work. This is an accepted
design, not an implemented or benchmarked API.

### Revision-check example

Two callers read the same configuration. One changes its limit. The other later
submits a complete replacement built from the older copy and overwrites that change.
Scalar patches reduce the fields being overwritten, but two calculations based on
the same old scalar can still overwrite each other's results. A List position can
also refer to a different item after an intervening insertion or removal.

The accepted check protects a mutation of one document. It is not a database/query
snapshot or a guarantee about other documents an algorithm read.

### Revision option A: optional expected-revision condition in v1 — accepted

Allow a caller to read a document value (or supported view/projection) together with
an opaque revision token captured coherently with that value. The same contract must
work for generated and schema-driven access. Returning a token does not require
materializing an otherwise borrowed or projected whole document. SDK shapes remain open.

A caller may attach the observed token to Replace, a scalar/collection mutation, or
Delete targeting an existing document. The master checks the expected token against
the current root as part of ordered execution, before accepting the mutation. If the
root exists and the token matches, execute the command normally. If the root is absent
or the token differs, report an explicit conflict and apply none of the command.
Do not silently reread, merge, recompute or retry on behalf of the application.

The condition covers the complete root, including all owned data. A change to an
unrelated field of that same root can therefore cause a conflict; per-field versions
are not part of this decision. An unconditional Delete of an absent root still returns
already absent. A conditional Delete with an obsolete token conflicts, including when
the target was removed. Insert retains its absent-key condition; Upsert remains the
explicit unconditional add-or-replace mode under this decision. No expected-absence
token or conditional-upsert API is introduced.

A change publishes its new revision coherently with the root and indexes. Any committed
logical change must invalidate an earlier token, including changing values and later
changing them back. The milestone baseline preserves revision for confirmed no-op outcomes. Full Save
does not detect equality, so a different token is not proof that
field contents differ. No numerical increment/count meaning is exposed to callers.
Physical relocation alone is not a new logical revision.

#### One current token, not stored historical versions

Store only the current revision metadata for each live root. Replace that metadata
as writes progress; do not keep an old document image for every update. Holding a
token does not pin old memory, block later writes, or enable ReadAtRevision. The
existing bounded recovery journal/base continues to own persistence history.

The revision condition is metadata for a write, not another primary key. Non-owning
references remain key-based and still resolve to a recreated key under Q02.1. The
condition must reject a token from a prior incarnation even though the key is the same.
Do not implement it as a per-key counter that resets on recreation.

#### Recovery and replica requirements

Tokens must not accidentally match another revision after deletion/recreation, a
master restart, restore, schema migration or MemoryOnly history reset. Preserve a
valid identity domain or explicitly invalidate old tokens when the history changes.
One possible design uses a database-history namespace and unique ordered mutation
identities; the representation is not selected. A physical address, reused slot,
wall-clock timestamp alone or resettable counter is insufficient as the guarantee.

Values and revision metadata must agree across journal replay, maintained saved state,
replica base transfer and later updates. A child returns the revision of the data it
actually serves. A lagging child's token may therefore conflict on the master; it
must not be relabeled with a newer token. Existing freshness rules are not strengthened.
A token from another key/entity/history must not satisfy the condition accidentally.

The master must serialize a root's revision check with its mutation; no root gate spans
storage I/O and no long caller-held lock is added. The nontrivial work is coherent
publication, bounded revision metadata, serialization, identity lifecycle and recovery
validation. Local handles, global history metadata and representation choices remain
engineering work; this decision does not require unbounded per-deleted-key tombstones.

#### Costs and benefits

Advantages: callers can protect read-modify-write and positional collection commands
against overwriting a newer root. Conflicts are visible and the application decides
whether to reread, recompute or abandon. Callers whose source is authoritative can
continue using the accepted unconditional operations.

Costs: revision metadata for live roots, metadata updates on writes, an additional
comparison for conditional commands, optional result/command payload, conflict handling
and recovery/replication work. Even unconditional logical changes must update the token
so later conditional callers can detect them. Under frequent updates to one root,
conflicts can require expensive application recomputation. Successful completion still
waits for the same Durable commit; the check is not a way to avoid storage latency.

For scale only: an extra 8 bytes per live root would add 8,000,000 bytes (about
7.63 MiB) for one million roots, or 80,000,000 bytes (about 76.29 MiB) for ten million.
A 16-byte field doubles those payload figures. These are arithmetic examples, not a
chosen token size or measured footprint; alignment, existing reusable metadata and
global context change actual cost. No separate CLR token object per root is required.

### Revision option B: defer revision-conditional writes beyond v1 — not selected

Keep the accepted existence conditions and ordered unconditional writes. Add no
public expected-revision contract in v1. Later successful writes may overwrite values
based on older caller reads. A preliminary read/check outside the writer does not
make a subsequent mutation conditional.

Advantages: smaller initial SDK/recovery surface and no revision metadata solely for
this feature. Workloads that import authoritative state or coordinate computations
through a single application owner may be satisfied. Partial assignments still limit
which fields a command replaces.

Costs: applications cannot ask the engine to reject a stale replacement, patch or
delete. Updates to the same field, stale complete copies and positional list changes
need application-level coordination or acceptance of overwriting. A single master
writer does not itself supply that coordination across a read/calculate/write cycle.

### Revision-check comparison and rationale

| Aspect | A: optional revision condition | B: defer the condition |
| --- | --- | --- |
| Reads | Optional coherent token with data; ordinary local read path remains | Current read surface |
| Writes | Maintain current metadata and compare when requested; conflicts can trigger caller work | Fewer feature-specific steps; existing schema/existence checks remain |
| RAM | Bounded metadata per live root plus global identity context | No extra metadata for this public condition |
| Historical data | No old root versions retained by this feature | No old root versions retained |
| Complexity | Coherent tokens across publication, recovery and replication | Simpler v1; callers own stale-write risks |

The decision selects **A**. The recommendation supports reusable .NET algorithms and
configuration workflows that can read, compute and write concurrently. It adds a
bounded conflict check with current metadata. Coherent publication and recovery
make this more involved than comparing two numbers; there is no measured overhead
yet. Existing fixed-field prototypes do not implement this feature. Implementation
must verify stale/same-revision races, delete/recreate, restart/restore and
replica-carried tokens before it is treated as correct.

### Revision-check limits and remaining work

No global snapshot, field-level versioning, automatic merge, retry, application
callback, cross-root transaction or permanent version history is introduced. A token
is not an operation ID or proof of whether a timed-out write committed. Unknown
commit outcomes still follow Q09.1; even a later conflict cannot identify which write
changed the root. The milestone contract fixes revision binding and no-op accounting; exact facade types
and serialization must implement it.

<a id="immutable-primary-keys-accepted-for-v1"></a>

## Immutable primary keys — accepted for v1

**Accepted on 13 November 2026:** option A. A document's primary key is
fixed for its lifetime. V1 has no rename operation. Assignments and replacement must
preserve the identity; supplying another key to Insert or Upsert addresses another
document, without moving the original.

Creating under a new key and deleting the old document remain distinct mutations.
Independent calls have no atomic two-key guarantee; an explicit same-database
WriteBatch can group these supported mutations without making the key mutable or
preserving the deleted root's identity. References retain their stored keys and are not
retargeted automatically. A recreated key denotes a new root revision identity under
the accepted revision-check contract. Native relocation remains possible without
changing the logical key. The scalar catalog owns key types/equality; Q12 fixes
identity to the open database and declared type.

### Primary-key example

A configuration document is stored under key `shipping-default`. The application
wants the same content under key `shipping-standard`. Other documents may contain
references to `shipping-default`. A rename must define not only where the data lives,
but how lookups, references, indexes, revisions and recovery observe the change.

The question concerns the root's primary key. Ordinary fields named Id, Name or Code
are not automatically primary keys; changing a reference field's stored target key
also remains an ordinary value mutation. Key types/comparison rules belong to
Q03.4.1; Q12 fixes lookup to type + key within the open database.

### Primary-key option A: immutable for a document's lifetime — accepted

Set the primary key when creating the root. Updates and Replace preserve that
identity. Reject an attempt to change it through a field assignment or replacement;
if an API carries both a target key and a value key, they must identify the same key
under its schema-defined equality. Exact API encoding remains open. This decision
does not change collection Map-key spelling, now settled by Q03.4.2, or the separate
remaining scalar comparison rules.

Insert and Upsert always address the key supplied to that command. Addressing a
different key acts on a different document; it never implicitly moves the old one.
No RenameKey command, alias or automatic forwarding table is supplied in v1.
The application's business name/code can remain a mutable ordinary field when it
does not need to be the stable primary key.

If necessary, an application can explicitly create a document under the new key and
remove the old document. When submitted as independent writes, both keys may exist
and a failure can leave both. Deleting first instead creates a gap and risks leaving
neither if creation fails. There is no automatic rollback or atomic two-key guarantee.
A revision condition on the old document protects that one deletion, not the entire
copy/create/delete workflow or the earlier copy. For supported mutations within one
database, the accepted explicit WriteBatch can make the submitted create/delete
group atomic. It does not include an earlier application read in a snapshot; use
appropriate revision conditions to reject stale source data when required.

References retain their stored keys. References to the old key become unresolved
when that key is deleted; the database neither finds nor rewrites them. Recreating
the old key still makes those references resolve to the newly created document under
the existing identity contract. A new root receives its own valid revision identity;
an old root's token cannot authorize a write to the new key. Changing reference
values in independent calls does not create a cross-document transaction.

Advantages: a stable identity boundary, fewer mutation/replay cases and no special
key-move coordination between primary entries, indexes and revisions. Native memory
can still move during compaction: the stable root entry records the current physical
location while the logical key remains unchanged.

Costs: applications that require renaming their primary identifiers need the explicit
copy/create/delete workflow, optionally grouped in one database WriteBatch, or a
stable-key schema with a separately mutable business field.
That schema is an application choice, not a requirement for a second database ID.
Creating a separate replacement document can temporarily duplicate its data and index
entries. This can cost more RAM and write work than a dedicated rename when renames
are frequent; there is no claim that immutability makes that workflow faster.

### Primary-key option B: explicit atomic rename in v1 — not selected

Add a separate command taking an existing source key and a new, absent destination
key in the same identity scope. Check both at ordered execution. A missing source or
an occupied destination fails without changes; overwriting another document is not
part of this alternative. A matching source revision could be required optionally,
using the accepted conflict mechanism. Same-key requests and exact API/result shapes
would need rules before implementation.

Publish the old-key removal, new-key installation, affected index changes and new
revision as one engine operation; journal replay and each replica must preserve it.
An individual protected observation must not expose half a rename. Independent reads
can still straddle it, and a query/graph traversal gains no common snapshot. No root
lock may span storage I/O. Durable success still waits for commit and publication.

The existing reference contract remains unchanged even with atomic rename: stored
old-key references are not rewritten or redirected. They may become unresolved,
while previously unresolved references to the new key may start resolving. This
alternative supplies no referential integrity or cross-document update cascade.
A revision from before the rename cannot authorize a write under the new identity.

Advantages: callers can request a key move in one command with coherent publication
and recovery. The engine may reuse owned data instead of materializing and copying
it in the application, depending on the physical layout. Applications avoid the
partial-failure window of two separate commands for the source/destination change.

Costs: an additional coordinated mutation covering two primary entries, key-dependent
secondary-index data, revision identity and enumeration behavior. Lock-free or
protected lookup paths must respect the publication boundary. Existing no-repeat
traversal rules are defined by key and do not imply that a renamed object cannot be
seen under two different keys during a live pass. Recovery, coalescing and replication
must all preserve the rename as a unit. Payload copying and the actual cost depend
on layout; neither constant time nor a full-document copy is assumed.

### Primary-key comparison and rationale

| Aspect | A: immutable key | B: explicit atomic rename |
| --- | --- | --- |
| Ordinary reads | Current key-to-location lookup | No inherent extra lookup if aliases are excluded; publication synchronization needs verification |
| Ordinary writes | Existing commands plus key validation | Same operations remain, with an additional coordinated command |
| Changing a key | Explicit create/delete; same-database WriteBatch can group them, but no Rename operation or retained root identity | One atomic engine command, with primary/index/revision work |
| Resident RAM | No rename-specific table; an explicit copy workflow can temporarily duplicate data | No alias table required; bounded staging for rename, possible reuse of owned blocks |
| References | Keep their stored keys; no automatic retargeting | Same rule; atomic rename does not repair incoming references |
| Complexity | Smaller v1 command and recovery surface | More publication, failure, recovery and live-traversal cases |

The decision selects **A** for v1. Primary-key stability fits the accepted key-based
identity and single-document mutation boundary. The main benefit is simplicity;
ordinary read speed need not differ. B is valuable when atomic business-key renames
are an actual workload requirement, but adds coordinated identity changes while still
leaving incoming references for the application to manage.

These are design tradeoffs, not benchmark results. The existing fixed-field storage
prototype implements neither general document renaming nor its recovery contract.
This is an accepted design, not an implemented public API. Schema migration,
automatic retry and numerical budgets remain separate questions.
The later numeric-assignment decision defers arithmetic commands beyond v1.

<a id="numeric-assignments-without-arithmetic-commands-accepted-for-v1"></a>

## Numeric assignments without arithmetic commands — accepted for v1

**Accepted on 13 November 2026:** option A. V1 assigns ready numeric
values through the existing mutation mechanism. Calculations stay in application
code; dedicated Add, Subtract and Increment commands are deferred beyond v1.

When a calculation depends on previously read data, the caller can read the needed
fields with a coherent root revision and submit the result conditionally. Conflicts
require an application decision; no automatic retry or guarantee of eventual success
is added. Assigning an authoritative ready value does not require a preceding read.
This is an accepted design boundary, not a new implementation or benchmark result.

### Arithmetic example

A document stores Count = 10. Two callers each want to increase it by one. If both
read 10 and independently assign 11 without a revision condition, the final value
can be 11. The master orders conflicting assignments but does not turn them into
two additions. Assigning a value supplied by an authoritative source is different:
that caller already knows the intended final value and may need no prior read.

With the accepted revision condition, both callers may read the same value/revision.
The first successful assignment changes the root; the second conflicts and changes
nothing. The application can obtain newer data, recalculate and submit another
conditional assignment. This prevents a stale overwrite, but does not promise success
within a bounded number of attempts or retry automatically.

### Arithmetic option A: assignments in v1; defer engine-side arithmetic — accepted

Keep numeric calculations in application code. Submit the computed value using the
existing scalar assignment command; attach the observed root revision when the
calculation must be based on unchanged data. Read only the required values with a
coherent revision where the access API permits it; a full-document copy is unnecessary.
Multiple assignments and collection commands can still form one atomic root mutation.

V1 exposes no dedicated Add, Subtract or Increment command and no arithmetic
expression/callback inside the writer. A later version can consider a bounded numeric
command set when a concrete workload requires it; this is not a delivery commitment.
Existing schema range/type validation still applies to every submitted value.
Application arithmetic must handle its own overflow and precision deliberately: a
wrapped value already within the field's range cannot reveal the caller's intention
to the database. Null/default treatment belongs to the application's calculation;
write-side missing-parent and initialization rules remain unchanged.

Advantages: reuses the accepted mutation, revision and recovery mechanisms. It adds
no arithmetic command encoding or server evaluation rules. Applications importing
complete values pay no extra read solely because this option is selected. Unrelated
reads and writes retain their existing behavior.

Costs: a dependent read/compute/write requires a read and a conditional write attempt.
That read may be local to the embedded master or a child; it is not necessarily an
extra network request. A lagging child's revision may conflict on the master, and
changes to any field in the same root can invalidate the condition. The application
must choose when to reread/recalculate or abandon; retry limits and freshness cannot
be assumed. Hot counters can incur repeated attempts and additional latency, CPU,
allocations and pending payloads. They should not be described as equally efficient
as direct atomic additions under contention.

No extra per-document metadata beyond the accepted revision mechanism is required.
Temporary state is driven by the requested fields, concurrent calculations and any
caller-managed retry policy; no new engine history is introduced.

### Arithmetic option B: atomic Add/Subtract in v1 — not selected

Add numeric commands that use the field value current at ordered execution, such as
adding 1 to Count. Two successful independent additions to Count = 10 produce 12,
without a preceding application read. The example assumes a valid integer result
and no other mutations. A supplied optional root-revision condition still checks the
whole command; omitting it allows the delta to apply to whatever current value the
writer accepts.

The bounded command would target numeric scalar fields, excluding primary keys,
Bool, enums and Flags. Existing root/parent requirements would apply. The proposed
baseline rejects a null numeric target instead of silently treating it as zero.
Commands could combine with the already accepted ordered edits in one root mutation;
any failure must discard all provisional changes, and successful publication includes
indexes and revision metadata. This alternative adds no general expression engine,
application callback or cross-root arithmetic transaction.

If this alternative is revisited, settle operand conversion, integer range checks,
Decimal precision/rounding and Float64 special-value/result rules. The proposed baseline
rejects integer/Decimal overflow rather than wrapping; finite floating-point rounding
still needs the declared type's semantics. Unsigned subtraction needs an explicit
operand/range rule. Storage support for a numeric type alone does not specify all of
these arithmetic behaviors. These details remain open within this alternative.

Advantages: one write command for a delta, no preceding caller read and no mandatory
read/compute conflict cycle for an independent counter update. A command can be small,
and the same scalar storage can be used. This can reduce work substantially for hot
counters; it does not make updates to one root parallel or remove durable commit cost.

Costs: new public/encoded operation forms, typed validation, ordered arithmetic and
failure tests. Recovery and background coalescing must reproduce every accepted
operation's effect. A last-delta-wins rule loses additions; blindly combining deltas
can change overflow or rounding behavior and must not replace ordered semantics.
Encoding resulting values is one possible strategy, not a selected journal format.
The existing fixed-field assignment prototype does not validate arithmetic commands.

Numeric fields can keep their existing storage layout; this feature requires no
per-update history. Bounded preparation still consumes temporary memory. Ordinary
reads need no new API step, although writer execution and publication can contend
with them. No measured throughput or memory advantage is claimed for the unfinished
engine.

### Arithmetic comparison and rationale

| Aspect | A: application calculation and assignment | B: atomic arithmetic command |
| --- | --- | --- |
| Assign an already known value | Existing scalar write, no preliminary read required | Same scalar write remains available |
| Dependent numeric change | Read/compute/conditional write; a conflict may require application work | One ordered delta command; no preliminary read for an independent addition |
| Ordinary reads | Existing path | Existing path; numeric work stays in the writer |
| Resident RAM | Existing data and accepted revision metadata | Same scalar data can be used; no permanent per-update history |
| Temporary RAM | Calculation state and potentially repeated attempts | Bounded command preparation; potentially fewer caller attempts |
| Complexity | Reuses the agreed v1 command set | Adds numeric semantics, encoding, recovery and coalescing cases |

The decision selects **A** for v1. The recommendation favors a smaller implementation
for configuration and value-refresh workloads, reusing revision checks for dependent
read/compute/write workflows. It does not claim that A is faster for counters.
High-contention counters remain a limitation: a future arithmetic extension should
be evaluated on that workload if it becomes a requirement. No delivery version or
performance budget for that extension is selected.

### Arithmetic boundaries and remaining work

Neither alternative introduces blind automatic retry after an unknown commit.
Repeating an addition can apply it twice; a revision condition can prevent a stale
write but does not identify which request committed. The accepted uncertainty and
Durable acknowledgment contracts remain in force. The later [operation-result contract](#operation-identity-and-retained-results-accepted-for-v1)
adds bounded same-token resubmission. Numerical budgets, additional conditions and
duplicate assignments remain separate; explicit database WriteBatch is accepted
under Q01.

<a id="full-document-save-and-explicit-partial-updates-accepted-for-v1"></a>

## Full-document Save and explicit partial updates — accepted for v1

**Accepted on 13 November 2026:** Save submits the complete final owned
document. Partial updates use a separate explicit API. V1 has no automatic change
tracking, dirty-field inference or setter-history recording for ordinary copies.

The application edits its own data without changing live arena storage. Only the
explicit submission asks the master to write. Insert/Replace/Upsert intent, optional
revision conditions, root/index atomicity and Durable completion retain their
accepted contracts. A full value does not recursively save independently referenced
roots. Public borrowed views remain read-only. This is an accepted design, not an
implemented API. Exact names remain open; input lifetime is accepted below.

### Three separate responsibilities

1. **Application editing:** the caller changes its owned values and nested owned
   collections. No arena bytes, indexes, journal or replicas change merely because
   a property setter runs. The caller holds no engine read/write gate while computing.
2. **Submission:** Save supplies the complete final document. A separate explicit API
   submits partial updates. Exact method names and internal capture timing remain SDK
   work; automatic change tracking is excluded in v1. Input lifetime is settled below.
3. **Engine publication:** the writer validates and prepares the submitted mutation,
   checks any expected revision and follows the agreed Durable or MemoryOnly pipeline.
   Readers observe a coherent root. Public borrowed arena views remain read-only.

For a complete value submitted after assigning Threshold = 10 and then Threshold = 20,
the database receives Threshold = 20. The earlier value is not an operation to replay
or reject. Database validation applies to the submitted state. Local setter or
collection validation, where present, is a separate application/type concern; there
is no requirement to record a history of all ordinary property assignments.

In Durable mode, the accepted candidate pipeline prepares bounded engine-owned work,
commits the journal and then publishes the root/index changes before reporting success.
No root gate spans I/O, and uncommitted changes are not exposed to readers. Editing
an application copy remains separate from the engine's physical publication work.
The subsequent [Q16 decision](physical-storage.md#targeted-mutation-preparation-and-publication)
accepts targeted preparation for explicit partial updates: publish fixed-size values
in place and install necessary prepared blocks after commit under root protection.
**Amended on 16 November 2026 (owner):** fixed-size values are not published in place;
the update publishes a new root block, see [memory and access](memory-and-access.md).
Full Save prepares a complete replacement without diff. Concrete scheduling, resource
reservation and failure transitions remain engineering work.

### Save option A: complete values and a separate partial-update API — accepted

Use an ordinary owned .NET value as the straightforward edit model. Saving an existing
complete copy can map to the accepted Replace capability, optionally with the revision
captured by its read. Create-only and Upsert intent remain explicit under the accepted
write modes; a generic Save name must not silently choose a new default behavior.
Repeated changes to the copy are ordinary application work and only the submitted
result matters.

Keep the accepted partial-update capability for callers that already know the target
fields and final values, such as updating one coefficient. Such an update can be
submitted once without first copying the entire document. How the SDK expresses that
partial update is a separate surface choice; it need not expose writable database
memory or turn every application setter into a write.

Advantages: simple ownership and validation, no mandatory tracking on every .NET
setter, and compatibility with caller-created values and reusable destinations.
The existing writer, optional revision check and durable publication mechanisms
serve both full and partial submissions.

Costs: supplying a complete copy can mean materializing, retaining, serializing and
validating the whole owned document even if one field changed. Local embedded access
avoids network transport but not all copying or validation. Full-replacement semantics
do not require blindly rewriting every native or PostgreSQL byte; internal diffing
is possible but neither selected nor free. An unconditional full replacement can
replace intervening changes, so a caller depending on its earlier read should request
the accepted revision condition.

This is the accepted v1 submission model. It preserves the previously agreed partial
and collection operations through a separate explicit API. It does not select exact
SDK signatures or a new default existence mode. The later input-lifetime decision
requires stable source data until asynchronous completion.

### Save option B: automatically tracked editable copies — not selected for v1

Generated or schema-driven editable values could record which scalar fields were
assigned and submit only their final values at Save. Repeated scalar assignments
need only replace one staged value or mark one field, without an append-only setter
history. Changes still remain local until explicit submission.

Advantages: convenient property-style editing with smaller sparse write payloads.
It can preserve untouched fields using the existing partial-update semantics.

Costs: tracking metadata and setter work, generated/schema-driven editing support,
clear initialization and input-lifetime rules, and integration with nested structures
and collections. An arbitrary uninstrumented POCO does not automatically report its
changes to the SDK. A dirty collection flag alone may still require replacing the
whole collection; efficient element edits need additional tracking or explicit
operations. Previously accepted collection commands do not by themselves supply
transparent collection tracking.

Comparing an edited copy with an original baseline is another possible tracking
implementation, with baseline memory and comparison costs. Comparing only against
the master's current value cannot reliably infer caller intent from a stale copy:
untouched fields may have changed since the original read. Revision checks detect
such conflicts; they do not automatically produce a field diff.

### Save cost comparison

| Aspect | Ordinary copy and explicit submission | Automatically tracked editable copy |
| --- | --- | --- |
| Local edits | Normal value/collection operations | Setters/wrappers also track intended changes |
| Sparse Save | Complete value; partial updates use a separate explicit API | Can submit only marked final scalar values; collection behavior needs design |
| Read/copy cost | Materializes requested owned values | A fully materialized tracked copy still pays that cost |
| RAM | Owned copy and bounded submission preparation | Owned/staged data plus tracking metadata or a comparison baseline |
| Complexity | Existing complete/partial capabilities, explicit caller intent | Additional generated/dynamic editing and collection-lifetime machinery |

The decision selects **A**. It keeps ordinary editing simple and avoids automatic
tracking metadata and collection wrappers, while explicit partial updates support
sparse writes. There are no measurements comparing these SDK designs. Neither adds
historical versions per local edit, direct application writes to the arena,
long-lived database locks or a transaction across multiple roots.

### Repeated scalar targets in an explicit partial update

**Accepted on 14 November 2026: option A.** One explicit partial mutation may contain
at most one scalar assignment per target path. Reject the entire mutation before
commit if the submitted assignments repeat a scalar target, even when their values
are equal. If the mutation belongs to an explicit WriteBatch, reject the whole batch.
Do not silently keep the last assignment or merge conflicting scalar intent.

For example, submitting both `Set("Name", "First")` and `Set("Name", "Second")`
for the same root field in one partial mutation fails validation. The caller sends
the intended final scalar value once. Syntax is illustrative, not a selected SDK API.

Ordinary Save still submits the final caller-owned document, irrespective of how
often its properties were assigned locally. Ordered collection commands, including
multiple List inserts/removes, retain their accepted execution semantics and are
outside this duplicate-scalar rule. Separate ordinary writes, duplicate root entries
inside WriteBatch and same-token retries have their own accepted contracts.

Schema-bound target identity, command encoding and the bounded uniqueness check
remain engineering work. This contract does not make public arena views writable.
[Rationale and alternative](../design/questions/mutation-commands.md#accepted-repeated-scalar-target-policy)
describe the preparation cost; no new benchmark result or implementation is claimed.

<a id="stable-write-inputs-until-completion-accepted-for-v1"></a>

## Stable write inputs until completion — accepted for v1

**Accepted on 13 November 2026:** option A. After starting an asynchronous
write, keep its mutable input unchanged until the operation completes. This includes
owned nested data, collections and buffers. Do not return them to a pool early.

After success, failure or cancellation completion, the engine no longer depends on
that caller-owned mutable input. A cancellation request alone is not completion.
This lifetime contract neither exposes writable arena memory nor holds a root gate
across waiting or I/O. It does not promise zero copying or resolve an uncertain commit.
Exact async signatures and internal capture timing remain engineering work.

### Asynchronous input example

The application obtains an owned document, changes a field and calls SaveAsync.
The call returns an awaitable while validation, queueing or storage work may still
be in progress. The application then changes the same object or clears its List
for reuse. Unless the engine has captured all required data, it could read a mixture
of values. The same problem applies to returning a mutable buffer to a pool while
the pending write still reads it.

The question is about the application's input object. It does not grant writable
access to the database arena or hold a database lock while the application waits.
SaveAsync and awaitable names describe the accepted contract, not implemented SDK
signatures. Both alternatives still wait for the selected write-completion guarantee.

### Input-lifetime option A: unchanged until completion — accepted

From the call until its returned operation completes, the caller must keep the
submitted mutable data alive and unchanged. The obligation covers the root, owned
nested values, collections and supplied mutable buffers, including aliases used by
other code. Do not refill the destination, clear a collection, mutate it from another
thread or return its storage to a pool during that interval.

Reading the stable application-owned data is allowed. Independent application-owned
copies remain usable. Non-owning document references carry keys; this rule does not
freeze the independently stored objects those keys identify. Public protected views
keep their existing short read-only lifetime and cannot become asynchronous inputs.

After the operation completes, whether successfully or with an error/cancellation,
the engine must no longer depend on the caller's mutable input. The caller may then
reuse it or return it to the pool. A synchronous rejection must likewise leave no
later dependency on that input. Requesting cancellation is not completion: the caller
must observe the operation's completion before reuse.

If an in-flight commit has an uncertain outcome, the engine must finish reading or
capture any still-needed data into engine-owned storage before releasing the caller
from this obligation. That does not turn an uncertain commit into rollback or grant
permission to retry. Definitely unstarted expired requests still must not execute
later under the accepted queue contract. No root gate spans queue waiting or I/O.

Advantages: one clear lifetime boundary that fits awaiting a write before reusing
its buffer. The engine need not promise an immediate complete snapshot merely to
allow early caller reuse; it may serialize or prepare bounded data when appropriate.
No change-tracking wrappers, long-lived native read leases or setter history are
introduced.

Costs: the input and its owned buffers remain unavailable for mutation/reuse while
the operation is pending. A storage interruption can extend that interval to the
accepted wait limit. Concurrent writes may require more application-owned inputs;
pooled instances cannot be returned early. Caller-retained data consumes process RAM
even if it is not an extra engine-owned copy, so bounded admission still matters.

This is not a zero-copy guarantee. Validation, encoding, transport and publication
may require engine-owned buffers anyway. The implementation may capture input early,
but the public contract does not expose early release in v1. Exact buffer layout,
copy timing and SDK types remain engineering work.

### Input-lifetime option B: capture before returning the awaitable — not selected

The API synchronously captures a stable engine-owned representation of all required
input before returning the awaitable. Once the call has returned, the caller may
change or reuse its original object while awaiting the write result. The caller must
still avoid concurrent mutation during capture; copying cannot make arbitrary
unsynchronized edits into a coherent input snapshot.

The representation can be encoded bytes or prepared values instead of another POCO.
The capture is isolated from later mutations through any original root/collection/
buffer alias. It must cover every mutable value the engine will use, including
explicit partial-update payloads. No database publication or durable success occurs
merely because capture finished.

Advantages: earlier object/buffer reuse and a clear captured value for the pending
write. Application pools can release the original input after the API call returns.
There is no need to preserve that original through a slow storage wait.

Costs: potentially substantial synchronous traversal/copy/encoding before the caller
receives the awaitable, especially for large documents. A complete retained capture
can increase peak RAM and memory bandwidth. It may reuse serialization buffers needed
by A, so an extra second POCO or a fixed memory multiplier must not be assumed.
The timing and peak cost require measurement. Pending captures must fit the existing
request-count and byte budgets; this option permits no unlimited snapshot queue.

The durable write still waits for PostgreSQL and coherent publication. Only the
caller's input-lifetime boundary changes, not the meaning of write completion.

### Input-lifetime comparison and rationale

| Aspect | A: input stable until completion | B: capture before returning awaitable |
| --- | --- | --- |
| Earliest input reuse | After the operation finishes, including error/cancellation completion | After the initial call returns successfully with its awaitable |
| Local editing during the wait | Use a separate owned value if needed | Original input is already detached from the pending write |
| Write work | No mandatory immediate full capture; encoding/preparation still exists | Mandatory complete capture before returning; storage work still follows |
| RAM | Retains caller input plus necessary engine preparation | Retains captured representation; caller may reuse original sooner |
| Ordinary database reads | Same arena/read contract; no caller-held database lock | Same contract; capture work can add CPU/memory contention |
| Complexity | Input-lifetime obligation and correct release on all completion paths | Complete alias-safe capture, synchronous work and capture admission |

The decision selects **A** for v1. It supports the requested pooling/reuse goals through an
explicit await boundary without promising a full immediate capture for every write.
Its limitation is real: storage waits also delay input reuse. B is useful if early
reuse is required, but should be selected with measured capture/peak-memory costs.
Neither is implemented or benchmarked by the existing fixed-field storage prototype.

No default timeout, pool implementation, per-write history, automatic retry or early
success is introduced. Exact async signatures, error shapes and command encoding
remain engineering work; duplicate-scalar rejection is accepted separately above.

<a id="operation-identity-and-retained-results-accepted-for-v1"></a>

## Operation identity and retained results — accepted for v1

**Accepted on 13 November 2026:** option A. Each submitted write has an operation
number/token that the caller can retain before submission. An explicit WriteBatch
has one token for the whole batch; independent writes sharing an internal SQL commit
retain separate tokens. This identifies the operation, not a root key or revision.

For a Durable master, persist a compact result receipt atomically with the mutation
journal effects in the same PostgreSQL transaction. Resolve uncertain outcomes through
authoritative status lookup or resubmission of the original operation with the same
token within a bounded retention window. Already committed effects must not execute
again. Concurrent duplicates must converge on the same operation; a reused token with
different request contents is an error. Request binding includes the database,
schema context, values and conditions, not only the target key.

Preserve the original result, including successful no-ops. In particular, repeating
an already-absent Delete must not delete a later object created under the same key.
Do not rerun a committed operation's original revision check against newer data.
No-op revision/position accounting is now fixed by the [milestone contract](../design/milestone-1-engine-contract.md#write-coordinator-and-revision-semantics). The later duplicate-identity
decision separately rejects repeated roots in an explicit WriteBatch; repeating
the token of that batch is still governed by this operation-result contract.

**Added by the owner on 15 November 2026:** retained results have no memory or storage
limit by default. On a MemoryOnly master they are counted in the instance budget; on a
Durable master the receipt budget is an option that is not set by default.

Receipts establish durable commit. Normal write success still requires coherent
master publication or recovery; they do not prove replica freshness. An absent
receipt while the original transaction may still complete means Pending/Unknown,
not definite rollback. Same-token resubmission must coordinate with an unresolved
first attempt. A status read from a lagging replica cannot establish non-commit.

Retain compact identity, request binding and result metadata, not complete historical
document bodies. Retained receipts stay in PostgreSQL; only bounded active coordination
state belongs in memory. Bound retention by time/capacity and account for receipt
work in admission budgets. Do not silently shorten a promised retention window under
pressure. Receipt retention is independent of saved-base coalescing and journal cleanup.

**Default accepted on 14 November 2026:** the safe-repeat/result-resolution window
is **15 minutes from first admission by the master**, configurable for new operations.
The master determines expiry; retries do not extend it. Same-history Durable restart
preserves promised validity, and configuration changes cannot shorten an already
promised window. This is not a storage timeout. Pending work must still be settled
safely before retirement. [Window rationale and sizing](../design/questions/operation-outcomes.md#accepted-safe-repeat-window)
describe the resource implications; exact generation and capacity mechanics remain
engineering work.

Expired or retired operation tokens must be rejected, never interpreted as new work.
The admission/retirement protocol must preserve that rule across cleanup, delayed
transactions and restart. Same-history Durable reopen preserves retained receipts;
restore establishes new history, so old-history tokens cannot execute there silently.
No permanent exactly-once guarantee or automatic transport retry is selected.

MemoryOnly has no durable result recovery across restart. Any active duplicate
coordination is bounded and scoped to its volatile history. A MemoryOnly child that
forwards a write uses the authoritative master's persistence mode and history.
Callers may release mutable inputs after completion, including an Unknown result;
a later resubmission needs the original request, while token-only status lookup does not.

The [operation protocol](../design/operation-retry-protocol.md) now fixes token ownership,
request binding, durable admission/results, expiry and retirement. Initial capacity
settings are in the milestone contract. SDK integration and complete performance/
failure verification remain E06/E07/Q13/Q14 work; the bounded SQL prototype does not
establish a production engine or its service objectives.
[Rationale, alternative and engineering constraints](../design/questions/operation-outcomes.md).

## Repeated roots inside an explicit WriteBatch

**Layer: submitted WriteBatch input at the master's write API and its preparation.**
The rule covers separate entries with the same type/key; ordinary
edits to one caller-owned .NET object before its single Save remain unrestricted.

**Accepted on 13 November 2026 for v1: option A.** An explicit WriteBatch admits
one operation per entity type/primary-key identity. Reject the entire batch before
commit when an identity occurs more than once, regardless of whether its root
currently exists or whether the repeated payloads are equal. Q01 atomicity and
the [multi-type Upsert contract](#atomic-batch-save-with-upsert-semantics) remain intact.

This concerns separately submitted entries within one explicit application batch.
It does not restrict local property assignments before one Save, ordinary writes
submitted as separate operations, or same-token resubmission of the whole batch.
Internal PostgreSQL group commit is scheduling infrastructure, not an input batch
to which this duplicate-rejection rule is automatically applied. Conflicting ordinary
writes continue through the accepted per-object ordering mechanism.

### A. At most one submitted mutation per root identity — accepted for v1

Validate uniqueness using entity type and primary-key equality from the schema.
Reject the complete batch before commit if that identity occurs in two commands,
including identities that do not yet exist. Do not silently keep the last command
or merge separately submitted commands. The rejection changes no application data
or root revisions.

A single explicit partial mutation may still update several fields or supported
owned collection targets. A complete Save contains the final edited value. The
caller may edit its independent copy any number of times before submission; those
local edits are not duplicate database commands. Exact builder/SDK syntax remains
engineering work. Repeated scalar targets inside one partial command follow the
separate accepted rejection rule above.

| Submitted batch | A result |
| --- | --- |
| Save User 42; Save Group 7 | Allowed, subject to ordinary validation and budgets |
| Save User 42; Save Group 42 | Allowed: entity type is part of the identity |
| One partial update of User 42 assigning Name and Enabled | Allowed, subject to the same checks |
| Save User 42 with value A; Save User 42 with value B | Reject the whole batch before commit |
| Save User 42; Delete User 42 | Reject the whole batch before commit |

### B. Interpret repeated-root commands in submission order — not selected for v1

Allow several commands for one identity. Each command must validate against the
staged result of the preceding command for that identity, while external readers
see none of those intermediate states. Prepare the complete final effects before
commit and retain the accepted whole-batch publication boundary. Command failure
still rejects the entire batch; this is not several independently committed writes.

This needs rules for creation/deletion followed by another command, revision
conditions against staged state, intermediate no-op outcomes and result reporting.
It is distinct from blindly selecting the last payload and can require an explicit
normalization step before journal/replay or final publication.

| Effect | A — accepted | B — not selected |
| --- | --- | --- |
| Ordinary reads | Existing read path and atomic publication: no member visible in its new state before all of the group is (amended on 22 and 23 November 2026, the owner's answers to Q57 of the open questions and on the Acceptance of S17) | Same visibility guarantees; intermediate batch states remain private |
| Write preparation | Check unique identities; prepare one root mutation per target | Interpret dependencies and validate intermediate states; final-effect coalescing may add work |
| Memory | Bounded identity-validation metadata plus existing prepared mutations | Also account for staged state and intermediate payloads; the exact peak depends on implementation |
| Implementation | Simpler revision checks, reservation planning and per-root publication plans | More command combinations, outcome rules and replay-equivalence checks |
| Caller convenience | Combine changes in one owned value or one explicit partial mutation | Separate helpers can append commands for the same root without combining them first |

The accepted contract is A for v1. It preserves atomic writes across different roots
while keeping one final submitted mutation per root. No numerical speed/memory
advantage or production implementation is claimed. Input validation must identify
duplicates under schema primary-key equality, report an explicit input error and
leave all application data/revisions unchanged. Exact SDK error shapes and bounded
validation storage remain implementation work.

## Cancellation at the commit boundary

**Layer: completion of a .NET write call on the master, including an explicit
WriteBatch and calls forwarded to the master. Accepted on 13 November 2026 for v1:
option A.** Honor caller cancellation before dispatch to storage; after dispatch,
finish the operation within the engine's finite failure deadlines and report the
actual outcome. Durable success still requires commit plus local publication,
uncertain outcomes remain distinct, and caller input stays stable until completion.

The boundary is the coordinator's synchronized handoff of a
prepared foreground group for storage execution. MemoryOnly has the corresponding
handoff to publication. This is an engine state transition, not evidence that
PostgreSQL has already committed. Exact state names and SDK signatures remain
implementation work; cancellation cannot race a reported non-execution with later
dispatch of the same attempt.

### A. Cancel before dispatch; complete normally after dispatch — accepted for v1

- While the operation is definitely queued/preparing and has not crossed the
  boundary, honor cancellation by withdrawing it, cleaning up its preparation
  and reporting confirmed non-execution. An explicit WriteBatch is withdrawn as
  a whole. Complete the call only after its mutable input is no longer retained.
- After the boundary, do not use one caller's cancellation token to interrupt the
  shared storage transaction or local publication. Finish the ordered engine path
  and report its actual success, failure or Unknown outcome. Other ordinary writes
  grouped for PostgreSQL storage are not cancelled by that caller.
- Keep storage/engine failure deadlines finite and separate from caller cancellation;
  this is not an indefinite wait or a promise that an I/O timeout proves rollback.
  An unresolved commit follows the accepted operation-status and ordered-stage
  rules. No automatic retry or child-acknowledgment wait is introduced.
- If commit is known, complete publication or follow the accepted instance-failure
  recovery path. A late cancellation must not turn a committed write into a
  confirmed not-executed result or leave an explicit batch partially published.

The caller may therefore request cancellation and still receive normal success
when the write crossed the boundary first. Cancellation before dispatch remains
effective. Loss of a forwarding connection can still hide the result; operation
tokens resolve that existing uncertainty and do not make transport cancellation
an authoritative rollback.

### B. Return early from the call after dispatch — not selected

Honor late cancellation as an early end to the caller's wait. If no definitive
result is available, report Unknown with the existing operation token, then let
the engine settle the already-dispatched group and publish confirmed work. This
must not be reported as confirmed non-execution or abort unrelated group members.
The engine must have detached all caller-owned mutable input before returning.

This reduces cancellation response time for the caller, but adds another early
completion path, state-resolution races and possible status lookups. It does not
remove the pending operation or release its prepared resources immediately.
Both options already need bounded unresolved-operation handling after I/O failure;
B additionally enters that detached-call lifecycle because the caller cancelled.

| Effect | A — accepted | B — not selected |
| --- | --- | --- |
| Reads and successful uncancelled writes | Existing paths and guarantees | Existing paths and guarantees |
| Late cancellation response | Wait for the engine path's result or its own failure deadline | May return Unknown earlier, requiring later result lookup |
| Temporary memory | Prepared work remains bounded until settled; the call continues waiting | The same work may outlive the call; early return does not release it |
| Complexity | One normal completion path after dispatch, plus existing failure handling | Additional early completion, input-detachment and status races |

No timeout value or measured performance advantage is selected. This decision does
not change duplicate-identity validation, atomicity, operation-token retention or
the accepted cancellation of definitely queued requests.

## Remaining Q13 work

Operation identity and bounded retained results are accepted above. Repeated scalar
targets inside one explicit partial mutation and duplicate root identities inside
WriteBatch are also settled: both reject the affected mutation/batch before commit.
Neither rule changes safe same-token resubmission of an original operation.

The later [Binary field decision](schema-and-types.md#binary-fields) accepts whole-value
replacement through Save or explicit field assignment. It preserves the same input
lifetime, root atomicity and memory-admission rules; it adds no byte-range patch commands.

Save, separate explicit partial updates and stable inputs until asynchronous
completion are accepted. Local assignments before submitting a final value remain
unrestricted by duplicate-scalar validation. Caller cancellation follows the accepted
dispatch boundary above. Conditions
beyond the accepted revision check, automatic resubmission, remaining validation failures
and exact API/encoding shapes stay unresolved. Primary-key mutation, dedicated
arithmetic commands and automatic change tracking are excluded in v1. Public borrowed
views remain read-only.

[Remaining mutation details](../design/questions/mutation-commands.md) keep Q13 partially
open. Active push of binary logical changes is accepted separately, with full values
for full writes and explicit partial effects, without invalidation or automatic Save
diff. Q08.1.2 accepts local reads during lag/disconnection and explicit progress checks.
Q08.1.3 accepts bounded resume/full reload and one arena with bounded read waiting.
MemoryOnly child restart is also accepted. See [question register](../design/questions.md#current-discussion), without closing the
outstanding mutation contracts.

The existing fixed-field prototypes are limited evidence, not a general mutation
implementation or proof of its performance and recovery guarantees.
