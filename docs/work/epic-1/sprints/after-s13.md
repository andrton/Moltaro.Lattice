# After S13 · Findings of the outside review

**Entered on 18 November 2026 as the planner wrote it.** An outside reviewer, who was
given none of the reviews of this epic, read `main` at commit e7b6e0e (after S12). It
reported 16 findings; the planner verified each against the code at that commit, refuted
none and corrected the severities. This is a fix package of its own on the full track —
tests first, by another agent than the implementer — on a branch of its own, started
right after S13 is merged, because S13 changes files the package changes. It is merged
before S14 starts; item 5 at the latest before S17.

No finding is a P1 in the scale of this epic: nothing is lost, doubled or returned stale
in ordinary use. Four of the reviewer's five P1 need a peer that sends what this engine
never produces; they are closed anyway where the check is exact and cheap.

**Fix, each with a test that fails before the fix.**

1. A committed write can end in `HistoryMismatch` (`WriteCoordinator`, `Deliver` and the
   synchronous `Outcome`; the same in the `finally` of `Forwarder.WriteAsync`). An empty
   session is admitted without being bound; a concurrent write on another database binds
   it; `Advance` throws after the commit. Rule: a session is bound to the history of the
   database at admission, before the write is executed; a session of another history, or
   one that loses that race, is refused with `HistoryMismatch` before execution; after a
   commit the result is never replaced by a failure of the session. One sentence is added
   to `LatticeSession`: "The first operation with a session binds it to that database's
   history." P3.
3. A success reply to a forwarded write whose sequence is not above the sequence the
   child has applied when it receives the reply is malformed: the child ends its
   connection (`PendingRequests.Receive`). P4, hardening, exact.
4. `Closed` that arrives while a reply or a transaction is being assembled is malformed:
   the connection ends as one that ended without `Closed`, so sent and unanswered writes
   are `IndeterminateOutcome` (`Follower`). The reviewer's premise is wrong — a success
   reply is one frame and a correct master never puts `Closed` inside a message — and the
   check is still right. P4.
5. `ReceivedChanges`: "a transaction names each key once" holds for skipped effects too —
   a delete of an absent key (`PrepareDelete`) and every skipped state record their key
   for the duplicate check. Test rows: a delete of an absent key and then a write of it;
   a skipped write and then anything on that key. P3; S17 relies on it.
6. `RetiredMemory.Drain` resets both triggers even when a reader kept it from freeing,
   and writes that retire nothing never look again (16 MiB stayed through 2,000 deletes
   of absent keys). Rule: while retired items remain after a look, a later published
   write of any kind looks again; the sentence of the arena protocol "what waits between
   two looks is bounded by those two thresholds … until its next operation" becomes
   true. P2.
8. `ChildConnection`: `cursor.Sent` moves when the last frame of the transaction is
   added, not before the first, so an Applied for a transaction not wholly sent is
   refused. Accounting only. P4.
9. `DeliveryTail`: `_taken` is cleared at the end of `Commit` and in the branch of
   `Release` without a cursor, and blocks let go are reset; a test with weak references
   states that after the last child has left the tail retains nothing, as the brief of
   S11 and the milestone contract say. The wider case the verification found is checked
   too: the whole unreleased tail of a child that lags, reachable through `_taken`. P2.
10. A closed database drops what it held: after the arena is disposed, the references to
    the root table, the index, the sets, the delivery tail, the retired list and the
    coordinator are released under the lock of the state; every entry point already ends
    in `ObjectDisposedException` before it touches them, and each is verified. A test
    with weak references on a closed instance that is kept alive. P3.
11. `ReadMemoryLimit` counts what is live at once (the decision on queries: "Sum
    simultaneously live buffers … check before growing"): during a growth of
    `ReturnedKeys` the old table is counted with the new. The exact thresholds of
    `ReadMemoryLimitTests` move; the test author restates them from the rule. P3.
13. The compiled schema is immutable, as `LatticeSchema` says: the nine public
    collections (`EntityTypes`, `EnumTypes`, `OwnedTypes`, `LatticeEntityType.Fields` and
    `.Indexes`, `LatticeOwnedType.Fields`, `LatticeEnumType.Members`,
    `LatticeIndex.Parts`, `LatticeIndexPart.Path`) are not arrays a caller can cast and
    write. The array is kept privately for the engine where a path indexes it; the read
    by key and the write are measured before and after — no fall. P2.
14. `CatchingUp`. The accepted decision on replication ("Connecting → Loading →
    CatchingUp → Ready") and the documentation of the enum say a child shows it while it
    applies changes up to its readiness position; the code folds it into `Loading` by a
    reading of 17 November 2026 that is recorded nowhere. The code follows the accepted
    decision: the state of the child is `CatchingUp` from the end of the load until R is
    applied; operations stay `Unavailable`. A test that asserts `Loading` in that phase is
    reported to the planner before it is touched. P2, low effect.
15. A step of the enumeration does not copy an entity it then cannot remember: room for
    the key of the entry is made before the copy, from the key in the entry, so the first
    entity of every pass and every growth boundary are copied once. Charging the limit
    for capacity grown for an entry that then turns out deleted is accepted: capacity is
    memory held; the comment of `EntityTable` is reworded to that. Asserted by
    allocation: `Enumerate().First()` of an entity of 1 MiB allocates no more than a
    read by key plus the table. P3.
16. `ChildArenaFullTests`: the sleep is replaced by an observable — a counter or a signal
    of "the applier waits for reads that hold retired memory", reachable by the tests of
    the engine — so the test proves the wait. P4.

17. From the review of S13. The slot array of the text table (`TextTable.TakeSlot`)
    doubles its length without a bound: at 2^30 slots the doubling overflows and the array
    is cut to 4 slots. The behavior: the write is refused with the memory refusal the
    engine already has (`WriteRejected`, memory) before anything is taken, never a
    truncated table; no new error and no option — it is a limit of the structure, stated
    once in the engine contract. The test reads the growth function at the boundary, not
    a billion entries. P3.
18. From the review of S13. The test of "a reference never resolves to other text" holds
    a read before it has taken its root, so the released read copies the new root. The
    stronger form: take the old root inside the hold, replace, write and delete texts of
    the same size class, drain, and read the old text through the old root, for one block
    and for a chunked entry. The reviewer's probe of that form passes; no fix is expected.
    P3.

Item 10 also covers the text table of S13: a closed instance that is kept alive holds
its slots and buckets today, as it holds the root table and the index.

**Document, no code.**

2. Values inside a received root are trusted (point 1 after the study of S12; decision
   O8: the replication port authenticates nobody). It lived only in this document. A
   dated paragraph goes into `docs/decisions/replication-and-hosting.md`: "A node checks
   the structure of what it receives so that no read leaves the received bytes; it does
   not validate field values again: a peer is the same engine with the schema agreed in
   the handshake, and a child validates before it forwards. A peer that sends invalid
   values can make a node hold an entity whose read throws or shows replacement
   characters; it cannot make it read or write outside its memory. Authentication of the
   port and what a hostile peer can do are outside this milestone (O8)." The header
   comment of `StoredText` ("only well-formed text is stored") says on which assumption.
   The verification found no memory unsafety and did not trace malformed chunked UTF-8
   and UTF-16 of odd length to the end: both are added to the tests of a received root
   as "throws a managed exception or is refused, never reads outside".
7. The read of an absent key repeats its lookup while writes are published during it
   (`EntityTable`), inside one protected section, without looking at the token; unbounded
   only at a rate of publication no lock of the writes reaches. The arena protocol gains,
   beside "a read holds retired memory back only for the length of one step": "a read of
   an absent key repeats its lookup when a write was published during it; the step ends
   with the first lookup no write crossed." P4.
12. One receive deadline covers a whole frame and is not renewed by partial reads, so "a
    load that keeps arriving is not ended" is false below about 17 kbit/s at the default
    and for a small `OpenTimeout`. The note carried to S34 from S12 is extended with this
    case: the deadline becomes a time without bytes, decided there with the case of
    bytes that wait. Until then one sentence at `OpenTimeout`: "A part is a frame of at
    most 64 KiB; it has to arrive whole within the timeout."

**Remarks for the coordinator.** The reviewer verified 1, 6, 9, 10, 11, 13 and 15 by
probes. The two reviewers of each sprint missed 6, 9, 10, 11, 13 and 15, which are
defects of ordinary operation; the report of the package says so, with the kind of check
that would have found each: a test with weak references of "retains nothing", a probe of
a limit at a growth boundary, a cast of a public collection. The coordinator tells the
planner only: a contradiction with a brief, a test of the contract that asserts the old
behavior (items 1, 11, 14), and the figures of item 13.

**Carried out and reviewed on 18 November 2026.** Tests first by another agent than the
implementer; two reviewers on commit cbed8db, a second round by one of them on 85dfb13.
Where a sentence above says otherwise, these replace it.

1. **Item 6 was narrowed twice by the planner, on what the implementer and the reviewers
   showed.** As written above, every published write would have looked while anything
   retired waited, which moves the epoch — a line of memory every read loads — on every
   write beside a held read. The rule that is built: a look resets its two counts; what
   it could not free stays retired and a later look frees it; a look that leaves retired
   items behind arms a second trigger — until a look leaves nothing behind, a published
   write that retires nothing counts as one item, so the next look comes within one
   window of writes of any kind. Without such a look a write that retires nothing counts
   nothing, and every load without a held read keeps the rate of looks it had. The bound:
   what waits between two looks is at most what reads held at the last look and one
   window. Counted by the reviewers as moves of the epoch for each 10,000 writes, before
   and after: replaces 78 and 78, with a read held or not; deletes of an absent key or
   inserts beside a held read 0 and 78; 99 inserts to 1 replace, pure inserts and
   nothing retired 0 and 0; after the held read left, 8,688 bytes kept for ever before
   and freed at the 128th write after.
2. **A P1 of the first round: item 1 was not closed for a write through a child.** The
   forwarder read the session as empty, a write on another database bound it, and the
   binding then returned without checking the history: the master executed the write and
   the call ended in `HistoryMismatch` (13 to 75 of 20,000 racing rounds; none on a
   master). Binding now checks the history of a session that has a position and binds an
   empty one, in one step for the session; a master and a child each bind once before
   execution. After the fix: none in 80,000 rounds. A test at the clock states it, and a
   bounded race guards it — put back on the old code it fails every time.
3. **The sentence of `LatticeSession`,** as the planner worded it: "The first read or
   write with a session binds it to the history of that database. Waiting with a session
   that is not yet bound waits for nothing and does not bind it; waiting with a session
   bound to another history fails as a read with it does." A write that completes in the
   hold of its own call has no test of its own for the binding: it runs before execution
   on every path and nothing can hold a write there.
4. **Item 11 is exact, and covers the list of chunks of String keys.** The limit counts
   the largest of the three moments while room is made — the places double, the list of
   chunks grows, the chunk is taken — because each releases what it replaces before the
   next allocates. `ReadMemoryLimitTests` stated no exact threshold, so none moved.
5. **Item 13:** the nine collections are read-only views over private arrays, made once
   for a schema; the engine reads the arrays through an internal span where it read an
   interface before. Its measurement is owed (see below).
6. **Item 17:** the slot array grows to the longest array there is and a write beyond it
   is refused for memory before a slot is taken or a block stored. **Item 10** covers the
   text table.
7. **No existing test asserted an old behavior** of items 1, 11 or 14; two comments that
   stated the old reading of item 14 were corrected.
8. **Two tests that failed once under load were wrong, the engine right,** each shown by
   a probe. The race of a wait with a publication and a loss made 50,000 handoffs between
   two threads inside a bound of one minute, which a loaded machine does not manage; it
   now races within a budget of time. The test of a child that closes before applying a
   write whose reply has arrived read the master's position after seeing the insert
   through a read — an insert is visible before the position moves — and let the default
   synchronization timeout run; it now names the sequence before the write and waits
   beyond the bound. One more test had the first pattern and was corrected.
9. **Left:** the test of an acknowledgment for a transaction not wholly sent still relies
   on 16 MiB being more than the buffers of the loopback hold; the only observable would
   be a switch in the sender of the master.
10. **Probes of the reviewers, all passing on the last commit:** weak references to every
    part of a closed instance that is kept alive, master and child, with and without a
    child having been connected (24 to 53 parts, none alive), and to the blocks of the
    delivery tail after the last child left; `ReadMemoryLimit` one byte below, at and one
    byte above what is live at every growth for Int64, Guid and String keys (87 runs);
    casts of every public collection of a compiled and a loaded schema; a churn of
    writers with shared and fresh sessions on two databases with a close in the middle.
11. **The hardest part to follow,** named by both reviewers: what counts toward the next
    look in `RetiredMemory`. Its head was rewritten as separate statements in the round.
12. **Carried on:** the close clears fourteen fields one by one and a part added later
    has to be added to that list — entry B4 of the [refactoring backlog](../../refactoring.md);
    notes for S14 and S17 at their briefs.
13. **To measure on a quiet machine,** which the owner could not give on 18 November
    2026; kept by the coordinator and run in one session: the read by key against the
    commit before S13 and the base against itself (S13); the read by key and the write
    before and after item 13; writes beside 16 readers and the read by key on 16 threads
    beside a writer for item 6.
