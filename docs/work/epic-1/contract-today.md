# Where the contract stands today

The state after the last closed sprint; the coordinator keeps it current.

**In the contract and working.** Opening and closing; master and child;
MemoryOnly. The compiled schema: the closed JSON descriptor with identities of entity
types, fields, indexes, enum types and owned types, scalar fields, enum and Flags types,
text policies and the string encoding, owned values, Lists, Maps, Sets and references,
index parts that are paths into owned values; binary save and load with a digest; both
fingerprints; the optional .NET class, bound by one table for every position, with a .NET
enum that has to agree with its enum type. Records and owned values that hold nested
values and copy what is set into them. Sets by class and by name support reading,
enumeration, insertion, replacement, upsert, deletion and expected revisions for scalar
and Enum fields, owned values, Lists, Maps, Sets and references. All declared key types,
both text comparisons, both encodings, and Plain and Dictionary text storage are supported.
Search through a declared secondary index
by its full key, by its leading fields and with a range, in either direction and in pages
with Offset and Limit, within the read limits, by class and by name, on a master and on a
child (S15, part 1). A write batch across entity types, committed on a master or through a
child (S17). Positions and sessions; a write through a
child completes when that child has applied it; waiting for a position with a timeout.

**How the engine does it.** Since S12 `LatticeDatabase` lives in the engine assembly and
opens the engine; the placeholder is deleted. Mechanisms of `Moltaro.Lattice.Engine`: the
arena of native memory — segments, size classes, regions, the block header, relative
addresses, the reserve, the memory limit and its counters — and the table of root entries
with generations (S04); the root layout compiled from an entity type of scalar fields,
with the writer and the reader of a root — fixed fields at their declared width, String
and Binary values inline, in a block of their own or in chunks (S05). The measurement
program `Moltaro.Lattice.Measurements` holds the reference fixture, its first scenario
and the development manifest (S05). The engine database: a MemoryOnly master that
inserts, reads, replaces, upserts and deletes entities of scalar fields by primary key,
by class and by name, with revisions, positions and sessions; layouts compiled at open;
the lookup claim and the protection of a root, with no code of the application inside
a gate; reads that allocate the returned copy and nothing else. The scenario tests of
reads and writes run on it (S06; through `LatticeDatabase` since S12). By the owner's decisions of 15 November 2026 an entity, a write
batch and the queue of writes have no limit of their own, and the validation of the core
refuses text that is not well-formed on both implementations. From S07 the engine
stores every key type of the catalog — the eight integers, String with either
comparison, Guid —, a String field compared without case, Enum fields and a schema in
UTF-16, through one comparison and hash service of the schema; the validation of the
core checks enum values, and a write batch takes one operation per entity as the schema
compares keys. Text compared without case is compared by a case table of Lattice's own
in the core, the simple uppercase mapping of Unicode 16.0.0 in version 1 of the table,
and not by the runtime: keys, Map keys, Set members and the identity of a batch read it
through one rule. From S08, part 1, a read of the engine is protected by an epoch and
takes no lock; what a write replaces or removes is retired and freed once the reads that
could reach it have left; the primary index is a hash table of the engine's own that is
read while a write is published; the package `Arc.Collections`, referenced by the engine
and used by none of its code, was removed in S13 by the owner's decision of 18 November
2026, and the secondary indexes of S14 are a structure of its own. Since S13 a String field
with Dictionary storage is stored once for the database: a text entry of the arena that
holds the exact bytes, the root holding its relative address, one count of the blocks
that refer to it changed only under the lock of the writes, and a table by content that
only writes use; a read of such a field costs what a read of a Plain value out of line
costs, and a child builds its own entries from the bytes it receives. From S08, part 2,
an enumeration of the engine goes on while entities change and the index grows: it
remembers the keys it has returned, returns no identity twice and every entity nobody
changed, and holds nothing between two steps; the options `ReadMemoryLimit` and
`ReadTimeLimit`, none set by default, bound the memory and the time of a pass, and no
call carries limits of its own; every hash of the primary index uses a seed the instance
draws when it opens. Since S09, part 2 a write of the engine is prepared and published
under one lock of the writes, one write at a time, and handed to a storage step in
groups of at most 32 writes or 8 MiB; in MemoryOnly that step completes at once and the
call publishes in the same hold. Writes to one entity are executed in the order of their
calls; a write is withdrawn by its token until it is dispatched; `MemoryLimit` is the
limit of the arena, and a write is refused for memory only when it does not fit beside
the stored data; a fault of a publication leaves the instance `Faulted`. The close of a
database is one operation that every later call joins; the token of an asynchronous read
is honored in every wait of its step; a key is given by name without a box. From S10 a master of the engine listens on its `ListenUri` and a child of the engine
connects over TCP, loads the schema and every entity with its revision, and becomes Ready;
a frame carries a CRC32C and no address travels; `OpenTimeout` bounds reaching the master
and the time without a frame. From S11 the master keeps writing while children
load and follow: a child ends its load at a fixed position of the master and applies the
master's changes in order, each whole, at the master's positions and revisions; the
master releases a change when every child has applied it, and until S33 nothing bounds
what it keeps for a child that falls behind. From S12 a child forwards its writes to its
master on its one connection: the child validates and encodes the root, the master checks
it as a child checks a root of a load and executes it, and the reply reaches the child
before the transaction of that write; the call returns when the child has applied it. A
master that closes ends its connections in order and says so with the frame `Closed`, so
a write it did not execute ends in `Unavailable` and only a connection lost without that
frame leaves a sent write `IndeterminateOutcome`. A wait for a position wakes only when a
publication reaches it. A child that lost its master serves reads at its position and
refuses writes; it does not connect again until S34. From S14 every declared secondary
index is a copy-on-write B+tree of the engine's own in the arena: its published nodes
never change and a reader takes no lock; the entries of a write are installed in the hold
that publishes its root, the upkeep of every index of a type in one place
(`ChangePublisher`); a search by the full key returns an entity only if its current state
has the key. A text key of 64 bytes or less is copied into the entry whatever the storage
mode of its field, a longer one references the text entry of its root, and a field that
is part of an index stores a value longer than 64 bytes as a text entry. A child builds
its indexes while it loads and keeps them while it follows. A search remembers the
entities it returned or passed over, so an entity is returned at most once, also across a
delete and a re-creation and when its key moves ahead of the walk. From S16 a unique
index gives a value one owner: the master refuses a write that would give it a second one,
with `UniqueConstraintViolation` and the name of the index, and a write whose value another
prepared write takes or frees waits for that write; a key with a null part is exempt; a
child checks its unique indexes at the position it becomes ready at and stops on a
violation it would have to apply. From S17 a write batch is one write at one position: its
changes are reserved and checked together on the state they leave, its members are stored
before the position is published, so no read sees a member in its new state before every
member is visible, and a child applies it through the same publisher and acknowledges only
its position; a batch committed through a child reaches the master as one request. Since
S18, owned values and Lists live in the root's storage tree and references are keys. S19
adds compact Map and Set storage through eight entries and a local hash lookup above eight.
Placement checks collection uniqueness under the declaration, as does a child skipping a
received state during catch-up. Wire format 6 carries the
entries, and the receiving node builds its own lookup. The layout version stays at 2.
Whole-entity operations support the collections through either API.

**In the contract, not implemented.** Entry points refuse with `NotSupportedException`;
tests are written and skipped; samples are written.

| Feature | Contract | Tests waiting | Sprint |
| --- | --- | --- | --- |
| Durable mode | `LatticePersistenceModeEnum.Durable`, the default | None; one test asserts the refusal | S27–S32 |

**Accepted for the milestone, not in the contract yet.** Each gets its contract, sample
and tests in the sprint named.

| Feature | Sprint |
| --- | --- |
| The memory budget as an option, the budgets of a read as options that are not set by default; resource errors. An entity, a batch and the queue of writes have no limit of their own (decisions O18, O19 and O20) | S08, S09, S17, S24 |
| Explicit partial updates | S20 |
| Protected read-only views | S21–S22 |
| Operation identities, retained outcomes and safe retry | S26 |
| The storage provider as an object of its own package | S27 |
| Activation of a restored database | S36 |
| Schema migration | S37–S38 |
| Diagnostics | S39 |
