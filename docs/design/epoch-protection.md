# How reads are protected: epochs and memory that is freed later

An explanation for a reader who uses the database or wants to understand it, not a
specification. The rule itself is in the [arena protocol](arena-protocol.md); an
animated version of this page is [epoch-protection.html](epoch-protection.html).

**Status.** The engine uses epochs for its owned reads and protected views by name.
The figures below compare the earlier owned-read protections on one development
machine with 20 logical processors.

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

A write publishes while reads are in progress. A reader that already took an entity
can keep reading that state. Retired memory stays until those readers leave.

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

No lock is taken. Before taking a root, a read checks the entry's stamp and the
published position. The owned copy belongs to the caller and holds no arena memory.

A [protected view](arena-protocol.md#protected-views) takes the same epoch and root.
It lends the state to a scoped synchronous callback, which leaves the epoch on return
or exception. `ViewAsync` waits for its session before taking a slot. Publication can
advance while the viewer keeps reading that root.

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

A write may free eligible retirements while holding the local writes lock. The
periodic maintenance worker on a Ready master or child also takes that lock and
drains eligible retirements in bounded steps. It can free an eligible retirement
after the reader leaves even when the application issues no further writes.

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

- **The set by name offers a protected view.** `TryView` and `ViewAsync` lend
  the state during a short synchronous callback.
- **A read is coherent for one entity.** It returns one written state. Several reads are
  still not a snapshot of the database.
- **Publication proceeds alongside active readers.** Any number of threads may write.
  Their changes are published one after another. The writer on this page is the thread
  publishing at that moment.
- **A close is complete.** Closing the database waits for the reads in progress and
  frees everything.

## What it costs

- **Memory is freed later.** An owned read holds an epoch while it copies an entity.
  An enumeration holds nothing between steps. A viewer holds an epoch until its
  callback finishes. Under memory pressure it can delay a write or child application.
  Closing and new reads when the slot table is full can also wait for a viewer.
- **Retired memory is counted.** It has its own counter, and it counts against the
  memory budget of the instance. A write that meets the budget first frees what can be
  freed and waits for the reads in progress before it is refused.
- **An idle Ready database drains safe retirements in bounded turns.** A held reader
  delays the slots it can still reach. Their reclamation can proceed after it leaves.

## What it is not

- Not a snapshot and not versioning: old states are not kept for readers to choose
  from. An old state lives until the readers that could reach it finish and the next
  drain reclaims it.
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
