# Arena and Arc index integration

Bounded follow-up to the [arena lifecycle experiment](README.md), measured on
13 November 2026. The same allocator, root entries, text pool and audits now run
with Arc.Collections 2.2.0, without a package fork or a new index implementation.
This is an in-memory mechanism experiment, without PostgreSQL.

## Implemented integration

The engine path uses `UnorderedMapSlim<Int32, RootClaim>` for primary lookup and
`OrderedMultiMap<CompositeIndexKey, RootClaim>` for one secondary index on
`(text, Int64 number)`. Equal secondary keys can identify different roots. The
8-byte locator contains a control slot and generation. The primary table reserves
1,024 entries at construction; growth is not remeasured here.

The 32-byte secondary key contains the fixed number and a stable text descriptor.
Stored keys contain no CLR string copy; only temporary query probes contain strings.
Comparison resolves the current native address under text protection, decodes to
bounded stack scratch and follows .NET Ordinal/OrdinalIgnoreCase. Neither raw UTF-8
order nor text IDs substitute for string comparison. The scratch bound covers this
fixture's at-most-64-byte text slots, not arbitrary large strings.

Each text entry counts root, prepared-root and index references; the last category
also includes prepared index replacements. Text may remain alive solely for an index.
Reclamation requires zero references and exclusive text protection. Lock order is
**root -> catalog/index -> text -> allocator**. Index lookup captures root lifetime
claims and releases index protection before waiting for root access. Compaction
repairs stable entry addresses without changing index keys or logical revisions.

Save prepares native replacement data and a new index reference before publication.
An insertion prepares a detached node through public staging Add/Remove APIs; an
update captures the existing node. Root and index mappings publish under root/index
protection. Abort releases prepared ownership without changing published entries.
Numeric updates reindex the fixed numeric component, retaining the same text owner.

**Equal-value replacement requires descriptor replacement too.** `SetNodeKey` keeps
the old key when values compare equal. For `alpha` -> `ALPHA` under OrdinalIgnoreCase,
the new descriptor can nevertheless own another allocation. This integration removes
and reinserts the same node with the prepared descriptor, then releases the old text
reference. Equal keys with distinct spellings/allocations are also checked during
duplicate-group head promotion. No package-internal field access is used.

Queries capture bounded root claims, not package nodes or native addresses, then
read each root coherently and recheck the predicate. Deleted lifetimes yield absence;
changed non-matches are discarded. Rechecking uses the immutable fixture text value
represented by the detached summary's group ID. This is not a production OQL executor.
Quiescent range ordering is verified; concurrent query-wide snapshots/final sorting
are not claimed. One catalog/index monitor serializes package access in this fixture.

## Run and resource limits

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet restore Prototypes/ArenaLifecycle/ArenaLifecycle.csproj --locked-mode --disable-parallel
dotnet build Prototypes/ArenaLifecycle/ArenaLifecycle.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
& ./Prototypes/ArenaLifecycle/Run-Bounded.ps1 -Groups @('index-checks', 'index-memory', 'indexed-off', 'indexed-on') -ConcurrentRepeats 3
```

Workers run sequentially at BelowNormal priority on one permitted logical processor,
with workstation GC and paced, fixed-count loops. Each has a 15-second wall/5-second
CPU guard, sampled 192 MiB working-set guard and hard 2 MiB native cap. Arc 2.2.0 is
centrally versioned within this prototype and resolved by its package lock file.

## Correctness evidence

Seven index scenarios passed:

1. Primary/secondary insert, replacement, scalar reindexing, duplicate keys, abort,
   deletion and root/chunk/text compaction; final deletion releases all text owners.
2. Captured candidates across a predicate-changing update, deletion and same-key
   recreation, without redirecting an old claim to the new lifetime.
3. Equal-value descriptor replacement, original spelling preservation and promotion
   of a duplicate-group head backed by a different spelling/allocation.
4. A held index/text comparison excludes relocation and makes publication wait;
   after release the same block can move and queries remain correct.
5. Sixteen warmed, uncontended prepared publications, including insert/replacement,
   allocate **zero managed bytes during publication**. Preparation and first-use
   synchronization resources are outside this assertion.
6. `UnorderedMapSlim<NativeTextKey, int>` works after text relocation when only the
   index owns the text. Final release frees it; stale descriptors are rejected before
   dereference and recreation changes generation. This is a String-key compatibility
   check, complementing the integrated Int32 primary path, not a complete String-primary SDK.
7. CLR-consistent Ordinal/OrdinalIgnoreCase ordering/equality/hashing for case aliases,
   Greek letters, combining text, U+E000 and U+10000. The last pair detects incorrect
   substitution of UTF-8 byte order for UTF-16 ordinal order.

All 256 text predicates, bounded numeric ranges within each text group and a full
range are compared with a logical fixture oracle at quiescent checkpoints.
Audits reconcile index counts/order, root lifetimes, native
owners and text references. The original directed/churn groups also passed after
integration. Every worker reported zero outstanding native allocations at exit.

## Retained memory

Three fresh processes compare the same 384-root dataset with the fixture's direct
Int32 lookup array and then both real indexes. Input/oracle arrays and warmed static
fixtures precede each GC baseline. Managed figures are the heap delta with the whole
engine alive after collection, including control/allocator metadata and spare index
capacity. They are not process RSS or cumulative allocation traffic.

| Measurement | Fixture lookup control | Arc primary + one secondary |
| --- | ---: | ---: |
| Native segments, 384 roots | 376 KiB | 376 KiB |
| Retained managed engine footprint | 103.58–103.60 KiB | 162.78 KiB |
| Incremental managed footprint relative to control | — | **59.18–59.20 KiB** |
| Native segments after deleting 288 roots, before compaction | 376 KiB | 376 KiB |
| Same 96 roots after compaction | 112 KiB | 112 KiB |

**1 KiB = 1,024 bytes.** The additional 60,600–60,624 managed bytes include replacing
the control lookup array, a primary capacity of 1,024 and 384 ordered entries. This
is not a universal bytes-per-object/index constant. Native content/capacity match
exactly: indexes add references, not text copies. The same 327 relocations return
264 KiB of native segments in both deletion fixtures.

The concurrent workload changes the text distribution. Native peak was **392 KiB**
in both modes; final capacity was 392 KiB off and 376 KiB on, including an 8 KiB empty
reserve in the latter. Live encoded content matched. Managed allocation traffic,
including the harness, was approximately 293 KiB off and 523–526 KiB on. Reacquiring
freed segments recreates their managed metadata; traffic is not retained heap growth.

## Latency measurements

Windows 10.0.26300 x64, .NET SDK 10.0.404/runtime 10.0.15. Indexes are enabled in both
timing modes; only compaction differs. Each run submits 4,096 primary reads, 256
equal-text ranges including root reads, 256 scalar updates, 768 full Saves and 256
Delete attempts, of which 160 remove a live root. Saves change text groups as well
as values/payloads. Enabled runs make 1,024 compaction attempts.

The table gives ranges across three fresh paired processes in **microseconds**.
Read timings include payload validation; Save includes preparation/publication but
no SQL. p99 is a sample percentile, not a maximum. No outliers were discarded.

| Complete operation | Off: p50 | On: p50 | Off: p99 | On: p99 |
| --- | ---: | ---: | ---: | ---: |
| Primary hit and protected payload validation | 1.7–2.2 | 2.1–2.2 | 12.0–29.1 | 20.8–27.5 |
| Indexed numeric update | 4.2–4.9 | 4.5–4.9 | 23.7–56.4 | 24.5–50.6 |
| Full Save | 6.0–7.9 | 7.0–7.6 | 76.1–99.6 | 128.7–303.3 |
| Delete attempt, including ordinary absence | 1.8–2.2 | 1.9–2.2 | 6.5–36.7 | 14.5–16.9 |
| Equal-text range plus protected root reads | 4.3–6.2 | 5.3–5.6 | 45.6–118.0 | 68.9–176.7 |
| Compaction attempt, including skips/no-work | — | 0.7–0.8 | — | 4.4–24.9 |

Enabled runs moved **793–798 blocks**, including 32 text blocks per run. Range queries
returned 327–331 rows over 256 queries: small groups, not large scans. Read misses
are excluded from the primary-hit percentiles.

**Original run: long tails were unattributed.** Enabled maxima included a **63.390 ms Save** and
**51.721 ms read**; disabled maxima included a 10.796 ms Save and 2.425 ms range query.
Scheduling interruption, lock waiting, GC and package work were not separately
profiled on this shared, low-priority, single-core workstation run. Their contribution
is unknown. These results do not establish bounded production pauses or prove that
compaction caused every tail difference. No stress run or unrequested optimization
was used to conceal these observations.

The subsequent [tail investigation](#bounded-tail-investigation) reproduces a
62.382 ms Save and 38.126 ms read with gate/GC/thread diagnostics. It narrows the
likely cause to execution stalls rather than measured gate contention or GC;
the original two operations have no equivalent trace and remain unattributed.

Do not subtract the earlier no-index timing table to calculate index overhead:
this workload also changes strings and performs secondary queries. The memory pair
isolates retained footprint; the timed pair compares compaction within this indexed
workload, subject to scheduling variance.

## Reproducibility and remaining work

Local ignored evidence: `.local/arena-lifecycle-20261113-204619-438` contains baseline
regressions, three memory comparisons and three indexed off/on pairs. Expanded
duplicate-head checks passed in `.local/arena-lifecycle-20261113-204923-106`; the final
expanded numeric-range checks passed in `.local/arena-lifecycle-20261113-205351-774`.
These assertion additions do not change the timed workload. Locked restore and
Release build passed with zero warnings.

The eleven-worker measurement/regression set used **3.625 CPU seconds** and **24.17
seconds of summed worker wall time**, including startup/warmup/pacing. The separate
scenario workers also stayed within guards. Observed process working sets stayed
below 40 MiB; native capacity has its separate 2 MiB cap. No worker remains running.

The selected packages work with the checked text-ownership protocol. Remaining
work includes arbitrary String-primary schemas, Plain occurrences, nullable/general
key catalogs, large/chunked comparers, scalable index protection, growth integrated
with parallel preparation, WriteBatch, durable/replay boundaries and multi-core
schedules. Tail latency needs attribution before a production latency promise.
Package choices and user-visible contracts are unchanged; E04 remains open.

## Bounded tail investigation

**Measured on 13 November 2026.** The follow-up instruments the same indexed trace,
without increasing dataset size, operation count, CPU affinity or process priority.
No package choice, arena algorithm or protection policy changes. The evidence does
not establish a production latency bound.

### What is recorded

`tail-off` / `tail-on` enable preallocated, per-thread diagnostic buffers. They
record operation/stage boundaries and acquisition/release timestamps for root,
catalog/index, text and allocator gates. A zero-time acquisition attempt identifies
observed contention; its failure is followed by the original blocking acquisition.
Compaction retains its nonblocking owner acquisition. Disabled tracing uses the
original blocking primitives. Gate intervals can nest: do not add their hold times.
Release timestamps precede the actual unlock call by recorder overhead.

Each operation also samples GC collection counts, cumulative
[GC pause duration](https://learn.microsoft.com/dotnet/api/system.gc.gettotalpauseduration),
and the executing thread's
[CPU times](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-getthreadtimes)
and [cycle count](https://learn.microsoft.com/windows/win32/api/realtimeapiset/nf-realtimeapiset-querythreadcycletime).
Cycle counts are not converted to milliseconds. In this environment CPU-time readings
advance in coarse 15.625 ms steps, sometimes exceeding a short operation's wall time;
a reported zero does not mean zero CPU work. They are supporting evidence, not a
microsecond-precision subtraction from wall time. Counter sampling surrounds the
timed operation by a small diagnostic boundary window.

Only the twelve longest operations per worker thread retain detailed gate/stage
traces; aggregate percentiles and counts cover every operation. No per-operation
trace serialization, file I/O or trace-object allocation occurs in the measured loop.
Buffer overflow or unbalanced gate recording fails the worker. An empty warmed
Begin/End measurement, including bookkeeping, measured p50 **0.6 µs**, p99 **0.7 µs**,
maximum **4.9 µs**. Real gate recording costs additional time. This is an instrumented
diagnosis, not an unchanged baseline benchmark.

The diagnostic path itself is warmed before the workload. The exploratory series
without that warmup showed first-read costs around 2–6 ms and is not pooled with the
final series. Normal workload warmup alone does not warm newly added probe methods.

### Calibration and final bounded series

`tail-checks` validates the recorder against three deliberate controls, outside the
natural workload: a held root reader produced **22.924 ms** of identified root-write
contention; a 15 ms requested sleep produced **15.751 ms** without engine gate waits;
a forced generation-0 collection was detected with **0.310 ms** of GC pause. These
controls validate distinct signals; they do not explain any natural sample by themselves.

The final three off/on pairs recorded **36,864 operations**, including read misses
and failed/no-work attempts. All six whole workload intervals had **zero generation
0/1/2 collections and zero added GC pause time**. No failed zero-time gate acquisition
followed by blocking was observed in this final series. The earlier exploratory
series did observe two contended operations; neither belonged to its largest tails.
These observations are specific to a small paced workload on one logical processor.

| Instrumented operation | Compaction off: p99, µs | Compaction on: p99, µs | Largest off / on, ms |
| --- | ---: | ---: | ---: |
| Read, including misses | 12.5–27.9 | 12.3–20.1 | 2.271 / **38.126** |
| Full Save | 123.9–252.7 | 96.4–160.3 | **62.382** / 1.802 |
| Equal-text range and root reads | 46.7–81.0 | 45.5–85.7 | 0.508 / 0.147 |

Use `TailDiagnostics` for these operation times. The older `Latencies` field also
encloses the new diagnostic End/bookkeeping call in instrumented groups; it is not
the table's source. Percentiles remain samples, not maximum guarantees.

### Two captured stalls

**Save, 62.3815 ms, compaction disabled (`tail-off-3`, key 2):** preparation took
0.0317 ms. All measured gate acquisition intervals together took **0.0049 ms**, with
no observed contention. Publication already held the root and catalog/index gates.
Between two short text comparisons its trace has a **62.332 ms gap**, from 0.0360
to 62.3680 ms after operation start, outside any measured acquisition interval.
The entire operation consumed 144,958 reported thread cycles; other captured Saves
lasting only 0.116–0.179 ms consumed more cycles (327,654–579,533). There was no GC.
This is evidence of elapsed time without proportional CPU work, not a 62 ms tree
update or a wait to acquire an engine gate. It also occurs without compaction.

**Read, 38.1260 ms, compaction enabled (`tail-on-2`, key 247):** a text read-gate
acquisition interval accounts for **38.1190 ms**, but the zero-time attempt succeeded;
the recorder never entered its blocking fallback. The root read gate was already
held. The entire operation consumed 77,270 reported thread cycles, with no GC.
Elapsed time around a lock call therefore cannot be treated as proof of lock
contention: a thread can stop executing inside that measured interval as well.
The probe cannot distinguish suspension before, within or just after the successful
zero-time call, or synchronization internal to the runtime primitive.

### Interpretation and remaining limit

**Scheduling or another OS/runtime execution stall is the leading explanation for
these reproduced long tails.** GC is excluded for these workload intervals, and
measured contention does not explain the two samples. Cycle counts support a stall
rather than tens of milliseconds of active index work. Low process priority and
one-processor affinity on a shared workstation make scheduling delay plausible.
No kernel context-switch/ready-thread/page-fault trace was taken: this experiment
does not prove which other task, OS event or runtime stall caused the gap, or assign
the original 63.390/51.721 ms observations retroactively.

The elapsed delay still matters: the 62 ms Save held both a root gate and the coarse
prototype catalog/index gate. Other callers could be delayed if scheduled during
that interval. Keep short protected work as an engineering requirement; do not
interpret this result as a reason to remove protection or promise bounded pauses.
The selected indexes and compaction mechanism do not require replacement on this
evidence. Target-environment scheduling/multi-core validation remains separate work.

### Reproduction and resource use

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet build Prototypes/ArenaLifecycle/ArenaLifecycle.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
& ./Prototypes/ArenaLifecycle/Run-Bounded.ps1 -Groups @('tail-checks', 'tail-off', 'tail-on') -ConcurrentRepeats 3
```

Thread CPU diagnostics require Windows; the runner uses the same BelowNormal,
single-logical-processor, 15-second wall/5-second CPU, sampled 192 MiB process and
hard 2 MiB native limits. The final seven-worker set used **2.609 CPU seconds** and
**16.245 summed worker wall seconds**, with a sampled process peak of **38.004 MiB**.
Its ownership/query audits passed and every worker ended with zero outstanding
native bytes. The forced GC/sleep/held-gate controls occur only in `tail-checks`.

Final ignored evidence: `.local/arena-lifecycle-20261113-210321-093`.
Exploratory evidence: `.local/arena-lifecycle-20261113-210149-370` and
`.local/arena-lifecycle-20261113-210156-482`; these are not pooled into the final table.

Release build passed with zero warnings/errors. The original seven directed checks
and seven index checks passed again with tracing disabled, in
`.local/arena-lifecycle-20261113-210705-678` and
`.local/arena-lifecycle-20261113-210711-178`. One preceding directed-worker attempt
(`.local/arena-lifecycle-20261113-210605-708`) was terminated by the existing wall/CPU
guard before producing a result. Its empty logs do not identify the cause; it is
neither counted as a pass nor included in latency statistics. No budget was raised
for the successful bounded retry, and no owned worker remains running.
