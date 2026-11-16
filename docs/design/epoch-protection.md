# How reads are protected: epochs and memory that is freed later

An explanation for a reader who uses the database or wants to understand it, not a
specification. The rule itself is in the [arena protocol](arena-protocol.md); an
animated version of this page is [epoch-protection.html](epoch-protection.html).

**Status.** Decided by the owner on 16 November 2026 and built in Epic 1, sprint S08,
part 1, for reads by key and the primary index. The figures below are measurements on
one development machine with 20 logical processors; they compare the three protections
and judge nothing else.

## The problem in one picture

A read by key copies an entity out of the database. A write replaces that entity. The
database has to make sure a reader never copies memory that a writer has just given
back.

The plain way is a lock: readers share it, the writer takes it alone. It is correct and
it does not scale. Every reader writes to the same lock, so the processors spend their
time passing that one piece of memory around.

| Protection | Reads, 1 thread | Reads, 16 threads | One writer beside 20 readers |
| --- | --- | --- | --- |
| Shared locks (sprint S06) | 2.3 M/s | 1.5–2.0 M/s in total | 0.55–0.86 M replaces/s |
| Reader counts per processor, writer waits for readers | 2.6 M/s | 21.1 M/s in total | 0.087 M replaces/s |
| Epochs, memory freed later (this page) | 2.0 M/s | 15.8 M/s in total | 0.41 M replaces/s |

The first row reads less with more threads. The second row reads well, and the writer
pays: before it frees the old copy it waits for every reader to finish. With a writer
that inserts and deletes it is the readers that starve instead.

The cause is the same in both rows: somebody waits for somebody else. The mechanism
below removes the waiting. In the third row the writer is slower beside 20 readers than
in the first, and that is the machine, not the protection: 20 readers now really run on
its 20 processors. Beside as many readers of another database the same writer does 0.5
to 0.65 M replaces a second. A writer that inserts and deletes does 0.64 M writes a
second beside 20 readers, against 0.02 M under the shared locks.

## The idea

> A writer never frees memory a reader might still be looking at. It puts that memory
> aside, and it is freed a moment later, once every reader that could have seen it has
> left.

Nobody waits for the other side to finish its work. A reader does not wait for the
writer, and the writer does not wait for readers. The price is that old memory lives a
little longer than it has to.

An everyday comparison: a reading room. Each visitor signs in on their own sheet at the
door and signs out on leaving. When the librarian replaces a book with a new edition,
the new one goes on the shelf at once and the old one goes on a cart. The cart is
emptied when everyone who was already inside at that moment has signed out. Nobody is
asked to leave, nobody queues at the shelf, and nobody finds a book taken out of their
hands.

## The parts

- **The epoch.** One number for the whole database, which only grows. Think of it as
  the page number of the sign-in book.
- **A reader's slot.** The database keeps a table of small slots, at least 128 of them,
  each on memory lines of its own. A read takes the slot of the processor it runs on, or
  the next free one, for the length of that read. On entering it writes the current
  epoch there; on leaving it marks the slot empty.
  This is the only thing a read writes, and no other reader writes to it. That is why
  reads scale with processors.
- **The retired list.** Memory the writer has replaced, each piece marked with the epoch
  at which it was put aside.

## A read

1. Enter: take a slot and write the current epoch into it.
2. Look the key up in the index and follow it to the entity.
3. Copy the entity out.
4. Leave: mark the slot empty.

No lock is taken. A read repeats its copy when a write of the same entity was being
published at that instant. The copy is the caller's own, as every read of this database
returns; nothing the caller holds points into the database.

## A write that replaces an entity

1. Build the new state beside the old one. No reader can see it yet.
2. Publish it: switch the one pointer that leads to the entity. From this instant every
   new reader finds the new state.
3. Retire the old state: put it on the retired list, marked with the current epoch.
4. Return. The writer has not waited for anyone.

The writer does not move the epoch at every write. It moves it, and looks at the
readers' slots, when enough has been retired to be worth freeing; several writes then
share one look.

A reader that had already entered may still be copying the old state. That is fine: the
old state is whole and is still there. It gets one written state, never a mixture.

## Freeing

A retired piece is freed when the epoch has moved past the piece's mark and no reader's
slot holds an epoch as old as that mark. In words: everyone who could have been inside
when it was put aside has left. Readers that entered after the epoch moved cannot have
found it, because the pointer already led elsewhere.

Only the writer frees, on the path of its own writes. The database starts no thread and
no timer for this.

## The index works the same way

The primary index is the table that leads from a key to its entity. A reader looks a
key up while the writer inserts, deletes or enlarges the table:

- An entry of the index is one machine word, so a reader sees it either before or after
  a change, never half written.
- When the table has to grow, the writer builds a larger one while readers go on using
  the old one, switches one pointer, and retires the old table exactly as it retires an
  old entity.
- The entry of a deleted entity is reused only after the readers that could have seen
  it have left, so a reader never follows an old entry to a different entity that was
  created under the same key.

## What an application can rely on

- **Nothing changes in the API.** No call, option or error is added for this.
- **A read is coherent for one entity.** It returns one written state. Several reads are
  still not a snapshot of the database.
- **Reads do not block writes and writes do not block reads.** Adding reading threads
  adds reads, up to the number of processors, and writes keep their pace. Any number of
  threads may write; their changes are published one after another, and "the writer" on
  this page is whichever of them is publishing at that moment.
- **A close is complete.** Closing the database waits for the reads in progress and
  frees everything.

## What it costs

- **Memory is freed slightly later.** A small, bounded amount of retired memory waits
  for the writer's next look at the readers. A read holds memory back only while it
  copies an entity: the database runs no application code and waits for nothing while a
  read is inside, and a slow caller between two steps of an enumeration holds nothing.
- **Retired memory is counted.** It has its own counter, and it counts against the
  memory budget of the instance. A write that meets the budget first frees what can be
  freed and waits for the reads in progress before it is refused.
- **An idle database may keep the last retired pieces** until its next write or its
  close.

## What it is not

- Not a snapshot and not versioning: old states are not kept for readers to choose
  from. An old state lives only until the readers that were already copying it are done.
- Not a garbage collector: nothing of the data is scanned. The writer looks only at the
  readers' slots, and knows exactly what it
  retired and when it may free it.

## A known technique

Nothing here is invented for this database. Protecting readers by epochs and freeing
memory later is a long-established technique of operating systems and in-memory
databases, usually called epoch-based reclamation.

## Words

| Word | Meaning |
| --- | --- |
| Epoch | A number of the database that only grows; it orders "entered before" and "retired after" |
| Enter, leave | The start and the end of one step of a read inside the engine |
| Publish | Switch the pointer, so that new readers find the new state |
| Retire | Detach memory from the database without freeing it yet |
| Drain | Free every retired piece that no reader in progress can reach |
