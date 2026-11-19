# The ordered secondary index

How a declared secondary index lies in memory, how a reader walks it while the writer
changes it, and what a write costs. The structure was chosen by the owner on 19 November
2026 ([index structures](../decisions/index-structures.md), the notes of that day); the
milestone contract states its rules under
[index preparation and traversal](milestone-1-engine-contract.md#index-preparation-and-traversal).
The internal types are written and refuse until they are implemented:
`src/Moltaro.Lattice.Engine/Storage/Ordered` holds the nodes and entries in arena memory,
`Engine/Indexes` the tree, its order and the index of an entity type, and
`Engine/Entities/IndexSearch.cs` the search. Nothing described here is built yet; the
[README](../../README.md) says what works.

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
identity. A search finds the first entry of a key and walks its run; a range will walk
from one key to another in the same order. An index on the members of a collection,
where a state would contribute a set of entries, is not built.

Entries are ordered part by part in the order of the index, each part by the comparison
of its field, and then by identity. Keys are never compared as raw bytes: an integer by
its value, a floating-point value by `CompareTo` (NaN first, the two zeros equal), a
Decimal by its value, a Guid by `CompareTo`, the temporal types by their ticks or day
numbers, text by the comparison of its field, the same in a UTF-8 and in a UTF-16
database: Ordinal by UTF-16 code units, without case by the code units of the texts
mapped through the case table of the core ([strings](../decisions/strings.md), Q46). A
null orders after every value of its part and equals another null, so a composite key
goes on to its next part. All of it is the one comparison service of the schema, which
gains the order beside its equality.

Two keys can be equal and not the same bytes: a text under another case, a Decimal of
another scale. They are one key with one place in the tree; an entry holds the bytes of
its own root's value, and a write that changes only the bytes rewrites the entry in
place.

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
one run does not decide. A write copies and retires one node of each index it changes,
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

**Open: a prefix beside a shared key.** Whether a shared key carries its first 8 bytes
beside the reference, so that most comparisons do not read the text entry, is decided by
a measurement in the engine on keys with random and with common heads. On the prototype
the prefix won on random keys and lost on keys of 32 bytes that share their first
bytes, as numbered logins, addresses and paths do. The head byte 254 is kept for that
form; nothing else of it is built.

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
the one of every block, confirmed for index nodes; the write of an index of long keys is
measured before it is questioned.

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
not copied, or into the root of the tree. After the store, the nodes it replaced are
retired. A split of a full node into two always fits: a leaf holds at least four of the
widest entries of its index, an inner node four of its widest separators.

### The entries of one write

What one write does to one index is the entry its old state contributes against the
entry its new state contributes: the same bytes, nothing; an insert of the entity, an
insert; a delete, a remove; another key, an insert of the new entry and a remove of the
old; an equal key in other bytes, a rewrite. Both roots stay as they are between the
preparation and the publication of the write, so each step computes this again.

The entries of a write are installed in the hold of the lock that publishes its root,
each by its own stores, in this order: the new entries are inserted, the root is
published, the old entries are removed or rewritten. Between those stores a reader may
find the new entry before the root has the new key, or the old entry after the root has
lost the old key; the check of every entry against its root covers both. A child that
applies a transaction of several effects inserts the new entries of all of them before
its position and removes the old ones after it.

### The reservation

Nothing grows inside a publication. In the hold that prepares a write, each index it
changes reserves the nodes the publication can take, as prepared blocks of the arena,
counted in its prepared bytes and in the budget, and as many places in the retired list.
A write withdrawn before its publication gives them back; a prepared, unpublished write
holds them while the storage step is busy, and no reader can reach them. A reservation
the arena cannot hold takes nothing and is refused as a write the memory budget cannot
hold is: before its commit.

The writes prepared and not published share one pool for each index. While *p* inserts,
rewrites among them, and *q* removes wait, the pool holds

> *p* · (2*H* + 1) + *q* · (*H* + 1) nodes, with *H* = *h* when *p* = 0, and
> *H* = *h* + 1 + ⌊(*p* − 1) / *I*⌋ when *p* ≥ 1,

where *h* is the height of the tree now and *I* the separators of its widest form an
inner node holds before it is full, at least four. With no insert waiting the tree cannot
grow, and removes alone are counted at the height it has. Why it is enough:

- An insert at height *H* splits at most every level and adds a root: at most two nodes on
  each level and one more, 2*H* + 1, and it retires at most one node on each level. A
  remove copies at most one node on each level, a merge of two leaves taking one for
  both, and retires at most one more, the neighbour of that merge: *H* + 1 serves both
  counts.
- The height grows only when the root splits. A root made by a split holds one separator,
  and one insert adds at most one separator to any node. So the first insert may grow the
  tree, and every further growth takes *I* inserts more: *H* bounds the height at each of
  the waiting publications.
- A preparation tops the pool up to the bound for all writes that wait, with its own
  counted; a publication consumes at most its share and gives back to the arena what is
  above the bound of the writes still waiting; a withdrawal does the same.

For one insert into an index of height 3, with nothing else waiting, the bound is 9 nodes,
9 KiB, while a publication takes about one. The peak of prepared bytes of an index
during a group of writes is reported with the implementation.

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
2. It reads the root entry the identity names. One that is not live is passed over: the
   entry of an insert is installed before its root is published, and the entries of a
   delete are removed after its position.
3. It checks the root against the key; an entry whose root no longer has the key is
   passed over.
4. It copies the root, remembers its identity and leaves the protection. The entity is
   built outside it.

The key is bound to the fields of the index once, outside the protection, and its fixed
parts are written on the stack in their stored form, by the one writer of that form the
root writer uses as well (`Storage/Values/StoredScalars.cs`; an entry copies its fixed
parts from its root as they lie); a search allocates what it returns and nothing for a
comparison. A root entry met in a node is never released and given to
another root while the search is inside, because the entries of a delete leave the tree
before its entry is retired, so the identity needs no check of its generation.

### The hot key

A key that many entities share is one long run: the prototype checked a run of 6,000
identities beside a writer. Its separators are whole entries, key and identity, so a
descent to one entity of the run is as long as to any other, and a write of one of them
copies one leaf of the run. A search returns the run one entity a step, each step one
descent and at most one more at a fence. What a hot key costs is the key repeated in
every entry and in every separator inside the run: there are no posting lists.

## How a range will be walked

Ranges, order and paging are built after the search (S15 of Epic 1); the tree is made so
that they need nothing new from it. A step holds nothing between two steps: no node, no
cursor, no slot of the protection. It enters the protection, seeks after the last (key,
identity) it returned, in either direction, returns the next entity and leaves. Forwards,
the next leaf is found by a new descent at the separator above the current one, at or
after which every entry of the next leaf lies; backwards, at the separator below it,
before which every entry of the leaf before lies: the same tree walked both ways, with no
links between leaves, after LeanStore. With entries unique by (key, identity), a step
that seeks strictly after the last pair returns no pair twice and every pair that is
there throughout, also inside a run of equal keys. Nulls last in both directions is a
rule of the walk: the null region of a part is visited after its values, whichever way.
Whether the identity of an entry stays the root handle, under which an entity deleted and
created again during a pass would come back as another identity, is chosen with ranges.

## A child

Nothing derived travels to a child. A child builds its indexes while it loads, one entry
of each index for each root it places, through the same reservation and publication as a
write, before it is ready; and it keeps them while it follows, with the order of a
transaction above. An entry its stream contradicts, one to insert that the index holds or
one to remove that it does not, is a divergence: the publication fails, and the child is
faulted as on any failed publication. A bulk build from sorted entries is not built; the
build time for 100,000 users is reported.

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
| LMDB, `libraries/liblmdb/mdb.c`, OpenLDAP Public License 2.8, commit `74dd6c3ec648c31857fe0a100f11c2a34619e2d7` | Copy a page before it changes (`mdb_page_touch`); a split that keeps the left page full when the new entry goes last (`mdb_page_split`); rebalance below a quarter of fill (`mdb_rebalance`); pages freed only after the oldest reader that could see them has left (`mdb_find_oldest`) | The copy of the whole path to the root and the meta page as the point of commit: here one aligned store of an address is the publication, so only changed nodes are copied. Its page format |
| ROWEX: V. Leis, F. Scheibner, A. Kemper, T. Neumann, "The ART of Practical Synchronization", DaMoN 2016, section 4 | Readers take no lock and check no version; a node that changes is built privately and published by one atomic store of its address; the replaced node is retired to an epoch | The radix tree itself, which needs keys whose bytes compare in the order of their values: a second definition of order beside the comparison service |
| LeanStore, `backend/leanstore/storage/btree/core/BTreeGenericIterator.hpp`, MIT, commit `fcb64bf39cf69c435173f76e062cbb305a9a998f` | The next leaf found by a new descent at the fence, the same way forwards and backwards, without links between leaves | Fences stored in each node; optimistic versions and restarts |
| DuckDB `string_t`, `src/include/duckdb/common/types/string_type.hpp`, MIT, commit `aa87a115e8ad8affc3a4ee2e373525cbb2c6efd8`; the binary view of Apache Arrow, Apache-2.0 | The form measured for the open question of a prefix beside a shared key | Not decided |

The study read also the skip lists of LevelDB (BSD-3-Clause) and RocksDB (Apache-2.0),
the adaptive radix tree of DuckDB, the synchronization of ARTSynchronized (Apache-2.0),
nbtree of PostgreSQL, `OrderedMultiMap` of `Arc.Collections` (MIT) and `ImmutableSortedSet`
of .NET (MIT); the prototype's [README](../../src/Prototypes/SecondaryIndexComparison/README.md)
names their commits. None of them is part of the design.
