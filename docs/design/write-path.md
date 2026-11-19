# How a write is executed: one at a time, grouped at the storage step

An explanation for a reader who uses the database or wants to understand it, not a
specification. The rule itself is in the
[milestone contract](milestone-1-engine-contract.md#write-coordinator-and-revision-semantics);
an animated version of this page is [write-path.html](write-path.html). How reads are
protected while writes run is explained in [epoch-protection.md](epoch-protection.md).

**Status.** Decided by the owner on 17 November 2026 and being built in Epic 1, sprint
S09, part 2. This page describes the intended behavior. Storage in PostgreSQL is not
built yet; where this page speaks of a slow storage step, it describes what the path is
prepared for.

## What a write has to guarantee

Every write of the database takes a place in one order. Its place is its *position*,
and three things rest on it: a child applies the master's changes in that order, a
session lets a caller read what it has just written, and a revision tells one state of
an entity from the next. So somewhere every write has to pass a single point, one after
another.

Any number of threads may write. What is single is that point.

## The idea

> A write is prepared and published under one lock, one write at a time. Writes are
> gathered into a group only where something slow has to happen: at the storage step.

When the storage step takes no time, as in a database kept in memory only, a write goes
straight through: one lock, taken once, by the thread that made the call. When the
storage step is slow, the writes that arrive meanwhile are prepared and form the next
group, and one commit of the storage serves the whole group.

## The parts

- **The lock of the writes.** One per database. Whoever holds it is the only one
  changing the database. Reads never take it.
- **The open group.** The writes that are prepared and wait for the storage step.
- **The storage step.** Makes a group durable and gives its writes their positions. In
  memory only it completes at once. With PostgreSQL it is a commit, which takes time.
- **Publication.** The moment readers can find the new state, and the position of the
  database moves on.

## A write when storage takes no time

1. The calling thread takes the lock of the writes.
2. It prepares the write: looks the key up, checks the condition of the write (for an
   insert, that no such entity exists; for an expected revision, that it still holds),
   and encodes the values into memory that no reader can see yet.
3. The storage step completes at once and gives the write its position.
4. Still holding the lock, the thread publishes the write and releases the lock.
5. The call returns the result.

Nothing is handed to another thread, and nothing waits.

## Writes when the storage step is slow

1. A group is in the storage step. The lock of the writes is free meanwhile: the wait
   for storage happens outside it.
2. Another caller takes the lock, prepares its write as above, puts it into the open
   group and releases the lock. It now waits for its result.
3. More callers do the same. A group holds at most 32 writes or 8 MiB; a larger write
   forms a group alone. A group never waits to fill: it takes what is there.
4. The storage step completes. The writes of its group are published in order, each at
   its own position, and their callers get their results.
5. The open group goes to the storage step, as one commit.

One slow commit is thus shared by all the writes that arrived while the one before it
ran. The more callers write, the larger the groups, and the less each write pays for
storage.

## Two writes to one entity

A write is checked against the *published* state. If an entity has an earlier write
that has not completed, a later write to the same entity cannot be checked correctly,
so it waits behind that one, first in first out, and holds nothing while it waits. Not
completed means prepared and not published yet, or itself still waiting its turn.

When the earlier write completes in any way, published, refused or cancelled, the later
one takes its turn and is checked against the state that is published then. So a later
write never overtakes an earlier one, whatever became of it.

When storage takes no time this happens only while a write refused for lack of memory is
being settled: otherwise a write is published in the same hold of
the lock that prepared it.

## How a publication is made

A publication installs what the preparation made, and nothing else: it obtains no
memory, grows no index, adds no root entry and runs no code of the application, because
the preparation took and reserved everything it uses. One part makes it, on a master for
a write and on a child for what it receives from its master
(`Engine/Entities/ChangePublisher.cs`). A master publishes the one change of a write; a
child publishes every change of a transaction at once, or one root of its load. A
publication at the sequence s has three steps:

1. **The stores before the position.** First the new entries of the secondary indexes of
   the entity type, of an insert and of a replace that moves a key, each by the one store
   of its tree ([ordered index](ordered-index.md#the-entries-of-one-write)); a search that
   finds one checks it against its root. Then an insert installs its root, then its root
   entry, whose state Live is stored last, then its entry of the primary index, which
   publishes both to a read by key. A replace stores the mark of a write in progress in
   the root entry, then the new root. A delete stores the mark, then the state Deleted.
2. **The position.** The position of the database becomes s. A root of a load has no
   position of its own: the end of the load sets it.
3. **The stores after the position.** A replace and a delete store the stamp s in the
   root entry; then a delete removes its entry of the primary index, and both take their
   old entries out of the secondary indexes, or rewrite an entry whose key is equal in
   other bytes. What they replaced or deleted is retired, and freed once no read can
   reach it.

Each store is a release, so a read that sees one sees every store before it, the values
the preparation wrote included. The position is s − 1 until step 2, so a read never
returns a state at a position at which the master did not have it:

- A read that finds an inserted key reads its stamp s, at s; one that misses it read
  s − 1 before its lookup.
- A read of a replaced or deleted key returns its old state only if the mark was not
  stored yet when it loaded the stamp the second time; the mark comes before the
  position, so that read saw the position s − 1 or earlier.
- A read that finds the mark backs off until step 3; one that loads the stamp s reads
  the new root, or the state Deleted at s, with the position s or later.
- A read that misses a deleted key does so after step 3, at s or later.

A delete of a key that is absent changes no entity and publishes only its position. Once
step 1 has begun nothing can fail but a defect, or the fault point a test of the engine
arms between steps 1 and 2; either faults the database, and a read that backs off on a
mark then gives up instead of waiting for a stamp that will never come.

## What an application can rely on

- **Order for one entity.** Writes to one entity are executed in the order their calls
  were made: a call made after an earlier call has returned its task is executed after
  it, awaited or not. A write that first waits for the position of its session takes
  its place when that wait ends. Calls that race on different threads have no order
  between them.
- **A write is whole.** Readers see the state before a write or after it, never a
  mixture.
- **Cancellation is honest.** Before a write is handed to the storage step, cancelling
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
  is the storage step, through groups.
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

| Word | Meaning |
| --- | --- |
| Position | The place of a write in the one order of the database |
| Prepare | Check the condition of a write and encode its values where no reader sees them yet |
| Group | The prepared writes that one storage commit takes together |
| Storage step | Makes a group durable and assigns positions; instant in memory only |
| Publish | Make a prepared write visible to readers and move the position on |
