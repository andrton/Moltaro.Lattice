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

A test that needs the commit of a batch is skipped with `SkipReason.WriteBatch`; one that needs
only a mechanism of the engine with `SkipReason.WriteBatchMechanisms`. Active now and passing: the
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
  taken; a Deleted one is taken by its stamp. See question 4 for a step that waited.
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
   the root envelope of a load and of a Write effect (wire format 5), and a child checks it as it checks
   the last mutation. A single write keeps the revision it has. The alternative, an unseeded hash of type
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
   pay. Held by `WholeOrNoneTests` at `AfterFirstChange` (the replace of 2 is the first change, the
   replace of 1 lies before it in every index).
5. **What an enumeration and a search of several steps owe.** Separate reads may combine states from
   before and after the position (decision on the document model). The tests hold, for an enumeration,
   each member in its state after the batch once its session is at s or later and in its state before
   while the session is before s; a pass that began before s may miss a member inserted into a slot it
   had passed. For a search of the deleted members they hold only what one step can see. Is that the
   reading of "seen whole by an enumeration and a search" in the Acceptance?
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

Premises of step 1 found wrong, left as they are for the reviewer:

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
