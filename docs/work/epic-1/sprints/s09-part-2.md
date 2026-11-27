# S09, part 2 · The write path after a proven source

**Track:** full, two reviewers · **Needs:** S09 · **Decided by:** the owner, 17 November
2026, in the planner's session. The owner read the figures of S09 and the comparison with
the industry and asked why the approaches of the industry are not simply repeated to keep
writes as fast as they can be. For reads the sources were studied and repeated; the
coordinator of S09 — a queue per identity, places of preparation, a commit stage — came
from the design documents, written before any measurement, and was never held against a
working system. Of three ways — this sprint now, the question left until S27, or left as
it is — the owner chose the first. S09 closes as it is.

- **Goal.** Writes keep every guarantee of S09 and follow a write path that an
  established system with one order of writes uses; several callers write no less in
  total than one.
- **Step 1, a study, before any code** — as in S08, part 1. The implementer reads the
  source, not summaries, of: the write group of RocksDB (the write thread: joining a
  group, leader and followers, the pipelined form); the execution of commands on one
  thread in Valkey; the single writer of LMDB; and Tsavorite for contrast. For each it
  writes what the system guarantees about the order of writes and about conditions
  (insert only if absent, an expected revision), what it costs per write, and what it
  does when a log has to be written first. It then proposes the path for Lattice and says
  for each guarantee of S09 how the path keeps it, and in a paragraph each how the batch
  of S17 (all its entities at one position), the partial update of S20 (which needs the
  current state) and the durable storage step of S27 fit. The coordinator and the planner
  read the report before anything is built. Licences by the rule of the process: each is
  checked at the commit read; where a project offers several, the permissive one is
  taken; a source under a licence that is not permissive is not opened.
- **What does not move.** The public contract of S09 and its Acceptance: the order of
  writes to one entity with its sentence on the wait of a session, consecutive positions,
  cancellation before dispatch and not after, the rule of when a write is refused for
  memory, the faults and what each caller sees, a close that is complete, no managed copy
  of an input, `ICommitStorage` as the boundary of the storage step and the bounds of a
  group it is handed. The scenario tests of S09 pass with their assertions unchanged. A
  test of the engine that states the internal shape of the coordinator and not a
  guarantee — what `QueuedWrites` counts, how many writes are prepared at once — is
  reported and restated by the test author.
- **What may go.** The queue per identity, its dictionary, the places of preparation, the
  number of acquisitions of the two locks: whatever the proven path does not need. If
  conditions are checked when a write is published, as in S08, and the path keeps the
  order of calls without a reservation, the reservation goes; the milestone contract,
  "Write coordinator and revision semantics", is then corrected with the sprint — the
  planner sends the text after the study — and the owner has agreed to the direction.
- **Acceptance, measured**, as restated by the planner on 17 November 2026 after the study
  (the line "the total of 4 and of 16 callers not lower than that of one caller" was
  wrong: no path whose work in memory is serial can meet it, that of S08 and of LMDB
  included). On the scenario of S09, on a quiet machine, next to S08 and S09: writes a
  second of one caller not lower than S08 beyond noise; the total of 4 and of 16 callers
  not lower than that of S08 at the same count; reads by key on 16 threads not lower than
  S09; bytes allocated per write as S08. Reported: p99 of a write for 1, 4 and 16
  callers, alone and beside readers; and what a large entity costs the others — how long
  the lock of the writes is held by one write of 32 MiB, and p99 of small writes of
  another caller while a caller writes entities of 32 MiB one after another; that pair
  decides whether a parallel form is ever needed, so it is measured carefully. A figure
  that misses is handled by the rule of the process. The implementation report also
  says: that the size of a write is counted by the writer and not walked afterwards; and,
  in a paragraph on fairness, whether a fair lock was measured and what it cost — the
  lock is not fair and p99 with 16 callers returns to the 1.8 ms of S08 — kept only if a
  figure justifies it.
- **The path, decided after the study** (the planner, 17 November 2026): the path the
  study recommends, the writer of LMDB with the group commit of PostgreSQL and Pebble at
  the storage boundary. It is written out in the milestone contract, "Write coordinator
  and revision semantics", which this sprint corrects. A leader with followers, as in
  LevelDB, was prototyped and dropped on its figures. Sources read at pinned commits,
  licences checked first: RocksDB (under Apache-2.0), LevelDB, Valkey, LMDB, Pebble,
  PostgreSQL, Garnet/Tsavorite; Redis was not opened, its licences are not permissive.
  What changes in the brief of S09: its Acceptance line "Writes to different entities
  prepare concurrently and are published in one order of positions" becomes "Writes are
  prepared and published one at a time, under the lock of the writes, in one order of
  positions"; "the values of a write are encoded outside the lock of the writes" becomes
  "a write is prepared under the lock of the writes, in the hold that checks its
  condition; publication only installs what was prepared"; "Who may touch what", with its
  three steps and the lock of the coordinator, is withdrawn whole. The refusal for memory
  keeps its rule, only when a write does not fit beside the stored data: in MemoryOnly no
  other write holds prepared memory when a caller prepares, so it is the path of S08 —
  free what is retired, wait for the reads that hold it, retry, refuse — and the
  resolution alone is kept, in a smaller form, for when the storage step is busy; its
  tests pass unchanged.
- **`ICommitStorage.CommitAsync`**, its documentation: "Called under the lock of the
  writes. The call itself only takes the group: it does not block, performs no I/O and
  runs no code that waits; whatever takes time is behind the task it returns. A task that
  is already completed means the group is committed, and the caller publishes it in the
  same hold. The group and its list belong to the commit stage and are reused: the
  storage reads them until its task completes and keeps no reference afterwards."
- **Counters.** `QueuedWrites` keeps its meaning, admitted and not completed. In
  MemoryOnly `LargestCommitGroup` is 1 (*corrected after review round 1:* except after a
  write refused for memory was resolved, when the writes it held back are prepared in one
  hold and published as one group), and the tests that form larger groups do so
  through the held storage. Tests of S09 that state the shape that goes are restated by
  the test author before the implementer starts: `CommitGroupTests.Writes_prepared_while_
  a_call_runs_the_commit_stage…` (it uses a storage that blocks inside its call, which the
  rule above forbids: it becomes "writes prepared while the storage step holds a group,
  whose calls returned their tasks, are dispatched and published by whoever completes the
  storage step"), `MemoryOnlyPositionsTests` (it waits for a group of more than one write
  under the storage of MemoryOnly, which never forms: it becomes "sequences are exactly
  1..N, one per write"), and comments that say which thread holds the lock of the writes.
- **Carried by this sprint under O16, tests first, as its first commits and independent
  of the study: three findings of an outside review of `main` at `6564720`** (the owner ran
  it on 17 November 2026; no P1, three P2; the planner agrees with all three, and the
  owner said they are fixed in this sprint, and merged all the same if the sprint ends
  with the coordinator of S09 staying).
  1. *A second `CloseAsync` completes before the memory is freed.* The first call sets
     `Closed` at once, so a second call returns a completed task while reads are still
     inside and a segment is still allocated; after the wait of the first call was
     cancelled, a `DisposeAsync` cannot wait for the release either. The rule: the close of
     a database is one operation. Every call of `CloseAsync` and `DisposeAsync` after the
     first joins it and completes when it completes; the token of a call ends the wait of
     that call only and never the close. Tests: with a read held inside the protection the
     first close waits, a second call is not completed, both complete after the read
     leaves and the arena reports nothing allocated; a first call whose token is cancelled
     ends in `OperationCanceledException` and a later `DisposeAsync` still waits for the
     release. The same is checked on the `LatticeDatabase` wrapper if it keeps a state of
     its own.
  2. *The cancellation of an enumeration is lost inside the wait for an epoch slot.* The
     token is checked before a step and not passed on; with every slot taken a cancelled
     `MoveNextAsync` goes on waiting, and after a slot is free it reports
     `ReadLimitExceeded` where the contract promises `OperationCanceledException`. The
     rule: the token of an asynchronous enumeration is honored inside every wait of a step
     — for the position of the session, for a slot of the protection — and between the
     rounds of a step; a cancellation is reported before a limit is. Test: every slot held
     through the internal switch, `MoveNextAsync` cancelled before `ReadTimeLimit` — it
     ends in `OperationCanceledException` while the slots are still held. X6: the wait for
     a slot has the token wherever the caller gave one; the synchronous forms have none.
  3. *Reads by name box a numeric key:* 100,000 reads of absent Int64 keys allocate
     2,400,000 bytes by name against 0 through a class. The contract changes; nothing is
     released. Every member of `ILatticeSet`, and of the batch by name if it takes a key
     as `object`, that takes a primary key gets a form that takes it without a box. The
     shape is chosen by a figure between two plain ones, in the contract step: (a) the key
     parameter becomes generic, `Get<TKey>(TKey key) where TKey : notnull` and so on,
     replacing `object key` — a boxed key still works as `TKey` = `object`; one member per
     operation, but a generic method of an interface is dispatched more slowly; (b)
     overloads beside `object key` for the key families `long`, `ulong`, `Guid` and
     `string` — more members, plain dispatch. Whichever reads by name with 0 bytes and is
     not slower per call than the form through a class beyond noise is taken; if both are,
     (a), for fewer members. The rule of the key stays: an integer of any width that fits,
     a Guid, a string; nothing parsed. Tests: the allocation test of reads by key gains
     the cases by name for Int64, Guid and String keys, present and absent, 0 bytes. The
     measurement scenario of reads by key measures by name as well from now on.
     *Chosen in the contract step, 17 November 2026: (a), the generic key.* Both shapes read
     by name with 0 bytes, and neither is slower than the read through a class (ratios to
     the class read of the same round between 0.87 and 0.99 for both, on a loaded machine);
     the form that took `object` allocated 24 bytes for an Int64 key and 32 for a Guid on
     every call. Against (b) stood, beside 40 members instead of 8: the language would
     convert `char` and `nint` to the `long` overload, so by name would accept what a class
     refuses, and a literal `null` would bind to `string`. What a caller sees: every call
     compiles as before; a literal `null` key no longer compiles. The delete of the batch by
     name changed alike; the delete of the batch through a class keeps `object` until S17
     decides how a batch stores its keys. `IndexKey` and `IndexBound` keep their values as
     `object` and allocate on every search: not this defect, since a search is not
     implemented, and a matter of the contracts of S14 and S15.
- **Out of scope.** The durable step (S27), the batch (S17), a second path for
  MemoryOnly.
