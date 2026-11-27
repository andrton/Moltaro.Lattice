# S14, part 2 · The cost of index upkeep and of a search

**Track:** full, a critical sprint · **Needs:** S14 · **Decided by:** the planner, 19
November 2026, on the figures of S14 ("What is weak" in its record). It comes before S14R
and S15 and is planned for the copy-on-write tree.

**The brief, as passed by the planner on 19 November 2026 after the study.** The study is
the record that follows this brief; its figures, its traps and its numbered proposals are
not repeated here, and where it proposes, this brief decides. E1 to E4 and E8 are entries
of the architect's review after S14 in the [refactoring backlog](../../refactoring.md).

- **Goal.** The upkeep of the indexes of a write and a search by a full key cost what
  their work needs, measured against the same type without indexes and against the bare
  tree of the prototype.
- **Review.** A critical sprint: one reviewer, and beside it a breaker, a second agent
  that writes probe tests against the guarantees this brief states and reports only what
  fails ([development process](../../process.md#review)). Its second target, after the end of a
  run of (d) (the planner, 20 November 2026): with `Need` the exact target, the reservation
  has no slack, so after any one publication — an insert that splits the root, a remove
  that merges into it, in either order among the waiting writes — the pool and the places
  still hold at least `Need` of what waits, at the tree as it then is. The probes are
  sequences that publish removes before inserts, and inserts that grow the tree while
  others wait. A third: a write refused at a later index after an earlier index topped up
  its pool — the top-up must not be what made it fail (the rule of (b)). A fourth, the
  boundary of the masks of (a): types of 63, 64 and 65 indexes, with none, a move and a
  rewrite mixed on both sides of the 64th, and a write that changes only an index beyond
  it (the planner, 20 November 2026). The reviewer checks one bound of (d): the UTF-8 bytes
  of a text part are compared as bytes up to `SearchKey.TextBytesPerPart` and decoded
  beyond it, so a key of 256 and of 257 bytes, and a part exactly at the bound in a
  composite key, give the same order and the same search as the oracle (the planner,
  20 November 2026).
- **Acceptance.**
  - (a) A replace that keeps every indexed key costs what it costs for the same type
    without indexes, within the gauge (today −20 % with one index, −62 % with six). The
    rule for skipping the upkeep of an index is the byte equality of its two entries, the
    one the replaced root contributes and the one the new root contributes, decided once
    in the preparation after the new root is written. Equality under the
    comparison of the index is not the rule, because an entry copies stored bytes, and a
    change of case only, the scale of a Decimal, +0 and −0 or the bits of a NaN change
    those bytes while the values compare equal. The decision is recorded with the
    prepared change, one for each index, and the reservation, the release, the
    installation and the removal read it (E2). A type may have more indexes than the bits
    of the record: from the last bit on, the bit is shared and says "decide again", and
    deciding again reads the same two roots, so it cannot differ from the preparation. The
    comment at the record states this rule; no limit goes into the schema language (the
    planner, 20 November 2026).
    As measured on 20 November 2026 on rented-arm64-1: a replace that keeps every indexed
    key costs, above the same type without indexes, about 30 ns for each index — 0.96,
    0.94 and 0.86 of its rate with one, three and six indexes, where it was 0.80, 0.56 and
    0.39. With one index this is within the gauge; with three and six it is not. The owner
    accepted it as measured (20 November 2026). What is left is the decision itself: it
    looks up the stored fields of each part on every write. The stored fields of an index
    are resolved once when its layout is compiled, in S16, after S14R has made the
    comparison of a part one, and the same line is measured there (entry F6 of the
    [refactoring backlog](../../refactoring.md)).
  - (b) The upkeep of one index for a write that changes its key costs at most 1.5 times
    the bare tree's work measured by the prototype on the same machine (today 3 to 3.5
    times). Its base comes first: the reservation as one closed formula (E1), with a test
    of the engine that holds the closed form against the loop. Then a standing reserve of
    nodes instead of topping up and trimming on every write, on three conditions: it is
    counted where prepared memory is reported and against the memory limit, like any
    other bytes; it is given back at close; and it is never the reason of a refusal (the
    owner, 20 November 2026, on the planner's proposal; it follows from the decision on
    write admission, "a write is refused for memory only when it does not fit beside the
    stored data, never for memory other writes hold for a time"). The rule: a preparation
    takes the need of its write and then tops the pool up to the reserve as far as the
    arena gives; a top-up that cannot be had is not a failure. Before a write is refused
    for memory — in its resolution alone, after what is retired was freed, and for a child
    before it stops following — every index gives back what its pool holds above the need
    of the writes still prepared, and the write is tried once more, taking its own need and
    no top-up. A write is refused only when that try fails. So a database with many
    indexes and a small limit opens, and writes to it whatever fits by itself. A write
    refused in its last try leaves every pool at the need of the writes still prepared,
    which for a write resolved alone is nothing: the nodes an earlier index took for it go
    back to the arena, not into its reserve (the planner, 20 November 2026; a refused write
    has no effect). Each entry is built once for a write.
  - (c) One object holds a search, with the key in it once; a search for one entity
    allocates that object and its result. The allocation test pins this to a number of
    bytes, so that one more object fails it. This answers the study's question on (c): a
    lazy `Find` holds its key between two steps, and holds it once. With it, a defect of
    consistency, fixed test first (E8): `FindAsync` delivers its failures by the rule of
    every asynchronous member (the owner, 20 November 2026, on the planner's proposal): an
    asynchronous member throws at the call only what the caller can know from its own
    arguments — a null or invalid argument, a default key, a key or options the index
    cannot serve; everything that depends on the state of the database comes through the
    returned task — a `LatticeException` of any code, also on a path that needs no wait,
    and `ObjectDisposedException` after a close. A close can come between the call and its
    first step, so the caller handles the task's path in any case, and it is what
    `ReadAsync`, the writes, `EnumerateAsync` and `OpenAsync` already do. The rule is
    written into `AGENTS.md`, "Shape of the public API", with this sprint (the owner,
    20 November 2026, on the review: it lived only here and in comments). Where the XML
    documentation of `FindAsync` or `WaitForPositionAsync` says how a failure arrives, the
    implementer corrects it in its commit. The test author checks every
    other asynchronous member that has a fast path written by hand against the same rule
    and lists what it finds. The form of `SearchKey` changes with it (E4). The list found
    the same defect in `EngineDatabase.WaitForPositionAsync`, both overloads: it joins
    this sprint, test first, and is fixed with `FindAsync` (the planner, 20 November 2026;
    the scope of E8). Its tests: Unavailable before the open and once faulted,
    HistoryMismatch, the lost master and a close come through the task; a timeout after a
    wait stays as it is.
  - (d) Targets on rented-arm64-1, one thread, `index-search`: a search by an Int64 key
    at least 700 k a second (today 450 k), by a text key of 8 bytes at least 450 k
    (163 k), by one of 100 bytes at least 200 k (119 k). A target that is missed is
    reported with the breakdown of what is left; it is not hidden, and it is not by itself
    a reason to change the layout. The means, from the study, in this order: (1) the
    comparison chosen once for an index; (2) the end of a run seen in the step that finds
    the entry — it touches what the fix of the end position in S14 protects (73ad394):
    those tests stay as they are, and the breaker takes it as its first target; (3) a leaf
    copied as a block; (4) text: no zeroing of unused stack, no unused buffers, and for
    Ordinal a comparison of bytes. What the Ordinal order is, the decision on queries says
    in [String ordering and query overrides](../../../decisions/queries.md#string-ordering-and-query-overrides):
    "Use explicit .NET StringComparer.Ordinal or StringComparer.OrdinalIgnoreCase
    semantics, as selected by the field, for ordering and supported range predicates." The
    same paragraph adds: "UTF-8 and UTF-16 storage produce the same logical order; physical
    byte ordering is not the contract." So an oracle test comes first, with the existing
    `CompareText` as the oracle: exhaustive over the code points around the surrogate range
    and U+E000 to U+FFFF, and over strings that differ first at such a place, for both
    storage encodings. For text stored as UTF-16 a comparison of little-endian bytes is not
    the order of the code units, so the implementation states what the fast path is there,
    or that there is none. Ordinal only: a comparison without case stays on the decoded
    form. `SecondaryIndex.HasKey` becomes "the order is zero" (E3).
  - (e) Answered by the study: `Find(in IndexKey)` saves 2 to 3 ns, and the contract does
    not change; `Arena.Payload` at every level costs 17 ns a descent, about 2 % of a
    search, and stays as it is. Left for the machine: the size of a node, 1 KiB against
    2 KiB, with readers beside a writer on ARM64 deciding; a prefix beside a shared key;
    text references for a node against for an entry. A prefix for each node with heads of
    4 bytes in a slot array changes the layout: it is not in this sprint, and it is put to
    the owner, with the figures, only if the text targets of (d) are missed after the
    comparison of bytes.
- **Tests first,** the first commits of the sprint, by the test author, in this order and
  before any implementation:
  1. The refusals the tests of S14 do not cover (point 7 of its record), which must pass
     on `main` as it is; a failure is reported to the planner at once, not skipped: a
     refused replace and a refused delete of an indexed type; a refusal at a later index,
     which releases the indexes already reserved; a throw after the indexes are reserved;
     a refused forwarded write; a refused change of a child.
     **The planner, 20 November 2026, on the rule of (b) the owner accepted that day.** A
     refusal happens with the pools at the need of what waits, which for a write resolved
     alone is nothing, so at that moment the tree is as on `main`. The premise of every
     refusal test of a master, in words for its comment: the fill ends by a refusal of a
     filler and leaves no free slot of the class of the nodes. A refused replace and a
     refused delete, a refused forwarded write and `RefusedReservationTests` of S14 keep
     their assertions; where a fill stops by a count without a refusal, only the fill is
     restated. A refusal at a later index stays if freeing one slot at a time shows it,
     with the write refused at least once before it succeeds; otherwise a type whose two
     indexes have nodes of two size classes, the plainer of the two, with the reason. A
     throw after the reservation is a cancellation, and also an OutOfMemoryException of
     the tests' allocator (`CountingNativeAllocator.FailNextAllocation`, an existing switch
     of the tests, not a fault point of the engine), which reaches the catch blocks of
     `TryReserveWithIndexes` and `TryPrepareReplace`. Two tests of `IndexPreparationTests`
     of S14 (a write withdrawn while it waits; one cancelled while it is prepared) take
     their state after the same writes were prepared and withdrawn once, as the test of
     the cancellation does, and where a node must be seen to be taken the waiting write is
     the first of its index; their assertions stay. A child that cannot hold a change
     stops following, as Q14, option A, says until the pause is built. Every one of them
     passes on `main` and under the rule; one that cannot be given such a premise is named
     to the planner, not skipped.
  2. The closed form of the reservation against the loop. Then, in a commit of its own,
     the figures of the reservation tests of S14 restated from the new formula, on the
     planner's word: the formula reserves fewer nodes.

     **The planner, 20 November 2026.** The formula is the closed form of today's
     `OrderedTree.Need()` and nothing else. With h the height now, r the room of the root
     (`Room()`), L = `LeastSeparators`, p waiting inserts and q waiting removes, every
     division integer and the whole computed in `long`:
     m = max(0, p − 1 − r); a = m / L, b = m % L; S = m + L·a·(a − 1)/2 + a·b, the sum of
     `Growth(k)` for k = 0 … p − 1; G = 0 when p ≤ r, else 1 + (p − 1 − r) / L, the height
     the removes see; Need = p·(2h + 1) + 2·S + q·(h + G + 1).
     The need is compared in `long`: a need the pool cannot count is a reservation that
     cannot be made, refused with nothing taken, as for an arena that cannot hold the nodes
     (the planner, 20 November 2026, on the review: narrowed to `int` it wrapped and
     reserved too few).
     The member its test addresses, the internal contract of this step:
     `internal static long Need(int height, int room, int leastSeparators, int inserts, int removes)`
     on `OrderedTree`. The instance `Need()` becomes a call of it, and it is the one target
     of `TryReserve` and `Trim`; `Bound` goes (E1). A contract commit of this one member,
     throwing `NotSupportedException`, comes before this step, by a contract author; step
     1 needs no member. The test keeps the loop as today's `Need()` and `Growth()` define
     it and compares it with the static member over a grid: height 0 to 4; room 0, 1,
     L − 1 and L; L of 2 and of the real layout; inserts 0 to 3L + 2; removes 0 to 3. It is
     skipped with the sprint's one shared reason until the implementation. A member and not
     the pool measured against the loop, because the standing reserve of (b) keeps the
     pool above the need of the waiting writes: the formula is held where it does not
     depend on the policy of the pool.

     The standing reserve, the rule its tests state: the reserve of an index is the need of
     one change of key at the tree as it is, `Need(h, room, L, 1, 1)` nodes. It is taken by
     the first preparation that touches the index, not at open. While writes wait, the pool
     of an index holds at most the larger of the need of those writes and the reserve; with
     nothing waiting it holds at most the reserve. A publication keeps the reserve; a
     withdrawal gives back what its write took, the reserve with it, down to the need of the
     writes still prepared; the next preparation tops it up; all of it is given back at
     close (the planner, 20 November 2026: `ContradictionTests` of S14 needs a withdrawal to
     leave the arena as it found it, and no flag on the tree). Its tests are part of this step, skipped with the same reason: (i)
     after every write is published, the prepared bytes of an indexed type are above zero,
     at most the reserve of its indexes, and counted in `Counters.Arena.PreparedBytes`; (ii)
     the reserve counts against the memory limit while it is held, and a write that fits
     only without the reserves of the indexes succeeds, after which the pools are below
     their reserve and nothing is refused; (iii) a close gives it back, and the arena ends
     with nothing prepared; (iv) a database with many indexes and a limit below their
     reserves opens, a first write that fits by itself succeeds though its reserve cannot
     be taken, and a write whose own need cannot be had is refused with nothing left taken
     ((ii) and (iv) as the rule of (b) restates them, the planner, 20 November 2026). The
     grid test and these go to `Engine/Indexes/Reservation` of the engine tests.

     The commit of the restated figures holds two changes, both on the planner's word: the
     helper `BoundOf` of `IndexPreparationTests` is replaced by the formula, with the room
     of the root the test knows from the tree it built, and `RefusedReservationTests` the
     same where it states a count of nodes; and every assertion that the prepared bytes are
     0 after the publication of writes to an indexed type becomes "at most the reserve of
     its indexes". A type without an index keeps 0. Assertions stay "within" (≤) where they
     are; nothing else of those tests changes. A test whose restated figure fails on `main`
     is skipped with the same reason, not loosened. The list of every assertion changed
     under this paragraph goes to the planner, and the reviewer checks that nothing else
     moved.
  3. The tests of (a), each asserting the content of the index through the test member
     (`EngineDatabase.IndexNodes`) and the result of a search: a replace that keeps,
     changes and swaps keys; a change of case only under a comparison without case; the
     scale of a Decimal; +0 and −0; NaN with different bits; null to null, where the bytes
     under a null are arbitrary and the null bit decides; a Plain descriptor is never
     compared as bytes; a long shared key replaced by an equal text, which is the same
     entry, so the upkeep is skipped and the references of the text count exactly as after
     a full upkeep; a replace that swaps the keys of two entities in two writes.
  4. The delivery rule of `FindAsync`, and the allocation of a search pinned to bytes.
     **The planner, 20 November 2026:** a ceiling of 400 bytes now for a search of one
     entity, the object and its result, skipped; after the implementation the test author
     pins the exact figure in a commit that states what the bytes are made of. The reviewer
     checks the composition — one object holding the key once, and the result — not only
     that the number passes, and that 24 bytes more fail it.
  5. The oracle test of the comparison of bytes. **The planner, 20 November 2026:** no new
     member. The oracle addresses the existing forms of `IndexOrder.Compare`, the walk order
     of a built index and a search for every key, against `ValueComparer.CompareText`, in a
     UTF-8 and a UTF-16 database. If the engine refuses or changes an unpaired surrogate on
     the way in, the test author reports what it does and tests what can be stored.

  The new tests of the master go to sub-folders of `Engine/Indexes` of the engine tests by
  area — `Refusals`, `Reservation`, `Upkeep`, `Search` — and those of a forwarded write
  and of a child to `Engine/Replication/Indexes`. The existing files of `Engine/Indexes`
  move at S14R ([refactoring backlog](../../refactoring.md)).
- **Before the plan of the machine is used:** B2 of the
  [refactoring backlog](../../refactoring.md). Done on 20 November 2026: every scenario of the
  plan runs on the one window. Its proof by listings stands; its series was not taken,
  because the workstation was not quiet, and is series 1 below.
- **The machine.** One start, at the end, by a plan written here before it: the series,
  their order, the expected length and what each decides. It starts after the review and
  the breaker have closed with no P1 to P3, on the head that passed them: a fix after the
  series would change the measured code and owe the series again (the planner,
  20 November 2026). The coordinator fills in the
  lengths. The rules for a start of a rented machine in [Acceptance](../acceptance.md#acceptance) apply:
  one queue on the machine, at most N/2 readers judged, and a fall called only after a
  series of three builds rotating.

  | # | Series | Expected length | Decides |
  | --- | --- | --- | --- |
  | 1 | The harness of B2, the old (`main` at f3d767f) and the new on the same engine, beside two builds of one commit as the gauge: read-by-key-threads at 1 and 16 threads, write-callers, index-read-by-key, and index-search at 1 and 8 readers. Owed: the workstation could not give it. dictionary-reads is not run: before B2 and after it, it uses the window index-search uses (`ReadWindow`, then `StepWindow`), readers with a writer beside them, so it adds no part of the harness | about 30 minutes: for each scenario three rounds of the old and the new and one pair of the gauge, 8 runs | that the new harness measures what the old one did |
  | 2 | Removed: the gauge is a second build of the same commit inside each series, rotating in its rounds, a stronger gauge than a spread taken earlier (the planner, 20 November 2026) | — | — |
  | 3 | `index-writes`, `index-search`, `index-read-by-key`, `index-build` over `main` and the head; and in each round once the bare tree of the prototype (`src/Prototypes/SecondaryIndexComparison`, `ordered writecost`, the copy-on-write tree, Int64, 1,000,000 entries, 500,000 writes, no reader, nodes of 1 KiB), whose replace is the denominator of (b), taken in the same start | about 35 minutes, three builds rotating (`main`, a second build of `main`, the head), three rounds | (a), (b), (d) |
  | 4 | The size of a node, 1 KiB against 2 KiB, in the engine: the head against the head with nodes of 2 KiB, a variant of one constant (`IndexLayout.SmallestNodeBytes`); `index-search`, whose 8 readers beside a writer decide, `index-writes` and `index-build` | about 30 minutes, three builds rotating (the head, a second build of the head, the variant), three rounds | (e) |
  | 5 | `write-callers` and `read-by-key-threads` over `main` and the head | about 60 minutes, three builds rotating (`main`, a second build of `main`, the head), eight rounds | reads by key and writes of the fixture's users, a type with three indexes: the reads are unchanged, the writes are this sprint's gain. That a type without indexes loses nothing rests on the lines of no index of `index-writes` in series 3; neither scenario writes such a type (entry F7) |
  | 6 | Text keys longer than 64 bytes, in the scenario `index-long-text`: search and writes with a text key of 100 and of 200 bytes, with distinct heads and with a common head. The head against two variants of it: a prefix of 8 bytes beside the shared key, and the references to a text held for each entry by its root instead of by each node again | about 20 minutes, four builds rotating (the head, a second build of the head, the two variants), five rounds | (e), the two questions left |

  The series run in the order 3, 5, 4, 6, 1, the most decisive first, so that a start cut
  short still answers the sprint (the planner, 20 November 2026). The variants are patches
  of the head, kept with the outputs of the start and never in the repository; a variant is
  built without its tests, whose statements of the layout fail by design. Eight builds —
  f3d767f, two of `main`, two of the head and its three variants — and the prototype take
  about 15 minutes more. The whole start is about three hours and ten minutes. The lengths
  are estimated from the start of 19 November 2026, which took a run of
  read-by-key-threads in about 96 seconds, of write-callers in 50 and of the prototype's
  writecost of two trees and three counts of readers in 20, and from the workstation for
  the scenarios of the indexes: index-writes about 124 seconds, index-search 64,
  index-long-text 55, index-read-by-key 25 and index-build about 15. A series is added
  during the start only for a reason the planner agrees to.

- **After it.** S14R holds structure only ([refactoring backlog](../../refactoring.md)); what the
  reviews of S14 found as cost is this sprint's. Then S15.

**The study of S14, part 2, 19 November 2026: where the cost of upkeep and of a search goes;
nothing is decided.** The figures are of the workstation (x64, i9-13900H, .NET 10.0.15,
Release, the process held to logical processors 0 to 7 as `development-5` says), for the
breakdown only, never compared with the rows of the rented machine. No sampling profiler is
installed (`dotnet-trace` and `dotnet-counters` are not), so they come from a stopwatch
harness outside the repository that calls internal members of the engine at 53b404c, and
from listings of the JIT (`DOTNET_JitDisasm`). A figure is the minimum of 11 rounds, the cases
of a group run in turn in every round; medians lie 5 to 20 % above. Other agents kept the
machine 13 to 53 % busy before the runs: differences under about a tenth decide nothing.

- **Read for (d):** LMDB, `libraries/liblmdb/mdb.c` and `lmdb.h` at
  `74dd6c3ec648c31857fe0a100f11c2a34619e2d7` (OpenLDAP Public License 2.8); LeanStore,
  `backend/leanstore/storage/btree/core/BTreeNode.hpp`, `BTreeNode.cpp`, `BTreeGeneric.hpp`,
  `BTreeGenericIterator.hpp`, `backend/leanstore/Config.cpp` and `frontend/shared/Types.hpp`
  at `fcb64bf39cf69c435173f76e062cbb305a9a998f` (MIT); RFC 3629 for the order of UTF-8. No
  code is ported.
  - LMDB chooses the comparison once for a database: `mdb_default_cmp`, called from
    `mdb_dbi_open`, stores `mdb_cmp_memn` (memcmp over the shorter length, then the lengths),
    `mdb_cmp_memnr` for `MDB_REVERSEKEY` or `mdb_cmp_cint` for `MDB_INTEGERKEY` (native
    unsigned integers of one size) in `md_cmp`. `mdb_node_search` is a binary search over the
    16-bit offsets of a page, one call of `md_cmp` a probe; on a branch page it starts at 1,
    the first key being empty, and for integer keys switches to the aligned `mdb_cmp_int` or
    `mdb_cmp_long`; the last probe's result gives the exact match, nothing is compared again.
    `mdb_cursor_set` compares the key with the first and the last key of the leaf the cursor
    is on and searches that leaf alone when the key lies between them. No prefix, no
    normalisation, no heads: keys are compared as stored.
  - LeanStore compares byte strings only (`cmpKeys`: memcmp, then the lengths); a typed key is
    folded by the caller into bytes in its order (`fold`, an integer byte-swapped with its
    sign bit flipped). A node stores both fences and `prefix_length`, their common prefix,
    removed from every key; a slot holds the offset, the lengths and a `head`, the first
    4 bytes after the prefix byte-swapped so that an unsigned compare gives memcmp's order.
    `lowerBound` compares the key with the prefix once a node, then searches comparing heads
    as integers and reads the rest of a key only on equal heads; above 32 slots a hint array
    of 16 sampled heads narrows the range first (`searchHint`). A seek tests the key against
    the current leaf's fences before it descends (`compareKeyWithBoundaries`).
  - What fits a tree whose published nodes never change, with entries (values…, identity):
    (1) a comparison chosen once for an index when its layout is compiled, after `md_cmp`: a
    descent written for the common shapes — one integer part never null, fixed parts, a text
    part — instead of a loop over the parts with a call by type at every probe; the order is
    the same and the layout unchanged. (2) For a copied text compared Ordinal, a comparison of
    bytes that is the existing order: UTF-8 bytes sort as code points (RFC 3629), which is the
    order of UTF-16 code units except where, at the first byte that differs, one side has a
    lead byte 0xEE or 0xEF (U+E000 to U+FFFF) and the other 0xF0 or above (a supplementary
    character): there the order turns around. A UTF-16 copy compares as its code units.
    Without case, bytes compare directly while both sides are ASCII (the case table maps ASCII
    by `Ascii.ToUpper`); at a non-ASCII character the present path takes over, since one may
    map to ASCII (ı to I). (3) A prefix for each node and heads in a slot array suit nodes
    written whole and never updated better than LeanStore's nodes changed in place — computed
    once when the node is written — but they change the layout (question 1). (4) A hint across
    steps does not fit, since a search holds no node between steps; inside a step the leaf it
    holds already tells whether the run of the key ends (proposal 2).
- **A search by a full Int64 key** (100,000 items, one index, distinct keys, as `index-search`;
  height 4: 1, 3, 78 and 1,640 nodes, 61 entries a leaf, 18.3 comparisons a descent). The
  scenario here: 649 k a second on one thread, 1.54 µs; a read by key in the harness 0.57 µs.
  Each part measured alone, with the root cold:

  | Part | ns | Made of |
  | --- | --- | --- |
  | Find and GetEnumerator | 297 to 325 | the key checked and bound, three objects; Find alone 260 here and 80 in a loop over a database of 1,000 items, not separated further |
  | Step 1: search key, protection | 22 + 9 to 14 | |
  | Step 1: descent | 512 | 18.3 comparisons of 18 ns; 4 `Arena.Payload` of 4.1 to 4.6 ns; the rest the cache lines of the leaves |
  | Step 1: entry by its stamp, `HasKey` | 8 + 120 | mostly the first load of the root, which the copy needs anyway |
  | Step 1: copy, Build, Return | 243 to 252 | what a read by key does |
  | Step 1 as a whole, `TrySearch` | 1,100 | |
  | Step 2: a second descent from the root, which finds the next entry has another key | 564 to 620 | |
  | The whole search | 1,717 to 1,853 | |

  - The comparison, `IndexOrder.Compare` inlined into `ChildOf` (listing at tier 1, 1,391
    bytes): at every probe it loads the layout and its parts again, slices with checks of
    bounds, spills, and calls `ValueComparer.CompareStored`, a switch over eleven types, with
    both spans on the stack. A descent written for one Int64 part — the long, then the
    identity, addresses translated without checks — takes 184 ns on the same tree, against
    512, and agrees with `TryFindFirst` on every key but the 1.6 % that need the fence.
  - `Arena.Payload`: 4.1 to 4.6 ns against 0.6 to 0.7 ns for the same span without the checks
    of the address; 4 a descent, about 2 % of a search today and about 8 % of a descent
    written for its key.
  - The second step: in 98.4 % of these searches the next entry lies in the leaf the first
    step held; otherwise the fence the first step passed shows it when its key differs.
  - The named candidate "a hint that misses and descends again": a first step descends twice
    when its entry is the first of its leaf, 1.6 % here, about 8 ns on average; an entry passed
    over because its root no longer has the key costs a whole descent, only in the moments of
    a publication.
  - Allocation: IndexMatches 288 bytes, the enumerator 64, IndexSearch 280, the Item 88 = 720.
    IndexMatches and IndexSearch each hold the bound key, 232 bytes. Three objects of those
    sizes cost 68 to 71 ns; a copy of the key 2 to 3 ns. By reading, the key is copied about
    eight times a search: into Find by value, the bound key, IndexMatches, IndexSearch, each
    SearchKey, the copy of the probe, each `After`.
  - The code of a search through the class is shared generic code
    (`SetIndex<ClassCopy<__Canon>, __Canon>` in the listing), as a read by key's is.
  - The wait for the stamp and the position: 8 ns alone. Beside a writer the scenario loses
    14 % (556 k); the clock's line and the copied nodes, not separated (read only).
- **A text key, Plain, Ordinal** (height 4, 19 comparisons a descent). At 8 and at 100 bytes:
  the whole search 4.03 and 6.13 µs (scenario 260 k and 205 k a second); a descent 1.66 and
  2.85 µs; the second step 1.36 and 1.99 µs. A comparison through the order 69 and 83 ns;
  `CompareText` alone 51 and 73 ns; the same without zeroing its stack 23 and 38 ns; the UTF-8
  bytes of a copied key against the key's 6 ns. `CompareText` of a stored and a given text
  zeroes three buffers of 512 bytes — the piece, and two for the mapping without case that
  Ordinal never uses — and of two stored texts four; the engine has no `SkipLocalsInit`. It
  decodes the whole stored text into a piece of 256 code units, though these keys differ in
  their first 7 characters. A search calls it 38 times (two descents) and `TextEquals` once:
  57 KiB of stack zeroed and 39 decodes. At 100 bytes the key is shared: the descent's 1.2 µs
  more are the decode of 100 bytes (about 0.4 µs) and the loads of the text entries (about
  0.8 µs).
- **A write with one Int64 index** (100,000 items loaded ascending, one caller, as
  `index-writes`; height 4). Added by the index: a replace that changes the key 3,245 ns (the
  type without index writes in 0.75 µs), an insert or a delete 1,225 ns, a replace that keeps
  the key 257 ns.

  | Part of a replace that changes the key | ns | Made of |
  | --- | --- | --- |
  | The reservation | 466 | 15 blocks taken, 2 used, 13 given back by the trims, as many places in the retired list; a block 27 ns. At height h: (3h + 3) for a change of key, (2h + 1) for an insert (9 here, 242 ns), (h + 1) for a delete (5, 136 ns) |
  | Entries built | 440 | 6: `ChangeOf` in `TryReserve`, `InstallNew` and `RemoveOld`, two entries each; one entry 60 ns |
  | Two descents | about 980 | of the insert and of the remove, as a reader's |
  | Two copies of a leaf of 61 entries | about 900 | each `BytesOf` twice (69 ns) over `EditedLeaf.At` and 61 `NodeWriter.Add` (275 ns); the leaf copied as one block 9 ns |
  | The rest | about 460 | two retired leaves, the trims, the stores, the searches inside the leaves, misses; not separated |

  A rewrite alone (reservation of an insert, descent, leaf copy, publication, retire, trim)
  1,774 ns. An insert or a delete is one descent, one leaf copy, three entries built.
- **A replace that keeps every key** touches no node: each index builds both entries and
  compares their bytes three times, six entries an index (`ChangeOf` to None 129 ns alone):
  the 257 ns are that; with six indexes 36 entries a write. The earliest point at which "no
  indexed value changed" is known exactly is in the hold that prepares the write, once the new
  root is written, where `ChangePublisher.TryReserve` runs. "Changed" must mean: the entry the
  new root contributes is not byte for byte the old root's. Not "unequal under the comparison":
  equal under it in other bytes is a rewrite, never a skip — a text changed only in case under
  IgnoreCase, a Decimal of another scale, −0.0 against +0.0, NaNs of other bits; an entry holds
  the bytes of its own root. Traps for a cheaper test on the stored fields: a null field may
  hold any bytes, so nulls are compared first (null to null is no change, null to "" is one); an
  inline Plain text compares by its bytes, never by its descriptor, whose offset depends on
  the root; a Dictionary text or a shared long text may compare by the address of its text
  entry, which the text table finds by the bytes; a DateTimeOffset stores its UTC ticks, so an
  offset changed alone changes no entry.
- **Proposals, by expected gain** (estimates from the figures above):
  1. (b, d) The comparison chosen once for an index, when its layout is compiled: a descent
     and a leaf search for one integer part never null, one for fixed parts at offsets resolved
     once, and the text path; the order unchanged. A descent 512 → about 190 ns: a search −0.6
     µs (two descents), a replace that changes the key −0.6 µs, an insert or a delete −0.3 µs.
  2. (d) The step that returns an entity also looks, inside the protection, at the next entry
     of its leaf, or at the fence when the leaf ends there; another key, with the sequence of
     the database unchanged over the step, ends the search at that sequence, as the second
     step would, without its descent. No guarantee changes: it is the end step's own
     condition. A lazy Find then ends at the sequence of its last step, not of a later
     MoveNext. −0.55 of 1.7 µs (Int64), −1.4 of 4.0 and −2.0 of 6.1 µs (text).
  3. (b) A leaf copied as blocks: with fixed parts its bytes are its count times the width and
     a copy is at most two block moves around the changed entry; with text parts the entries
     move as one block and the offsets shift by one constant. About −0.4 µs a leaf copy, −0.8
     µs a replace that changes the key, −0.4 µs an insert or a delete.
  4. (d) Text: `[SkipLocalsInit]` on the comparisons, the buffers of the mapping only for a
     comparison without case, a piece no longer than the decision needs, and for a copied
     Ordinal key the comparison of bytes with its one turn (what fits, point 2), with tests of
     surrogate pairs against U+E000 to U+FFFF. A comparison 69 → about 10 ns; with 2, a search by 8 bytes 4.0 → about 1.1 µs.
  5. (a, b) The change of each index decided once, in the preparation, and kept with the
     prepared change (a bit an index); the publication builds only the entries it inserts or
     removes: 6 → 2 entries a change of key (−0.25 µs), and nothing in the publication for a
     replace that keeps the key: 257 → about 90 ns an index, about 10 with the test on the
     stored fields above. (a) needs the latter: within the gauge with six indexes is about
     10 ns an index on 1.23 µs.
  6. (b) A standing reserve: the pool of each index is kept at the bound of one change of key
     and not trimmed below it; a write takes what it uses, 2 nodes for a change of key, and the
     next preparation adds that many. −0.4 of 0.47 µs; about 15 KiB stay prepared for each
     index at height 4, counted in the budget.
  7. (c) Find returns one object that is the enumerable and its first enumerator, with the
     state of the search and the bound key once: 720 → about 380 bytes, about −40 ns. FindAsync
     runs its search on the stack: the list, its array, the result and the entities.
  8. Smaller: SearchKey refers to the bound key instead of copying it (about 20 ns); the last
     probe of the leaf decides the match without `SameKey` (one comparison of 19).
  Together, by the estimates on this machine: an Int64 search 1.7 to 1.85 → about 1.1 µs, a
  search by 8 bytes of text 4.0 → about 1.1 µs, the upkeep of a change of key 3.25 → about 1.2
  µs, which on ARM64 would be about 1.15 times the bare tree's 1.14 µs.
- **A target for (d)** that the breakdown supports without a change of layout, on
  rented-arm64-1, one thread, `index-search`: an Int64 key 700 k a second or more (today 450 k);
  a text key of 8 bytes 450 k or more (163 k); of 100 bytes 200 k or more (119 k), without a
  prefix.
  After S15, part 1 the same start reads 692 k for an Int64 key against the target of 700 k
  and 426 k for a text key of 8 bytes against 450 k, on a machine that read 5cc64c8 itself 6
  to 8 % lower than on 20 November (730 k and 474 k against 796 k and 503 k). The targets
  stay; they are measured again in the start of S16 (the owner, 21 November 2026).
- **The questions of (e):**
  - The size of a node: not answered. A descent compares about log₂ n times whatever the
    size; a larger node saves a level (a `Payload` and a miss) and doubles the copy of a leaf,
    which proposal 3 makes 9 ns a KiB. Measured after 3, on ARM64, readers beside a writer.
  - A prefix beside a shared key: at 100 bytes a descent spends about 0.8 µs loading text
    entries; a prefix decides random heads, not common ones. Measured in the engine on random
    and common heads after 1, 2 and 4, which change the ratio; LeanStore's head after the
    node's prefix serves both, at a change of layout (question 1).
  - `Find(in IndexKey)`: answered: it saves one copy of 232 bytes, 2 to 3 ns of 1.7 µs.
  - Text references for a node or for an entry: not measured (the write measured has no text
    key). By reading, a copy of a leaf with 28 shared keys takes 28 references and its free
    releases 28; to be measured on `index-writes` with keys of 100 bytes.
  - `Arena.Payload` at every level: 4 a descent, 17 ns of a descent's 512 today, about 8 % of
    one after proposal 1; its checks are the validation of an address read from a node, left
    by the owner to this timing.
- **Questions.** For the owner: (1) a node layout with a common prefix for each node and a
  head of 4 bytes for each entry in a slot array, after LeanStore, for indexes with text parts:
  a change of layout and of the order's code; not for fixed parts, whose entries already are
  the array of the key. For the planner: (c) "no copy of the key on the heap" cannot hold for
  Find, which is lazy and holds the key between two steps; proposal 7 holds it once.
- **Not done.** No sampling profile; no figure on ARM64; the 16 keys, by name and beside a
  writer only through the scenario; nothing of nodes of 2 KiB, text references or writes of
  text keys measured.

**Carried out and reviewed on 20 November 2026.** Where a sentence above says otherwise,
these replace it.

1. **What was built.**
   - (a) The change of each index is decided once, in the preparation, by the bytes of
     the two entries, and kept with the prepared change in two masks (no change, a move,
     a rewrite). From the 64th index of a type on, the last bit is shared and says
     "decide again", which reads the same two roots.
   - (b) The reservation is the closed form `OrderedTree.Need`, its one target. A
     standing reserve of one change of key is kept for each index and is never the
     reason of a refusal. A publication keeps it; a withdrawal gives it back; before a
     refusal every index gives back what it holds above the need. A need the pool
     cannot count is refused with nothing taken.
   - (c) One object holds a search, with its key once: 328 bytes, and the entity it
     returns, through a class; 320 and the record by name. `SearchKey` refers to the bound
     parts. `FindAsync` and both `WaitForPositionAsync` deliver everything that depends on
     the state of the database through the task.
   - (d) The comparison of an index of one number of 8 bytes is chosen once. The end of a
     run is seen in the step that finds the entry. A leaf is copied as blocks. An Ordinal
     text of a UTF-8 database is compared as bytes with its one turn; text stored as
     UTF-16 is compared decoded. The check of a found root is "the order is zero".
   - Proposal 1 is done in that form only: no scenario measures fixed parts at offsets
     resolved once, so the plainer form stays; it is not a debt. The second half of
     proposal 8, and a first piece of 32 code units for decoded text, were built and
     taken out again on the review: their figures lay inside the noise.
   - For the machine: the scenario `index-long-text`, and three variants as patches kept
     outside the repository (a node of 2 KiB, a prefix of 8 bytes beside a shared key,
     the references of a text held by its root for each entry).
2. **Tests first.** The test author's commits came before the implementation, 16 of them
   skipped with the sprint's reason until their step. The figures of S14 restated on the
   planner's word moved only where the planner named them, which the reviewer checked. The
   allocation of a search is pinned to its exact bytes. `NothingHeldAfterCloseTests`, which
   failed four times in 20 runs of a diagnostic series, has its cause shown: the shared
   array pool trimmed between its two measurements, the difference equal to the bytes it
   freed, the instance holding nothing. That the first failure was the same event is likely,
   not shown. It now measures in a process of its own, a test marked explicit that an
   ordinary run counts as skipped, which is why 151 tests are skipped and not 150. One
   failure of `OpeningAndClosingTests` was not reproduced in 30 runs on the head and 30 on
   `main`: entry F2.
3. **The review: two rounds.** An independent reviewer and a breaker.
   - Round 1: one P1 from the breaker; seven P3 and six P4 from the reviewer, all taken or
     placed.
   - **The P1.** `IndexOrder.HasKey`, written in this sprint when the check of a found root
     became "the order is zero", did not move its place in the search key past a fixed part
     that was null. So a search by a composite key with a null fixed part before another
     fixed part missed the entity that had the key and, while a writer changed it, returned
     entities under a key they did not have. On `main` the check was right: this sprint
     brought it. The tests of the brief did not see it, because none had a composite index
     with a null before another part; the reviewer's first round did not either. Its tests
     came first (df0cf43, fdd4f8c): a scenario through the public API, and every comparison
     of a search held against every root on composite indexes with a null in each place.
     The fix is 96be5e7.
   - Round 2: no P1, P2 or P3; three P4, placed in F3 and F4.
   - The check that the end of a run is reported at the sequence before the step only when
     the sequence did not move has no test in this sprint. A mutant that drops it passes
     every test and every probe through the public API, since no public path sees that
     sequence alone. A probe of the engine catches it in every run, but only through private
     fields, and no member is added for it now: its test is owed to S15, entry F5.
   - Four commits of the branch do not build, df0cf43 to def8b32: tests written against an
     earlier head were brought onto the branch without a build. fdd4f8c mends it; the merge
     squashes them.
4. **The hardest part to follow.** For the first round, the early end of a run in
   `EntityTable.TrySearch`, whose reason needs three orders from three files. For the
   second, the place of each fixed part in the scratch of a search key, worked out by three
   loops; E3 joins them at S14R.
5. **A small change of the sprint.** The rule of every asynchronous member is written into
   `AGENTS.md`, "Shape of the public API" (the owner, 20 November 2026).

**Checked by the coordinator:** on 471b756, whose code the head carries, the builds in
Debug and Release with 0 warnings, the format clean, 2,710 tests with 2,559 passed, 151
skipped and 0 failed in both, the samples to the end.

**Accepted and known.** The managed room of a refused reservation, F3, for S17; the need
grows with the square of the changes waiting, for S17's linear bound. The head of
`ValueComparer`, F4, for S14R. The top-up near the memory limit, for S24. The test of the
sequence a search ends at, F5, for S15. The stored fields of an index resolved once, F6,
for S16. A line of `write-callers` over a type without indexes, F7, for the next start. The
references of a shared text for each entry, F8, the owner's question.

**Measured on the rented machine, 20 November 2026**, the head 9e34443 against `main` at
3229ba1, by the plan above, in the order 3, 5, 4, 6, 1. The start took 3 hours and 11
minutes against the plan's 3 hours and 10, and all 163 runs reached their end line. The
figures are in [Measured results](../acceptance.md#measured-results).

Two mistakes of the plan:
- Row 5 named a type without indexes, and its scenarios wrote the fixture's users, which
  have three.
- Series 1 had one pair of the gauge, too few to judge a single line.

- (d) is met. Even the lowest single run of each search is above its target: Int64
  768 k against 700 k, text of 8 bytes 492 k against 450 k, of 100 bytes 288 k against
  200 k. The gauge on these lines is 0.96 to 1.03. So the question of a prefix for each
  node is not put to the owner.
- (b) is met: the upkeep of one index is 1.27 times the bare tree's replace of the same
  start, its spread 1.23 to 1.31, against 1.5.
- (a) is met with one index and not with three and six: 0.94 and 0.86. The owner accepted
  it as measured (acceptance (a)).
- A type without indexes loses nothing. This rests on the lines of no index of
  `index-writes` in series 3: the head over `main` 1.005 to 1.037, against a gauge of
  0.982 to 1.018. Series 5 wrote the fixture's users, which have three indexes: there
  the reads by key are unchanged, and the writes are the gain of this sprint.
- The questions of (e):
  - A node stays at 1 KiB: 2 KiB read at most 4 % faster beside a writer, at the edge of
    the gauge, and wrote slower. Nobody reopens it without a new reason.
  - The prefix of 8 bytes beside a shared key is not taken: it wins on distinct heads
    and loses on common ones, which long keys usually have.
  - The references of a shared text held for each entry gain 1.45 to 2.1 on writes of
    long keys. That changes a rule of the decision on strings, so it goes to the owner
    after this sprint (F8).
- The harness of B2 agrees with the old one on read-by-key-threads up to 8 threads, on
  write-callers and on index-search. One line does not agree: index-read-by-key with 0
  indexes, 1 thread, alone, reads 0.888 of the old, in rounds of 0.94, 0.87 and 0.89. Its
  gauge is a single pair, at 1.066.
  - This series cannot confirm a fall: it has two builds rotating and one pair of the
    gauge, not three builds.
  - It is not the engine: both sides run the same engine, and the two harnesses read that
    one line differently.
  - So the figures of that line recorded before B2 are not compared with figures after it
    until the difference has a cause.
  - The cause is looked for by reading, before the next start of the machine (entry F9).
- Not judged:
  - the readers above 8 on 16 cores, among them 0.903 for readers of the other database
    beside one caller that inserts and deletes, which is bimodal in both builds of `main`,
    and in series 1 read-by-key-threads at 16 threads, 0.898 alone [0.90 1.13 0.89] and
    0.882 beside a replace [0.88 1.08 0.88];
  - insert and delete with a common head of `index-long-text`, whose gauge is bimodal;
  - one round of 0.56 at 200 bytes.
- Not explained:
  - In the breakdown of (a) on the workstation, a decision costs 4 to 5 ns more inside a
    write than alone.
  - The rented figures of (a) do not lie on one line: 56, 28 and 33 ns an index for one,
    three and six indexes, where the workstation's do.
