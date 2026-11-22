# The ordered secondary index

How a declared secondary index lies in memory, how a reader walks it while the writer
changes it, and what a write costs. The structure was chosen by the owner on 19 November
2026 ([index structures](../decisions/index-structures.md), the notes of that day); the
milestone contract states its rules under
[index preparation and traversal](milestone-1-engine-contract.md#index-preparation-and-traversal).
`src/Moltaro.Lattice.Engine/Storage/Ordered` holds the nodes and entries in arena memory,
`Engine/Indexes` the tree, its order and the index of an entity type,
`Engine/Entities/ChangePublisher.cs` the upkeep of every index with the data, and
`Engine/Indexes/IndexWalk.cs` with `Engine/Entities/IndexSearch.cs` and
`EntityTable.TrySearch` the search: by the full key, by the leading fields and a range on
the next, in the index order or its reverse, and in pages. The [README](../../README.md)
says what works.

[Index access](../decisions/index-access.md) · [Index lifecycle](../decisions/index-lifecycle.md) · [How reads are protected](epoch-protection.md) · [How a write is executed](write-path.md)

## What it is

A B+tree of the engine's own whose published nodes never change. A write copies each
node it changes and publishes the copy by one store of its address into the live parent,
or into the root of the tree. A reader takes no lock, checks no version and never
retries. What a write replaces is retired and freed after the reads that could reach it
have left, by the same protection of reads as every other read
([epochs](epoch-protection.md)).

The price, accepted by the owner: a write copies one node of each index it changes and
retires the old one, so retired memory grows by about one node for each index write
while a read is held, and a write beside sixteen readers costs about twice what a tree
changed in place costs. In return a reader never meets a node that is being written.
The figures are those of the prototype, `src/Prototypes/SecondaryIndexComparison`, one
run of five seconds a case; the engine measures the structure again.

## Entries and their order

An entry is the key of an entity in this index and the identity of its root entry, the
8-byte root handle. A state of an entity contributes one entry to each index, so an
entry is unique by (key, identity), and the entries of one key form a run ordered by
identity. A search finds the first entry of a key and walks its run; a range walks from
one key to another in the same order. An index on the members of a collection,
where a state would contribute a set of entries, is not built.

Entries are ordered part by part in the order of the index, each part by the comparison
of its field, and then by identity. Keys are compared by their values, never by bytes in
another order than theirs: an integer by its value, signed or unsigned at its width; a
Boolean false before true; a floating-point value by `CompareTo`, NaN first and the two
zeros equal ([queries](../decisions/queries.md), Q07.3.6); a Decimal by its value, so 1.0
and 1.00 are equal; a Guid by `CompareTo`; the temporal types by their ticks or day
numbers; text by the comparison of its field, the same in a UTF-8 and in a UTF-16
database: Ordinal by UTF-16 code units, without case by the code units of the texts
mapped through the case table of the core ([strings](../decisions/strings.md), Q46). A
null orders after every value of its part and equals another null, so a composite key
goes on to its next part. All of it is the one comparison service of the schema, which
gains the order beside its equality.

The comparison is chosen once for an index, when its order is made from its layout. An
index of one part that is never null and whose stored form is a signed integer of 8 bytes
— an Int64 or an Enum over one, a DateTimeOffset as its UTC ticks, a TimeOnly or a
TimeSpan as its ticks — compares that number, read where the layout places it, in the
order of the comparison service, without the loop over the parts and the comparison by
type; every other index takes the loop. A text part compared Ordinal in a UTF-8 database is
compared by its bytes where they lie, the copy in an entry or the text entry of a shared key,
against the key of a search encoded to UTF-8 once for the step: UTF-8 bytes sort as code
points, which is the order of UTF-16 code units except where, at the first byte that
differs, one text has a character of U+E000 to U+FFFF, lead byte 0xEE or 0xEF, and the
other one beyond U+FFFF, lead byte 0xF0 or above; there the order turns around, and the
comparison turns it. A key whose UTF-8 bytes take more than 256 bytes, a text in chunks, a
comparison without case and every text of a UTF-16 database, whose little-endian bytes are
not in the order of its code units, are compared decoded, a piece of 256 code units at a
time. The check of an entry against its root, whether the root still has
the key, is that same order being zero.

Two keys can be equal and not the same bytes: a text under another case, a Decimal of
another scale. They are one key with one place in the tree; an entry holds the bytes of
its own root's value, and a write that changes only the bytes rewrites the entry at its
place in the order: its leaf is copied with the new bytes, or split when they do not fit,
as every change of a node is (see "Writes"); no published node is written.

**An entry is a hint.** A search reads the root entry each entry of its run names and
returns the entity only if the current state of its root has the searched key. So a
reader never finds an entity under a key it no longer has, whatever moment of a write it
meets, and an entity that has the key for the whole search is found, because its entry
is in the tree the whole time.

## Nodes in the arena

A node is one block of the arena, of kind `IndexNode`, with the block header of every
block. Its payload begins with an 8-byte header: the count of entries or separators, the
level (0 for a leaf), flags, and the bytes its entries take. Every address in a node is a
relative address of the arena; nothing is a pointer.

| Node | After the header |
| --- | --- |
| Leaf, fixed keys | The entries, one width each, in order |
| Leaf, keys with text | An offset of 2 bytes for each entry, then the entries in order |
| Inner, fixed keys | Count + 1 child addresses of 8 bytes, on 8-byte boundaries, then the separators |
| Inner, keys with text | The child addresses, the offsets, then the separators |

Separator *i* is a copy of an entry: every entry below child *i* is below it, every
entry below child *i* + 1 at or above it.

An entry is the identity (8 bytes), a null bitmap of one bit for each part that can be
null, and the parts in order. A fixed-width part is the bytes its root stores for it, at
its width; a null part keeps its width. A text part is one head byte: 0 to 64, a copy of
that many bytes of the stored text in the encoding of the database; 255, the length (4)
and the relative address (8) of a text entry. So, in a leaf:

| Key | Bytes for an entry | Entries in a full leaf of 1 KiB |
| --- | --- | --- |
| 4 bytes (Int32, DateOnly) | 12 | 82 |
| 8 bytes (Int64, DateTimeOffset, Float64) | 16 | 61 |
| 16 bytes (Guid, Decimal) | 24 | 41 |
| Text copied, *L* ≤ 64 bytes | 11 + *L*, the offset included | 51 at *L* = 8, 22 at 32, 13 at 64 |
| Text shared, longer than 64 bytes | 23, the offset included | 42 |

A nullable part adds one byte to an entry for up to eight of them. Random inserts leave
leaves about two thirds full: the prototype measured 18.8, 25.1 and 38.6 bytes an entry
for keys of 4, 8 and 16 bytes inserted in random order, 12.9, 17.2 and 26.5 ascending.

**The node is 1 KiB.** A node takes the size class of 1,024 bytes, the last of the
16-byte steps, so it loses no slack of a class: 992 bytes of payload. The prototype
searched at 1.90 million a second on one thread and 7.32 million on sixteen with nodes of
1 KiB, and at 2.15 and 8.26 million with nodes of 4 KiB: a difference near the tenth that
one run does not decide; in the engine on the rented machine a node of 2 KiB read at most
4 % faster beside a writer and wrote 2 to 3 % slower (20 November 2026), so the node stays
at 1 KiB. A write copies and retires one node of each index it changes,
so a node of 4 KiB would copy, and hold back under a held read, four times the bytes for
every write. The engine measures it again. An index whose four widest entries, every text
part a copy of 64 bytes, do not fit a leaf of 1 KiB, or four widest separators an inner
node, takes the smallest size class above that holds them. An inner node of 1 KiB holds
four separators of at most 234 bytes, so this begins at four text parts in one index, or at
three when the fixed parts and the null bitmap take 32 bytes or more together: 31 still
fit. An index whose four widest entries do not fit the largest block of 64 KiB fails the
open with `InvalidSchema`, as a fixed part of a root beyond one block does: a rule of the
schema language, the same on every node, which `LatticeIndex` states with the widths. 251
text parts that are never null fit, 252 do not.

### Fences

A node keeps no fence of its own. The fences of a node are the separators of its
parent on each side of it, or further up when it is the first or the last child: a
descent passes them on its way down. LeanStore stores both fences in each node, which
costs two keys a node and their copy with every copy of the node; here a reader that
needs the next leaf already passed its fence.

## Text keys

An entry holds a copy of a text key of 64 bytes or less, whatever the storage mode of
the field: a Dictionary text of that length is copied as a Plain one is, and no
reference is taken for it. A longer key references the text entry its root already
references. For that, a field that is part of an index stores a value longer than 64
bytes as a text entry whatever its storage mode, a rule of the layout compiled from the
schema; such an entry is found by its bytes, so equal long values of an indexed Plain
field are stored once. A value of 64 bytes or less of an indexed field stays in its root:
an indexed value is never moved out of the root because it is indexed. The owner chose
this form on 19 November 2026; the prototype measured a search by a copied key three to
five times faster than through a shared entry at 8 and 32 bytes, and sharing short values
cost a read by the primary key 14 to 25% at 8 bytes.

**No prefix beside a shared key.** A shared key does not carry its first 8 bytes beside
the reference. Measured in the engine on the rented machine on 20 November 2026, with
keys of 100 and 200 bytes, the prefix made a search 1.26 and 1.30 times as fast where the
keys differ in their first bytes, and 0.89 and 0.93 where they share them, as numbered
logins, addresses and paths do; long keys usually share their heads. The head byte 254
stays kept for a form of its own; nothing of it is built.

### Text references

A text entry has one count, and a block holds one reference for each Text descriptor it
contains, from the moment it is written until it is freed
([strings](../decisions/strings.md), Q04.4, the settlement of 18 November 2026). A node of
an index follows the same rule: it holds one reference for each shared key among its
entries and separators. The writer takes one more reference at the address of each shared
key when it writes a node, at its publication, which allocates nothing and cannot fail;
the drain releases them, by address, when it frees the node. A separator that is a copy of
a long key keeps its text entry alive after the entity is gone, as long as the separator
is in the tree.

The cost is one reference taken for each shared key of each node a write copies, and one
released when that copy is freed. In an index whose keys are all longer than 64 bytes a
leaf holds up to 42 of them, about 28 at the fill random inserts leave, so a write takes
and later releases about that many; in an index whose keys are copied, none. The rule is
the one of every block, confirmed for index nodes. Measured on the rented machine on 20
November 2026, with keys of 100 and 200 bytes, references held for each entry by its root
instead of for each node made a replace that changes the key 1.45 to 1.51 times as fast,
and an insert and a delete 1.9 to 2.1, with a search unchanged; that would change the rule
of the decision on strings, and the owner deferred it to the sprint of compaction. The rule
of the decision on strings is unchanged.

## How a reader descends, and why it is safe

A read enters the protection of reads, loads the address of the root of the tree, and
goes down: at each inner node it finds the child whose range holds the key and loads its
address, until a leaf; in the leaf it finds the first entry at or after the key. Then it
leaves. It writes nothing but its slot of the protection.

What is written after a node is published: nothing but the child addresses of an inner
node, each replaced by the writer with one store of 8 bytes on an 8-byte boundary.

- **The writer** fills a new node, which no reader can reach, with plain stores: its
  header, its offsets, its entries or separators, its children. It publishes the node by
  a store with release semantics (`Volatile.Write`) of its address into a child of the
  live parent or into the root of the tree. On x64 that store is an ordinary `mov`, and
  stores are not reordered with earlier stores; on ARM64 it is `stlr`, which orders every
  earlier load and store before it. A text entry that a new key references was written
  before, and is published before the node that references it.
- **The reader** loads the root and every child address with acquire semantics
  (`Volatile.Read`): on x64 an ordinary `mov`, since loads are not reordered with later
  loads; on ARM64 `ldar` or `ldapr`, which orders every later load after it. It reads the
  node behind the address with plain loads, and sees all of it as the writer wrote it.
  It does not rely on the order of loads that depend on an address: every address it
  follows is itself an acquire load.
- **An aligned 8-byte store is atomic** on both, so a reader sees an old address or a new
  one, never a mixture. The children of an inner node lie on 8-byte boundaries: a block
  starts on a 16-byte boundary, and the node header is 8 bytes.
- **Nothing a reader reaches is freed while it is inside.** The writer unlinks a node by
  the store that publishes its replacement, then retires it tagged with the current
  epoch; the drain moves the epoch on by a full fence and reads the slots of the readers.
  A reader enters by a compare-and-swap, a full fence, before it loads the root. Of the
  two, either the writer sees the reader's slot and keeps the node, or the reader sees the
  new address and cannot reach the old node ([epochs](epoch-protection.md)).
- **A descent through nodes of different moments is right.** A node's separators never
  change. A child is replaced in its live parent only by a copy that holds the same range
  of entries; a split or a merge changes the separators, so it copies the parent too and
  publishes the parent higher up. So every node a reader reaches from a parent lies
  within the bounds that parent gives it, and every entry it reads was in the tree at
  some moment of its descent.

There is no state a reader can see half written, so it checks no version and never
retries. The reads of the root entries that a search makes afterwards follow the stamp of
the root entry, as a read by key does.

## Writes

The writer is the holder of the lock of the writes. An index write is an insert of an
entry, a remove, or a rewrite of an entry at its place.

- **Insert.** The leaf of the entry is copied with the entry in it. A full leaf splits
  in the middle of its bytes into two new leaves, and its parent is copied with one
  separator and one child more, or splits in turn; a root that splits gets a new root
  above it. An entry appended after the last entry of the last leaf leaves the full leaf
  as it is and starts a new one, as LMDB biases its split, so ascending keys leave full
  leaves.
- **Remove.** The leaf is copied without the entry. A leaf left below a quarter of its
  bytes merges with a neighbour under the same parent when both fit three quarters of a
  node, and the parent is copied without the separator between them. A leaf left empty
  goes away from its parent. Inner nodes are not merged; one left without children goes
  away from its own parent, and a root left with one child gives way to it. A tree left
  without entries has no node.
- **Rewrite.** The leaf is copied with the entry's new bytes; when they do not fit, it
  splits as for an insert.

Each new node is written whole and published by one store, into the nearest node that is
not copied, or into the root of the tree. A copy of a leaf, and a merge of two, moves the
entries it keeps as blocks: the entries before the changed one in one copy and those after
it in another, and for entries behind offsets their offsets moved by the one distance the
block moved; the bytes the edited leaf takes are those of the leaf with the one change,
without a walk over its entries. After the store, the nodes it replaced are
retired. A split of a full node into two always fits: a leaf holds at least four of the
widest entries of its index, an inner node four of its widest separators.

### The entries of one write

What one write does to one index is the entry its old state contributes against the
entry its new state contributes: the same bytes, nothing; an insert of the entity, an
insert; a delete, a remove; another key, an insert of the new entry and a remove of the
old; an equal key in other bytes, a rewrite. Equality under the comparison of the index is
not the rule for nothing, since an entry holds the bytes of its own root: a text changed in
case only, a Decimal of another scale, +0 and −0 or NaNs of other bits are equal keys in
other bytes, and rewritten.

It is decided once, in the hold that prepares the write, once the new root is written, and
recorded with the prepared change, one bit for each index and one more for a rewrite; the
reservation, a withdrawal, the publication and the top-up of the reserve read it, and a
replace that leaves every entry as it was passes every index by. The decision reads the
stored fields of the two roots instead of building their entries, by the rule an entry is
written with: a part null in both is the same, whatever bytes lie under the null, and null
in one only is not; a fixed part is the same when its stored bytes are, so a DateTimeOffset,
stored as its UTC ticks, with another offset alone is the same; a text of 64 bytes or less
when its bytes are, wherever they lie, never by its descriptor, whose offset depends on the
root; a longer text when it references the same text entry. Whether two entries that are
not the same have equal keys is the order of the index applied to the two roots. So the
publication builds only the entries it inserts, removes or rewrites, each once. The masks
have a bit of their own for the first 63 indexes of an entity type; the last bit stands for
every index from the 64th on, which each step decides again by the same test, which builds
no entry.

The entries of a write are installed in the hold of the lock that publishes its root,
each by its own stores, in this order: the new entries are inserted, the root is
published, the old entries are removed or rewritten. Between those stores a reader may
find the new entry before the root has the new key, or the old entry after the root has
lost the old key; the check of every entry against its root covers both. A publication
of several changes, as a child applies a transaction of several effects, inserts the new
entries of all of them first, then stores every entity, then publishes its position, and
removes the old entries after it ([the write path](write-path.md#how-a-publication-is-made)).

### The owner of a value of a unique index

A write that sets a value of a unique index, an insert, or a replace that moves its key,
is checked in the hold that prepares it, after the change of every index is decided and
before any node is reserved, so that a refused write takes nothing from a pool. A key with
a null part is not checked. Between two publications the tree holds one entry for each live
root and nothing else, so the owner of a value is the entry of that value: one descent to
the first entry at or after the entry of the new root under the lowest identity, compared
by the order of the index, which is the comparison of every part; where that place is past
the end of its leaf, one more descent at the fence, to the first entry of the leaf beside,
as a walk goes on. The fence is a probe there and never an owner: a separator is a copy of
an entry made when its leaf split, and it stays in its inner node when that entry leaves its
leaf, since a remove changes the leaf alone and only a leaf left empty or a merge takes a
separator away; so a separator may be the copy of a removed entry, or of an entry rewritten
since. A replace that keeps its key, in the same bytes or in others, is not checked. No key
is built from the values of the write, since a write forwarded by a child brings only its
root. The writes prepared and not published, which the tree does not show, are the write
coordinator's to know ([the write path](write-path.md#two-writes-to-one-entity)).

The changes of one publication, as a transaction a child receives, are checked together,
on the state they all leave (`ChangePublisher.TryReserve`), after every one of them is
decided. The entity types are taken in the order of their first change and the unique
indexes of each in the order of their declaration. In an index, every value a change sets
is looked up as above, and an owner that a change of the same publication frees, by a
delete or by a move of its own key, is no conflict; then the values the changes set are
sorted by the order of the index, of equal values the earlier change first, so that two
changes that claim one value lie side by side and the later one conflicts. The conflict
found is that of the first index that has one, and in it the change that comes first. So
one publication may exchange the values of two entities, or free a value and give it to
another entity, which no order of single writes could apply one at a time. One change
makes one descent for each index in which it sets a value, as before.

### The reservation

Nothing grows inside a publication. In the hold that prepares a write, each index it
changes reserves the nodes the publication can take, as prepared blocks of the arena,
counted in its prepared bytes and in the budget, and as many places in the retired list.
A write withdrawn before its publication gives them back; a prepared, unpublished write
holds them while the persistence step is busy, and no reader can reach them. A reservation
the arena cannot hold takes nothing and is refused as a write the memory budget cannot
hold is: before its commit. So is a reservation whose bound the pool cannot count, more
nodes or places than an Int32 holds.

The writes prepared and not published share one pool for each index. While *p* inserts,
rewrites among them, and *q* removes wait, the pool holds their *need*, the most nodes
their publications can take from the tree as it is now:

> *Need* = *p* · (2*h* + 1) + 2*S* + *q* · (*h* + *G*(*p*) + 1),

where *h* is the height of the tree now, *G*(*k*) the most levels *k* inserts can add to
it, and *S* = *G*(0) + … + *G*(*p* − 1), the levels the inserts before each insert can
add. With *r* the inserts the root takes before it can split and *I* the separators of
its widest form an inner node holds before it is full, at least four, *G*(*k*) is 0 for
*k* ≤ *r* and 1 + ⌊(*k* − 1 − *r*) / *I*⌋ above it. The sum has a closed form, so that the
need costs the same for any number of writes waiting: with *m* = max(0, *p* − 1 − *r*),
*a* = ⌊*m* / *I*⌋ and *b* = *m* mod *I*, *S* = *m* + *I* · *a*(*a* − 1) / 2 + *a* · *b*
(`NodePool.Need`, which a test of the engine holds against the sum). Why it is
enough:

- An insert at height *H* splits at most every level and adds a root: at most two nodes on
  each level and one more, 2*H* + 1, and it retires at most one node on each level. A
  remove copies at most one node on each level, a merge of two leaves taking one for
  both, and retires at most one more, the neighbour of that merge: *H* + 1 serves both
  counts.
- The height grows only when the root splits. The root takes *I* minus its separators or
  entries, at least none, inserts before it can split, since a node with fewer than *I*
  fits one more; a root made by a split holds one separator, and one insert adds at most
  one separator to any node; a leaf holds at least as many of the widest entries as an
  inner node holds separators, so a root leaf grows no sooner. So the *k*-th insert finds
  the tree at most *G*(*k* − 1) levels above *h*, and every remove at most *G*(*p*). A
  remove never grows the tree, nor makes its root fuller without making it lower by a
  level, so the order in which the waiting writes are published does not matter.
- A preparation tops the pool up to the need of all writes that wait, with its own
  counted; a publication and a withdrawal never take a node, and what they give back is
  counted at the tree as it is then. A publication takes at most the first share of the
  need and leaves the need of the rest no larger than what is left: a split makes a root
  of one separator, which counts its growth from there, and a remove that lets a fuller
  child take the place of the root lowers the tree by a level. A withdrawal only lowers
  the need.

The need grows with the square of the inserts waiting, since each insert is counted at
the height the inserts before it can reach. A second bound is linear: from a tree of
height *h*, *p* inserts and *q* removes, published in any order, take at most

> *Bound* = *p* · (2*h* + 3) + 2*q*

nodes and retire at most

> *PlacesBound* = *p* · (2*h* + 3) + *q* · (*h* + 2 + ⌊log2 *p*⌋)

(`NodePool.Bound`, `NodePool.PlacesBound`; a randomized test of the engine holds both
against what publications take and retire, for random, ascending and merging keys,
through splits of the root, growth of the height and removes that merge). An insert that
splits *s* levels takes 2*s* + 1 nodes; at most *h* of its splits are at levels the tree
had, and the splits of all the inserts at the levels above those are fewer than the
inserts. A remove takes at most two nodes, a merged leaf and the copy of its parent,
since every other node of its path changes only one child address, in place. An insert
retires no more nodes than it takes; a remove at most the nodes of its path and the
neighbour of a merge, at a height the inserts raise by at most 1 + ⌊log2 *p*⌋ levels.

That bound holds for a sequence of publications from the tree as it was when the sequence
began, not again at the tree after each of them, so the pool keeps a ledger
(`Indexes.NodePool`): the height of the tree at its base, the inserts and removes
reserved since then and not withdrawn, and the nodes taken and the places used since
then. The base is the tree as it is whenever nothing waits, and it moves to the tree as it
is when the bound of the changes waiting, counted from there, is no more than what the
ledger leaves. The pool holds the lesser of the need at the tree as it is and what the
ledger leaves of the bound, nodes and places alike: each is enough by itself, a
publication lowers what the ledger leaves by exactly what it takes, and it leaves the need
of the rest no larger than what is left. For one insert the need is the lesser, 2*h* + 1
against 2*h* + 3; for one remove at a height of 2 or more the bound is, 2 nodes against
*h* + 1. A Debug build asserts after every preparation, publication and withdrawal that
the pool and the places hold at least the lesser bound of the writes still waiting, and
that the ledger never takes more than it was charged.

The reserved nodes are linked through their own payload, each holding the address of the
next in its first bytes, so the pool takes no managed room for them: a reservation the
arena refuses leaves nothing behind. A reservation whose nodes do not fit beside the slots
in use under the memory limit is refused before it takes any, so that it obtains no
segment for nodes it would give back. The places in the retired list are managed room,
reserved once the nodes are had.

**The standing reserve.** Once a preparation has touched an index, its pool keeps the need
of one change of key, an insert and a remove, at the tree as it is: *Need* with *p* = *q* =
1, 5 nodes for a tree of one leaf, 11 or 12 at height 3. A write then takes its nodes from
the pool, about one for an insert or a remove and two for a change of key, and the next
preparation that touches the index tops up what it took, instead of every write taking its
whole need from the arena and giving the rest back after its publication. The reserve is
taken by the first preparation that touches the index, not at the open. While writes wait,
the pool holds at most the larger of their bound, the lesser above, and the reserve; with
nothing waiting, at most the reserve, and the close frees it with the arena. Its nodes are prepared blocks,
counted in the prepared bytes, which are therefore above zero once a write of an indexed
type has returned, and against the memory limit, like any other bytes. The places in the
retired list follow the need alone: places held for no write would keep the retired list
from letting its room go.

The reserve is never the reason of a refusal: a write is refused for memory only when it
does not fit beside the stored data, never for memory other writes hold for a time
([write admission](../decisions/write-admission.md)).

- A preparation takes the lesser bound of its write first, and once every index of the
  write has it, tops each pool up to its reserve as far as the arena gives; a top-up that
  cannot be had is not a failure.
- A publication gives back what is above the larger of the lesser bound of the writes
  still waiting and the reserve. A withdrawal gives back what is above that bound, the
  reserve with it: what a write took and did not publish goes back to the arena, never
  into a reserve.
- Before a write is refused for memory — in its resolution alone, after what is retired
  was freed; on a child before it stops following, or before a root of its load ends the
  open — every index of the database gives back what its pool holds above the lesser
  bound of the writes still prepared, and the write is tried once more, taking its own
  and no top-up. It is refused only when that try fails, and then leaves every pool at the
  lesser bound of the writes still prepared, which for a write resolved alone is nothing.

So a database with many indexes and a small memory limit opens, and writes to it whatever
fits by itself.

For one insert into an index of height 3, with nothing else waiting, the need is 7 nodes,
7 KiB, while a publication takes about one; the pool keeps what is left up to the reserve
for the next write.

What it costs when many changes wait: the *k*-th insert is counted at the height the
inserts before it can reach, so the need grows with the square of the inserts prepared
and not published, divided by *I*. By the formula, 600 inserts waiting on an index of one
Int64 part, *I* = 40, of height 3 whose root has room for 20 need 13,170 nodes, about
13 MiB; their linear bound is 600 · 9 = 5,400 nodes, about 5.3 MiB, and the pool holds
the lesser. No mode built today lets more than one write wait: the persistence step of
MemoryOnly completes in the hold that prepares the write, write batches are not built,
and a child prepares one root of its load, or one transaction of its stream, at a time.
A transaction of many changes reserves for all of them at once, each index once for
their inserts and removes together, and a write batch, prepared whole before its
publication, will reserve the same way.

### The retired node

A node that a write replaced is a retired item of its own kind, freed by the drain like a
retired root. Freeing it releases the reference of every shared key it holds and then
frees its block. Its bytes toward the next look of the drain are its slot and the slots of
the text entries its shared keys name, as a retired root counts the text entries it
names: an over-count, which only brings a look earlier. Each index write retires about one
node, so the moves of the epoch follow the rule of the drain: on the prototype 78 for
10,000 writes to an index, which did not show as a cost of the read by key.

## The search by a full key

A search by the full key of an index is a step at a time, like an enumeration, and holds
between two steps only the key and the identity of the last entity it returned:

1. It enters the protection of reads and descends to the first entry of the key after the
   last identity returned. If the leaf ends before such an entry, it descends once more
   at the fence of the leaf, the separator above it, at or after which every entry of the
   next leaf lies. No entry of the key: the search ends.
2. It takes the root entry the identity names by its stamp, whatever its state, as a read
   by key does, so that it waits through the mark, or a stamp above the position, of an
   entity of a publication in flight and ends at its position or a later one, never before
   it. The root entry of an insert whose root is not stored yet holds the mark, so the step
   waits for it as well. No entry the tree names keeps the mark for good: the entries of an
   insert enter the tree in the publication that stores its root entry after them, and
   those of a delete leave before its root entry is retired; a publication that fails
   between the two faults the database, and the wait then reports `Unavailable`. An entry
   that is not live, a delete, is passed over. A step whose take waited walks again from
   where it began ("How a range is walked", below).
3. It checks the root against the key; an entry whose root no longer has the key is
   passed over.
4. It copies the root, remembers its identity and leaves the protection. The entity is
   built outside it, and only then does the search move past its identity, so that an
   entity whose build fails is read again by the next step.

A step that ends without an entity takes the sequence of the database read when it began
as the position of its end when it is unchanged after the walk, as a read of an absent
key does, and walks again otherwise, in a round like any other ("How a range is walked",
below). An entity deleted and created again while a search
runs has a new root entry, so a new identity, which can lie after the one the search
returned before; the search passes it over by the keys it remembers, as every walk does
("How a range is walked", below).

The step that finds an entity sees the end of the run as well when it can: the entry
after the one it found, in its leaf, has another key; or that entry is the last of its
leaf, and the leaf has no fence, or a fence of another key, above which every entry of
the next leaf lies. Then the search keeps the sequence of that step, the sequence of the
entity it returned, and its next step ends there without a descent. A fence of the key
says nothing: then the next step descends as before. The next step still enters the
protection, so that a step after a close is refused as every step is. A lazy search so
ends at the sequence of the step that returned its last entity, not at the sequence of a
later call.

A search by the full key of a unique index with no part null ends at the entity a step
returns, whatever the fence shows: at the sequence of that step the entity held the value,
and no other root did ([unique indexes](../decisions/unique-indexes.md): non-null full-key
equality has at most one matching root). A root a later step could meet took the value
after it, when the owner moved away between two steps, and the search would return two
owners of one value. So a search by the full key of a unique index, with no part null,
returns at most one entity, also when writes move the value while it runs. Whether a
search ends so is decided once, when it starts (`IndexSearch`), for every form that steps:
the lazy search and `FindAsync` with its Offset and its Limit, through a class and by
name; a step that returns an entity only reads that choice. A key with a null part, a key
of leading parts and a range end as any search does.

What the sequence of an answer is, in the words of the contract (`LatticeFindResult`): a
position the node had applied when the search looked at the index. It is read in the step
after the descent, so it covers every write whose effect the descent saw, an entry a
delete or a replace took away among them: such a write publishes its position before it
removes the entry. It may be later than the last time the search looked at the index, so
an entity written into the key while the search ran may be absent from an answer whose
position covers that write. A session that moves to it waits for no less than the answer
showed; the position is no snapshot of the run at one sequence, which the contract does not
promise (`ILatticeIndex`: a search "is not a snapshot"). An end without an entity keeps its
check: there the sequence read when the walk began is the only sequence the answer reports,
and the walk again after the sequence moved keeps a session at or after a delete it passed
over.

The key is bound to the fields of the index once, outside the protection, and its fixed
parts are written on the stack in their stored form, by the one writer of that form the
root writer uses as well (`Storage/Values/StoredScalars.cs`; an entry copies its fixed
parts from its root as they lie); a search allocates what it returns and nothing for a
comparison. A root entry met in a node is never released and given to
another root while the search is inside, because the entries of a delete leave the tree
before its entry is retired, so the identity needs no check of its generation.

A lazy search, `Find`, is one object: the enumerable it returns is also the enumerator its
first `GetEnumerator` gives, and it holds the bound key once and where the search stands
between two steps (`IndexSearch`): the identity after the last entity returned, the
sequence of an end a step saw, the read limits and what the search has spent of them, its
direction and its Offset, and the reference to what a walk of more than one entity
remembers (`WalkProgress`, "How a range is walked", below), which the search lets go of at
its end, on `Dispose`, when a read limit fails it and at a step after the close or after a
fault of the database; a further `GetEnumerator` is a search of its own. The search key of a step
refers to that bound key and does not copy it. A search that returns one entity so
allocates that object and the entity: through a class 360 bytes and the entity, by name 352
(`SearchObjectAllocationTests` gives the composition). `FindAsync`
runs its search to its end with the position on its stack, and allocates the list of its
result, the result and the entities; in a Release build nothing more when it needs no wait.
A Debug build makes the state machine of an asynchronous method a class, which every call
allocates.

### The hot key

A key that many entities share is one long run: the prototype checked a run of 6,000
identities beside a writer. Its separators are whole entries, key and identity, so a
descent to one entity of the run is as long as to any other, and a write of one of them
copies one leaf of the run. A search returns the run one entity a step, each step one
descent and at most one more at a fence. What a hot key costs is the key repeated in
every entry and in every separator inside the run: there are no posting lists.

## How a range is walked

A search by the leading fields of an index and a range on the next, in the index order or
its reverse, is the same walk as the search by a full key, which is its narrowest case
(`Engine/Indexes/IndexWalk.cs`, `Engine/Entities/EntityTable.cs`). The tree needed nothing
new for it. A step holds nothing between two steps: no node, no cursor, no slot of the
protection.

1. **Its ends.** The entries of a search lie between two search keys in the order of the
   index: the leading values with the lower bound of the range, and the leading values with
   the upper bound. A search key that gives fewer parts than the index stands before or
   after every entry those parts equal, an edge, which is an identity no root entry has,
   below or above every one; so the order compares the parts the key gives and then the
   identity, and needs no rule of its own for it. An inclusive lower bound stands before
   every entry of its value, an exclusive one after them, and the upper bound the other way
   round. A null is in no range, so a range with no upper end on a part that can be null
   ends before the nulls of that part, at the search key of that null. The walk starts at
   one end and ends at the other, which it never meets, since an edge equals no entry.
2. **One descent.** A step enters the protection and descends once, in either direction,
   to the first entry at or after where it starts, or to the last entry before it
   (`OrderedTree.Seek`): the leaf, the place, and the fence of the leaf on the side it walks
   to, the only one a walk follows.
   Forwards, the next leaf is found by a new descent at the separator above the leaf, at or
   after which every entry of the next leaf lies; backwards, at the separator below it,
   before which every entry of the leaf before lies: the same tree walked both ways, with no
   links between leaves, after LeanStore.
3. **Where it starts.** A search by a full key starts at an identity in its run, after the
   last entity it returned. Any other search starts after the entry of the last entity it
   returned, which it holds between two steps in detached form: a copy that owns the text of
   a shared key, under a head of its own, which the order reads as any entry
   (`IndexLayout.Detach`).
4. **The hint.** Each entry is a hint. The step takes the root entry it names by its stamp,
   as a read by key does, and returns the entity only if the root still has the key of that
   entry, by the order of the index (`SecondaryIndex.HasKeyOf`; for a full key `HasKey`), so
   that it returns an entity only at the place of its key and in the order it walks.
5. **No entity twice.** Over a range an entity whose key moves ahead of the walk would be
   met again under its new pair, and an entity deleted and created again has a new identity,
   so a walk remembers the primary keys of the entities it returned or passed over by Offset,
   as an enumeration does ([queries](../decisions/queries.md), Q07.3.5), and passes over a
   key it remembers. The set, the detached entry and its staging sit in one object
   (`WalkProgress`), made at the first step that returns an entity without seeing the end of
   the search, or that passes over a match by Offset; a search that finds one entity or none
   and passes over no match never makes it. A lazy search by a full key of an integer or a
   Guid key makes it one step later: its first step keeps only the primary key of the entity
   it returns (`FirstKey`), taken from the root it copied in that step, and the next step
   makes the set and begins it with that key, so a search that takes the first of many
   entities and stops allocates no set and reads that entity once. The kept key counts
   against `ReadMemoryLimit` from the step that keeps it, as everything a search remembers
   does. Three searches make the set at their first step instead: a search by a String key,
   whose text would have to be copied out of the root inside the protection; `FindAsync`,
   which counts its first entity as it adds it, with what the walk remembers; and a search by
   a range, which keeps the detached entry of its entity in the `WalkProgress` anyway.
6. **The early end.** With the entity it returns, a step sees the end of the search when it
   can: the entry after it on the side of travel is not of the search, or the fence on that
   side lies beyond the end, or there is none. The search then keeps the sequence of that
   step, and its next step ends there without a descent, as for a full key; so a Limit gives
   "no more" without a further step.
7. **Rounds and room.** A step walks at most `EntriesPerRound` entries inside one period of
   the protection, the hints it passes over and the matches it skips by Offset included, then
   stands at the next entry and enters again. What a step needs and does not have, a
   `WalkProgress`, room for a key or for an entry, is counted against `ReadMemoryLimit` and
   made outside the protection, and the step reads its entry anew; a round that cannot write
   where it stands goes on from where the search stood, and what it remembered stays.
8. **A take that waited.** A take that waited for the position of a publication found its
   entity in the state the publication left, while the entries the step passed before were
   seen before it, a member of the same publication among them in its state before. So the
   step walks again from where it began: a search by a full key from the identity it stood
   at, any other from the entry it stands after, the place a round of the step stood at
   dropped (`WalkProgress` keeps that place apart from the entry it stands after). The new
   round looks at the state of the instance and at the token, as every round does; what the
   step passed over by Offset is remembered, so the walk counts none of it twice, and one
   step never holds a part of a publication. A step that never waits pays one test of a
   byte for it. Steps of one search may still combine states before and after a
   publication, as separate reads may ([the document model](../decisions/document-model.md)).
8. **Offset and Limit.** Offset is linear in the matches it passes over, which are
   remembered and counted once; Limit ends the walk after its last entity, and one more look,
   which copies nothing, says whether a match follows.

Nulls are last in the index order, and a walk in reverse is its exact mirror, so a null
comes first there (the owner's decision of 21 November 2026). The default of a query, nulls
last in both directions with an override for each key ([queries](../decisions/queries.md),
Q07.3.2), is the query layer's to build over the two regions.

What a walk allocates: the object of a lazy search, the entities it returns, and once it
returns more than the entities of one step its `WalkProgress`, with the set of its keys,
whose room grows as an enumeration's does, and two arrays of the detached entry; nothing
for each entity beyond what it returns and that room (`WalkAllocationTests`). A lazy search
by a full key that takes the first of many entities allocates its object, the entity and
the key it keeps, 40 bytes.

## A child

Nothing derived travels to a child. A child builds its indexes while it loads, one entry
of each index for each root it places, through the same reservation and publication as a
write, before it is ready; and it keeps them while it follows, with the order of a
transaction above. An entry its stream contradicts, one to insert that the index holds or
one to remove that it does not, is a divergence: the publication fails, and the child is
faulted as on any failed publication. A bulk build from sorted entries is not built; the
build time for 100,000 users is reported.

A child checks no unique index while it loads and catches up: the roots it copies may
reflect different positions of its master, so two of them may hold one value for a while,
and the tree, ordered by key and identity, holds both. At the position it becomes ready
at, before it is ready, it walks each unique index once in order, each entry compared with
the last one before it that has no null part; a value held twice is a protocol failure, and
the open fails. From then on a transaction it receives is checked on the state it leaves,
as the changes of one publication are (above): its effects may exchange values, or take
a value another of them frees, and one that would give a value a second owner stops the
child as a protocol failure does, before anything of the transaction is published;
nothing is repaired.

## What the structure does not do

- No posting lists: an entry names one entity, and the key repeats in each entry of a run.
- No counts per subtree: Offset is linear in the entries it passes.
- No suffix truncation and no prefix compression: a separator is a whole entry.
- No links between leaves: the next leaf is a new descent at the fence.
- No merge of inner nodes, and no bulk build.
- No change in place: every change of a published node but one child address is a copy.
- No move of a node: compaction does not move index nodes in this milestone.

## Sources

The design follows these, read under their licences; no code is ported, so
`THIRD-PARTY-NOTICES.md` gains nothing.

| Source | What was taken | What was not |
| --- | --- | --- |
| LMDB, `libraries/liblmdb/mdb.c`, OpenLDAP Public License 2.8, commit `74dd6c3ec648c31857fe0a100f11c2a34619e2d7` | Copy a page before it changes (`mdb_page_touch`); a split that keeps the left page full when the new entry goes last (`mdb_page_split`); rebalance below a quarter of fill (`mdb_rebalance`); pages freed only after the oldest reader that could see them has left (`mdb_find_oldest`); the comparison chosen once for a database (`mdb_default_cmp`), here once for an index | The copy of the whole path to the root and the meta page as the point of commit: here one aligned store of an address is the publication, so only changed nodes are copied. Its page format |
| ROWEX: V. Leis, F. Scheibner, A. Kemper, T. Neumann, "The ART of Practical Synchronization", DaMoN 2016, section 4 | Readers take no lock and check no version; a node that changes is built privately and published by one atomic store of its address; the replaced node is retired to an epoch | The radix tree itself, which needs keys whose bytes compare in the order of their values: a second definition of order beside the comparison service |
| LeanStore, `backend/leanstore/storage/btree/core/BTreeGenericIterator.hpp`, MIT, commit `fcb64bf39cf69c435173f76e062cbb305a9a998f` | The next leaf found by a new descent at the fence, the same way forwards and backwards, without links between leaves | Fences stored in each node; optimistic versions and restarts |
| RFC 3629, UTF-8 | That the bytes of UTF-8 sort as code points, from which the one turn against the order of UTF-16 code units follows | — |
| DuckDB `string_t`, `src/include/duckdb/common/types/string_type.hpp`, MIT, commit `aa87a115e8ad8affc3a4ee2e373525cbb2c6efd8`; the binary view of Apache Arrow, Apache-2.0 | The form of a prefix beside a shared key, measured in the engine | Not taken |

The study read also the skip lists of LevelDB (BSD-3-Clause) and RocksDB (Apache-2.0),
the adaptive radix tree of DuckDB, the synchronization of ARTSynchronized (Apache-2.0),
nbtree of PostgreSQL, `OrderedMultiMap` of `Arc.Collections` (MIT) and `ImmutableSortedSet`
of .NET (MIT); the prototype's [README](../../src/Prototypes/SecondaryIndexComparison/README.md)
names their commits. None of them is part of the design.
