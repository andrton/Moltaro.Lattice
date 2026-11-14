# Bounded secondary-index comparison

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
