# Bounded arena lifecycle experiment

Implements and checks the [arena protocol](../../../docs/design/arena-protocol.md): native
size-class allocation, managed root control entries, synchronous protected reads,
targeted scalar writes, prepared full replacement, deletion, shared text references
and owner-aware relocation. This is an isolated mechanism experiment, not the
production engine, a durable write benchmark or a replacement for the selected indexes.

The subsequent [Arc index integration](INDEXES.md) reuses this implementation with
real primary/secondary maps and explicit index ownership of shared text. Original
no-index measurements below remain historical evidence for their stated fixture.

## Run and resource limits

Requires .NET 10 SDK and PowerShell 7. The extended project references Arc.Collections
2.2.0 through local Central Package Management and a package lock. Runtime experiments
need no PostgreSQL, network service or containers.
From the repository's `src` directory:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet restore Prototypes/ArenaLifecycle/ArenaLifecycle.csproj --locked-mode --disable-parallel
dotnet build Prototypes/ArenaLifecycle/ArenaLifecycle.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
pwsh -NoProfile -File Prototypes/ArenaLifecycle/Run-Bounded.ps1
```

The runner executes directed checks, bounded churn and three alternating pairs of
concurrent runs with compaction disabled/enabled. Workers run sequentially at
BelowNormal priority. On Windows each worker pins itself to one permitted logical
processor; runtime processor count is also one, workstation GC is selected and
tiered compilation is disabled. All operation counts are finite. Concurrent loops
pace batches with 2 ms sleeps outside the timed operation, rather than saturating
the machine. The OS may schedule a longer sleep.

Each worker has a 15-second wall / 5-second CPU guard and a sampled 192 MiB process
working-set guard. Native capacity has a hard 2 MiB fixture cap. On timeout the
runner stops only its owned worker. Thread handshakes and joins also have deadlines.
The benchmark has no persistent service or load generator. Raw JSON and logs stay
under ignored `.local/arena-lifecycle-*` directories.

Run one focused group with `-Groups directed` or `-Groups churn`. For a single
comparison pair, call the runner from PowerShell with
`-Groups @('concurrent-off', 'concurrent-on') -ConcurrentRepeats 1`.

## What is actually stored and synchronized

| Item | Fixture representation; not a production default |
| --- | --- |
| Native segment | 8 KiB; one capacity class per segment; maximum 256 segments |
| Allocation classes | 64-byte text slots, 128-byte roots, 256-byte owned chunks |
| Availability | Preallocated per-class segment bitmaps plus preallocated slot stacks; no tree-node allocation when a slot becomes free |
| Root body | 128 bytes containing scalar/inverse values, revision, shared text token and up to eight relative chunk descriptors |
| Owned payload | 128, 512, 1,024 or 1,536 bytes in ordinary runs; at most 2,048 bytes in the cap check |
| Shared text | Up to 256 distinct 16-byte UTF-8 strings; stored/prepared reference counts and protected stable entry addresses |
| Dataset | 384 roots initially; explicit hole-making phase leaves 96 roots |
| Control entries | One fixed managed struct array with 1,056 entries; generation and lookup claims prevent premature reuse |
| Key lookup | Fixed Int32 fixture map with 1,024 keys; not ArcSlim or a production primary-index comparison |
| Dictionary lookup | Fixed fixture group IDs; tests shared storage/lifetimes, not a production string hash table |
| Protection | 64 root stripes, one dictionary gate, one allocator gate and a short catalog gate |
| Empty reserve | At most one empty native segment, keeping its previous class; no cross-class region reassignment |

Preparation has private root/chunk blocks and prepared text references. Old published
state remains readable until publication. Fixed-field updates keep the same native
root address. Readers do not retain native addresses after their protected scope.
Owned copies contain detached CLR strings and byte arrays.

An exclusive root gate drains readers of its owned blocks, allowing synchronous
reclamation after detachment. A root lifetime claim keeps metadata alive, not old
payload bytes. Shared text has separate reader protection and cannot be freed while
another stored or prepared reference exists. A compaction candidate is revalidated
under its owner gate; movement repairs the descriptor and does not advance revision.
Busy owners are skipped, and relocation uses existing compatible destination space.

## Checks and limitations

Seven directed scenarios passed, including two expected negative-control failures:

- Copies stay unchanged after updates; shared text survives removal from one root.
- Preparation/abort release reservations, and reads still see the old published
  value while a replacement is prepared. Scalar updates do not replace root blocks.
- A retained old lookup sees absence after deletion/recreation. Stale relocation
  candidates cannot move a new lifetime under the same key, including control reuse.
- A deliberately held root reader blocks the writer; the compactor skips that root
  and can move it after release without changing values or revision.
- A text reader on one root blocks shared-text reference release by another writer.
- A prepared reference retains text after its last stored reference disappears.
- The 2 MiB cap rejects insertion and replacement before publication, preserving
  existing values. Deliberately corrupting a scalar pair or payload is detected.

The directed test labels combine related assertions; the negative controls are
inside the seventh scenario, not unexpected suite failures. Churn adds eight
cycles with constant live cardinality and independent logical-value, ownership,
reference-count and control-slot audits. Concurrent runs use one reader, one ordered
writer and one compactor/pacing thread, with the same submitted write trace in both
modes. Every worker frees all native allocations before exit.

This does not validate production packed root gates, multi-core execution, arbitrary
schema layouts, List/Map/Set mutation algorithms, large directories, Arc index
retainers, WriteBatch publication, durable commit/failure recovery, replication,
parallel preparation or automatic compaction triggers/fairness. The protocol explains
where those owners must integrate; their lifetime claims cannot be omitted merely
because this isolated model has no long-lived retired payloads.

## Measurement method

Measured on 13 November 2026: Windows 10.0.26300, x64, .NET SDK 10.0.404 and runtime
10.0.15. Stopwatch frequency was 10 MHz, giving 0.1 microsecond tick resolution.
Zero/near-zero samples do not mean literally free operations. Warmup precedes each
group. CPU-affinity and runtime-processor settings were recorded in every result.

Each latency is elapsed time for the complete named in-memory operation, including
lock waits, allocator work and scheduling interruptions. Read samples also validate
every payload byte and a paired scalar invariant; they are not bare pointer loads.
Owned-copy samples allocate/copy arrays and decode strings, immediately following
a read of the same root. They are not a controlled cache-equivalent comparison with
protected reads. Save includes private preparation and publication, but no SQL.

The concurrent comparison submits 4,096 reads, 256 scalar updates, 768 full Saves
and 256 Delete attempts per run. Of those Deletes, 160 remove an existing root;
missing results remain separate normal outcomes. Read hits/misses are reported
separately. Enabled runs make 1,024 compaction attempts, including skips/no-work.
Different interleavings may change the hit count and relocation count.

Percentiles use nearest-rank samples. The following ranges span three process runs;
they are not confidence intervals. No outliers were discarded. The instrumented
churn run was repeated separately to time its initial packing phase; engine code and
the concurrent workload were unchanged by that additional timing instrumentation.

## Observed memory

**KiB below means 1,024 bytes of allocated native segment capacity, not traffic,
journal size or total process RAM.** Live encoded bytes include root headers and
descriptors. The initial 384 roots have 300 KiB of owned payload, 48 KiB of encoded
root bodies and 4 KiB of distinct UTF-8 text: 352 KiB altogether.

| State | Live encoded bytes, KiB | Class slack, KiB | Reusable holes, KiB | Empty reserve, KiB | Native segments, KiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Initially 384 roots | 352 | 24 | 0 | 0 | 376 |
| After deleting 288 roots, before compaction | 88 | 6 | 282 | 0 | 376 |
| Same 96 roots after compaction | 88 | 6 | 10 | 8 | 112 |
| Each of eight subsequent cycles, 384 live roots | 352 | 24 | 0 | 8 | 384 |

Compaction returned 33 segments, or **264 KiB**, to the native allocator. It moved
327 blocks: 64 roots, 231 chunks and 32 shared-text blocks, copying 65,664 bytes
(64.125 KiB) of encoded content. That tiny, quiescent packing phase took **159 µs**
in the instrumented run. It was not a global database write pause and does not
predict the time needed to compact gigabytes under load.

All eight churn checkpoints held native capacity at **384 KiB**, including the
8 KiB empty reserve. The concurrent runs peaked at **392 KiB** with either setting.
Peak private root/chunk preparation in these workloads was 1,664 bytes. The separate
capacity-refusal scenario deliberately reaches the 2 MiB native cap. Shared text retained by a
prepared reference remains in the internal live-text accounting, so that last
number is not a claim to include all preparation metadata or shared text. The
capacity cap covers every native segment regardless of its block state.

There is no asynchronous retired-payload queue in this model: exclusive gates drain
readers before freeing the corresponding blocks. This is a consequence of these
access paths, not evidence that production index/replication retainers cost zero RAM.

The managed heap after forced collections was approximately **777–779 KiB** across
the instrumented churn checkpoints, including the engine, fixture oracle, 256 source
strings/UTF-8 fixtures, timing arrays and accumulating result records. That roughly
2 KiB increase includes the retained checkpoint records; it is not native arena growth.
Before the experiment, after warmup, the worker's managed baseline was about 300 KiB.
These totals do not isolate production metadata overhead. Process working-set peaks
observed before JSON serialization were approximately **29–33 MiB**, dominated by
the runtime and harness as well as the small database. Native freeing need not lower
process working set immediately.

Managed allocation traffic during each concurrent phase, including the harness,
was about **188 KiB without compaction** and **276–343 KiB with compaction**.
Releasing and reacquiring segments also recreates their managed slot metadata in
this model; the fixed availability bitmaps do not eliminate that cost. Traffic is
cumulative allocation, not retained heap growth. Pooling/reassigning region metadata
has not been selected or measured here.

## Observed latencies

Concurrent comparison, **microseconds**; p99 means 99% of samples in that run
completed within the stated time. MemoryOnly publication only, with paced load:

| Operation | Compaction off: p50 range | Compaction on: p50 range | Off: p99 range | On: p99 range |
| --- | ---: | ---: | ---: | ---: |
| Protected read hit, including validation | 1.6–1.9 | 1.7–2.1 | 15.7–25.2 | 11.7–23.7 |
| Scalar update | 0.3–0.4 | 0.4 | 2.5 | 2.1–6.0 |
| Full Save | 1.9–2.5 | 2.2–2.6 | 43.7–102.2 | 56.6–99.1 |
| Delete attempt, including ordinary absence | 0.3–0.5 | 0.4–0.5 | 2.5–3.0 | 2.8–5.4 |
| Compaction attempt, including skips | — | 0.6–0.7 | — | 4.7–8.2 |

Enabled runs completed **579–774 actual relocations**. The distributions overlap;
this sample does not establish a reliable percentage overhead or throughput limit.
It does show that the bounded compactor can run alongside readers/writes in the
tested schedule without a sustained global stop. It still consumes CPU, transient
memory and owner/allocator protection time.

Do not interpret p99 as a maximum: the largest concurrent read was **5.756 ms with
compaction disabled**, and the largest Save was **3.768 ms**, also disabled. Enabled
maxima included a 0.540 ms read, 0.396 ms Save and 0.168 ms compaction attempt.
No scheduler/lock profiling separated the causes, so these observations do not mean
compaction improves worst-case latency. A guaranteed maximum pause is unproven.

The separate instrumented churn run measured 256 owned copies at p50 **2.0 µs**,
p99 **9.6 µs**, maximum **12.5 µs**. Its 353 compaction-step samples, including the
initial packing and later no-work checks, had p50 **0.5 µs**, p99 **3.3 µs** and maximum
**138.3 µs**. These tiny inputs are mechanism checks, not production service targets.

## Evidence and conclusion

The final eight-worker validation/comparison run used approximately **1.84 CPU
seconds** and **17.97 seconds of summed worker wall time**, including runtime startup,
warmup, validation and intentional pacing. The additional instrumented churn worker
used **0.375 CPU seconds** and **3.64 seconds wall time** as measured by the launcher.
All workers stayed within their guards and reported zero outstanding native bytes.

Local raw evidence, intentionally ignored: `.local/arena-lifecycle-20261113-201917-012`
and `.local/arena-lifecycle-20261113-202041-416`. Earlier development measurements
are not combined into the tables above. The Release build passed with zero warnings.

The checked root/chunk/text ownership protocol is a viable basis for continued
engineering. These results support safe reclamation and bounded native capacity for
the tested churn; they do not close E01–E04 or establish production readiness.
The subsequent [Arc index integration](INDEXES.md) checks selected indexes and
short shared-text lifetimes. General key schemas, multi-root publication and actual
durable/replay boundaries remain integration work. No new product behavior or
production segment size is accepted by these experiments.

The [bounded tail investigation](INDEXES.md#bounded-tail-investigation) adds optional
Windows-only gate, GC and thread CPU diagnostics through the `tail-checks`, `tail-off`
and `tail-on` runner groups. It reproduces long execution stalls without measured
gate contention or GC; it does not select a new locking algorithm or change the
historical latency measurements above.

The sibling [WriteBatch experiment](../WriteBatch/README.md) references this project
to verify a database publication barrier, PostgreSQL commit and application-crash
recovery on the same allocator/index implementation. The friend-assembly declaration
exists only for this experimental reuse; the arena project has no SQL dependency.
