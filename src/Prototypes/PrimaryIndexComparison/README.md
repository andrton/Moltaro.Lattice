# Primary index collection comparison

Standalone comparison of existing hash maps for an index from a primary key to an
8-byte root locator. It measures retained index memory, temporary memory during a
resize, lookup, insertion and replacement of an existing locator. It implements
neither a custom hash table nor the Lattice database engine.

## Candidates

| Runner name | Collection | Pinned package |
| --- | --- | --- |
| `Blitz` | `BlitzMap<TKey, ulong>` | [Faster.Map 9.0.3](https://www.nuget.org/packages/Faster.Map/9.0.3) |
| `Dense` | `DenseMap<TKey, ulong>` | Faster.Map 9.0.3 |
| `ArcSlim` | `UnorderedMapSlim<TKey, ulong>` | [Arc.Collections 2.2.0](https://www.nuget.org/packages/Arc.Collections/2.2.0) |
| `Arc` | `UnorderedMap<TKey, ulong>` | Arc.Collections 2.2.0 |

Versions belong to this prototype's `Directory.Packages.props`; `packages.lock.json`
pins the dependency graph. Restore uses the public NuGet feed only. These are
prototype dependencies. The subsequent [integration decision](../../../docs/decisions/index-structures.md)
selects ArcSlim for the first engine implementation; no production engine exists yet.

## Build and run

Prerequisites: .NET 10 SDK and PowerShell 7. Run from the repository's `src` directory:

```powershell
dotnet restore Prototypes/PrimaryIndexComparison/PrimaryIndexComparison.csproj --locked-mode
dotnet build Prototypes/PrimaryIndexComparison/PrimaryIndexComparison.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet Prototypes/PrimaryIndexComparison/bin/Release/net10.0/PrimaryIndexComparison.dll verify
```

Start with the small, twelve-case smoke comparison:

```powershell
pwsh -NoProfile -File Prototypes/PrimaryIndexComparison/Run-Comparison.ps1 -Counts 10000 -ProcessRepetitions 1 -Samples 3
```

The full comparison is explicit:

```powershell
pwsh -NoProfile -File Prototypes/PrimaryIndexComparison/Run-Comparison.ps1
```

It uses 100,000 and 1,000,000 entries, three key types and four maps, with three fresh
processes per combination: **72 workers, sequentially**. Each worker warms up and
then times five complete passes for hits, misses and updates. Workers use workstation
GC and disable tiered compilation. No database, network service or production data
is involved after package restore.

Run substantial comparisons on an otherwise idle machine. A single worker performs
several complete index builds and full collections; sequential execution does not
guarantee an unobtrusive desktop workload. There is a 120-second timeout per worker,
not a total-suite deadline. The runner disposes each completed process and terminates
its own process tree on timeout or cancellation through its cleanup block. It does
not terminate unrelated processes. The first full run took 321 seconds; desktop
interference makes its timings preliminary, as explained in the evidence record.

PowerShell parameters also accept subsets of `Maps`, `Keys` and `Counts`. Counts are
limited to 1,024 through 2,000,000, repetitions to 1 through 5 and samples to 3 through
15. These are harness bounds, not database limits.

The default output is a new ignored `.local/primary-index-<UTC timestamp>` directory.
It contains one JSON result and stdout/stderr logs per worker, plus `summary.json`
and `summary.md`. Raw output is not committed. To summarize existing results without
running benchmarks:

```powershell
pwsh -NoProfile -File Prototypes/PrimaryIndexComparison/Summarize-Results.ps1 -Directory .local/<run-directory>
```

## Bounded integration checks

After building, run the small source-driven compatibility checks separately from
the performance suite. This child shell limits the runtime's perceived processor
count without changing the invoking shell's environment:

```powershell
pwsh -NoProfile -Command {
    $env:DOTNET_PROCESSOR_COUNT = '2'
    $env:DOTNET_TieredCompilation = '0'
    dotnet Prototypes/PrimaryIndexComparison/bin/Release/net10.0/PrimaryIndexComparison.dll integration
}
```

The cases use at most 600 live map entries and two concurrent writer tasks:

- Native UTF-8 descriptors in both maps: 28 pair/comparison cases per map, including
  Unicode, embedded NUL, empty text and case-sensitive/insensitive equality. Lookup
  allocation is checked after warmup; text relocation repairs the descriptor before
  releasing the old buffer. The 16-byte descriptor and 1,024-byte input ceiling belong
  only to this bounded fixture, not the schema or production key layout.
- Forced hash collisions: insert 600 keys and remove 200, checking survivors and
  allocation caused by deletion maintenance. DenseMap's observed allocation is a
  differentiating behavior, not a failed correctness check.
- Arc capacity ownership: rebuild an equivalent larger map before a simulated
  commit, reserve insertion credits, reject a capacity-budget excess, cancel credits,
  publish without allocation and reuse deleted slots through 16 cycles. Two writers
  reserve independently; readers access old contents while both await simulated commit.

`ReservedArcIndex` wraps the unmodified package. A mutation mutex serializes index
changes and copying. Readers take only a separate reader/writer gate, which is held
exclusively for metadata swaps and published changes, not the copy or commit wait.
No lock spans I/O. This is not the full database's root-retention, WriteBatch or
uncertain-commit implementation. The numeric reservation check and bounded native
key check are separate probes; their combination with a moving arena remains engine work.

To check the runtime without hardware intrinsics in an isolated child process:

```powershell
pwsh -NoProfile -Command {
    $env:DOTNET_EnableHWIntrinsic = '0'
    dotnet Prototypes/PrimaryIndexComparison/bin/Release/net10.0/PrimaryIndexComparison.dll platform
}
```

That command verifies Arc lookup and DenseMap's expected constructor rejection when
Vector256 acceleration is unavailable. It is not an ARM64 or Linux execution test.
See [integration evidence and selection](../../../docs/design/prototype-evidence.md#primary-index-integration-and-selection).

## Workload and measurement boundaries

Every map receives the same unique synthetic `Int64`, `Guid` or 20-character `String`
keys and the same 8-byte values. A deterministic generator and shuffled traversal
use seed `20261113`. Each timed pass visits every key. Missing probes are disjoint
from stored keys. String hits use equal text in distinct string objects, so they
cannot succeed solely through reference identity.

Keys and probe arrays are prepared before measurement. String objects use ordinary
managed UTF-16 text; this does not implement native UTF-8 keys, a string dictionary
or schema-defined case-insensitive comparison. The generic struct adapters dispatch
to the package implementations; no per-item reflection or boxed index interface is
used in the timed loops.

| Metric | Meaning |
| --- | --- |
| Retained index bytes | Live managed heap after full GC minus the pre-index live-heap baseline. Includes the map, its buffers and capacity slack; excludes precreated input keys, probe arrays and baseline runtime state. |
| Bytes per entry | Retained index bytes divided by the number of live entries. For strings this includes stored references, not the string objects or their text. |
| Build ns/insert | One empty-map construction and insertion of all keys, including natural growth, divided by the inserted count. Input generation and later correctness checks are excluded. |
| Hit / miss ns | Average time per lookup in a complete shuffled pass, including shared loop/checksum overhead. Five pass averages produce a process median. This is not a per-request latency percentile. |
| Update ns | The same measurement for assigning a new locator to an existing key through the indexer. It does not represent changing a database object or committing a transaction. |
| Allocated bytes per sample | Managed allocation traffic on the benchmark thread during a full lookup/update pass. It is different from retained memory. |
| Public capacity | The package's `Size` or `Capacity`; different collections need not interpret it as the same maximum entry count. |

The summary takes the median of the three process medians for lookup/update timing.
JSON also retains the smallest and largest process medians and the individual
samples. Build time has one measured build per process. Default package load factors
are preserved; this is equal-content comparison, not forced equal capacity.

`InputHeapBytes` in raw JSON is the entire pre-index live-heap baseline, including
runtime state. It must not be interpreted as the fixture's exclusive allocation size.

## What the resize peak means

The harness discovers the last public-capacity increase while growing from capacity
16 to the target record count. It releases that probe, rebuilds through the entry
immediately before this increase, collects garbage, and measures the single insertion
that grows the map. Each candidate can reach this boundary at a different count;
`Growth.CountBefore` and the old/new capacities identify the exact event.

The insertion runs inside a bounded 512 MiB no-GC allocation budget. For these pinned
types, inspected storage fields are managed arrays. With collection prohibited,
their old buffers and all newly allocated buffers coexist until measurement ends.
The reported **controlled transient index bytes** are:

```text
retained index bytes before resize + bytes allocated during that insertion
```

A live-heap reading independently checks this total; full GC afterward measures
the retained replacement buffers. The worker fails if the no-GC region cannot be
established or maintained. The 512 MiB budget is not added to reported index bytes.

This captures short-lived old-plus-new managed storage without relying on a sampler
to catch a millisecond event. It is **not** process RSS, committed GC heap, native
memory, or the maximum of an entire uncollected build. Natural GC timing can change
whole-process peaks and pause duration. Resize timing excludes entering/exiting the
no-GC region; ordinary operation timing does not use that region.

## Correctness and limits

`verify` checks four maps, three key types and nine sizes, including empty maps and
small capacity boundaries. It verifies values, counts, equal-text string lookup,
absence, updates, deletion/reinsertion and repeated removal of missing keys. Each
benchmark independently verifies full contents before and after updates, a 25%
delete/reinsert cycle, and every expected value after the measured resize.

There is no standard .NET Dictionary baseline. The original performance run does
not include the later bounded integration cases described above. Neither includes
production arena compaction, sustained growth/shrink, a root lifetime protocol,
SQL commit or replication. Correctness here is not a package certification or engine-safety proof.
Production adoption still requires compatible equality, capacity reservation before
commit and safe publication/reclamation under the accepted engine contracts.

Measured results and their limitations are in
[the evidence record](../../../docs/design/prototype-evidence.md#primary-index-collection-comparison).
The [index decision](../../../docs/decisions/index-structures.md) remains the owner of
production selection.
