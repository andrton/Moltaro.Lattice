# Bounded secondary-index comparison

This project holds two experiments. The first, below, is the package comparison of
13 November 2026, kept as the record of the earlier selection. The second,
[ordered structures for one writer and readers without a lock](#ordered-structures-for-one-writer-and-readers-without-a-lock),
is the prototype of the study that reopened the structure after the owner removed the
package (18 November 2026); its code is under `Ordered/`.

Compare `Arc.Collections` 2.2.0 `OrderedMultiMap` with `Kaos.Collections` 4.1.0
`RankedMap` for an ordered secondary index. This is a standalone experiment, not
the engine, a PostgreSQL benchmark or proof of concurrent publication safety.
No custom tree or package fork is used. Exact dependencies are locked.

The [measured results](../../../docs/design/prototype-evidence.md#bounded-secondary-index-comparison)
support the accepted Arc choice for v1 because it supports preparing insertion
nodes before commit. Kaos retained substantially less memory. That accepted tradeoff
is explicit in the [index decision owner](../../../docs/decisions/index-structures.md#secondary-index-arc-orderedmultimap).

## Run

Prerequisites: .NET 10 SDK and PowerShell 7. Commands run from the repository's `src` directory.
Only the public NuGet source is configured; no database or private data is needed.

```powershell
$env:DOTNET_PROCESSOR_COUNT = '2'
dotnet restore Prototypes/SecondaryIndexComparison/SecondaryIndexComparison.csproj --locked-mode
dotnet build Prototypes/SecondaryIndexComparison/SecondaryIndexComparison.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
pwsh -NoProfile -File Prototypes/SecondaryIndexComparison/Run-Bounded.ps1
```

The default is 10,000 entries, two key distributions, two maps and three fresh
processes per case. Three additional short workers check range correctness and
pre-commit preparation. Each worker runs sequentially at BelowNormal priority,
with workstation GC, tiered compilation disabled, runtime processor count two and
a 20-second deadline. The runner kills only its own worker on timeout/failure and
pauses 150 ms between workers. The benchmark itself is single-threaded. Processor
count is a runtime setting, not CPU affinity or an OS CPU quota.

`-Count` is bounded to 1,024–20,000 and `-Repetitions` to 1–3. Do not turn this
runner into a load test. Raw JSON and logs go under ignored `.local/secondary-index-*`;
the runner prints that directory. `workers.json` records wall and CPU time, not
process peak RAM. Resource settings and package versions also appear in sample JSON.

## Equal contents and measurement units

- Each entry has a 16-byte value-type composite key `(Int64 Group, Int64 Score)`
  and an 8-byte locator. It contains no document or managed string. Inputs are
  deterministic, shuffled with seed 20261113 and allocated before the baseline.
- `Unique` has one distinct search key per entry. `Repeated` has 100 distinct
  search keys, each occurring 100 times at the default count. Locators stay unique.
- Retained bytes are the managed heap delta after full GC, with the map kept
  alive. They include map metadata and spare capacity, excluding input fixtures,
  the native arena and documents. They are not RSS, an allocation peak or total
  engine memory. Build allocation counts temporary objects as well as the map.
- Each timed operation has two warmups and five short samples after an earlier
  256-entry type/JIT warmup. Use the range of fresh-process medians; these tiny
  timings are sensitive to scheduling and are not throughput or p99 guarantees.
- `Hit`/`Miss` perform 4,096 precomputed lookups per sample. A hit returns one
  matching locator; it does not enumerate the entire duplicate group. Misses are
  above the index's maximum key, not uniformly distributed absent interior keys.
- `Range8Keys` visits and sums every matching locator in 64 ranges. Each range
  covers eight distinct keys: eight records for `Unique`, 800 for `Repeated`
  at 10,000 entries. Times are per range query, not per visited record.
- `UpdateKey` relocates 512 preselected entries between original and changed
  keys. Both maps first locate the exact old key/locator pair. Arc then uses
  `SetNodeKey`; Kaos removes that pair and adds the changed pair. The retained
  footprint excludes a permanent node-handle side table. Duplicate-group search
  is therefore included in update cost.

Inputs, query sums, ordering and final exact key/locator contents are checked
outside timing. State validation catches missing, duplicated and incorrectly
updated locators. The checksum is consumed during timing. Point, range and update
workloads differ; their numbers must not be treated as interchangeable operations.

## Preparation before the simulated commit

Starting with 10,000 unique entries, prepare 32 key changes, eight deletions and
eight insertions. Check that the live map is unchanged, then publish and verify
the complete resulting map. The two preparations solve the allocation problem
in different ways; they are not equivalent data structures or production designs.

**Arc:** retain handles for existing mutations. Obtain eight nodes through a
private staging map's public `Add`/`RemoveNode` APIs. Removal clears key/value,
so the plan owns those separately. Publication reuses these detached nodes with
`Add(key, value, node)`, moves existing nodes and removes deleted ones. Assert
zero managed allocation during this publication. No full index copy is required.

**Kaos control:** copy the entire map privately, apply the changes to the copy,
then publish by reference assignment. This demonstrates unchanged live contents
and allocation-free pointer publication, but costs O(n) preparation and additional
live index memory per copy. It is not a recommended production write path. The
public `Capacity` property controls B+ tree branching factor (default 128), not
reserved entry capacity. The inspected insertion/removal paths allocate navigation
objects, and splits can allocate nodes; there is no public API to reserve them.

Preparation allocation and additional retained memory are separate metrics.
Retained memory excludes temporary objects already collected; it is not the
transient allocation peak. A sub-timer-resolution pointer assignment is not a
zero-cost atomic transaction. No PostgreSQL commit, competing writer, reader gate,
root-lifetime protocol, cancellation or ambiguous commit is implemented here.

## Reproduced Kaos range failure

With the exact 4.1.0 package, insert keys 0 through 299, then enumerate
`ElementsBetween(10, 299)`. The measured run raises `ArgumentOutOfRangeException`
instead of returning 290 entries. `ApiChecks` records that failure separately.

The timed adapter deliberately uses `ElementsFrom(lower)` followed by an upper
bound check, corresponding to Arc's `GetLowerBound`/`Node.Next`. It passes the
same 290-entry check and every timed range check. There is no automatic fallback
or concealed passing result for `ElementsBetween`.

Inspected sources:
[Kaos 4.1.0 RankedMap](https://github.com/kaosborn/KaosCollections/blob/2500a36dbae3f7844bd7abca644e0eafd070d979/Source/RankedMap/RankedMap.cs),
[Kaos navigation allocation](https://github.com/kaosborn/KaosCollections/blob/2500a36dbae3f7844bd7abca644e0eafd070d979/Source/Btree/Btree.NodeVector.cs),
[Arc 2.2.0 OrderedMultiMap](https://github.com/archi-Doc/Arc.Collections/blob/d9edfc9db902a8eea1505031b0f4cbfea90f921b/Arc.Collections/Collection/OrderedMultiMap.cs).
Newer Kaos source tags are not the evaluated NuGet binary.

## Remaining engine validation

Native string/key ownership, all schema comparison rules, safe concurrent node
acquisition, delete/reinsert lifetimes, WriteBatch publication and traversal during
mutation still belong to engine integration. Managed node reclamation follows GC;
unlinked nodes are not an immediate return of physical RAM. A stored handle per
root/index would add memory and has not been selected or included in the footprint.
Neither this small experiment nor a package enumerator establishes those contracts.

## Ordered structures for one writer and readers without a lock

Candidates for the ordered secondary index of the engine, under the engine's rules: one
thread changes a structure (the holder of the lock of the writes); a reader takes no lock
and only enters the protection of reads, an epoch; what a write removes is retired and
freed after the readers that could see it have left; nodes live in native memory and are
reached by relative addresses of eight bytes; an entry is unique by (key, identity), so
equal keys form a run ordered by identity; a write takes its nodes before its publication,
and the publication allocates nothing. The code is synthetic and is not the engine: it
models the parts of the engine a comparison of structures needs, and says in each file
what it models and what it leaves out.

### Candidates

| Name | Type | Modelled on (no code is ported) |
| --- | --- | --- |
| `cow` | `Ordered/Structures/CopyOnWriteTree.cs`: a B+tree whose published nodes never change; a changed node is copied and published by one store of its address into the live parent | LMDB `libraries/liblmdb/mdb.c` (OpenLDAP Public License 2.8, commit `74dd6c3ec648c31857fe0a100f11c2a34619e2d7`); the ROWEX principle of Leis et al., "The ART of Practical Synchronization", DaMoN 2016; the leaf-to-leaf walk of LeanStore `BTreeGenericIterator.hpp` (MIT, commit `fcb64bf39cf69c435173f76e062cbb305a9a998f`) |
| `optimistic` | `Ordered/Structures/OptimisticTree.cs`: a B+tree changed in place, a version per node that readers validate | LeanStore `sync-primitives/Latch.hpp`, `storage/btree/core/BTreeNode.cpp` (MIT, same commit); optimistic lock coupling of ARTSynchronized `OptimisticLockCoupling/Tree.cpp` (Apache-2.0, commit `f00003ec7991da09c3dfc1c84da97c1965106eb3`). Fixed keys only |
| `skiplist` | `Ordered/Structures/SkipList.cs`: a skip list with one writer, and a delete the sources do not have | LevelDB `db/skiplist.h` (BSD-3-Clause, commit `29465588d3dfdc80fba5e198037582b8fdb5dd60`); RocksDB `memtable/inlineskiplist.h` (read under Apache-2.0, commit `09fe540613c0777e666411e7dd54aa84a535be6b`) |
| `arc` | `Ordered/Structures/LockedMultiMap.cs`: the contrast, `OrderedMultiMap` of `Arc.Collections` 2.2.0 (MIT) behind a `ReaderWriterLockSlim` | The package itself; it keeps equal keys in the order they were added, so it cannot seek after an identity |

Shared parts: `Ordered/Memory/NodeArena.cs` (the engine's arena reduced: segments, relative
addresses, a 32-byte header per block, the engine's size classes, free lists, live, prepared
and retired bytes), `ReadEpochs.cs` and `RetiredBlocks.cs` (the engine's protection of reads
and retired memory reduced; the engine's are adapted from LightEpoch of Garnet/Tsavorite,
MIT). `Ordered/Text` models entities with one indexed text field as the engine stores a root
(a 48-byte control entry, a root block with a 16-byte descriptor, Plain text inline up to 64
bytes) and five forms of a text key on the `cow` tree: `root` (the entry names the entity and
a comparison reads the root), `copy` (the entry holds the key), `prefixroot` (the first eight
bytes, the root breaking a tie), `shared` (the entry and the root name one text entry) and
`sharedprefix` (the first eight bytes and the shared entry). `root` and `prefixroot` are not
candidates and are measured as reference lines only (their lines say `candidate=False`): a
separator that names an entity goes on steering searches after the entity is gone and its control
entry is reused, and a replace that changes the key leaves the entry at the old key's place while
the root already holds the new one. The check passes for them only because its entities never
change key and are never deleted.

### Restore and build

The only package is `Arc.Collections` 2.2.0, pinned with the old experiment's. It restores
from the local packages folder without a download:

```powershell
dotnet restore Prototypes/SecondaryIndexComparison/SecondaryIndexComparison.csproj --locked-mode --source "$env:USERPROFILE/.nuget/packages"
dotnet build Prototypes/SecondaryIndexComparison/SecondaryIndexComparison.csproj -c Release --no-restore
```

### Scenarios

Run from `src`:

```powershell
dotnet Prototypes/SecondaryIndexComparison/bin/Release/net10.0/SecondaryIndexComparison.dll ordered <scenario> [--option value ...]
```

Each case prints one line: the scenario, then `name=value` pairs, its parameters first.
A list option is comma-separated; `none` is the empty list.

| Scenario | What | Options and defaults | Kind |
| --- | --- | --- | --- |
| `bytes` | Bytes per entry of fixed keys after building by inserts one at a time: node slots in the arena, header included, and the managed heap after a full collection | `--structures cow,optimistic,skiplist,arc --keys int32,int64,key16 --distributions random,ascending,hotkey,halfdeleted --count 1000000 --node 1024` | Deterministic; about a minute |
| `textbytes` | Bytes per entity of each text form: the index, the root side (roots, value blocks, text entries) and the text table's managed slot and bucket, added as 48 bytes | `--forms root,copy,prefixroot,shared,sharedprefix --lengths 8,32,100 --count 200000 --node 1024 --text random` | Deterministic; about a minute at 1,000,000 |
| `check` | Correctness under change: a writer toggles entries, takes a key's run out and back, appends ascending keys and churns a hot key; walkers go over ranges forwards and backwards in steps that each seek after the last (key, identity); searchers look for stable entities. Counts order violations, missed stable entries, search misses, and checks the contents at the end. Before it, the preparation: a withdrawn write changes nothing, and 32 publications allocate nothing | `--structures cow,optimistic,skiplist,arc --forms root,copy,prefixroot,shared,sharedprefix --seconds 10 --node 256 --walkers 2 --searchers 2` | Correctness; five threads; eight cases of `--seconds` each |
| `find` | Exact search by a full key, every identity of the key up to eight, on reader threads of their own, alone and beside a writer that takes an entry out and puts it back | `--structure cow --key int64 --count 1000000 --node 1024 --distribution random --threads 1,16 --writer off,on --seconds 5` | Timing; the build and four cases of `--seconds` |
| `range` | A step of `--length` entries from a key, forwards or backwards, the same way | as `find`, with `--direction forward --length 100` | Timing; as `find` |
| `writes` | Inserts a second from empty to the count, then deletes a second, one thread, each a prepared write and a publication | `--structures cow,optimistic,skiplist,arc --keys int64 --orders random,ascending --count 1000000 --node 1024` | Timing; 10 to 20 seconds per structure |
| `text` | The text forms on the `cow` tree: `find`, `range`, `write` and `readroot` (what a read by key pays for the text inline in the root or in a text entry) | `--forms root,copy,prefixroot,shared,sharedprefix --lengths 8,32,100 --text random --operations find,range,write,readroot --count 1000000 --threads 1,16 --writer off,on --seconds 5` | Timing; per form and length the build and about ten cases of `--seconds` |
| `writecost` | One write per index, `cow` against `optimistic`: phases of insert, delete, and replace that changes the key (the new entry in, the old one out), each write a prepared write and a publication on a thread of its own, beside 0, 1 or 16 reader threads that search a full key each. One line per case: writes a second and the readers' searches a second | `--structures cow,optimistic --key int64 --count 1000000 --writes 500000 --readers 0,1,16 --node 1024` | Timing; about 30 seconds per structure |
| `looks` | How often index writes move the epoch: entities with 1, 3 or 6 indexes in one arena and one list of retired blocks, an entity write changing every index and published once; insert, delete, replace that changes every key; with and without a read held inside the protection, and with and without the retirement of the entity's old root (64 bytes) | `--structures cow,optimistic --indexes 1,3,6 --writes 10000 --count 1000000 --node 1024 --held off,on --roots off,on` | A count, deterministic with one writer; about 30 seconds |
| `primaryread` | The read by key beside index writes: readers that enter the protection, copy a 64-byte record from a separate array nothing writes, and leave, so that the only memory they share with the writer is the epoch; the writer takes an entry out of an index and puts it back on the `cow` or the `optimistic` tree, or no writer runs | `--writers none,cow,optimistic --readers 1,16 --count 1000000 --node 1024 --seconds 5` | Timing; the builds and six cases of `--seconds` |

`Run-Ordered.ps1` runs every timing case one after another, each in a fresh process, and
writes the lines to the console and to `src/.local/ordered-<time>/lines.txt`:

```powershell
pwsh -NoProfile -File Prototypes/SecondaryIndexComparison/Run-Ordered.ps1 -Seconds 5
```

At five seconds a case the groups take about: `find` 3 minutes, `range` 3, `writes` 1,
`nodes` (the `cow` tree at nodes of 256 to 4,096 bytes) 2, `text` 25, `writecost` 1, `looks`
half a minute, `primaryread` 1; about 37 minutes in all. `-Groups find,range` runs a part;
`-Seconds 3` shortens every case (`writecost` and `looks` run a number of writes, not a time).

The look, in `Ordered/Memory/RetiredBlocks.cs`, is a model of the engine's rule
(`Engine/Protection/RetiredMemory.cs`): a look when the blocks retired since the last one reach the
slots of the protection (`ReadEpochs.SlotCount`: 128 up to 64 logical processors) or their bytes
reach one arena segment (4 MiB, `LatticeDatabaseOptions.ArenaSegmentSize`); after a look that left
something behind, a write that retired nothing counts as one block until a look leaves nothing
behind. Each look is one move of the epoch that every read's entry loads.

### What the prototype does not show

- Timings mean something only on a quiet machine; nothing in this project judges them.
- The check proves the protocol of each candidate in this model: one writer, readers in an
  epoch, nodes of this arena. It is not a proof of the engine's index.
- Not modelled: the reference counts of text that index nodes would hold, the engine's text
  table (its slot and bucket are added as arithmetic), the comparison of the engine's
  `ValueComparer` (UTF-8 decoded to UTF-16 pieces; here ASCII bytes compared), nulls,
  composite keys, Decimal and floating-point order, the checks of the engine's
  `Arena.Payload`, several entries published in one hold, and the engine's read by key itself,
  for which `primaryread` reads a stand-in that shares only the epoch with the writer.