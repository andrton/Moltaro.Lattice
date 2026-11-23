# S17 · Atomic write batch: the internal contract

A working document of [Epic 1](epic-1-core.md), sprint S17, written at the end of step 1
for the planner to read before step 2; it is removed at the close of the epic, as the
working documents are. The brief is the section "S17 · Atomic write batch" of the epic
document; the study and the planner's notes it names are outside the repository. What is
not decided here is a question at the end, not a decision.

## Step 1

Three commits, the tests before any implementation:

- `8776aa8` the stubs below;
- `6f5b7a6` the tests through the public API, in `src/Moltaro.Lattice.Tests/ReadsAndWrites/Batches`,
  where `WriteBatchTests`, `WriteBatchBuildingTests` and `WriteBatchEntityTests` moved with no
  assertion changed;
- `dcfd4d2` the tests of engine mechanisms, in `Engine/Batches/{Visibility,Waits,Memory}`,
  `Engine/Indexes/Reservation`, `Engine/Replication/{Batches,LoadingUnderWrites}` and
  `Replication/{Forwarding,Handshakes,Transactions}` of `Moltaro.Lattice.Engine.Tests`. It is the
  last commit of step 1 with code; this document follows it.

At the end of step 1 a test that needed the commit of a batch was skipped with one shared reason,
and one that needed only a mechanism of the engine with another. Both are gone: the second with the
commit of a batch on a master (`ea67085`), and the first became `SkipReason.BatchThroughAChild`
(`73596ac`), which skips only a batch committed through a child. Active and passing at the end of
step 1: the
linear bounds against today's tree, the two traces' load test, a child that refuses a value of a
published owner no effect frees, a transaction of 200 effects that names its first key at its end,
every pair of kinds of a repeated identity, an empty commit refused at the call, and the new
assertion of `MalformedTransactionTests`.

## Stubs the tests address (NotSupportedException)

- `EngineDatabase.ArmPublicationSwitch(PublicationPointEnum point, Action action)`: the next
  publication that passes the place runs the action once, on the publishing thread inside the lock of
  the writes, on a master and on a child; an exception of the action fails the publication as a fault
  point does. Test helper: `HeldPublication`.
- `Entities/PublicationPointEnum`: `AfterNewEntries`, `AfterFirstChange`, `BeforePosition`,
  `AfterPosition`.
- `Indexes/NodePool`: `static long Bound(int height, int inserts, int removes)`,
  `static long PlacesBound(int height, int inserts, int removes)`. Built in part 1, with counts of
  `long`, since the ledger charges every change since its base.

## Internal types and members of step 2

The study's names are kept unless a line says otherwise.

Point 1, L1 with G6 and F3:
- `ChangePublisher.TryReserve(Span<PreparedChange> changes, bool checksUnique, out TakenValue taken)`
  replaces the form of one change: every change decided first (its masks), a taken value found on the
  state the span leaves, then each index reserves once for the summed counts, then the entity's own
  room. Changed from the study: one `out TakenValue` in place of `out int failing, out int index`,
  since the coordinator needs the owner as well to find a write that frees it.
- `Entities/TakenValue(int Change, int Index, RootHandle Owner)`: the failing change in the span, the
  place of the unique index, the published owner (default when two changes of the span claim the value).
  New name.
- `PreparedChange.TakenIndex`, `TakenBy` and `PreparedWrite.Taken` go; the change is 96 bytes again.
- `ChangePublisher.Release(ReadOnlySpan<PreparedChange>)`, `TopUp(ReadOnlySpan<PreparedChange>)`: by
  the summed counts of each index, once.
- `SecondaryIndex.TryReserve(int inserts, int removes)`, `Release(int inserts, int removes)` replace
  the forms that take an `IndexChangeEnum`; `CountsOf` is summed by the publisher.
- `Indexes/NodePool` (G6), owned by `OrderedTree`: `TryReserve(int inserts, int removes)`,
  `Take()`, `Release(int inserts, int removes)`, `TopUp()`, `GiveBackReserve()`,
  `AfterPublication()`, and the two bounds; the ledger (budget charged since the base, nodes taken);
  the reserved nodes linked through their own payload, so no managed collection (F3).
  `OrderedTree.TryReserve(int, int)` and `Release(int, int)` keep their signatures and call the pool,
  since the tests of the tree address them. As built, every member of the pool but the static ones
  also takes the height of the tree and the room of its root; `Take(out Span<byte> payload)` gives the
  payload it read the link from, and `TakeInsert`, `TakeRemove` count what a publication takes;
  `AfterPublication(int retired, ...)` the nodes it retired; `Need` and `Growth` moved from the tree.

Point 2:
- `EntityTable.TakeEntry(ref RootEntry, ReadEpochs, PositionClock, CancellationToken, out ...)`: the
  clock is loaded right after the first load of the stamp; a stamp of 0 or above that clock backs off
  as the mark does, looks at the state of the instance and at the token, and tries again; the clock so
  loaded is the position of the answer, so `Math.Max(at, stamp)` becomes `at` at its three places. No
  load is added to a read by key: the clock was loaded for the answer already.
- `ChangePublisher.PublishBeforePosition(ReadOnlySpan<PreparedChange>, ulong stamp)`: the new index
  entries of every change; then for every change the stores of its entity with the final stamp (an
  insert: root, stamp, Live, primary entry; a replace: mark, root, stamp; a delete: mark, Deleted,
  stamp). `PublishPositionAndAfter`: the position, then removals, old entries and retirement.
- The search: an entry whose root entry is Free or Prepared is passed over before its stamp is
  taken; a Deleted one is taken by its stamp. See question 4 for a step that waited. As built (B1, form
  C): no entry is passed over before its stamp is taken; the root entry of an insert not stored yet holds
  the mark, and the take waits through it.
- The capture (`Capture.TryCopy`) passes its token to `TakeEntry`.

Point 3:
- `Entities/UniqueClaims`: for each unique index of a prepared span, the changes that set a value,
  sorted by the order of the index, with the published owners the span frees; `FindClaimant(in
  PreparedChange change, int index)` and `FindFreeing(RootHandle owner, int index)`, each returning the
  change or -1. Changed from the study: `FindClaimant`/`FindFreeing` in place of `Claims`/`Frees`,
  which are the names of the members of the publisher they generalise.

Point 4:
- `PendingWrite.Members` (the operations of a batch), `PendingWrite.Keys` (its `PublicationKeys`),
  `PendingWrite.SharesEntityWith(PendingWrite other)`, `PendingWrite.Holder` (renamed from
  `ValueHolder`); `PendingWriteStateEnum.WaitingForWrite` replaces `WaitingForValue`, keeping its value.
- `WriteCoordinator.SubmitBatchAsync(IReadOnlyList<BatchMember> members, LatticeSession?,
  CancellationToken)`; `FindLastSharing(PendingWrite)` replaces `FindLastOfEntity`; two single writes
  keep the chain.
- `Writes/BatchMember`: the kind, the table, the key, the expected revision and the root of a member,
  as question 8 says where it comes from. New name; the study's `ForwardedMember` is the same record,
  so one type serves both.
- `Entities/PublicationKeys`: a set of (table, key) of one publication by the comparison of the entity
  type; `TryAdd(EntityTable, in EntityKey)`, `Contains`, `Clear`; on the master for a forwarded batch,
  on a child for "each key once".
- `PreparedWrite.Changes`: a span over the one inline change or over the array of a batch.

Point 6: `ReceivedChanges` prepares a transaction into one span, checks it with
`ChangePublisher.TryReserve(span, checksUnique: true, ...)` once the child is Ready, and uses
`PublicationKeys` in place of the scans of `_prepared` and `_skipped`. The tests that already feed a
child a transaction of several effects (`WholeTransactionTests`, `MalformedTransactionTests`,
`KeyNamedOnceTests`, `RefusedChildChangeTests`, `AbandonedIndexChangesTests`, `AbandonedRootTests`,
`ChildNullUpkeepTests`, through `MergedTransaction` or `TransactionPayload.Make`) build it in the form a
master will send for a batch: one transaction at the sequence of the first, the effects in their order,
each Write carrying that sequence. Checked, they stay; should question 1 add the number of a member to
the envelope, their helpers gain it as edits that state nothing new.

Point 7, as the study's addition names them: `Protocol.WireFormat = 5`; `ForwardEncoding.BatchKind`
(4); `WriteBatchHead`/`ReadBatchHead` (minimum position, count as a UInt32 of at least 1);
`WriteMemberHead`/`ReadMemberHead` (kind, type id, expected revision); `ReadMemberRoot` (a root ends
where its length says); `Forwarder.WriteBatchAsync(IReadOnlyList<BatchMember>, LatticeSession?,
CancellationToken)`; `ForwardedCall.EntityTypeOf(int? operationIndex)`; `RequestReceiver.SubmitBatch`;
`WriteCoordinator.Forward(IReadOnlyList<BatchMember>, ForwardedOrigin, LatticeSession?)`;
`ForwardedOrigin` with the entity types of the members, which `Refuse` resolves the index by;
`ForwardEncoding.NameIndex(reply, entityType)` with the type of the failing member, and a refusal of
`UniqueConstraintViolation` to a batch without `OperationIndex`, or with one beyond its members,
refused by the child. The test helper `ForwardPayload.BatchRequest` writes these shapes.

D5: `TransactionFormat.WriteHeader`, `WriteWriteEnvelope`, `WriteDeleteEnvelope`,
`WriteUnchanged`; `TransactionEncoder.Write(DeliveryTail, ulong sequence, ReadOnlySpan<PreparedChange>,
Arena)`: one effect for each change, in the order of the members.

## The order of the commits of step 2

1. L1 with G6 and F3: the span form of the publisher, `TakenValue`, `NodePool` as a type with its
   intrusive list and its ledger, the linear bound and once per index; no change of behavior for a
   single write (question 9). The tests of `LinearBoundTests` that are skipped come alive.
2. Point 2, with TEXT B (X6) and TEXT 3 (`arena-protocol.md`, `write-path.md`, `ordered-index.md`):
   the order of the publication, `TakeEntry`, the publication switch; the merged transaction of 300
   effects on a child and the tests of `Engine/Batches/Visibility` on a child by a relay come alive.
3. Points 3 and 4 with the commit of a batch on a master: `UniqueClaims`, the wait for a write,
   `SubmitBatchAsync`, the revision of a member (question 1).
4. Point 6: the child.
5. Point 7 with D5: the forwarded batch and the wire format 5.

## What the two traces found

**Callers of `TakeEntry` on a child before it is Ready.** `TakeEntry` is called by
`EntityTable.TryCopy` (`TryRead`, `TryReadNext`), `EntityTable.Step` (`TrySearch`, `SearchHasMore`)
and `EntityTable.TryTakeLive` (`Children.Capture.TryCopy`). On a child the first four are reached only
through the sets (`ClassSet`, `RecordSet`, `SetIndex`, the enumerations and searches over
`SetOperations`), and `EngineDatabase.Set<T>()` and `Set(name)` give a set only to a Ready instance
(`ThrowIfNotReady`); a child never leaves Ready but for Faulted or Closed. `Capture` exists only on a
master with a `ListenUri`, which `Validate` refuses for a child. The members of the instance that a
test or the measurement program reaches (`Counters`, `TextReferences`, `IndexNodes`, `DrainRetired`,
the switches) refuse a child that is not Ready, and none of them calls `TakeEntry`: they read under the
lock of the writes. The load and the catch-up (`RootPlacement`, `ReceivedChanges.Place`,
`TryPrepareWrite`, `TryPrepareDelete`, `Apply`, `ValidateUniqueIndexes`, `ChildLoad.PresizeIndexes`)
read root entries under the lock of the writes through `TryFind` and
`RootEntry.LastMutationSequence`, never by the stamp; `ValidateUniqueIndexes` walks the trees only.
So no read runs on a child before Ready, although its roots carry stamps above its position
throughout the load (0 until `EndLoad`, S after it, each transaction's sequence during the catch-up),
and the load waits for no read. Once Ready the child is at R and no root has a stamp above R:
`RootPlacement.End` refuses a root above R, and `Follower.CatchUpAsync` applies up to R before the
state changes. Held by `StampsAboveThePositionTests` (active).

**`Children.Capture` on a master during a publication.** `Register` and `End` take the lock of the
writes, so neither overlaps a publication. `TryCopy` takes no lock: it enters the protection on the
task of the connection, never on the publishing thread (a publication is synchronous under the lock
and yields nothing). A copy during a publication at s therefore began after `Register`, so S < s, and
the transaction at s is in the tail for that cursor. Under the new rule a copy that meets a stored
member backs off until the clock is s and copies its state at s; a member not stored yet is copied in
its state before; a member being deleted is passed over at once, since `TryTakeLive` looks at the
state first. So the copies may hold a part of the batch, but the child applies the transaction at s
after the load by its rule (members it holds at s skipped, the others applied), is Ready at R ≥ s (R is
read in a later hold), and serves nothing before Ready (first trace): at Ready it holds all of the
batch, and a batch published after R none of it. A copy never carries a stamp above R. Its wait ends:
the publication waits for nothing, a fault ends it with Unavailable, and the close of the master ends
it through the token. Held by `CaptureDuringABatchTests` (skipped: it needs the commit).

## Questions

1. **The revision of a member.** The Acceptance wants two members of one batch with revisions that do
   not satisfy a condition on each other, and the engine contract says the revision binds history,
   type and key, and the position. Today it is (history, last mutation sequence), unique only because a
   position changes one entity; two members at s get equal revisions. Neither the brief nor the study
   says how. The revision must be the same on every node (a forwarded write is checked on the master
   against what a child read, and a loaded entity has its master's revision), so the slot of a root
   entry cannot serve. Proposed: the number of the member in its batch, 0 for a single write, kept in
   the four reserved bytes of `RootEntry` beside `State` and loaded with it as one 8-byte word inside
   the window of the stamp (no added load), folded into the high word of the revision; it travels in
   the root envelope of a load and of a Write effect (wire format 5); a child stores the member it
   receives and checks nothing about it (the planner, 22 November 2026). A single write keeps the revision it has. The alternative, an unseeded hash of type
   and key, adds a hash to every read and lets a caller make two members collide. The envelope grows by
   four bytes, which moves offsets in test helpers (`MarkedRoot`, `TransactionPayload`): edits that state
   nothing new, in the commit of point 3. Held by `BatchRevisionTests`.
2. **A batch in which no member changes anything** (for the owner). `WriteBatchTests.Deleting_an_absent_key_in_a_batch_is_not_a_failure`
   asserts that such a batch has the position of the batch before it, i.e. takes none, and the
   Acceptance keeps that assertion. `mutation-commands.md` ("the milestone contract now assigns
   successful no-ops a retained result and stream position"), the engine contract ("absence no-ops ...
   receive a durable result and stream position") and `LatticeWriteResult.Position` ("A write that
   changed nothing has a position of its own as well") say it takes one, as a single delete of an
   absent key does. A decision record contradicts the brief. Recommended: the decision stands, and that
   one assertion becomes `NotEqual` by the owner's word.
3. **The two tests of the contract stage.** `WriteBatchTests.Executing_a_batch_is_not_implemented_yet`
   and `WriteBatchBuildingTests.Write_batch_is_built_on_a_master_and_its_commit_is_not_implemented_yet`
   assert `NotSupportedException`. Recommended: they go in the commit that makes a batch execute, with
   the skip reason and the `NotSupportedException` of the remarks of `ILatticeWriteBatch` ("Nothing has
   been released"), and "every test of `WriteBatchTests`" reads as every other test.
4. **A step of a search that waited.** The study holds a search whole because an insert's entry is
   passed over before its stamp. It misses the replace that moves a key: all new entries are installed
   before any entity is stored, so a step held between the stores of two members passes over the new
   entry of an earlier member whose root is still old, then waits at a later stored member, and after
   the position returns the later and not the earlier, in one step. Proposed: a step whose take of an
   entry waited for the position walks again from where the step began; only steps that meet a member
   pay. Held by `BeforeAndAfterThePositionTests` at `AfterFirstChange` (the replace of 2 is the first
   change, the replace of 1 lies before it in every index). *Decided by the owner on 23 November 2026* (form C, in
   B1 below): one step is not promised to be of one position; a step whose take waited goes on, and the
   walk again that B1 built is removed.
5. **What an enumeration and a search of several steps owe.** Separate reads may combine states from
   before and after the position (decision on the document model). The tests hold, for an enumeration,
   each member in its state after the batch once its session is at s or later and in its state before
   while the session is before s; a pass that began before s may miss a member inserted into a slot it
   had passed. For a search of the deleted members they hold only what one step can see. Is that the
   reading of "seen whole by an enumeration and a search" in the Acceptance? *Decided by the owner on 23
   November 2026* (form C, in B1 below): a search or an enumeration that runs across the publication may
   return some members as they were before it and others as after; no member is visible in its new state
   before the position, and a read that begins at or after it sees every member.
6. **The barrier in two other decision records.** `mutation-commands.md` ("publishes the complete group
   under its database barrier"; "atomic publication barrier" in the table of repeated roots) and
   `unique-indexes.md`, step 4 ("the ordinary root/WriteBatch barrier") still name it. Corrected with
   the commit of point 2, as TEXT 2 corrected `document-model.md`?
7. **The token of a read that waits.** The brief's test wants an asynchronous read that waits for a
   position to end with its token, which `TakeEntry` does not look at today. TEXT B could say "retries
   until the position is published, the instance has faulted, or the token of an asynchronous read is
   cancelled".
8. **Where a batch keeps its members.** The planner's note encodes each member to its root at its
   `Add`, on a master and on a child alike. The core cannot encode a root (`RootWriter` is the
   engine's). Recommended: the core's `WriteBatch` takes one argument from the engine, the encoder of a
   member, and holds `BatchMember`s; the master prepares a batch as it prepares a forwarded write
   (`PlaceForwarded`), so one path serves both, and the encoding happens outside the lock of the writes.
   The cost is a managed copy of every root until the commit, which the record reports.
9. **The standing reserve and the withdrawal.** The tests of S14 hold the standing reserve as the need
   of one change of key by `OrderedTree.Need`; they stay if the reserve keeps that definition and the
   ledger governs only the writes that wait (recommended). Point 5 says a withdrawn reservation gives
   back exactly what it took; `ordered-index.md` says a withdrawal gives back the reserve as well. The
   tests of step 1 read point 5 (the reserve is kept); before a refusal for memory the reserves still go
   back, so after a refused batch the prepared bytes are at most what they were. `UncountableNeedTests`
   computes its boundary from `Need`; with the linear bound its assertions hold, for memory rather than
   for the count, and whether its boundary is recomputed from `NodePool.Bound` is the coordinator's.
   *Decided by the planner:* the reserve keeps its definition, the ledger governs only the writes that
   wait, and a withdrawal gives back the reserve as well, as `ordered-index.md` says; point 5 reads
   "gives back what it took, and the reserve as `ordered-index.md` says". `UncountableNeedTests` keeps its
   boundary from `Need`. Part 1 found two tests of step 1 that assume the reserve kept (below).
10. **The bound of the places.** The study's sketch, q(h + 1) + p, cannot hold: an insert that splits
    retires one node for every level it copies. `LinearBoundTests` holds p(2h + 3) + q(h + 2 + log2 p)
    on today's tree (an insert retires no more than it takes; a remove its path and a neighbour, at a
    height p inserts raise by at most 1 + log2 p); proposed for `NodePool.PlacesBound`. *Decided by the
    planner:* `NodePool.PlacesBound` is p(2h + 3) + q(h + 2 + log2 p). The study's sketch q(h + 1) + p
    was found not to hold, for the reason above: p does not bound the places of p inserts.

## Part 1 (A)

Two commits: `65af9ce`, L1 with G6 and F3 and the reservation of point 5, which leaves a child reserving
each change as it prepares it; `55c67b9`, a child's transaction prepared whole and then reserved and
checked on the state it leaves.

What it does beyond the names above:

- The pool holds the lesser of two bounds of what the changes waiting can take: their need at the tree
  as it is, and what the ledger leaves of the linear bound of the changes charged since its base. Each
  is enough by itself. So one insert reserves what it did (2h + 1 against 2h + 3), while one remove at a
  height of 2 or more reserves 2 nodes in place of h + 1, and a change of key at a height of 4 or more
  less than its need. That shows only while a pool is below its standing reserve: before the first
  preparation that touches the index, and in the last try before a refusal for memory.
- A reservation that is not made gives back its nodes and puts the ledger back as it was. One whose nodes
  do not fit beside the slots in use under the memory limit is refused before it takes a node, so that
  it obtains no segment for nodes it would give back: `UncountableNeedTests`, whose reservation the
  linear bound now counts, is refused so, with its assertions as they were.
- The path of the writer grows only once the pool has its nodes; an exception there withdraws the
  reservation, and the reserve goes back with it, as for any withdrawal.
- `ChangePublisher.TryReserve` keeps the path of one change of a type without an index; for an indexed
  type it decides, checks and reserves over the span. A child prepares the effects of a transaction
  unreserved and reserves them together (`ReceivedChanges.TryReserve`).

Tests that now pass: `LinearBoundTests.Bounds_of_the_pool_are_the_linear_bounds_at_every_height_and_count`,
`ChildBatchTests.Child_stops_at_a_transaction_two_effects_of_which_claim_one_value_with_nothing_of_it_applied`,
and the new `ChildBatchTests.Child_applies_a_transaction_that_exchanges_unique_values_on_the_state_it_leaves`,
an exchange forged by the relay, which no order of single writes can apply.

Premises of step 1 found wrong. *Decided by the coordinator after review round 1:* each test is changed
to `ordered-index.md` in a commit of its own, which gives the old and the new assertion (`d6cf4f2`,
`cf01f8c`, `a1926bb`); the first and the third are no longer skipped, the second needs the commit of a
batch.

- `LinearBoundTests.Reservation_withdrawn_gives_back_exactly_what_it_took_and_keeps_the_standing_reserve`
  holds the reserve after a withdrawal. By `ordered-index.md` it goes back: with nothing else waiting the
  pool keeps nothing, so the prepared bytes go from 8,192 (the reserve of the index of 500 entries,
  8 nodes) to 0, and the live and retired bytes stay as they were.
- `RefusedBatchTests.Batch_withdrawn_before_its_dispatch_gives_back_exactly_what_it_took` (part 2) holds
  the arena as it was while an insert waits beside the batch; after the withdrawal the pool of `ByLogin`
  keeps the lesser bound of that insert, not the reserve, so the prepared bytes fall by the difference.
- `LinearBoundTests.Reservation_made_or_refused_takes_no_managed_room_for_its_nodes` takes 318,560
  managed bytes for a reservation of 1,500 inserts, made: 216,024 are the 4,500 places of the retired
  list (`Queue<RetiredItem>`, 48 bytes an item), which the brief keeps in managed memory (F3 asks it of
  a refused reservation only), and the rest the bookkeeping of the second segment the arena obtains
  (`ArenaSegment`). The case refused in two segments is not reached either: by the linear bound the
  reservation is 4,500 nodes, 4,608,000 bytes, which two segments hold; its comment counts the need, some fifty
  thousand nodes.

## Part 2, B1: point 2

Part 2 is split by the coordinator into B1, point 2 proven on a child, and B2, the commit of a batch on a
master with points 3 and 4 and the revision of a member; both are reviewed on one branch and merged
together after the one start of the machine. B1 is `c3dfeed`, point 2 with TEXT B, TEXT 3 and the
barrier sentences, and `312506c`, its tests on a child. What B1 built for the wholeness of one step, a
second walk after a take that waited, is gone in form C (below).

- **Form C, on the owner's word** (23 November 2026, in the coordinator's session: "Утверждаю. И пункт 1,
  и удаление старых папок."; the reading of the Acceptance he changed that day, which the document of the
  epic records). The engine owes five points, and a test holds each:
  1. no member of a batch is visible in its new state before its position on that node, by key, by a
     search and by an enumeration, on a master and on a child: `BeforeAndAfterThePositionTests`,
     `ChildPublicationTests`, `MergedTransactionPrefixTests`;
  2. a read that begins at or after the position sees every member: `ReadAtThePositionTests`, which
     reads while the old entries of the members are still in the indexes, and the searches the restated
     tests add after the publication;
  3. the position of an answer is never below a stamp it holds: the same tests, in which the only stamps
     above the position before the batch are those of its members, so a member in its new state at a
     position before the batch would fail them;
  4. a read by key is exact: the reads by key of `BeforeAndAfterThePositionTests` and
     `ChildPublicationTests`, whose state follows their position;
  5. no read is left waiting after a fault, a close or a cancellation: `WaitingReadTests` and
     `ChildPublicationTests`, the close added by form C.

  A search or an enumeration that runs across the publication may return some members as they were
  before it and others as after, as separate reads may.
- **What form C builds.** The publication in its order and the take that does not accept a stamp above
  the position, as B1 built them; nothing else. `EntityTable.Step` goes on after a take that waited, as
  at `a326551`: the second walk, the start of the step it went back to, `waited` of `TakeEntry`, and
  `WalkProgress.WalkFrom`, `DropStand` and the place of a round kept apart from the entry the walk
  stands after are removed, and `WalkProgress` is as at `a326551`. The finding P1-2 of the review of B1,
  on what the second walk dropped, goes with it.
- **The root entry of an insert at the mark** is kept, because it is not built: it is the state
  `RootTable.Add` leaves until the publication stores the entry, as at `a326551`, and the take waits
  through the mark that the stores of a replace and a delete need for the stamp. None of the five points
  needs the wait for an insert: a step that passed over such an entry would show the insert as it was
  before, which form C permits. But passing it over needs a load of the state before every take of a
  step, which the wait does not: with it `Step` was measured at 4,908 bytes against 4,897, the calls the
  same. So the brief's pass over an entry whose root is not installed is not built either.
- **Found against the brief, by B1.** A step that passed over such an entry could then meet a later member
  after the position and return it while the insert it passed was visible at the same position:
  `MergedTransactionPrefixTests` found it in Release ("search: [1] at 1"). Form C permits that answer, and
  the wait at the mark gives the same one when the walk held a leaf from before the entry was installed.
- **The listings** (a Release driver over the public API, tiered PGO off, the driver of B2 kept outside the
  repository; `a326551`, then `10f8403`, then form C). `Step`: 67 calls each; 4,889, 5,017 and 4,897
  bytes; a frame of 968, 1,016 and 984. At its take it passes the token and the place of the member, the
  member being B2's, stores no stamp, and tests nothing more. `TakeEntry`: 2, 3 and 3 calls; 130, 237 and
  215 bytes; the same five loads of shared memory as at `a326551` (the stamp, the position, the state
  with the member as one word, the root, the stamp), one comparison of the stamp with the position, the
  member taken from the word, and the call that throws for the token out of the path. `TryCopy`: 8 calls
  each; 357, 395 and 371 bytes. `TryRead`: 16 calls each; 1,329, 1,343 and 1,344 bytes. `StepWithoutWalk`
  (7 calls, 407 bytes) and `TrySearch` (2 calls, 214 bytes) are unchanged. The allocation tests of a read
  and of a search pass with their assertions as they were.
- **The tests restated on the owner's word**, in a commit of their own after form C, by the restatement list
  of B1: `WholeOrNoneTests` (renamed `BeforeAndAfterThePositionTests` since), `ChildPublicationTests`
  and `MergedTransactionPrefixTests`, each renamed to what it now states. A search that runs across the publication finds no member before s, and at s or
  later some of the members in their order and in their state after it (`BatchSchema.ExpectNoMemberBeforeThePosition`),
  in place of none or all; the same searches begun after the publication find every member. Before that
  commit, form C fails them as expected: `WholeOrNoneTests` at `AfterNewEntries` ("[11, 12]") and at
  `AfterFirstChange` ("[2, 11, 12]"), on the master and on a child; `ChildPublicationTests` at the same two
  places; `MergedTransactionPrefixTests` in its three runs ("search: [1] at 1", a step that held a leaf from
  before the entry of B was installed). `SearchStepAcrossAPublicationTests` and the probes of the list are
  not on this branch.
- **One publisher.** A child applies its transactions through the publisher of the instance
  (`DatabaseParts.Publisher`), which holds the switch of the tests for a master and a child alike.

## Part 2, B2: the commit of a batch, points 3 and 4

Seven commits after `ee4ddd2`: `d6cf900` the row "the wire format 4" of `RefusedPeerTests` (the
planner's answer (a)); `81b3229` the child stores the member and checks nothing (answer (b)); `cc1faaa`
the encoding at `Add` (point 8); `ea67085` the commit of a batch on a master; `fb343eb` one way to wait
for another write; `4e1d896` the sorted claims of a prepared batch; `8927259` the calls of a single write,
after the JIT listings.

What it does beyond the names above, or otherwise:

- **The boundary (point 8).** The core's `WriteBatch` takes one argument from the engine,
  `Access/Batches/IWriteBatchExecutor`, which `EngineDatabase` implements: `EncodeRoot(entityType,
  values)` and `ExecuteAsync(operations, session, token)`. A member is encoded at its `Add` to its root in
  the load format after its envelope (`Entities.RootValues.Export`), an array of its exact length up to a
  piece of a transfer (64 KiB) and the pieces of a `TransferBuffer` above, so that a root of any size is
  held and none lies in the large object heap. The last piece is used in part, so a root above 64 KiB takes
  up to one piece more than its length: a root of 65,598 bytes takes two pieces, 131,072 bytes, about twice
  its length, one of 200,062 bytes four, and one of 3,145,790 bytes 49, 2 % more (the reviewer's probe).
  Trimming the last piece needs a segment of its own beside `TransferPiece`; it is not built, as the copy is
  managed memory held only until the outcome of the batch. `WriteBatchOperation` holds the kind, the
  entity type, the key, the expected revision and the root. The parameter `copyBinary` of the core went
  with the copy it made. The commit lets go of the operations, so a batch kept after it holds no root,
  and the record of the batch in the engine lets go of its members at its outcome
  (`PendingWrite.LetGoOfInputs`), so a task of the commit kept after it holds none either. The cost: a
  managed copy of every root from its `Add` to the outcome of its batch.
- **The numbers of the members.** `Writes/BatchMember.NumberOf`: from 1 in the order of the members for
  a batch of two or more, 0 for a single write and for a batch of one, whose revision is then that of a
  single write (the planner, 22 November 2026; the rule of the read side that needed it was dropped
  afterwards, and the numbering is kept). `PlaceOf` gives the place a refusal reports. Held by
  `Engine/Batches/Revisions/MemberRevisionTests`, which also holds, for answer (b), that a child reports
  for each member the revision its master reports after the transaction and after a load.
- **One write.** `WriteCoordinator.SubmitBatchAsync(BatchMember[] members, session, token)`: the keys
  in the form of their entity type (`Sets.KeyArgument`, by `EngineDatabase`), one `PendingWrite` with
  `Members` and `Keys` (`Entities.PublicationKeys`), set by `BeginBatch` apart from `Begin`. A repeated
  identity is `ArgumentException` at the call, which the core never submits; an entity type not stored is
  `NotSupportedException` in the task, and so is a batch on a child until part 3.
- **The preparation.** `WriteExecutor.TryPrepareBatch`: member by member, the lookup and the condition
  (`CheckCondition`, shared with a single write and inlined), a new root entry for an insert, the root
  placed by the path of a forwarded write (`Place`, which `PlaceForwarded` became), then one
  `ChangePublisher.TryReserve` over the span. A failed condition carries the place of its member; a
  refusal for memory names no member; `TakenValue.Change` is given to the coordinator as the place of the
  member. `PreparedWrite.BatchChanges` and `SetBatch` hold the changes in an array of the record, and
  `Changes` spans it.
- **One way to wait (point 4).** `PendingWrite.SharesEntityWith` replaces `IsOfSameEntity`,
  `FindLastSharing` replaces `FindLastOfEntity`; two single writes keep the chain, and every other pair
  waits through the list of the writes that wait for another write (`WaitingForWrite`, `Holder`). The wait
  came with the commit of a batch, since a batch is not correct without it; `fb343eb` gives it its name and
  one description, with no change of behavior.
- **Unique values (point 3).** The searches for a claimant and for a write that frees a value run over
  the changes of every prepared write; a prepared batch answers through `Entities.UniqueClaims`, the
  places of its claims sorted by the order of each unique index and its root entries sorted, made on the
  first question (`PreparedWrite.ClaimsOf`), so a batch nobody asks about sorts nothing. Changed from the
  study: `Claims(change)` and `Frees(owner, index)` answer yes or no, since the batch itself is the holder.
- **The transaction.** `TransactionEncoder.Write(tail, sequence, changes, arena)` writes one effect for
  each change, a single write's as well; the forms of one write and one delete went.
- **Sizes.** `PreparedChange` 96 bytes; the record of a write grows by 32 bytes, `PendingWrite` from 344
  to 360 and `PreparedWrite` from 136 to 152, for a write that waits only (a write published in the hold of
  its call reuses its record).
- **The listings** (Release driver over the public API, tiered PGO off, against `ee4ddd2`). A single write
  makes no added call on its path: `Prepare` 5, `FormGroup` 12, `MarkCompleted` 7,
  `WriteExecutor.TryPrepare` 37, `TryPrepareInsert` 21, `TryPrepareDelete` 14, `TryPrepareReplace` 21, as
  before. The calls `Admit` (25 to 27), `Start` (13 to 17) and `TryPrepareTwice` (22 to 24) add lie in the
  branches of a batch or of a write that waits for a batch; `PublishOne` (7 to 8) calls the transaction
  encoder in place of `Deliver`, only while a child is registered. `ChangePublisher`, `EntityTable.Step`
  (67 calls, 5,017 bytes), `TakeEntry` (3, 237), `TryRead` (16, 1,344) and `TrySearch` are unchanged.

Found against step 1, reported and not changed: `RefusedBatchTests.Batch_withdrawn_before_its_dispatch_gives_back_exactly_what_it_took`
inserts accounts 2 to 200, among them 10, which the held write inserts; by point 4 the batch waits for that
write and prepares nothing, so its first assertion fails. It stays skipped. Proposed: members from 11.

## The parts

Too large for one review. Proposed:

- **A.** L1, G6 and F3 with the reservation of point 5 (`NodePool`, the ledger, the linear bounds,
  once per index), and the check on the final state for a span on a child, which already receives
  spans. No batch commits yet; a single write keeps its behavior and its figures (K1 and L2 in the start
  of the machine).
- **B.** Points 2, 3 and 4: exposed whole, the commit of a batch on a master, unique values on the final
  state against unpublished holders, the one wait for a write, the revision of a member. The riskiest;
  if it is still too large, point 2 alone (B1, proven on a child by merged transactions) before the
  commit (B2).
- **C.** Points 6 and 7: the child and the forwarded batch, the wire format 5.

## Part 3, C: points 6 and 7

Written before the code, for the planner to read. No order of stores changes: a child publishes a batch
through the publisher of part 2, and a forwarded batch is published as a batch of the master.

**Point 6, the child.** Built already: one publisher (B1), a transaction checked on the state it leaves
(part 1), acknowledged only at its position (`ChildBatchTests`). Left:

- `Entities/PublicationKeys.Clear()`: empties the set and keeps its capacity. Its head adds that a child
  keeps one set, changed under its lock of the writes by the thread that applies.
- `Child/Loading/ReceivedChanges`: a `PublicationKeys` replaces the scans of `ThrowIfNamed` over
  `_prepared` and `_skipped`; a key `TryAdd` refuses is named twice (`NamedTwice`, as today), and `_skipped`
  becomes the count `SkippedStates` takes. Held by `KeyNamedOnceTests`, unchanged.

**Point 7, the body of a batch request** (wire format 5, little-endian; `ForwardPayload.BatchRequest`):

| Part | Bytes |
| --- | --- |
| Kind | 1: `ForwardEncoding.BatchKind`, 4, the value after those of `WriteKindEnum` |
| Minimum position | 0, or 1, its history and its sequence (UInt64 each), as a request carries it |
| Count | UInt32, at least 1 |
| Each member, in its order | its kind (1 byte, a `WriteKindEnum`), its type id (UInt32), its expected revision (0, or 1 and two UInt64; never for an insert), its key (`PrimaryKeys.WriteKey`), and but for a delete the length of its root (UInt64) and the root with its envelope, last mutation 0 and member 0 |

The body ends after its last member. The reply has the bytes of today's.

`Replication/Forwarding/ForwardEncoding`:
- `BatchKind`; `IsBatch(body)`, its first byte.
- `WriteMemberHead`/`ReadMemberHead`: kind, type id, revision. `ReadMemberHead` refuses a kind above Delete,
  so a member that is a batch, and a revision on an insert. `WriteRequestHead`/`ReadRequestHead` become the
  member head and the position, so the two share one encoding.
- `WriteBatchHead`/`ReadBatchHead`: the position and the count.
- `ReadRoot` returns what its length announces and stands after it; `ExpectEnd` closes every request, one
  write as well. Today `ReadRoot` demands that the root end the body and `ExpectEnd` serves a delete; one rule
  then covers both: a root ends where its length says, and the body after its last part.
- `SmallestMember`, 10 bytes (kind, type id, mark, the length of an empty String key): a count above what the
  rest of the body holds at that size is refused before the array of members is allocated, by the rule of the
  receiver that what it takes grows with the bytes received, not with what a peer announces.

**How the master submits it.** `Children/RequestReceiver.SubmitBatch`: the head; each member read and checked
as one write is (a stored entity type, its key, `CheckRoot`, which holds the key) into
`Writes/BatchMember(kind, table, key, revision, root)`, the root a slice of the request body after its
envelope, whose member number is not checked, as for one write; a repeated (table, key) refused by
`PublicationKeys.TryAdd`; then the end of the body. Each failure is `ValidationFailed` before anything is
submitted, and the connection ends. Then one call, `Writes/WriteCoordinator.Forward(BatchMember[] members,
PublicationKeys keys, ForwardedOrigin forwarded, LatticeSession? session)`, the twin of `Forward` for one
write: `TryEnter`, then `ForwardAfterAsync` (which gains the members and the keys) or `Admit` with members,
keys and origin. So it is prepared, waits, is refused and is published as a batch of the master (part 2), and
its reply is recorded as a forwarded write's: a success in the hold that publishes it, `Changed` when a member
changed an entity; a refusal where its outcome is given.

**How a refusal resolves its index.** Both ends resolve the identity of a unique index in one entity type: the
write's, or for a batch that of the member `OperationIndex` names, which `Violation` always sets for a batch
(the member is the one Q58 decides on the master).
- `Writes/ForwardedOrigin`: a constructor for a batch with the entity types of its members (`LatticeEntityType[]`,
  no root) and `EntityTypeOf(int? operationIndex)`, by which `Refuse` maps the name to the identity. Not the
  members themselves: the record lets go of them, with their roots, before the refusal is recorded
  (`PendingWrite.LetGoOfInputs`).
- `Child/Forwarding/ForwardedCall`: the same constructor and `EntityTypeOf`; for a batch, no number or one beyond
  its members is `ValidationFailed`, the connection ends and the call ends `IndeterminateOutcome`.
  `PendingRequests.Receive` asks it only for a reply that names an index, so a refusal for memory passes.

**How the child sends and waits.** `Child/Forwarding/Forwarder.WriteBatchAsync(BatchMember[] members,
LatticeSession? session, CancellationToken)`, with the steps of `WriteAsync` shared, not copied: a member of an
entity type not stored is `NotSupportedException`, then the token, the close, the session bound, the sender; the
request written from the roots the core encoded at `Add` (`RootValues.Export`), each after its envelope; one
`ForwardedCall`; the reply; the wait on the clock of the child, bounded as for one write; the session moved in
`finally`. The child applies the batch at one position, so with its clock there every member is visible.
`EngineDatabase.ExecuteAsync` builds the members on either node and hands them to the coordinator or the
forwarder; its `NotSupportedException` goes, and `SkipReason.BatchThroughAChild` with its last use.

**The cost the record states**, for a batch whose request is B bytes, about its encoded roots:
- On the child: the roots from their `Add` to the commit, as on a master; 2B while the request is written from
  them; then the request, B, until the reply, held by the call as for one write; then the transaction, B, in the
  buffer of its stream while it is applied, kept afterwards (question 4).
- On the master: B of managed memory for the request from its first frame to the outcome of the batch, outside
  the budget; B in the arena; B in the tail until every child has applied it; for each member its record, its
  entry in the set of entities, its change (96 bytes) and its entity type (8 bytes).
- Held back: the child sends its requests one at a time, so its later writes wait until the last frame of the
  batch is sent; its Applied pass between the frames. The master checks every root on the task of the
  connection and prepares and publishes the batch in one hold of the lock of the writes; meanwhile it reads no
  frame of that child, neither an Applied nor a request, and every other write waits for the lock, as for a
  batch of the master.
- Figures: a probe on the workstation, kept outside the repository, of B and the managed bytes on both nodes for
  batches of 1,000 and 10,000 users of the fixture.

**Tests.** `ForwardedBatchTests` 1 and 2 need the master; its 3 and 4, `BatchThroughAChildTests` and
`WriteBatchTests.Batch_through_a_child_is_on_that_child_when_it_completes` need both ends. Every case of the list of
point 7 has a test, "a peer of wire format 4 refused" in `RefusedPeerTests` (active since part 2); of point 6, the
exchange applied and the violation that stops the child are in `ChildBatchTests` (active). Two sentences have none,
and get one first, in a commit of its own, skipped with the shared reason until the code:
- "A child never acknowledges a part", for a batch it forwarded:
  `ChildBatchTests.Child_that_forwarded_a_batch_acknowledges_no_sequence_of_it_and_completes_no_commit_before_it_has_applied_all_of_it`,
  held at `BeforePosition` on the child, through a relay.
- The minimum position of the head: `ForwardedBatchTests.Batch_request_waits_for_the_position_of_its_head_and_runs_once_direct_writes_reach_it`,
  a hand-made child as in `MinimumPositionTests`; `ForwardPayload.BatchRequest` gains an optional position, an
  edit that states nothing new.

**The commits.** The two tests; point 6; the wire and the master (`ForwardedBatchTests` 1, 2 and the position); the
child (the rest); the documents: the remarks of `ILatticeWriteBatch` (the `NotSupportedException` of a child goes),
`README.md`, `docs/design/architecture.md` and the cost sentences (question 2).

**Questions, each with the answer recommended.**

1. **The wire format.** The brief says `WireFormat` becomes 5; part 2 made it 5 for the member number in the
   envelope, and nothing was released between. Recommended: it stays 5, and its comment says 5 adds to 4 the member
   number and the batch a child forwards; `RefusedPeerTests` keeps "the wire format 4".
2. **The page of the cost sentences.** No `docs/design/replication*` exists; forwarding is described in
   `milestone-1-engine-contract.md`, "Replication transport and initial loading", the paragraph "A child forwards a
   write as a request". Recommended: the two sentences there, the bytes held on each node and what a large batch
   holds back, and nowhere else.
3. **A child's set keeps the capacity of its largest transaction**, as `_prepared` does (the note carried from S13
   names both): about 60 bytes a key (an entry of 56 and a bucket) beside the 96 of a change. Recommended: kept and stated in the record; a trim
   of both is a decision of its own.
4. **A child's stream keeps the pieces of its largest transaction** (`TransactionAssembler`, `TransferBuffer`):
   after a batch of B bytes every child holds B managed bytes, outside its budget, for the life of its connection.
   True of every batch since part 2, found while costing this one. Recommended: stated in the record and entered in
   the refactoring backlog; giving the pieces back after a transaction of several frames, at the cost of new pieces
   for each, is outside points 6 and 7 and is built only on the planner's word.
5. **The child holds its request until the reply**, as for one write. Recommended: kept; dropping it after the last
   frame shortens the hold, not the peak of 2B.
6. **`Child.Forwarding` uses `Writes.BatchMember`**, an edge it does not have today. Recommended: accepted; `Writes`
   refers to nothing of `Child`, and one record serves a batch of the master, a forwarded batch and a child's
   request.
7. **One add to the set for every effect a child applies**, a hash and a store, where a transaction of one effect
   scans an empty list today. Recommended: built plainly, and `following-children` owed to the next start of the
   machine beside the write lines already owed.
8. **No line of the measurement program for a forwarded batch.** Recommended: none; the bytes come from the probe,
   and what a batch holds back is its send and its commit, which part 2 measured.
