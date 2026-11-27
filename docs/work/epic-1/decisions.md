# Open questions

Everything raised while planning was answered by the owner on 14 November 2026; the
answers are recorded below and the sprints are written to them. The first
independent review raised seven questions, which the owner answered on the same day. The
second review raised two more, P8 and P9, and the fourth one, P10; the owner answered all
of them, and none is open. A question that
appears later goes to the owner, as the [development process](../process.md) says; it is not
settled inside a sprint.

## Answered after the fourth review, 14 November 2026

**P10. Answered on 14 November 2026: an edit that states nothing new is made in the commit
that needs it.** A sprint that removes a member, changes the form of a descriptor or
deletes a class forces edits on existing tests, samples and code that state nothing new:
the same use under another name, the same descriptor in another form, a helper that names
another class. Three reviews found such edits assigned to nobody, or to a step in which
they could not pass. Whoever makes the commit that would otherwise break makes the edit
in that commit, in tests and samples as well; an assertion is never changed this way,
and the reviewers check exactly that. The [development process](../process.md#a-sprint-on-the-full-track)
says so, and the briefs of S02, S03 and S12 name each such edit. *Not chosen:* skipping
the affected tests until the implementation, which leaves a sprint without its tests and
still does not let the contract commit build.

## Answered after the second review, 14 November 2026

**P8. Answered on 14 November 2026: the node program and the measurement program may reach
inside the engine.** Exception X3 covers them with the three test projects, under the
same condition; neither is ever shipped. The node program arms fault points inside its
own process, so that a test can kill a master at a chosen point. The measurement program
opens the engine before S12, when only the internal entry exists, and reads the counters
of the engine before S39 makes them public. *Not chosen:* producing the figures of the
first sprints from tests, which would put measurements into a test lane.

**P9. Answered on 14 November 2026: an index may name a field inside an owned value.** A
part of an index key is a field of the entity type or a path from such a field through
owned values to a field inside them; a path never goes through a List, a Map or a Set,
because indexes over the members of a collection are outside the milestone. When an
owned value on the path is absent, that component of the key is null, with the accepted
rules for a null component: the entity is found by a search for null, and a unique index
does not compare it. The records of unique indexes and of queries already describe this
behavior; the descriptor and `LatticeIndex` get the form in S03, the storage in S18.
*Not chosen:* indexes over fields of the entity type only.

## Answered after the first review, 14 November 2026

**P1. Answered on 14 November 2026: contracts stage by stage, put into code by a contract
author.** The planner writes the types and members into the briefs of a stage before that
stage begins, and the owner approves the contract of each stage before its first sprint.
A contract author, a role of its own, puts the contract of a sprint into code as the
first step. The [development process](../process.md#the-plan) now says so. *Not chosen:*
writing the whole public API of the milestone into the briefs before the plan is
approved. The types and members of the first stages, sprints S02 to S12, are written
into their briefs below and are approved with the plan.
*Note of 21 November 2026:* the contract author is no longer a role of its own; by the
owner's word of that day the implementer of a sprint puts its contract into code in step 1
of the [development process](../process.md).

**P2. Answered on 14 November 2026: sprints that join several behaviors are split.** The
first plan had 32 sprints. Seven of them were split in two after the first review and
three more after the second, which gives 42; the order did not change. With the same answer the owner decided that every sprint, on
either track, has an independent review, and that review and fixes repeat until a review
finds no P1, P2 or P3; the [development process](../process.md#review) says so, and it holds
for this plan as well.

**P3. Answered on 14 November 2026: children load in parallel.** The starting value of
the milestone contract, one capture at a time, would not let ten children that start
together become ready within the 30-second open timeout, and the owner withdrew it.
Captures run in parallel; their number at once is an option, 10 by default, and a child
beyond it waits. Each capture has its own start position, fence, watermarks and
readiness position. The open timeout stays one timeout that counts everything from the
open call, the wait for a turn included. The decision is entered in the
[milestone contract](../../design/milestone-1-engine-contract.md#replication-transport-and-initial-loading)
and in the [Q08 record](../../decisions/replication-and-hosting.md). The bootstrap
experiment covered one capture only, so several at once are verified in S11, S33 and
S35.

**P4. Answered on 14 November 2026: the three test projects of the engine may reach
inside.** Exception X3 covers `Moltaro.Lattice.Engine.Tests`,
`Moltaro.Lattice.PostgreSql.Tests` and `Moltaro.Lattice.Cluster.Tests` under one
condition: each such test states an invariant written in a decision record or in the
engine contract. It covers fault points inside the engine that only a test can arm,
reading the provider's tables, and damaging a connection on purpose. Fault points are
internal, off unless a test arms them, and not reachable through the public API. With
the same answer the text of principle 11 reads "No other shipped assembly references
those two packages": the measurement program uses the index package for the managed
reference, and the provider tests use the PostgreSQL driver to create and drop their
databases. Both texts in the brief of S01 are the approved ones.

**P5. Answered on 14 November 2026: one store, one master, enforced.** The question was
whether the epoch check also covers the background transactions of a superseded master.
The owner replaced the epoch decision as a whole: see O9 below. Every transaction that
changes the store, foreground or background, checks the leadership number, so the gap
the review found does not exist in the new rule.

**P6. Answered on 14 November 2026: a restored copy is the procedure's to activate.** The
content of a restored PostgreSQL database is identical to the original, so the engine
cannot tell it from an ordinary restart; opened without activation it continues the old
history. The accepted design leaves this to the restore procedure, and the owner keeps
it so: the procedure says plainly that a restored copy is activated before it is opened,
and S36 exercises it. *Not chosen:* a marker that infrastructure sets on a restored
target, which moves the same human error one step earlier and is new scope.

**P7. Answered on 14 November 2026: migration is for a Durable master only.** A MemoryOnly
master has no saved data to convert and loses its data whenever it closes; to change its
schema an application closes it and opens it with the new one. The migration call on a
MemoryOnly master is refused with an error that says so. *Not chosen:* converting in
memory, which would be a mechanism of its own without a saved copy to fall back on.

## Decisions of the owner, 14 November 2026

Each entry says what was decided, what was not chosen, and which sprints it shapes.

**O1. Three assemblies.** The database is three shipped assemblies, each depending only
on the one before it. `Moltaro.Lattice` is the public contract and what needs no engine:
options, errors, the schema and its compiler, the access interfaces and values; it has no
package dependency. `Moltaro.Lattice.Engine` holds arena, indexes, coordinator,
replication, MemoryOnly and the entry point `LatticeDatabase`, which keeps its namespace;
it owned `Arc.Collections`, pinned to exactly 2.2.0, the version the evidence was measured
on, until the owner removed the unused reference on 18 November 2026 (S13). `Moltaro.Lattice.PostgreSql` is the Durable provider and owns `Npgsql`; a child never
loads it. Dependency injection references the engine, because it opens the database; the
Identity package references only the contract. The options take the storage as an object
of the provider package, and `StorageConnectionString` is removed. *Not chosen:* the
engine inside `Moltaro.Lattice` with `Arc.Collections` as its single dependency. Principle
11 of `AGENTS.md` is reworded in S01 with the approved text. Shapes S01, S12, S27.
*Changed by the owner on 27 November 2026:* a storage provider depends on the contract,
not on the engine, and implements the public provider contract of
[Q09.6](../../decisions/persistence-backends.md#a-public-provider-contract-and-a-second-provider);
`Moltaro.Lattice.SqlServer` is a second provider. Principle 11 says so. Shapes S27–S32,
S31S, S36, S37.

**O2. The placeholder is frozen and leaves in S12.** Nothing new is implemented in it; a
declaration it cannot honor is refused on write, as a unique index is today. In S12 the
engine does everything the placeholder does, the entry point switches and the placeholder
is deleted in the same change; no switch between two implementations remains. From S06
to S12 the engine's tests open it through an internal entry of the engine assembly and
then use only the public interfaces; this is exception X4 of the
[exceptions register](../exceptions.md). *Not chosen:* implementing index
search, unique indexes and the batch in the placeholder so that Epic 2 could start
earlier.

**O3. The full type model before PostgreSQL.** The order is that of
[dependency order](README.md#dependency-order). *Not chosen:* PostgreSQL directly after the batch,
which gives durable data sooner but builds the journal, the applier and the operation
fingerprint for complete writes first and reworks them for ordered collection commands,
and measures the memory gate near the end.

**O4. Test lanes enabled by environment variables.** As in [test lanes](test-lanes.md#test-lanes).
The PostgreSQL job and the cluster job become required checks of `main` when their lanes
exist. *Not chosen:* tests that fail when the service is absent; test projects outside
the solution.

**O5. Engine mechanisms are tested directly.** `Moltaro.Lattice.Engine.Tests` may test
internal types of the engine; each such test states an invariant written in a decision
record or in the engine contract. For such a sprint the brief names the internal types under test, and the contract step
puts them into code, documented and refusing, so that the tests are
still written first and by another agent. This is exception X3 of the
[exceptions register](../exceptions.md); `AGENTS.md` gets the approved text in S01. The
answer to P4 above extends it to the provider tests and the cluster tests.
*Note of 21 November 2026:* by the owner's word of that day the tests are still written
first, in step 1 of the [development process](../process.md), now by the implementer of the
sprint; the independent view moved to the reviewer and the breaker.

**O6. Protected views in both forms.** A view by name through field handles, and a typed
view with properties for an entity class. The typed view needs code written for each
class at build time, so a source generator is delivered as an analyzer project of its
own, which owns the Roslyn dependency at build time only. Typed copies keep accessors
compiled once when a class is bound. Nothing else depends on the generator; leaving it
out later would be a decision of the owner about the scope. *Not chosen:* views by name
only. Shapes S21–S22.

**O7. A child's open has a finite timeout.** It is an option, 30 seconds by default; a
child that is not ready in time fails with `Unavailable` and is closed. The text of
`ILatticeDatabase.OpenAsync` that lets a child wait without end is replaced. Shapes S10.

**O8. The engine does not protect the replication port.** The handshake binds database,
history and schema and authenticates nobody. That the port is reachable only from the
nodes of the database is a precondition of deployment, stated in the product
documentation in S10. *Not chosen:* a shared secret in the handshake; TLS inside the
engine. What a node therefore trusts of a peer is decision O21.

**O9. One store has one master at a time, and the engine enforces it.** The owner first
chose to detect a superseded master by an epoch, and on the same day replaced that with
a stronger and simpler rule. A Durable master takes the leadership of its store when it
opens — a session lock in PostgreSQL, held while the instance is open — and a second
master that opens the same store is refused at once and takes nothing over. When the
master closes or its process dies, PostgreSQL frees the store. So that the rule also
holds for a master that lost its leadership session while its process lives, taking the
leadership advances a leadership number, and every transaction that changes the store —
commit, base application, cleanup, receipt retirement, migration — commits only when
that number is still its own. The master checks its leadership session at an interval
that is an option (of the database since S27 places what the engine applies there),
5 seconds by default; when the session has ended it
pauses writes and tries to take the leadership again within the storage recovery wait; it
goes on only when it holds the lock again and the leadership number is still its own, and
faults otherwise, so that it never continues over writes it has not seen. It is exclusion, not election: the engine appoints nobody.
This changes the accepted design, which left the case to the host; it is requirement R35
and was entered in the
[milestone contract](../../design/milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary)
and in the [Q15 record](../../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection)
on 14 November 2026. Shapes S27, S28–S29, S30–S31, S36, S37.

**The design reference follows these decisions.** On 14 November 2026 the decisions that
change or complete the design — O7, O8, O9, O10, O11, O12, D1, D3, D4, D6, P3, P7 and P9 — were
entered in the milestone contract, the operation retry protocol, the performance
requirements, the architecture page and the decision records they touch. The briefs
below point to those documents, not to this list.

**O10. The schema descriptor.** An index has a required stable `Id`. The top array is
`EntityTypes`, not `Tables`. An index has no `Kind`. Shapes S02, with the Identity
package's descriptor and `README.md`.

**O11. Where and how the gates are judged.** On one reference machine, the owner's
workstation; that is enough to close the epic, and a network-separated run is not
required. The master, every child and PostgreSQL run as separate containers with declared
limits of CPU and memory; the manifest records that the containers run under a virtual
machine of the workstation. Before a gate run the measurement program checks the state of
the machine — enough free memory for the sum of the limits, a quiet processor for a
stated time, no foreign containers — and does not start when the check fails. Sixteen
concurrent write callers; 2 minutes of warm-up and 10 minutes of measurement for the
ordinary profile; readiness measured 5 times, every run within the target; PostgreSQL 18
as the only tested and stated version. S25 writes the manifest of the single-node runs
and S40 the manifest of the topology; the owner approves each before its first gate run.

**O12. Two limits the design did not give.** A MemoryOnly master keeps operation outcomes
without a limit of their own, by the owner's decision O20 of 15 November 2026: they are
counted in the instance budget, and an unexpired outcome is never dropped (the limit this
decision first gave them, the smaller of 32 MiB and 10% of the instance budget, is gone).
The time limit of a traversal, when an application sets one, counts only time inside the engine, not the
time the caller spends between two items. Shapes S26, S08.

**O13. Code shared by the test projects has a project of its own.** Decided while S01 was
reviewed. `Moltaro.Lattice.TestSupport` is a library that is never shipped and holds no
tests; a test project references it for code that two or more test projects need, and
such code is not copied between them. The review of S01 found a helper of the dependency
tests in two test projects; two more helpers were already copied between the tests of the
core and those of dependency injection; and the tests of S22, S27 and S35 will need the
same code again. *Not chosen:* creating the project when a third copy appears; a
duplicate that is not removed when it is seen stays. `AGENTS.md` gets the approved rule
in S01. Shapes S01.

**O14. The coordinator pushes and merges closed sprints.** Decided after S01. The owner
gave the coordinator of this epic a standing permission: when a sprint is closed and no
decision of the owner is open, the coordinator pushes its branch, opens the pull request,
squashes it into `main` once every job that CI runs for the pull request has passed, and
goes on to the next sprint; it reports after the merge. Every pull request is squashed,
also that of a sprint on the full track. The series contract, tests, implementation,
fixes is then no longer in the history of `main`: after the merge it is visible in the
pull request on GitHub only. What the coordinator stops for is otherwise unchanged and is
listed in the [development process](../process.md#when-to-stop-and-ask-the-owner). A release
tag, the rulesets and the settings of the repository stay the owner's. *Not chosen:* the owner merging every sprint by hand, which made the owner a
relay between two steps that need no decision; a merge commit for a sprint on the full
track. [Branches and releases](../branches.md#who-does-what) has the rule. Shapes every
sprint from S02 on.
*Note of 21 November 2026:* by the owner's word of that day, a coordinator no longer goes on
to the next sprint after a merge: it stops at the boundary, and a new coordinator session
takes the next sprint, as the [development process](../process.md#new-sessions-at-a-sprint-boundary)
says.

**O15. A .NET enum that does not agree with the schema is refused, as a class is.**
Decided while S02 was carried out; the question is Q1 of the
[open questions](../open-questions.md). A property of a bound class may be a .NET enum. That
enum has to agree with the enum type the schema declares for the field: the same
underlying type, the same members — every name with its value, in both directions — and
the Flags mark. An enum that does not agree is handled exactly as a class that does not
agree with its entity type: the binding fails with `BindingFailed` and names the
property. Nothing is mapped, guessed or tolerated, and a schema that changed without the
class is found when the class is bound, not when a value is read. *Not chosen:* leaving
the rule to the implementation; accepting an enum that names a part of the declared
values. Shapes S02.

**O16. A small change goes into the branch of the sprint that is open.** Decided while
S02 was carried out. A change that comes up during a sprint and is small — an answer of
the owner that touches a rule, a document, a file of the repository's settings or the
brief of a later sprint; a sentence of documentation — is made in the branch of that
sprint and merged with it, and gets no branch and no pull request of its own. It is
named in the brief of the sprint or in the [open questions](../open-questions.md) with the
answer that caused it, and it is reviewed with the sprint like everything else on the
branch. The light track stays for a change that is made while no sprint is open, or that
cannot wait for the open one. *Not chosen:* a branch, a pull request and a review round
of their own for every such change, which cost the owner a merge each and bought nothing.
Shapes every sprint from S02 on; S02 carries, besides its own work: the register of open
questions with its rule in the development process; the rule that review rounds are not
limited by a number, and this rule on small changes, both decided by the owner while the
sprint was open and named here; the squash in the ruleset of `main` (Q2); the sentences
"Not implemented yet" in the documentation of unique indexes (Q12) and the completed
documentation of the write batch and of `Set<TEntity>()` (Q6); and the texts that the
owner's answers added to the briefs of S03, S04 and S37 and to the document of Epic 2
(Q3, Q4, Q5, Q9, Q10, Q11).

**O17. The default of the instance memory budget is computed.** Decided on 15 November
2026 and given through the planner's session, when the owner learned that the limit of the
arena is required. The budget is an option whose default is half of the memory the .NET
runtime reports as available to the process when the instance opens: the physical memory
on a machine without limits; in a container by default three quarters of the container's
limit; a heap hard limit configured for the garbage collector lowers it. It is enforced
as Q14 accepts. An application that prefers to run into swap sets the budget above
physical memory. *Not chosen:* the fixed 256 MiB, far too small for real data; all of the
memory; no limit unless one is set. Shapes S09; it rode in the branch of S05 under O16,
with one sentence of the engine contract and a note in the decision on write admission.
The owner's answer to Q37 of the [open questions](../open-questions.md) corrected the source
after the review of S05: the decision first read "the limit of its container, or the
physical memory of the machine", which the runtime does not report, and the owner chose
the runtime's figure, said plainly, over platform code of the project's own.

**O18. A write batch has no limit of its own.** Decided on 15 November 2026 and given
through the planner's session. The owner first asked what the limit of 128 entities was
for and called it excess configuration, then refused a size limit as well, fixed or taken
as a share of the memory budget: the size of a batch is the application's choice, and the
task of the database is to store it atomically. A batch has no limit on the number of its
operations and none on its size, and no option for one; it is refused only when a
resource cannot hold it — the memory budget of the instance, the hard limit of the
journal, when one is set. The rule that follows: a bounded buffer bounds what accumulates and never
refuses one transaction for its own size; what a transaction needs is reserved in the
instance budget before it is committed, and one the budget cannot hold is refused before
it has any effect. *Not chosen:* 128 operations and 8 MiB by default; a size limit derived
from the budget. Shapes S09 and S17 and the contracts of the later stages; it rode in the
branch of S06 under O16, with the engine contract, the decision on the document model,
the architecture, `README.md` and the documentation of `ILatticeWriteBatch`. Principle 5
of `AGENTS.md` called a batch "bounded"; the owner approved the sentence without those
words with decision O19.

**O19. An entity has no size limit of its own.** Decided on 15 November 2026 and given
through the planner's session, after decision O18 and for the same reason: how large an
entity is, is the application's choice. The 1 MiB starting limit of the engine contract
and the option that was to carry it are removed; an entity is refused only when the
memory budget of the instance cannot hold it. One String or Binary value stays bounded by
the storage format at 268,173,376 bytes, the capacity of one directory block of chunks: a
fixed rule, not an option, and a value above it is `ValidationFailed` with the field
named. *Not chosen:* keeping the option without a default. Shapes S06 and S09, reaches
back into S05, whose `RootWriter` loses `maxEntitySize`, and forward into every brief
that sized a buffer or a test by "1 MiB per entity", which the planner words with its
stage. With the decision the owner approved principle 5 of `AGENTS.md` without the words
"which is bounded"; the coordinator made that edit on the owner's own word in its
session, given on the same day, because `AGENTS.md` is the instruction file of the agents.

**O20. No default turns ordinary load into a refusal.** Decided on 15 November 2026 and
given through the planner's session, as the general form of O18 and O19, and confirmed to
the coordinator for `AGENTS.md`. A limit exists only where the engine cannot work without
it or a resource cannot hold the work; the database does not cap what an application
chooses to do, and where it can it waits or slows down instead of refusing. An
application that meets an error in production has a problem, and one that meets slowness
changes how it writes. Three limits lose their limit altogether: the queue of writes
(1,024 requests and 16 MiB), the read budget as a default (8 MiB and 5 seconds) and the
memory of retained outcomes on a MemoryOnly master (32 MiB); what they held is counted in
the memory budget of the instance. Two keep an option that is not set by default: the
hard limit of the journal and the budget of the receipts, both 1 GiB before; without a
hard limit the time of a recovery grows with the unapplied tail. The read budget stays as
an option an application may set. *What stays as it is:* the memory budget of the
instance and its refusal (O17, Q14); every timeout of a wait, because a wait has to be
finite and its error means a real failure; the rules of the schema language; the bound of
one value in the storage format; internal tuning that never refuses; the limits of
delivery to children, which cost a slow child a reload and an application nothing. Shapes
S08, S09, S17, S26, S31 and S32; principle 9 of `AGENTS.md` carries the rule. Gate G6 was
written for a queue that refuses: the planner words it again with the manifest of S41 and
brings it to the owner then.

**Questions that do not block are written down.** A gap that a sprint finds and that no
brief, decision or rule answers is entered in the [open questions](../open-questions.md),
with what the code does in the meantime. The coordinator names the new entries when a
sprint closes, and all of them are gone through with the owner when the epic closes.

**Options, not constants.** A timeout or a limit that an application can observe is an
option with a default, validated at open. The owner added this to principle 9 of
`AGENTS.md` on 14 November 2026; every brief below follows it. By the owner's answer to
Q28 of the [open questions](../open-questions.md) it speaks of a running instance: a rule of
the schema language, such as the depth of a declaration, is fixed and the same in every
process, and principle 9 says so since S03.

**The exceptions register.** A departure from a rule of `AGENTS.md` is recorded in
[exceptions to the rules](../exceptions.md) with its reason: entered when the owner approves
it, and moved to **In force** by the change that introduces it.

**O21. Values received from another node stay trusted.** Decided on 18 November 2026,
when an outside reviewer called it a P1. The planner asked whether values received from
another node stay trusted or a validator on receipt is added; the owner, as the planner
relays, answered that they stay trusted and that it is written into the decision, so that
other reviewers see it and it does not keep coming up. A node checks the structure of everything it receives and
does not validate the values of fields again; the decision, its consequence and what
remains a defect are in
[trust between the nodes of one database](../../decisions/replication-and-hosting.md#trust-between-the-nodes-of-one-database).
*Not chosen:* a second validator over received bytes, on the master under the lock of
the writes and for every root of a load.

## Where two design documents differ

A comparison of the design documents on 14 November 2026 found the seven places below.
The owner confirmed D1, D3 and D6, which change what a caller observes or what a gate
measures; for the others the plan names the document it follows.

**D1. Which writes stop during a schema migration.** The
[decision](../../decisions/schema-and-types.md) stops reads and writes of the affected data
and left the exact scope open; the
[milestone contract](../../design/milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration)
pauses all writes, drains the writer and has every child reload fully. *Confirmed: the
milestone contract.* On the master all writes pause and reads of unaffected entity types
continue; on a child every read waits within the reload bound. A migration is planned as
a stop of writes for the whole database. Shapes S37–S38.

**D2. Journal triggers.** The milestone contract starts with 64 MiB or 5 seconds of
unapplied journal and, by the owner's decision of 15 November 2026, no hard limit unless
an application sets one; the
[journal decision](../../decisions/checkpoint-and-journal.md) and the historical part of the
performance requirements name 256 MiB and 30 minutes as a trial value that was not
approved. *The milestone contract prevails*; S31 uses its values and reports how they
behave.

**D3. The position of an empty database.** [Session consistency](../../decisions/session-consistency.md)
treats position zero with a valid history as the initial prefix; the milestone contract
defines a wire position as a positive sequence; the placeholder returns sequence zero for
a new database. *Confirmed:* a new database has a valid position — its history and
sequence zero, nothing applied yet — and the first committed transaction gets sequence
one. A positive sequence is what a commit is given; the initial prefix stays
representable in a session and on the wire. Shapes S06, S10.

**D4. How a revision relates to a position.** Every document keeps the two apart, while
the milestone contract builds a new revision from the position of the operation. *Both
hold:* inside the engine a revision binds history, entity and position; in the public
contract it stays opaque. A caller may compare two revisions of one entity for equality
and nothing else: no order, no conversion to a position, no meaning across entities.
S06 states this in the documentation of `LatticeRevision`; S17, where two entities can
first change at one position, tests that they do not share a usable revision.

**D5. "v1" and the first milestone.** Several accepted rows of the
[register](../../design/questions.md) say "v1" for features the milestone defers, such as a
query language with its scan fallback. *The planning handoff prevails:* every requirement
above is taken from it and from the milestone contract, never from a "v1" label.

**D6. The mix of the reference fixture.** The performance requirements ask for one entity
change per write call at 200 changes a second; the fixture reaches 200 with 192
single-entity calls and 4 two-entity batches a second. Its read mix contains 5% range
reads with Limit 20, while the 1 ms target names identifier and exact-index reads.
*Confirmed:* the fixture is the ordinary profile, and each target is judged on the
operation it was accepted for. G3 is judged on the single-entity calls; batches are
reported as their own class and have no threshold. G2 is judged on identifier and
exact-index reads; range reads are reported as their own class against the same figure,
and a miss there does not block the epic but is reported to the owner as its own line.
Shapes S25, S40.

**D7. Starting values the records call illustrative.** The 30-second reload wait, the
pages of 4,096 root entries and the 1 MiB cap of an entity are starting values in the
milestone contract and "not accepted defaults" in their decision records. *The milestone
contract prevails as a baseline to measure.* They are verified where their mechanism is
delivered: the root-entry pages in S04, the entity cap in S05 and S09, the reload wait in S34. A value that fails its check is changed on the evidence and reported; no guarantee
changes with it. The cap of an entity was removed by the owner's decision O19 of 15
November 2026: an entity has no limit of its own.

## Choices made in the contracts of S02 to S12

The blocks "Types and members" settle the following points, which no decision record
settles. They are approved with the plan, each by name.

- **The descriptor is closed.** A property the reader does not know is `InvalidSchema`.
  Before S02 such a property was ignored, and a misspelt `Comparison` would silently
  have given the wrong comparison. By the owner's answers of 14 November 2026 to Q7 and Q8 of the
  [open questions](../open-questions.md), the same holds for a property in a place where it
  does not apply — a text policy on a field that is not a String, an enum type named by
  a field that is not an Enum — and for a property given twice.
- **The string encoding of a database is declared in its schema**, as `StringEncoding`.
  The decision makes it a setting fixed when the database is created and leaves its
  place open; in the schema it is saved, loaded and bound into the fingerprint with
  everything else that decides the layout.
- **Identities stay `int` in the public model** and must be at least 1. The milestone
  contract calls them positive UInt32; the engine keeps them as UInt32, and every value
  an `int` can hold from 1 up fits.
- **The value of an enum member is `Int128`**, the one type that holds every value of the
  eight integer types without reinterpreting bits.
- **A fingerprint covers what decides layout, comparison and constraints**, and not
  names, `ClrType`, the catalog version or the order of the descriptor; the list is in
  the block of S02.
- **In a bound class a collection is `List<T>`, `T[]`, `Dictionary<K, V>` or
  `HashSet<T>`.** Interface types are refused for now and can be added without breaking
  anything.
- **A read budget is two options of the database**, `ReadMemoryLimit` and `ReadTimeLimit`,
  none set by default; they hold for every enumeration and, from S15, every search, with
  one error code and the exceeded limit named in `Limit`. No call carries limits of its
  own (the owner, 16 November 2026).
- **An address is `lattice://host:port`**, and `ILatticeDatabase.ListenUri` reports the
  address a master actually listens at, so that port 0 can be used.
- **A field is its identity, its name and its declaration.** `LatticeField` loses `Type`,
  `IsNullable` and `ValueType`, which `Declaration` now holds; `LatticeIndex` loses
  `Fields` in S03, which `Parts` replaces. Nothing is released, so nothing is kept twice.
- **Two fingerprints.** `LatticeSchema.Fingerprint` binds a child to its master and
  covers layout, comparison and constraints; `LatticeEntityType.LayoutFingerprint`
  covers how one entity type is stored and tells a migration which types to convert.
- **The identity of a database is a `Guid`**, and the wire protocol starts with the magic
  `LATT` and version 1. The design gives neither a value.
- **The reference fixture has a scalar form** for the sprints that cannot store its
  collections, Dictionary strings and unique indexes yet. The gates use the full form
  only.
- **What the placeholder does with what is added before S12.** A new option is validated
  and otherwise not used by the placeholder, and its documentation says so until S12. A
  new member the placeholder cannot honor throws `NotSupportedException` there, as
  everything not implemented does: the enumeration with limits of S08. `ListenUri`
  returns the configured address, which is the truth for the placeholder.
