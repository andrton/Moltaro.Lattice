# Arena access, publication and reclamation protocol

The engine's arena layout, ownership and publication protocol. The early gated arena
experiment of 13 November 2026 is recorded below with its original scope. Evidence
and limitations belong to the experiment's README and the prototype evidence record.

[Engine foundation](engine-foundation.md) · [Allocation policy](../decisions/allocation-and-compaction.md) · [Read access](../decisions/memory-and-access.md)

The subsequent [bounded experiment](../../src/Prototypes/ArenaLifecycle/README.md) passed
its directed, churn and paced concurrent checks. Its [Arc index extension](../../src/Prototypes/ArenaLifecycle/INDEXES.md)
also checks index-owned text, equal-key descriptor replacement and candidate handoff.
This is evidence for the specified fixtures, not all schedules or production owners.

## Ownership and addresses

The native allocator owns segments and size-class slots. A block has a relative
segment/offset address, logical length, capacity, state and owner. Slots are free,
prepared, live or retired. Readers and other retainers protect a detached retired
slot until reclamation. The native arena budget includes prepared blocks and
relocation destinations alongside published data.

A managed struct control entry identifies one root lifetime: key, generation,
published revision, root address and live or deleted state.
The primary index reaches this entry, never an unprotected native pointer. Owned
chunks identify the root lifetime and the owning edge that must be repaired when
they move. A shared text descriptor names a stable text ID. The reader acquires the
current address from the text locator. Each stored root, prepared root and index
node holds its own references during its lifetime. A separator remains a text owner
after the final indexed entity with that text is removed.

All addresses are internal. Relocation changes addresses, not keys, revisions or
logical contents. Published dictionary text is immutable; an update acquires a
reference to the replacement text. It never overwrites a string shared by roots.

## Stored structures and root transfer

The engine uses the same fixed structure for the fields of an entity and for a collection
entry. A List or Set entry has one member. A Map entry has a key and a value. A structure
packs a null bitmap, the fixed-width fields and a consecutive run
of 16-byte descriptors, without padding. Owned values are flattened into the structure
that contains them. Each group follows the order of the paths of the field identities.
A Reference occupies the form of its target's key. A collection occupies one descriptor.
The layout compiles a table of those places and each collection's entry layout. It is
immutable and shared by all threads that read or write roots of that type.

The bitmap uses the low bit of its first byte for the first nullable position. A set bit
means null. A nullable owned value has its own presence bit and every field underneath
has a bit, including fields that would otherwise require a value. When the owned value
is absent, every bit below it is set. Bits beyond the last position are clear. Local
writers zero the fixed-width bytes under a null bit. On receipt those fixed-width bytes
may have any contents, since readers consult the bit first and skip the bytes. A null
descriptor must be all zero, including on receipt. This follows the
[trust boundary for received roots](../decisions/replication-and-hosting.md#trust-between-the-nodes-of-one-database)
between structure validation and value validation.

### Where the bytes lie

```text
root block              block payload begins
                        v
[32-byte header]        [bitmap | fixed-width fields | descriptors] [inline values]
^                       ^ structure begins
root place offsets and inline locations count from here

block of records        block payload and first record begin
                        v
[32-byte header]        [bitmap | fixed | descriptors] [record 1] ... [inline values]
^                       ^ record 0 place offsets count from here
inline locations count from here, across all the records in this block
```

A record starts at its stride within the payload. Its field and descriptor offsets
count from that record's start. An inline descriptor's location counts from the
start of the block that holds it, with the header included. A reader
uses the structure's bytes to find the descriptor and the whole containing payload to
reach the inline value. For a root, place offsets also include the header, so subtract
the header size when applying those offsets to the payload. The fixed part of a root,
including the header, fits in at most 65,536 bytes. The record of an entry fits in a
chunk with room for the inline values it can describe. Records that contain native descriptors align their descriptor locations and
record strides. The engine acquires each location atomically, including in later
packed records. The loader validates
canonical transfer descriptors and writes the native layout separately.
The engine compiles the layout of every declared entity type at open. A layout
beyond either limit fails open with
`InvalidSchema`, which names the entity type. The failed instance is Closed.

A String, a Binary or packed collection entries are placed by encoded length. Packed
entries have a fixed stride and contain no descriptors. An entry can cross a chunk
boundary. Up to 64 bytes fit inline when the block has room, including
an empty value at the current inline end. A value that goes out of line uses one block
through 4 KiB and a directory of chunks above it. A chunk holds up to 65,504 payload
bytes. The directory holds one descriptor per chunk, up to 4,094 chunks, so the byte
limit for one value is 268,173,376. A larger value is refused with `ValidationFailed`.

A collection whose entries contain descriptors is stored as whole records, with an
inline area after the last record of each block. A descriptor of kind Records carries
the entry count and names the block of records or its directory of record blocks. An
empty collection of this form has location zero. One chunk reserves enough
room for each record and up to 64 bytes for each of its descriptors. Every chunk except
the last holds the resulting number of records per chunk. The maximum element count is
that number times the capacity of a chunk directory. An out-of-line value block or
directory is owned by the block containing the descriptor. A chunk is owned by its
directory.

### Map and Set lookups

A whole Map or Set written or received with up to eight entries stores them under the same descriptor
as a List. Lookup scans the entries with the comparison of the key or member
declaration. A larger collection has a Lookup descriptor with the entry count and
the address of its lookup block. The block begins with the entries descriptor at
offset 32. Packed entries of a hashed collection use their own block or chunks.

Partial edits switch from compact to hashed when the ninth entry is added and
back to compact when a removal takes the count from five to four. At counts
from five through eight the resident form therefore depends on the preceding edits.

The rest of a contiguous lookup block is a sequence of eight-byte slots. Each
occupied slot holds a 32-bit hash and the entry ordinal plus one. The all-zero slot
is empty. To find the home slot, multiply the hash by the slot count and take the
high 32 bits of the product.
Collisions use linear probing. Home and every probe use the same active capacity,
with occupancy at most 70 percent. The largest contiguous lookup has 8,186 slots.
The allocator charges whole size-class slots, including padding outside the active
payload.

Above that capacity, the lookup block contains the eight-byte relative addresses of
its slot chunks. Full chunks have 8,188 slots and the last chunk fills its own size
class. A lookup uses at most 4,094 slot chunks. This gives a bound of 23,465,170
entries. The entry layout can impose a smaller bound. A slot chunk's backlink
names its address position at offset 48 plus eight times its chunk ordinal.
The lookup owns its entries through the descriptor at offset 32. A compatible
unreferenced spare slot can remain available to the writer after the retirement
epoch. It lies outside every active probe range. Growth can reuse such a slot.
If retained capacity stays strictly above four times the required slot count for
five seconds, bounded maintenance returns spares until retained capacity reaches at
most twice the requirement. A required foreground allocation may recover a ready
spare when the arena has exhausted its ordinary allocation paths at the native
limit. An optional index-reserve refill uses the ordinary paths. Prepared borrows
and retired slots remain protected.

Text keeps its encoded bytes as specified by the schema. A nonempty Dictionary text,
or a text value longer than 64 encoded bytes used as a secondary index part, uses an
entry of the text table. This also applies to an index path into an owned value.
The descriptor of kind Text holds one reference to that entry. The entry owns its
own blocks, allocated by the same length rule as other values out of line. An empty
text is inline. Short Plain text, including indexed Plain text, follows the ordinary
inline and out-of-line placement rule.

### Transfer order and checks

The transfer of a root starts with its layout version in little-endian order, then
the root payload, then the values it holds out of line in descriptor order. Capture
adds the type id and last mutation sequence before that body. A collection of records is
written in preorder. Each record block is sent first as its whole payload. The
out-of-line values it names follow, record by record and descriptor by descriptor,
recursively for collections of records. Inline bytes travel with their containing
payload and keep their offsets. Each out-of-line location is sent as zero,
including the locations of text entries and record blocks. Dictionary text is sent as
bytes for each reference. Native headers, capacity slack and chunk directories never
travel. The payload and value bytes use the machine's byte order, which nodes agree on
in the handshake. A whole-root body carries a Map or Set through the descriptor and
entries a List would use. Export substitutes the entries descriptor for the local
Lookup descriptor in the containing payload. Both stored export and write forwarding
use an out-of-line descriptor for short packed entries of a hashed collection. The
receiver rejects an inline descriptor for a hashed collection. Existing List bodies
retain their byte layout. Wire format 7 adds sparse committed effects and preserves
the whole-root layout version 2. Peers with older wire formats are refused at the
handshake. [Replication transport](milestone-1-engine-contract.md#replication-transport-and-initial-loading)

The export writes the block payload when the walk enters it. The same walk visits a
descriptor after any record blocks it names, so freeing can safely release children
before their parent. The length calculation uses the same walk as writing. Capture
protects the published root from reclamation while it copies. The delivery of a write
can copy the new root under the writes lock.

Before it allocates a root, a receiving node checks the complete body against the
layout. It checks the layout version, the agreement of the null bits and the
descriptors, descriptor kind and length or count, and that the wire uses zero for
out-of-line locations. Packed sizes must give a whole number of elements and the
element bitmaps have the same rules as other structures. The check accounts for every
inline range after the fixed part of the root or after the records of one block. They
must follow one another in location order, without gaps or overlaps, to the exact
end of the payload. The order of the descriptors is independent of the order of the
inline locations. Each block must fit its storage. The check bounds counts and lengths
by the bytes remaining before reading them and consumes the whole input. Invalid
structure is `ValidationFailed`. The check uses scratch storage bounded by the
layout and block sizes. The receiver uses the checked representation to read the key,
validating the encoding when the root key is text. Other field values travel as bytes.

### Pending placement

The checked root payload and every copied record block leave the descriptors of
values that follow them pending. A pending descriptor has kind Null, a nonzero
length or element count and location zero. It names no storage. The walk therefore
treats it like any other descriptor of kind Null and can free a root whose placement
stopped partway through. Where a List directory is already allocated, entries of
chunks not yet allocated are zero and name no chunk.

Placement uses the local writer's allocation rules to replace pending descriptors
with local locations, leaving the inline offsets intact. Record blocks are linked
before their contents are placed, so a failure can free every block obtained so
far. Each completed block of records ends where the last inline value ends. A local
Dictionary entry is found or added by its bytes. Slot accounting includes the
prepared root, its blocks and any added text entries, the same way the writer
counts them. A memory budget refusal returns the null address and leaves no part of
the root allocated. A native allocation failure propagates after the same cleanup.

For a hashed collection, the new lookup payload is initialized before the holder
receives its address. Each acquired chunk becomes reachable from the initialized
directory. The lookup is built with the receiver's instance seed. Placement checks
uniqueness for both forms, including compact packed entries already inline. A
duplicate fails before publication and the transaction gives back every prepared
effect. A child skipping an older effect checks the same invariant using validated
wire slices and temporary scratch. That check uses its own cursor and leaves the
arena unchanged.
The caller publishes the prepared root when the write or load is ready.

## Lock and lifetime order

1. Under the short catalog gate, look up a root and claim its entry lifetime.
   Release the catalog gate before waiting for root protection.
2. Acquire root protection and revalidate that the claimed lifetime is still live.
   A deletion already published means absence. Never redirect an old claim to a
   replacement root with the same public key.
3. Access owned blocks only under root protection. Access dictionary bytes under
   additional dictionary protection. Neither a span nor an address escapes.
4. Release dictionary/root protection before dropping the lifetime claim under
   the catalog gate. Reuse a deleted control slot only after its last claim drains.

In the indexed fixture, nested memory work follows root -> catalog/index -> dictionary
-> allocator. The catalog/index gate may protect a text comparison but never waits
for a root gate: pin/lookup/release scopes end before root acquisition. The allocator
never acquires an owner gate. A compactor discovers a candidate under allocator
protection, releases it, then acquires the owner and revalidates the candidate.

The experiment uses striped reader/writer gates for roots, one dictionary gate and
one allocator gate. This deliberately bounds lock-object overhead; unrelated roots
sharing a stripe can contend. Production packed root gates and dictionary sharding
are not established by these measurements, and the experiment does not validate a
batch over several roots under these gates. The engine's batch takes no root gate and no
publication barrier: it publishes its position after every one of its entities is
stored ([Bounded WriteBatch extension](#bounded-writebatch-extension), below).

The Arc extension implements index publication protection between root and dictionary
protection. Candidate lookup retains root lifetimes under the catalog/index gate,
releases it, then reads roots and rechecks the predicate. Removed keys release text
ownership only after protected index traversal has drained. Comparers protect each
native dereference, so compaction can update a stable text address without reindexing.
The checked lock order uses one coarse catalog/index gate; scalable gates and the
remaining production E04 integration are not established by this experiment.

No catalog, root, dictionary or allocator gate spans database/network I/O. Protected
reads are short synchronous internal work. Test hooks used to force an interleaving
are not a public callback API or permission for application code to wait under a gate.

## Deferred reclamation, 16 November 2026

Where the text above says otherwise, this section replaces it for the engine.

By the owner's decision of 16 November 2026 the engine protects a read by an epoch and
frees later, as `LightEpoch` of Garnet/Tsavorite does. A read enters the protection when
its step inside the engine begins and leaves when the step ends. Memory that a read
could still reach when a write replaces or removes it — an old root with its blocks, a
replaced table of an index, the entry of a deleted root — is retired, not freed: it is
detached from its owner, counted as retired, and freed once every read that had entered
before it was retired has left. A write being published never waits for readers, and a reader
never waits for the writer on its ordinary path.

Each read sees one written state of an entity. A retired entry becomes reusable once the
reads that could see it have left. A read cannot follow an old entry to a recreated key.
A close waits for reads in progress before freeing memory. An owned copy is taken in one
step inside the epoch. Application code that builds an owned result runs after the
read leaves. A read that meets an unpublished stamp waits for that publication in memory.
A read of an absent key repeats its lookup when a write was published
during it; the step ends with the first lookup no write crossed. The publishing thread
looks at the readers when the items retired since its last look reach the number of slots
or their bytes reach one segment, and whenever memory is short, a drain is asked for or
the database closes. What a look leaves behind, because reads still hold it, stays retired
and counted in the retired bytes, and a later look frees it; the count toward the next
look starts at the look, so that a read held long does not make every write look. A look
that leaves retired items behind arms a second trigger: until a look leaves nothing
behind, a published write that retires nothing counts as one item, so the next look comes
within one window of writes of any kind. Without such a look a write that retires nothing
counts nothing, so a load without a held read looks as often as it did without that
trigger. What waits between two looks is bounded by those two thresholds beside what
reads hold.
The bytes of a retired root are the slots of its
own blocks and those of the entry of every Dictionary text it names: an entry whose last
reference lies in retired roots stays live until the drain frees them and is freed with
them, so it waits for the same look. A text that other roots share is counted for each
root that names it. That over-counts, which is safe: it only makes a look come earlier,
and a look frees nothing a read can still reach. An enumeration enters and leaves at each step,
and within a long step after each bounded piece of walking, and holds nothing between
two steps. A reader that finds no free slot of the epoch
table waits for one and never fails. A partial update publishes a new root block that
shares unchanged value blocks. Physical relocation can update a published owning edge
atomically while keeping the logical value immutable (see
[reclamation and relocation](#reclamation-and-relocation)).

Retired memory is the engine's until it is freed: it is counted, it counts against the
memory budget of the instance, and a write that meets the budget first frees what can be
freed and waits for the reads that hold the rest before it is refused. Foreground
operations can reclaim eligible retirements, and the periodic worker takes bounded
steps during idle periods as well as active writes. It can return the last eligible
retirement after a reader leaves without another application write.

### Protected views

`TryView` and `ViewAsync` on sets by name and class sets use the same epoch
table and take one stamped root. A generated view binds its field paths once
for the actual schema before entering the epoch. Both sets run the synchronous
viewer inside the epoch and read every field from that root. A hit samples the root, its member revision and the answer's
sequence together. A raced delete answers at its tombstone's stamp. A stable miss
answers at the sequence that remained unchanged across the lookup. `ViewAsync`
waits for its session before entering an epoch and advances it to the captured answer
position after the operation returns. The callback can borrow only read-only parts of
the stored bytes. A scalar split across chunks is gathered by value from its parts.
`GetString` decodes the parts in the schema encoding into an owned string.

An open view holds its original state across later writes. Publication proceeds while
the viewer runs. The epoch keeps memory retired since it began from being freed. A
new read can wait for a free slot if all slots are held. Under memory pressure a write
or a child's application of a change can wait for reclamation. A close waits for the
viewer before freeing any memory. Those four waits can last as long as the viewer runs
([exception X6](../work/exceptions.md#x6-waits-inside-the-engine-that-have-no-timeout)).
A fault refuses new views and leaves retired memory for close. An exception from the
viewer leaves the epoch before it reaches a caller's exception filter.

The caller must avoid waiting, I/O and nested database operations in the viewer. The
engine adds no guard. A separate read still takes its own slot. Independent views share
no snapshot.

### Ownership across partial updates

Under the writes lock, a partial update prepares a fresh root from the published
predecessor. Only one prepared version may target that key. In the new holder, inline
bytes are packed again and their offsets are calculated there. A value the available
inline area cannot hold goes through the usual out-of-line placement. The published
predecessor's payload and inline offsets stay valid for protected readers.

An unchanged out-of-line branch can be borrowed from the predecessor of the same
root lifetime. The old holder's address and descriptor offset remain in the block
header during preparation. Newly allocated blocks have the new holder as their
owner, or the root image and descriptor offset before there is a final root address.
On abort the free walk enters only owned edges. A new descriptor directly referring
to Dictionary text acquires one reference. A shared subtree keeps the references
already held by its physical descriptors.

After all recoverable refusals, the publishing thread makes new prepared blocks live
and transfers each borrowed backlink before storing the new root. The transfer of a
whole branch changes its top backlink and leaves its descendants in place. Retirement
and free descend only when both the parent address and descriptor offset match.
Protected readers and export still follow every descriptor reachable from their root.
They read the payload without consulting ownership metadata.

Old versions may still refer to a transferred branch. The retirement queue frees its
eligible prefix in retirement order. By the time a later version frees a shared block,
the earlier versions have already followed or skipped their aliases while the block
header still exists. A shared live slot counts once in the arena. Preparation counts
its acquired slots and references. For the size of one entity, its reachable values
still count even if a retained older version can also read them. Relocation must
account for current readers, prepared writes and older aliases before it moves a block.

## The stamp of a root entry

A read by key and the capture of a load take the state, the root and the sequence of the
last mutation of a root entry as one state, without a lock, while the publishing thread
changes the entry. The stamp of the entry is the sequence of its last mutation, and one
value, 0, which no published write has, is no stamp but the mark of a write in progress. A
write stores the mark, then the new state, or the new root and the number of its member, then
its own sequence as the new stamp, each store with release semantics, and the position of the
database only after every entity of its publication is stored. A read loads the stamp with
acquire semantics, then the position of the database, and backs off while it finds the mark or
a stamp above that position, which is a publication whose position is not published yet; it
loads the state and the root with acquire semantics, and loads the stamp again: the same stamp
both times is one state, another one means that a write came between, and the read takes the
entry again. The reader's half is in `Engine/Entities/EntityTable.cs`, the writer's in
`Engine/Entities/ChangePublisher.cs`.

The number of the member of a write batch is part of the revision of a state. It shares one
aligned word with the state, and a read loads the two by one load, between its two loads of
the stamp, so a read by key loads nothing more for it. A replace stores it after the mark and
before its stamp, by a store with release semantics, as it stores the root: a read that loads
the new member then finds the mark or the new stamp at its second load of the stamp, and takes
the entry again. A plain store, or one before the mark, could become visible before the mark
on hardware that orders stores weakly, which x64 does not show, and a read could pair the old
stamp and the old root with the new member: a revision that no state had. An insert stores the
member, as it stores the root and the stamp, before the state Live and before its position: the
capture of a load takes the entry only once it loads Live, a read by key only once the index
maps the key to it, and a search only once the position covers the stamp, each by an acquire
of a store made after the member. A delete stores no member, since no read takes the revision
of a deleted state, and the next lifetime of the slot stores its own.

The protocol is that of `SeqLock` of crossbeam (`crossbeam-utils/src/atomic/seq_lock.rs`) and
of the optimistic read of its `AtomicCell` (`atomic_cell.rs`), at commit
`a28f40fdddb70bea8e5faf0efad9af0be4b9dbea`. What differs, and why:

- The word is the sequence of the last mutation of the entry, so the stamp is the sequence
  that names the state copied, and the read loads no word besides the one it takes for its
  revision; the entry has no room for a word of its own, since it stays at 48 bytes. The mark
  is 0 where the source's is 1: no published write has the sequence 0. The new stamp is the
  sequence of the write where the source's is the old stamp plus 2: sequences only grow, so
  neither repeats a stamp a read holds.
- The source loads the data with volatile loads, which order nothing, and puts an acquire
  fence before a relaxed second load of the stamp. Here each load of the data is itself an
  acquire, which keeps the second load of the stamp after it, as the fence does; and each
  store of the writer is a release, so the store of the data keeps the store of the mark
  before it, as the release fence after the source's swap does.
- The source's writers take the lock by a swap with acquire semantics, because several
  contend for it; here one thread publishes, under the lock of the writes, and stores the
  mark.
- The source's read, after one failed attempt, takes the lock of the writers so that writers
  do not starve it. A read here takes no lock and writes nothing but its slot of the
  protection, so it tries again: at once after a failed validation, and while it finds the
  mark or a stamp above the position after a back-off by `SpinWait`, which spins, then gives
  up the processor, as the source's `Backoff.snooze` does for a writer that finds the lock
  held. The source has no position: the stamp above it is this engine's, so that no read
  takes an entity of a publication of several entities in its new state before its
  position, and a read that begins at that position sees all of it; a search or an
  enumeration that runs across the publication may still return some of its entities as
  they were before it and others as after.
- The source has a write that releases the lock without a new stamp (abort). A write here
  stops between its stores only when its publication fails, which allocates nothing and does
  no I/O, so only a fault injected by a test or a defect fails it; that faults the database,
  and a read that retries on the mark gives up with Unavailable.

## The reads of an entity table

The entities of one entity type are read by key, by enumeration and through a secondary
index (`Engine/Entities/EntityTable.cs`). Every read runs inside the protection of reads,
takes no lock and writes nothing but its slot of the protection, while the write executor
changes the primary index and the root entries under the lock of the writes. A write being
prepared looks its key up under that lock, outside the protection, since nothing changes
them while it holds the lock. The search through a secondary index is described with the
index ([the ordered index](ordered-index.md), "The search by a full key").

### A read by key

1. It looks the hash of the key up in the primary index, which gives the slots of the root
   entries whose key has that hash, and compares the key with the key each of their roots
   stores ("The comparison of a key", below). The root of a slot the index names is live
   or retired, never freed, while the read is inside, and every root of one lifetime holds
   the same key: the primary key is immutable, and a replace keeps the stored spelling of a
   String key compared without case.
2. It takes the state, the root and the sequence of the last mutation of the entry as one
   state, by the stamp of the entry ("The stamp of a root entry", above): a write stores
   the mark before it changes the root or the state and its own sequence after, and a read
   that finds the mark, or another sequence after it took the rest, takes them again. It
   writes nothing for this.
3. A live root is copied whole, with every block it owns, into memory of the engine: the
   blocks of a published root never change, and a root that a write replaced or deleted is
   retired, not freed, until the read has left, so the copy is one state
   ([memory and access](../decisions/memory-and-access.md)), never memory that was freed or
   given to another root.
4. It leaves the protection. The caller builds its result from the copy, and only then does
   code of the application run. A read allocates the returned copy and nothing else.

A deleted entry is released, and its slot given to another root, only after the reads that
could have found it have left, so a read never follows an old entry to a key that was
created again.

### The position of an answer

For a root it copied: the sequence of the database read right after the first read of the
entry's sequence, which is never below it, since a read does not take a stamp above the
position; the state copied is the entity's at that position, because every write of the
entity up to that position stored its stamp before the position was published, and the
second read of the entry's sequence finds it. For a root deleted before the read reached it: the sequence of
its delete. For an absent key: the sequence of the database read before the lookup, when
the lookup ended with the sequence unchanged; otherwise the read looks up again. An insert
publishes its entry in the index before the sequence of the database and a delete the
sequence before it removes the entry, so an absence seen at an unchanged sequence holds at
that sequence.

### An enumeration

An enumeration goes a step at a time. Between two steps nothing is held but what its pass
holds, managed memory of the enumeration. A step is made of rounds, each inside the
protection of reads, which it enters and leaves, and each walking a bounded number of words
of the index, so that no round holds back what writes retire for longer than a bounded piece
of work, however many entries a step has to pass, as after a growth. A round:

1. It looks at the time limit of a read, then takes the current table of the index with
   its version. A version other than the one its position belongs to means that the index
   grew since, and the walk starts again at the start of the new table: a growth places
   every entry anew, so a position in the old table means nothing in the new one. Mapping
   it, bucket b to buckets 2b and 2b + 1, would miss the entries the growth moved from the
   overflow buckets of the old table into main buckets that the position has passed.
2. It walks the words of the table from the position. For each entry it reads the key the
   root of the entry stores and skips it when the pass has returned that key: that keeps a
   key from being returned twice, after a growth, whose walk meets again what was walked,
   and after a delete and an insert of a returned key, whose new entry the walk may meet at
   a later word.
3. Otherwise, when the key the entry holds has no room in the pass, the round ends before
   the entry, before it copies anything; outside the protection the room is counted against
   the memory limit of a read and made, and the next round reads the entry anew. So a step
   copies the entity it returns once, the first of a pass and one whose key makes the room
   grow included, and nothing large is allocated inside the protection. With room, it copies
   the root as a read by key does, skips a root deleted meanwhile, and otherwise stages the
   key and the position after the entry, which the pass takes once the entity is built
   outside the protection. Room made for an entry that then turns out deleted is counted all
   the same: the limit counts the capacity the pass holds, and the pass holds it whether or
   not that key is remembered.
4. At the end of the table, the sequence of the database read when the round began is the
   position of the end when it is unchanged after the walk, as for an absent key; otherwise,
   and when the round walked its words without finding an entity, the next round goes on
   from the position.

The pass counts the time of every step, its rounds and what lies between them included, and
fails the round that would exceed a limit.

Every entity that nobody changes during the pass is returned once: its entry is in the index
throughout the pass, an entry does not move within a table, so a walk of a table meets it at
its word, and a walk of a grown table from its start meets it too; the key it stores is the
same in every root of its lifetime, so it is skipped only once it has been returned. Every
returned entity is one written state, by the stamp of its entry. An insert, a replace, a
delete or a growth never ends the pass, and a replace changes no entry of the index at all.

### The comparison of a key

The equality, the hash and the order of values follow the rules the schema declares, in one
service across the indexes and direct lookups (`Storage/Values/ValueComparer.cs`): the
primary index uses its equality and its hash, the secondary indexes its order, and nothing
else in the engine compares a key with stored text.

What equality is, for each type of a primary key:

- An integer and a Guid are held in the index as their value, and the equality and the hash
  of that .NET value are the key's: two integers of the key type are one key when they are
  the same number, two Guids when their 16 bytes are the same.
- A String follows the comparison its field declares, with the same result in a UTF-8 and in
  a UTF-16 database: Ordinal, the equality of the UTF-16 code units, or OrdinalIgnoreCase,
  by the case table of Lattice's own ([strings](../decisions/strings.md), Q04). The rule of
  equality and hash for text is the schema's, `LatticeValueDeclaration.TextComparison`,
  which the core follows for Map keys, Set elements and the keys of a write batch as well.

A stored text is compared with a given one a piece at a time. The stored text is read as
UTF-16, 256 code units at a time, into a buffer on the stack, and each piece is compared with
the same code units of the given text by the rule; the texts are equal when every piece is
and both end together. That is the answer of the rule for the whole texts. Both comparisons
hold texts of different lengths different and compare them place by place: a code unit with
the code unit at the same place, and without case a surrogate pair with a pair at the same
place, as one scalar value, whose mapping keeps the number of code units. No piece of the
stored text ends inside a pair, so a pair of the stored text is always compared whole; where
the given text has the first half of a pair at the end of its piece, the stored text has
another character at that place, which a high surrogate standing for itself equals under
neither comparison, so both the pieces and the whole texts differ. The order of an index
compares a text of a UTF-8 database under Ordinal by its bytes instead, with the one turn
that makes their order that of the UTF-16 code units ([the ordered index](ordered-index.md),
"Entries and their order"); every other comparison reads the code units. No culture is
consulted, and nothing of the casing of the runtime is called.

The hash of a key in the primary index is the engine's own, seeded with the seed of the
instance, so that the index depends on nothing a process sets and nobody who does not know
the seed can choose keys that share a hash: Marvin32, the hash of text of the .NET runtime,
over the UTF-16 code units of the text, and for a comparison without case over the code units
the case rule maps the text to, the mapping every hash without case follows. It is a hash of
code units, so a text hashes the same in a UTF-8 and in a UTF-16 database. The engine
computes it from the text a caller gives, when it makes the key, before the read enters the
protection of reads, and the index keeps it in its entry, so no stored text is ever hashed.

A comparison allocates nothing, whatever the length of the text and wherever it lies: its
buffers are pieces of 512 bytes of its stack, those of the mapping only for a comparison
without case, and none of them is zeroed, since every code unit a comparison reads it wrote
first. A lookup costs the hash of the given text, one pass over it, and for each key of the
index with the same hash, in practice the one that is looked for, one pass over its stored
text, decoded from UTF-8 or copied, and one pass of the rule over both. Without case a code
unit that equals its counterpart needs no mapping, and one that does not is looked up in the
case table.

## Read and mutation paths

The current engine publishes new roots for complete writes and for partial updates
that change an entity. [The root stamp](#the-stamp-of-a-root-entry) and the transaction
position coordinate visibility with the indexes. An update that changes nothing
publishes a position and keeps the current root and revision. The
[partial update ownership rules](#ownership-across-partial-updates) cover sharing
and cleanup of earlier versions.

### Earlier bounded experiment

An ordinary copy read claims the root, acquires protection, copies scalar fields,
owned chunks and dictionary text, then releases all protection. The returned arrays
and strings belong to the caller. A protected calculation reads the same bytes and
returns only a detached scalar result; it allocates no object graph.

Full Save reserves a root block, owned chunks and a prepared dictionary reference.
Fill and validate these private blocks before storage dispatch. Failure or cancellation
before dispatch returns every reservation. A pending mutation retains its lifetime
claim and conflict-order ownership, not a read/write gate, during a durable wait.
This experiment supplies the resulting ordered publication path without a storage
provider; it is not a test of PostgreSQL or the parallel preparation scheduler.

After a known commit, acquire root and necessary dictionary protection, validate the
prepared predecessor, publish the new root/revision, convert prepared string claims
to stored references, and detach old owned blocks and old string references. The
protected reader cannot observe a mixture of old and new fields. Root lifetime and
allocator resources must already exist; the production path must also reserve index
resources before commit. If committed publication fails, use the accepted instance
fault/reopen policy, not an attempted rollback of durable data.

In the gated experiment, a fixed-field update modified its fixed fields under exclusive root
protection, without replacing the root or its payload blocks. Delete removes the
catalog mapping and marks the old lifetime dead under root protection, detaches its
blocks and releases stored text references. A queued old lookup sees absence; a new
lookup may reach a newly created lifetime. Prepared operations retain old entries
until they publish or abort, so slot reuse cannot change their target.

## Reclamation and relocation

On a Ready master or child, a periodic worker offers a turn every 16 ms. Ticks
can coalesce while work is in progress. A turn enters the counted operation barrier,
tries the local write lock without waiting, and rechecks admission under it. It
drains safe FIFO work and checks at most one collection spare for shrink. The
reclamation and text maintenance share 64 work units, including actual releases.
A bounded 128-unit search starts from regions below half live occupancy, resumes
across turns and looks for existing compatible vacancy in earlier segments. At most
one move publishes in a turn. Retained relocation copies together charge at most
one 64 KiB slot. A prepared master write prevents relocation. Close joins the
worker before its final cleanup.

The mover checks the live owner and each backlink it follows before publication.
A release operation changes one root address, owning descriptor location, lookup
chunk word or stable text locator address. The reader or exporter acquires the
complete address under its existing epoch. A reader that saw the old location
may keep using its storage until that epoch ends. The retire tag follows publication.
A raw relocation retirement releases just the old slot because ownership of the
child graph moved with the copy. Secondary index nodes stay in place. Index searches
keep resolving their root handles and text IDs. Relocation preserves payload
values, revisions, counters for logical data and canonical transfer bytes.

### Earlier gated experiment

The prototype described here used synchronous root reads. Acquiring exclusive
root protection drained readers of owned data, so a detached block could be freed in
that scope after descriptor publication. A queued lookup retained control metadata.

Shared text is freed only when root, prepared and index counts are all zero and
exclusive dictionary protection excludes text readers. A root gate alone is
insufficient. The Arc extension supplies index retainers, including prepared keys;
replication and other production owners still need integration. Zero asynchronous
retired bytes here must not be generalized to unimplemented retaining subsystems.

The compactor selects a live slot in a sparse segment with compatible destination
space in an earlier segment. It tries the owner's exclusive gate without waiting;
a busy owner is skipped. Recheck generation, current descriptor and allocation owner
after acquiring protection. Reserve one existing destination, copy the bounded block,
repair the root/dictionary/child descriptor, and release the old slot. There is no
whole-object copy for moving one chunk, no revision change and no journal entry.

Compaction does not allocate a new segment merely to relocate. It has a bounded
candidate scan and one-block work step. The harness schedules steps; production
trigger/fairness scheduling remains separate. An empty segment may enter the bounded
reserve; excess completely unused segments are returned to the native allocator.
Releasing native allocations does not guarantee an immediate fall in process RSS.

## Required bounded checks and measurements

These checks belong to the gated prototype.

- Coherent copy/calculation reads across updates; detached copies remain unchanged.
- A held reader delays mutation, while compaction skips that busy owner.
- Lookup before deletion, deletion before root acquisition, same-key recreation and
  delayed control-entry reuse; stale relocation candidates must be harmless.
- Full Save preparation and abort, readable old state during a simulated storage
  wait, targeted fixed-field writes, and shared text retained by another root or
  prepared write.
- Churn at bounded live cardinality with holes, root/chunk/text relocation, complete
  native ownership/reference audits and a final zero-allocation disposal check.
- Separate latency distributions for protected reads, owned copies, in-place writes,
  full replacements, deletion and compaction steps. Include waits and scheduler
  effects; do not subtract them or call a tiny run a throughput guarantee.
- Report native allocated capacity, useful live bytes, live class slack, prepared
  capacity, reusable space, empty reserve and relocation peak separately. Report
  managed heap, allocation traffic, process working set and CPU time separately.

All checks are synthetic and resource bounded. This is a mechanism experiment,
not a release plan, production allocator or proof of arbitrary reader schedules.

## Bounded WriteBatch extension

The [WriteBatch experiment](../../src/Prototypes/WriteBatch/README.md) owns one ArenaLifecycle
instance behind a database publication barrier. Its Read, Query and compaction paths
participate in the barrier; roots/indexes/text still use the protection above.
The existing lock order is extended at the outside by database protection.

Whole-batch validation/preparation precedes a real PostgreSQL commit, with no arena
gate spanning storage I/O. The prototype's writer permit serializes preparation as
well as commit/publication; parallel preparation is not established by this check.
Publication drains protected access, installs all roots and indexes, and advances
the applied position only after the last member. Failure marks the instance faulted
before opening access, and reopen rebuilds one arena from the independent fixture
base and whole committed journal batches. There is no second serving arena.

The engine keeps the guarantee of that barrier without it (the decision on the document
model, "Atomic WriteBatch within one database", amended on 22 November 2026, the owner's
answer to Q57 of the open questions): no member of a publication of several entities is
visible in its new state before its position, a read that begins at or after the position
sees all of it, and no answer carries a position below a stamp it holds. A publication at
the sequence s installs the new entries of the secondary indexes of every member, then
stores every member with the stamp s, and only then publishes the position s ([the write
path](write-path.md#how-a-publication-is-made)). A read that meets a member before the
position finds the mark or the stamp s above the position and waits, inside the protection,
until the position is published or the instance has faulted, or its token ends the wait;
the position of its answer is the one it read once the wait ended, s or later. A read of an
entity that is no member is never held, and nothing drains. A read by key is exact. A
search or an enumeration that runs across the publication may return some members as they
were before it and others as after, as separate reads and scans may (the same decision, and
[database scope](../decisions/database-scope.md)): a step goes on after a take that waited,
and what it passed before stays passed ([the ordered index](ordered-index.md#how-a-range-is-walked)).
A master publishes the commit of a batch, and a child a transaction of several effects, by
the same publisher.

Controlled interleavings, pre-commit rollback, post-commit publication failures,
same-token repeats and four abrupt application-exit/reopen cases passed. Publication
pause and memory observations belong to the experiment report. The fixture's fixed
limits, receipt retention and binary encoding are not a production persistence format
or final unified memory budget; the accepted 256-byte base/applier remains separate.

The subsequent [saved-unit extension](../../src/Prototypes/WriteBatch/UNITS.md) exercises
256-byte base units and background application on that same path. Maintenance owns
only its SQL connection/cursor transaction, never arena protection. Saved units,
applied position and revision floor commit together; cleanup removes only covered
journal rows and preserves receipts. A single recovery SQL snapshot ties the base
to its tail. The independent fixture format still needs general-schema integration.
