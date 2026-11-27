# Stages

## Stage 0. Ground

- [S01 · Assemblies, package ownership and the default lane](sprints/s01.md)

## Stage 1. Schema

Both sprints work in the contract assembly and need no engine.

- [S02 · Schema: identities, text policies and enums](sprints/s02.md)
- [S03 · Schema and values: owned types, collections and references](sprints/s03.md)

## Stage 2. One node, in memory

- [S04 · Arena](sprints/s04.md)
- [S05 · Root layout and values](sprints/s05.md)
- [S06 · Reads and writes of one entity](sprints/s06.md)
- [S07 · Every key type and declared value](sprints/s07.md)
- [S08 · Enumeration under change and the growth of the index](sprints/s08.md)
- [S09 · Write coordinator and write limits](sprints/s09.md)
- [S09, part 2 · The write path after a proven source](sprints/s09-part-2.md)

## Stage 3. Master and children

- [S10 · A child loads the database](sprints/s10.md)
- [S11 · A child loads under writes and follows the master](sprints/s11.md)
- [S12 · Writes through a child, sessions; the placeholder is removed](sprints/s12.md)

## Stage 4. Indexes and batches

**The stage, S13 to S17, was approved by the owner on 18 November 2026, as the planner
relays.** Its contract is the public contract that stands today: `StringStorage`,
`set.Index(name)`, `Find` and `FindAsync`, `IndexKey`, `IndexBound`, unique indexes and
`ILatticeWriteBatch` have been in it, with documentation, samples and skipped tests,
since the plan was approved. The internal types and members of each sprint are written
in its contract step, after its study, and reviewed in the sprint, as in S10 to S12.
What changes something the owner approved comes back to him inside the stage: the
structure of secondary indexes with the fate of `Arc.Collections`, and the form of
`IndexKey` and `IndexBound` if a measurement changes it, both in S14. The planner's pass
over the open questions on the same day found none that the stage touches: Q31 and Q33
are decided with S24; of Q35 the open points are 4 (S20), 5 (S37) and 7 (S18, S19); Q40
is S26 to S36; Q52 is the owner's after S27.

- [S13 · Text entries and Dictionary strings](sprints/s13.md)
- [After S13 · Findings of the outside review](sprints/after-s13.md)
- [S13R, part 2 · Refactoring that owes a measured figure](sprints/s13r-part-2.md)
- [S14 · Secondary indexes and exact search](sprints/s14.md)
- [S14, part 2 · The cost of index upkeep and of a search](sprints/s14-part-2.md)
- [S15 · Ranges, order, paging and the binding rules](sprints/s15.md)
- [S16 · Unique indexes](sprints/s16.md)
- [S17 · Atomic write batch](sprints/s17.md)

## Stage 5. The full type model and access

**The record of S17R** (closed on 23 November 2026; its entries are W1 to W5 of the
[refactoring backlog](../refactoring.md), the architect's review after S17). It was
coordinated by the eighth coordinator of the epic, who stays for the sprints of the night
by the owner's word in the planner's session, not replaced at this boundary. The
implementer and the narrow reviewer ran at Opus 5.5 high. Five commits, one entry each, W5
the commit of comments and documents; no assertion changed and no test was removed, which
the coordinator and the reviewer checked line by line. The JIT listings of the callers each
entry names, a Release driver over the public API with tiered PGO off, its engine compared
with the tree's build, are kept outside the repository: W1 makes three calls fewer on an
insert and on a delete into an Int64 index (`TopUpIndexes` 4 → 3, `OrderedTree.TryReserve`
5 → 4, `NodePool.TopUp` 4 → 3, `Trim` 5 → 4), while `NodePool.AfterPublication` is no
longer inlined into the tree's (one call more a publication) and `LeftAfterMove` grows from
680 to 895 bytes with no call; W3 takes the frame of the single path from 168 to 96 bytes;
W2 leaves the path that succeeds as it was and makes fewer calls in the preparations of a
child; W4 leaves the writers of the delivery identical. The review took one round: no P1,
no P2, 2 P3 (two comments that cited what no longer said it) and P4s, of which one sentence
at the root of `OrderedTree` was taken; the fix, comments only, was read by the
coordinator. The hardest part to follow, by the reviewer: that the height and the room the
pool keeps are never stale, which holds because every store to the root and the height of
the tree sits in `Insert`, `Remove` or `Rewrite`, each ending in `AfterPublication`; a
comment there now says so. Every suite at the head: 3,215 tests, 3,086 passing and 129
skipped, in Debug and in Release. No start of the machine: the figures are owed to the
start of S18, on `index-writes` with 3 and 6 indexes, `unique-writes` lines 1 to 3 and
`following-children`; if `index-writes` falls, the first remedy to try is to inline
`NodePool.AfterPublication` again.

**The record of the search by index against a model** (closed on 24 November 2026; tests
only, beside S17R and S18). The owner asked on 23 November 2026 whether the search by index
is tested close to completely for correctness across the types of a part. An audit of the
tests found equality close to complete for every type, and order and range held against a
model only for Int32, Int64 and String. This work adds one differential test of a search
against a sorted list in memory, in `Moltaro.Lattice.Tests/Indexes/Model`, with the shared
model there and the tests in `Scalars`, `Composites` and `Unstored`. It was coordinated by
the eighth coordinator of the epic; the implementer ran at Opus 5.5 high, its one reviewer
at xhigh, no breaker.

- *What it covers.* Every scalar type that can be a part, nullable and never null, 400
  entities a cell (at least 4 leaves at the narrowest entry), seeded values and the edges of
  each type (zero, NaN in three forms and the empty text stored beside the nulls); the exact
  search of every stored value and of values never stored; ranges with every kind of bound,
  on, between and beyond stored values; both directions; pages by Offset and Limit with
  `HasMore`, an Offset at the end and past it included; through a class and by name; on a
  master and on a child, which builds its index once from the load and once while
  following. Composite keys of three and four parts of mixed widths, a fixed part after a
  text part included, with every count of leading parts and a range after them. The path of
  a single never-null part of eight signed bytes (`IndexOrder.OneNumber`) has a test of its
  own. A Reference part and a part on a path into an owned value are written and skipped
  with the shared reason. The two older oracles became the one model, and no assertion
  changed.
- *Mutants killed by hand,* each made in the engine of a copy and restored: an order of
  signed or unsigned numbers read the other way, an exclusive lower bound taken as
  inclusive, nulls at the wrong end and a reverse walk started before them, a comparison
  without case that does not map a non-ASCII letter or a surrogate pair, a separator one
  entry off, `HasMore` true after a full page, a Decimal ordered by its scale, the UTF-8
  order around the surrogates, a zero fixed part or a NaN taken as null, an offset past a
  text part one byte off. An inner node splits only in the composite of four parts.
- *What stays empty, and why.* Leading parts for Int8, UInt8, UInt16, UInt64, Float32,
  TimeOnly, TimeSpan, Enum and Flags, which are a part of no composite key of the tests;
  Boolean ranges whose bounds are only stored values, since it has no value between or
  beyond false and true; negative values on the eight-byte path for DateTimeOffset and
  TimeOnly, whose ticks are never negative; Reference and owned paths, skipped until S18
  stores them; text that is not normalized; text with Dictionary storage in the model
  (`TextKeySearchTests` keeps its equality); trees of three levels.
- *Review:* two rounds. Round 1: no P1; one P2 (the nullable cells held back the values
  that share a null's bytes or its edge of the order, so a mutant that took a zero part or
  a NaN for null passed the model) and four P3 (no fixed part after a text part, an id check
  that named nothing, no page past the end, the folder). Round 2: every finding closed. The
  hardest part to follow, by the reviewer: which searches a cell makes, assembled from four
  members of `ModelSearches` and the comments of the runs.
- *No defect of the engine was found.* The tests add about 25 to 35 seconds to a Debug run
  of `Moltaro.Lattice.Tests` and 10 to 15 in Release, under a minute, so no lane of their
  own. Every suite at the head: 3,274 tests, 3,142 passing and 132 skipped, the three new
  skips being the Reference and owned paths.

- [S18 · Owned values, Lists and references](sprints/s18.md)
- [S18R](sprints/s18r.md)
- [ARM64 follow-up after S18R](sprints/s18r-arm64-follow-up.md)
- [S19 · Maps and Sets](sprints/s19.md)
- [S20 · Partial updates](sprints/s20.md)
- [S20I · Investigate the Debug timeouts](sprints/s20i.md)
- [S20R · Refactor packed Map scalar pairs](sprints/s20r.md)
- [S21 · Protected views by name](sprints/s21.md)
- [S21I · Protected List traversal](sprints/s21i.md)
- [S22 · Typed protected views](sprints/s22.md)
- [S22C · Cancellation of a registered position wait](sprints/s22c.md)
- [S23 · Compaction and shrink](sprints/s23.md)
- [S23A · Evacuate nearly empty segments](sprints/s23a.md)
- [S24 · The memory budget](sprints/s24.md)
- [S24A · Budget ownership, withdrawal and dictionary growth](sprints/s24a.md)
- [S24R · Capacity wait and ownership cleanup](sprints/s24r.md)
- [S25 · Reference fixture, managed reference, the memory gate](sprints/s25.md)

## Stage 6. Durability

**The word for the durable store**, a note of the planner of 21 November 2026 until the
contracts of the stage are written: the engine says "persistence" for what makes a write
durable and "storage" for the form of the entities in memory, while the public contract
says "storage" for the durable store of a provider: `LatticeDatabaseOptions` with its
`StorageConnectionString`, and the documentation of `ILatticeSet`,
`LatticeDatabaseStateEnum`, `LatticeErrorEnum` and `LatticePersistenceModeEnum`. The
contract of the stage, before S26, changes it and lists it, or keeps it with its reason.

- [S26 · Operation identity and safe retry](sprints/s26.md)
- [S27 · PostgreSQL provider and Durable open](sprints/s27.md)
- [S28 · Durable commit](sprints/s28.md)
- [S29 · Storage failures and the loss of leadership](sprints/s29.md)
- [S30 · Saved base and background application](sprints/s30.md)
- [S31 · Journal limits, cleanup and receipt retirement](sprints/s31.md)
- [S32 · Recovery under failure](sprints/s32.md)

## Stage 7. Replication to release level

- [S33 · Bounded delivery and slow children](sprints/s33.md)
- [S34 · Reconnect, reload and a restarted master](sprints/s34.md)
- [S34A · Advisory locks](sprints/s34a.md)
- [S35 · Cluster verification](sprints/s35.md)
- [S36 · Restore and a new history](sprints/s36.md)

## Stage 8. Operation

- [S37 · Schema migration on the master](sprints/s37.md)
- [S38 · Migration across the database](sprints/s38.md)
- [S39 · Diagnostics](sprints/s39.md)

## Stage 9. Release gates

**Carried from the planning of observability (Epic 3, R4), 19 November 2026.** A
measurement must not put a shared write on the read path: what a read contributes is
either read from state the engine keeps anyway when a collector asks, or kept per thread
and summed on request. An instrument that needs a counter incremented by every read on
one cache line is not built.

- [S40 · Ordinary-load gates](sprints/s40.md)
- [S41 · Burst and recovery profile](sprints/s41.md)
- [S42 · Close](sprints/s42.md)
