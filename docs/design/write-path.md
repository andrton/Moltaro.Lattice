# How a write is executed: one at a time, grouped at the persistence step

An explanation for a reader who uses the database or wants to understand it, not a
specification. The rule itself is in the
[milestone contract](milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
an animated version of this page is [write-path.html](write-path.html). How reads are
protected while writes run is explained in [epoch-protection.md](epoch-protection.md).

**Status.** Decided by the owner on 17 November 2026 and built in Epic 1, sprint S09,
part 2; the write batch on a master in sprint S17, part 2, and the batch a child forwards in
part 3. Persistence in PostgreSQL is not built yet; where this page speaks of a slow
persistence step, it describes what the path is prepared for.

## What a write has to guarantee

Every write of the database takes a place in one order. Its place is its *position*,
and three things rest on it: a child applies the master's changes in that order, a
session lets a caller read what it has just written, and a revision tells one state of
an entity from the next. So somewhere every write has to pass a single point, one after
another.

Any number of threads may write. What is single is that point.

## The idea

> A write is prepared and published under one lock, one write at a time. Writes are
> gathered into a group only where something slow has to happen: at the persistence step.

When the persistence step takes no time, as in a database kept in memory only, a write goes
straight through: one lock, taken once, by the thread that made the call. When the
persistence step is slow, the writes that arrive meanwhile are prepared and form the next
group, and one commit of the persistence step serves the whole group.

## The parts

- **The lock of the writes.** One per database. Whoever holds it is the only one
  changing the database. Reads never take it.
- **The open group.** The writes that are prepared and wait for the persistence step.
- **The persistence step.** Makes a group durable and gives its writes their positions. In
  memory only it completes at once. With PostgreSQL it is a commit, which takes time.
- **Publication.** The moment readers can find the new state, and the position of the
  database moves on.

## A write when persistence takes no time

1. The calling thread takes the lock of the writes.
2. It prepares the write: looks the key up, checks the condition of the write (for an
   insert, that no such entity exists; for an expected revision, that it still holds),
   and encodes the values into memory that no reader can see yet.
3. The persistence step completes at once and gives the write its position.
4. Still holding the lock, the thread publishes the write and releases the lock.
5. The call returns the result.

Nothing is handed to another thread, and nothing waits.

## Writes when the persistence step is slow

1. A group is in the persistence step. The lock of the writes is free meanwhile: the wait
   for persistence happens outside it.
2. Another caller takes the lock, prepares its write as above, puts it into the open
   group and releases the lock. It now waits for its result.
3. More callers do the same. A group holds at most 32 writes or 8 MiB; a larger write
   forms a group alone. A group never waits to fill: it takes what is there.
4. The persistence step completes. The writes of its group are published in order, each at
   its own position, and their callers get their results.
5. The open group goes to the persistence step, as one commit.

One slow commit is thus shared by all the writes that arrived while the one before it
ran. The more callers write, the larger the groups, and the less each write pays for
persistence.

## Two writes to one entity

A write is checked against the *published* state. If an entity has an earlier write
that has not completed, a later write to the same entity cannot be checked correctly,
so it waits behind that one, first in first out, and holds nothing while it waits. Not
completed means prepared and not published yet, or itself still waiting its turn.

When the earlier write completes in any way, published, refused or cancelled, the later
one takes its turn and is checked against the state that is published then. So a later
write never overtakes an earlier one, whatever became of it.

When persistence takes no time this happens only while a write refused for lack of memory is
being settled, and when the writes it held back are prepared together at its end: otherwise
a write is published in the same hold of the lock that prepared it.

A unique index is the same question across entities. A write whose value a published
entity holds is refused at once, unless that entity has a write prepared and not published
that gives the value up, a delete or a key move; a write whose value a write of another
entity, prepared and not published, takes, an insert or a key move, cannot be checked
either. In both cases the write waits for that one write, holding nothing, and when it
completes in any way the waiting write is checked again from the start against what is
published then. The write it waits for waits only for its persistence step, so the wait
always ends. When persistence takes no time this happens only when the writes held back
while a write refused for lack of memory is settled are prepared together at its end:
otherwise a write is published before the next one is prepared.

The two waits are one wait for another write (`PendingWriteStateEnum.WaitingForWrite`): the
waiting write holds nothing, stays first among the later writes of its entity, and when the
write it waits for ends in any way it is started again from the beginning, so it waits again
behind whatever then shares an entity with it, and is checked against what is published
then. No two writes wait for each other: a wait for an entity goes to a write admitted
earlier, and a wait for a value to a prepared write, which waits for nothing but its
persistence step.

## A write batch

A batch is one write with several changes: one admission, one preparation, one place in a
group, one position. A group never splits it, and it is never refused for its size: like
any write, only a batch that does not fit into the memory beside the stored data is refused,
as a whole, before anything is written.

- **Members are captured before commit.** The engine encodes a complete write to a
  root when the application adds it, outside the writes lock. An update already holds
  a snapshot of each command. The batch checks the copied content and the layout at
  addition and consumes the update on success. A child encodes the commands when the
  batch commits. The record of the batch releases its members when it records the
  outcome.
- **Prepared as a forwarded write is.** Member by member, in their order, the key is looked
  up and the condition checked. The master places a complete root or prepares an
  edited root from the current one and the member's ordered commands. Then all the
  changes reserve together, each index once for their sum, and the unique indexes are
  checked on the state the batch leaves: two
  members may exchange values, and a delete frees a value another member takes. Which member
  fails is judged in two phases
  ([Q58](../work/open-questions.md#q58-which-member-of-a-batch-is-the-failing-one-when-two-of-them-claim-one-unique-value)).
  First each member against the state before the batch, in their order: the first member
  that fails a condition of its own, a revision or a key that must be absent or present, is
  reported. Only when every member passes are the unique values judged, on the state the
  batch leaves, which does not exist before: of the members that violate one, the
  lowest-numbered is reported. So of two members that claim one value the later is
  reported, since the earlier alone would be accepted; against a published owner that no
  member frees, the member that claims its value; and of the indexes one member violates,
  the one declared first in its entity type. A failing member refuses the whole batch and
  reports its place.
- **Waits as one write.** The entities of a batch are one set. A batch waits for the latest
  earlier write, not completed, that shares an entity with it, and a single write whose
  latest earlier write of its entity is a batch waits for that batch; two single writes
  keep their chain. Batches of the same entities in opposite orders never wait for each
  other in a cycle.
- **Asked through its claims.** A batch that is prepared and not published answers a later
  write that asks who claims or who frees a value through its claims sorted by the order of
  each unique index, by a binary search, not by a scan of its members
  (`Engine/Entities/UniqueClaims.cs`). The claims are sorted on the first question.
- **Published whole.** Its changes are published at one sequence as one publication (below),
  and its transaction carries one effect for each change, with the number of its member.
  A partial effect carries the commands that changed the entity and the sequence of the
  root they extend. The successful result gives the outcome of every submitted command,
  including no-ops.
- **Revisions of their own.** The members share the position of the batch; the number of a
  member is part of its revision, from 1 in their order for a batch of two or more, 0 for a
  single write and for a batch of one, whose revision is then that of a single write
  ([milestone contract](milestone-1-engine-contract.md#write-coordinator-and-revision-semantics)).

**Why a write that starts again never chains behind the wrong write, and why the waits end.**
The argument rests on two facts of the coordinator (`Engine/Writes/WriteCoordinator.cs`). Every
write admitted and not completed whose call has left the lock is in one list, in the order of
admission; the one write not in it yet is the write of the call that holds the lock, which is
admitted, started and added in that one hold. And a write that starts, at its admission or
again after the write it waited for has ended, looks for the latest write of that list that
shares an entity with it and was admitted before it, searching back from its own place
(`FindLastSharing`).

- *It never chains behind a write that already has a later one.* Chains link single writes of
  one entity only. Say a single write W starts and finds the single write L, and L already has a
  write X behind it in its chain. X is a single write of the same entity, admitted after L. If X
  was admitted before W, X lies between L and W in the list, so the search back from W meets X
  before L and does not find L. If X was admitted after W, then W was in the list when X started,
  later than L and of the same entity, so X found W, not L, and chained behind W or waited for
  it. Either way L has nothing behind it when W finds it, so W takes the place right behind L,
  and the writes that were chained behind W, while it waited, stay behind it.
- *No two writes wait for each other.* A write waits in a chain, or for the write it shares an
  entity with, only for a write admitted before it, since the search goes back from its own
  place. It waits for a value only for a write that is prepared, and a prepared write waits for
  nothing but its persistence step. The writes being resolved after a refusal for memory wait for
  writes admitted before them or for prepared writes, and the writes they defer wait for them,
  which were admitted earlier. So every wait goes to an earlier admission or to a write that waits
  for nothing, and a cycle would need a wait to a later admission that is itself waiting: there is
  none. Every wait ends when the write it waits for ends, and that one ends because the writes it
  waits for are earlier still, down to a write that waits for its persistence step alone.

## How a publication is made

A publication installs what the preparation made, and nothing else: it obtains no
memory, grows no index, adds no root entry and runs no code of the application, because
the preparation took and reserved everything it uses. One part makes it, on a master for
a write and on a child for what it receives from its master
(`Engine/Entities/ChangePublisher.cs`). A master publishes the one change of a single write
or the changes of the members of a batch; a child publishes every change of a transaction
at once, or one root of its load. A
publication at the sequence s has three steps:

1. **The stores before the position.** First the new entries of the secondary indexes of
   every change, of an insert and of a replace that moves a key, each by the one store of
   its tree ([ordered index](ordered-index.md#the-entries-of-one-write)); a search that
   finds one checks it against its root. Then, change by change, the stores of each entity
   with the stamp s: an insert installs its root, then its root entry, with the stamp s and
   the state Live stored last, then its entry of the primary index, which publishes both to
   a read by key. A replace stores the mark of a write in progress in the root entry, then
   the new root, then the stamp s. A delete stores the mark, then the state Deleted, then
   the stamp s.
2. **The position.** The position of the database becomes s, once every entity of the
   publication is stored. A root of a load has no position of its own: the end of the load
   sets it.
3. **The stores after the position.** A delete removes its entry of the primary index, and
   a replace and a delete take their old entries out of the secondary indexes, or rewrite
   an entry whose key is equal in other bytes. What they replaced or deleted is retired,
   and freed once no read can reach it.

Each store is a release, so a read that sees one sees every store before it, the values
the preparation wrote included. A read does not take a stamp above the position of the
database, which it loads right after the stamp ([the arena
protocol](arena-protocol.md#the-stamp-of-a-root-entry)), and the position is s − 1 until
step 2, so a read never returns a state at a position at which the master did not have it,
and never an entity of a publication in its new state before its position:

- A read that finds an inserted key before step 2 finds the stamp s above the position
  and waits for it; one that misses it read s − 1 before its lookup.
- A read of a replaced or deleted key returns its old state only if the mark was not
  stored yet when it loaded the stamp the second time; the mark comes before the
  position, so that read saw the position s − 1 or earlier.
- A read that finds the mark, or the stamp s before step 2, backs off until the position
  is s; then it reads the new root, or the state Deleted at s, with the position s or
  later.
- A read that misses a deleted key does so after step 3, at s or later.

A read that begins at or after step 2 sees every entity of the publication in its new state,
also while step 3 is still taking the old entries away: a search passes over an old entry by
its root. A search or an enumeration that runs across the publication may return some of its
entities as they were before it and others as after, as separate reads may
([ordered index](ordered-index.md#how-a-range-is-walked)).

A read waits only when it meets an entity of the publication, for as long as the
publication takes in memory, which for a batch and for the transaction of a child grows with
its changes (exception X6). A delete of a key that is absent changes no entity and publishes only its
position. Once step 1 has begun nothing can fail but a defect, or the fault point a test of
the engine arms between steps 1 and 2; either faults the database, and a read that backs off
then gives up with `Unavailable` instead of waiting for a position that will never come.

### Waiting for a published position

Under the lock of the writes, step 2 takes the position clock's gate. `PositionClock`
publishes the coherent sequence with a volatile store. Immediately afterwards, if
the sequence reaches any registered targets, the clock samples monotonic time and
records it as their first reach. Later publications leave that sample intact. Each
waiter of a target compares the shared sample with its own deadline. The budget
starts before registration, and a sample at the deadline counts as timely.

Registration and recording first reach use the same gate. If publication has already
reached the position when a wait registers, the clock samples time after observing
the sequence under the gate. The wait can succeed if that observation is within its
original budget and its token has not been cancelled. An already satisfied first
check uses the local sequence and lifecycle state without a gate or time sample.

A publisher calls `Wake` after it releases the lock of the writes. Timer delivery is
independent of `Wake`. After checking the database state, a registered waiter checks
its own token before deciding the outcome from the target's reach time, a lost master
or an expired budget. The token can already carry a cancellation request while
another callback delays notification. A wait can succeed on expiry when its target
was reached in time, or time out when a late publication arrived before the timer ran.
The sequence store and timestamp sample are separate operations. A scheduling pause
between them can move the sample past the deadline. The clock then reports timeout
because it lacks timely evidence. A wait checks `ReadEpochs.Fault`'s authoritative
mark before returning success. A mark already set causes `Unavailable`, including
during the interval before the position clock has been notified of the fault.

When the master has confirmed a forwarded write, an application timeout reports the
committed position and the missed synchronization deadline. The child may have
applied the position by the time the caller observes the error. The underlying failure
identifies a lost master or a fault when either ends the wait early.

The clock gate protects in-memory waiter bookkeeping and its order is always the
lock of the writes followed by the clock gate. A waiter takes only the clock gate
and waits outside it. The notifying thread holds neither lock when it completes
signals.
The [session-consistency decision](../decisions/session-consistency.md) owns the
guarantee exposed to the application.

## What an application can rely on

- **Order for one entity.** Writes to one entity are executed in the order their calls
  were made: a call made after an earlier call has returned its task is executed after
  it, awaited or not. A write that first waits for the position of its session takes
  its place when that wait ends. Calls that race on different threads have no order
  between them.
- **A write is whole.** Readers see the state before a write or after it, never a
  mixture. So do they each member of a batch, on the master and on a child: no answer
  carries the position of the batch before all of it is visible, and a read that begins
  once its node has reached that position sees all of it. A search or an enumeration that
  runs while the batch is published may return some members as they were before it and
  others as after, as separate reads may.
- **Cancellation is honest.** Before a write is handed to the persistence step, cancelling
  it withdraws it: it did not run and left nothing behind. After that, the call returns
  what really happened.
- **Nothing is refused for being busy.** Writes that arrive faster than they complete
  wait their turn. A write is refused only when it does not fit into the memory of the
  database beside the stored data.
- **Reads are not held up.** A read takes no lock and goes on while writes run.

## What it costs

- **Writes do not speed up with more writing threads.** The work in memory is serial,
  so several threads write about as much in total as one, or somewhat less. Every
  database that keeps one order of writes in memory pays this; what scales with callers
  is the persistence step, through groups.
- **A very large entity holds the others back.** Its values are encoded under the lock,
  so other writes wait for it. Reads do not. Measured: one write of 32 MiB holds the
  lock for about 3 ms.
- **The lock is not fair.** Under many writing threads a single write can wait longer
  than the average. Measured at its worst: beside a caller that writes entities of
  32 MiB one after another, a caller of small writes completed 1,500 to 4,300 a second
  where alone it completes about two million, and a single small write waited up to
  about 110 ms. A write that large gives its processor up once after it releases the
  lock; that helps and bounds nothing. Measured with and without that yield alternated, on
  a machine with other load, beside a caller that writes entities of 32 MiB one after
  another: the p99 of the small writes of another caller was 98 to 115 ms in 12 of 14
  rounds without it and a few µs in the other two, and with it below 18 ms in 10 of 15
  rounds and 24 to 59 ms in the others; the maximum stayed at 110 to 125 ms either way.
  It bounds nothing because a yield gives the processor only to a thread ready on the
  same one, and the lock of .NET lets arriving callers pass a waiting one until that one
  has waited 100 ms. Ordinary writes pay one comparison of their size for it. A gate that
  hands the lock over first in first out was measured and is not used: it bounded the
  wait of a small write behind large ones to one large write, but small writes then went
  no faster than the large ones, and with more threads than processors, 16 writing callers
  beside 16 readers, the writes all but stopped.
- **Measured in memory only**, on the development machine: one calling thread replaces
  about 1.6 million entities a second, 16 threads about 1.0 million in total; reads by
  key on 16 threads beside them stay at about 15 million a second.

## A known design

Nothing here is invented for this database. One writer at a time under one lock is how
established embedded stores execute writes, and gathering the writes that arrive during
a commit into the next commit is the group commit that database servers have used for
decades.

## Words

The words of the engine, as its code and its comments use them, each with one meaning.

| Word | Meaning |
| --- | --- |
| Position | The place of a write in the one order of the database |
| Prepare | Check the condition of a write and encode its values where no reader sees them yet |
| Group | The prepared writes that one commit of the persistence step takes together |
| Persistence step | Makes a group durable and assigns positions; instant in memory only |
| Publish | Make a prepared write visible to readers and move the position on |
| The lock of the writes | The one lock of a database under which writes are admitted, prepared, handed to the persistence step and published; held for work in memory only, never across I/O or a wait |
| A hold | One time a thread takes the lock of the writes and releases it; what a hold decides for other threads, an outcome or a wake-up, is carried out after it. The verb keeps its plain sense, as in the doubles of the tests that hold something back (`HeldPersistence`, `HeldRead`) |
| The protection of reads | The epochs a read enters and leaves ([how reads are protected](epoch-protection.md)): memory that writes retire while a read is inside is freed only after it has left |
| Storage | The form of the entities in memory: the arena, the roots and their layouts, and the Plain or Dictionary storage of a text |
| Persistence | What makes a write durable: the persistence step of a Durable database, which MemoryOnly completes at once |
| Deferred write | A write admitted while a write the arena refused twice is resolved; it waits, first in first out, until the resolution ends |
| Waiting for a value | A write whose value of a unique index a prepared, unpublished write of another entity takes or gives up; it waits for that one write and is prepared again when it completes |
| Wait for reads | The last step of that resolution: outside every lock it waits for the reads that hold retired memory, then tries once more |
| Capture | What a master gives a child that loads from it: its state at one sequence, copied a root at a time while writes go on |
| Deliver | Send the children that follow a master the transactions after their capture |
| Pass | One enumeration over the entities of a type, a step at a time |
| Switch | A hook of the tests that runs an action once at one place of the engine, so that a test knows a step has been reached: the read switch inside a read by key, the growth switch in the growth of a primary index, the wait switch at the first wait of such a resolution; armed by `ArmReadSwitch`, `ArmGrowthSwitch` and `ArmWaitSwitch` |
