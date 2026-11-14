# Prototype evidence

[Documentation map](README.md) · [Architecture](index.html)

The sections preserve experiment chronology and the decisions available at each
measurement. The subsequent decision selects background journal application with
coalescing and a 256-byte baseline PostgreSQL saved unit for v1; the [decision record](../decisions/checkpoint-and-journal.md) owns
the current contract. Historical open-method statements below are not new decisions.

## Bounded operation admission and retry

**Measured on 14 November 2026.** [OperationRetry](../../src/Prototypes/OperationRetry/README.md)
passed 13 directed SQL protocol groups and two actual process-crash/recovery boundaries.
It checks durable first admission, fingerprints/results, concurrent duplicates,
expiry, no-ops, same-history reopen and retirement ordering. This supports the
[selected retry protocol](operation-retry-protocol.md); native publication, real
transport faults and multi-node integration remain release checks.

With a bounded reused connection pool, twelve paced samples had median admission
2.568 ms and median complete two-transition SQL call 7.403 ms (maximum 13.672 ms).
The first fresh-connection run cost 141.409 ms median per complete call; its connection
setup cost is documented rather than presented as engine latency. Final workers used
2.125 seconds CPU, 14.049 summed wall seconds and at most 44.465 MiB sampled working
set. This excludes PostgreSQL process memory/CPU and proves no throughput or p99 SLO.

The subsequent [Unique-index extension](../../src/Prototypes/OperationRetry/README.md#unique-index-extension)
passed nine tiny Arc-based final-state validation checks, including concurrent claims,
null exemption, composite/case comparison and atomic swaps. It checks the selected
constraint semantics, not SQL/native/replica integration for unique indexes.

## Packed record layouts

**Measured on 12 November 2026. These are synthetic loop measurements, not
database request latency or a comparison with an external implementation.**

LayoutComparison used .NET SDK 10.0.404 / runtime 10.0.15, Windows build 26300,
x64, Intel Core i9-13900H (20 logical processors). Each layout ran in a fresh
process, sequentially, in forward and reversed order. Tiered compilation was off.
Each process used seven samples of twenty invocations after warmup.

The table shows the range of the two process medians, in nanoseconds per item.
The scan's denominator is visited input rows; reads and updates use 50,000
precomputed random positions. These values must not be compared as identical
operations across different rows of the table.

| Records | Operation | Row layout, ns/item | Column layout, ns/item |
| --- | --- | --- | --- |
| 250,000 | Random full-record read | 5.32–6.63 | 28.67–30.83 |
| 250,000 | Projected filtered scan | 1.26–2.22 | 0.54–0.67 |
| 250,000 | Random two-field update | 2.42–3.43 | 3.73–3.96 |
| 1,000,000 | Random full-record read | 9.69–10.44 | 66.11–66.87 |
| 1,000,000 | Projected filtered scan | 2.92–2.94 | 0.57–0.63 |
| 1,000,000 | Random two-field update | 3.25–3.59 | 4.19–4.27 |

All eight processes passed initial-read, filter and final-state checksum checks.
All measured operation loops reported zero managed allocation. Both layouts
contain 64 payload bytes per record: 16 MB or 64 MB using decimal units. The
experiment therefore demonstrates no payload-memory saving from columns alone.

The retained managed heap delta is approximate, not RSS. Strings, indexes,
segmentation, concurrency, persistence, networking and native storage are absent.
The repeatedly visited position set favors a warm working set. The exact projection
is selective and synthetic. Results cannot choose the universal storage layout.

**Decision impact at the time of measurement:** keep row and column layouts open. The experiment rejects
an assumption that columns improve every operation. Next layout evidence should
use representative record widths, query projections, key indexes and access
distributions. String storage needs a separate cardinality/churn experiment.
See the [runnable prototype](../../src/Prototypes/LayoutComparison/README.md).

**Later decision, 13 November 2026:** the decision selects [record-oriented fixed fields
with separate variable payloads](../decisions/physical-storage.md#record-oriented-layout-with-separate-variable-payloads)
as the v1 baseline. This selects a design for the target workloads; it does not turn
these managed numeric measurements into evidence for the complete native engine.
The later [relative-addressing acceptance](../decisions/physical-storage.md#relative-block-addressing)
also does not add a measurement. [Size-class reuse](../decisions/allocation-and-compaction.md)
and [automatic incremental compaction](../decisions/allocation-and-compaction.md#automatic-incremental-compaction)
are now accepted separately, as is [chunked large-payload storage](../decisions/physical-storage.md#large-payloads-in-bounded-chunks).
The [bounded empty-segment reserve](../decisions/allocation-and-compaction.md#empty-segment-retention-and-release)
and release of excess are also accepted without a new measurement, as is
[automatic collection shrink](../decisions/allocation-and-compaction.md#automatic-shrinking-of-unused-collection-capacity).
Numerical sizes/thresholds/reserve/work budgets and remaining pressure rules stay open.
These earlier measurements establish no allocation, chunk-layout, compaction,
reserve-policy or collection-shrink result.

## PostgreSQL storage and streaming

**Measured on 12 November 2026; isolated synthetic storage experiment.**
[PostgresStorageComparison](../../src/Prototypes/PostgresStorageComparison/README.md)
owns the workload, commands, resource limits and missing engine costs. This is an isolated experiment; its settings do not accept a product acknowledgment
policy, batch delay or throughput SLO.

Environment: local PostgreSQL 18.4, Npgsql 10.0.3, .NET 10.0.15 / SDK 10.0.404,
Windows build 26300, Intel Core i9-13900H with 20 logical processors. Server GC and
tiered compilation were disabled. PostgreSQL used `fsync=on`,
`full_page_writes=on`, `synchronous_commit=on`, `open_datasync`, 128 MiB shared
buffers and WAL compression off. No other active client query was observed at
startup; background server/OS work was not isolated. Both the PostgreSQL data and
temporary file journal were on a local NTFS/NVMe volume. This is not a cloud or
network-separated service result.

The final run lasted about 136 seconds and contained two passes, each with
three-second offered-load scenarios. The second pass reversed backend, rate and
batch order. All 26 measured scenarios verified their acknowledged persisted
mutations and native final state. Four standalone and two concurrent state loads
passed full chunk hash checks. Rollback/reconnect verification passed. Cleanup
confirmed zero remaining prototype databases, connections and temporary journals.
Server crash/power-loss recovery was not tested.

An earlier run exhausted the read-latency sample buffer, making its samples cover
only the beginning of long scenarios. The sampler was changed to one in 4,096 reads
with capacity for 500,000 samples and the experiment repeated. No samples were
dropped in the final run. The tables below use only that complete-sampling run.
Earlier observations also showed substantially lower transfer rates and larger
write-latency spikes; warm-cache results below are not a worst-case envelope.

### Completed durable writes

Each logical change encodes 32 bytes; a PostgreSQL transaction inserts one binary
batch. Batching waits approximately 2 ms and caps the batch at 64 changes. At
10,000 changes/s the observed average PostgreSQL batch contained about 23 changes.
End-to-end p99 includes intended-arrival delay, queueing, collection, commit and
native publication. Ranges cover the two passes; they are not confidence intervals.

| Backend / commit policy | Offered changes/s | Completed changes/s including drain | End-to-end p99, ms | Explicitly rejected per pass |
| --- | --- | --- | --- | --- |
| PostgreSQL, one change | 1,000 | 1,000 | 1.86–2.82 | 0 |
| PostgreSQL, bounded batches | 1,000 | 1,000 | 4.18–7.37 | 0 |
| PostgreSQL, one change | 5,000 | 4,981–4,998 | 9.71–16.87 | 0 |
| PostgreSQL, bounded batches | 5,000 | 4,999 | 5.54–6.25 | 0 |
| PostgreSQL, one change | 10,000 | 7,975–8,107 | 600.50–613.80 | 941–981 of 30,000 |
| PostgreSQL, bounded batches | 10,000 | 9,999 | 2.75–3.34 | 0 |
| File, one change / Flush(true) | 10,000 | 623–1,218 | 3,392–7,175 | 22,272–24,773 of 30,000 |
| File, bounded batches / Flush(true) | 10,000 | 9,997–9,998 | 4.28–4.30 | 0 |

Single-change PostgreSQL writes filled the 4,096-item queue at 10,000/s; their
drain took approximately 585–639 ms. Batched PostgreSQL runs at that rate observed
at most 39–107 queued changes and less than 0.3 ms final drain. The file baseline
shows the cost of flushing this simple growing journal per mutation; it does not
establish that PostgreSQL is faster than an optimized file-backed engine.

Batching also adds delay at low input rates, visible in the 1,000/s results. It is
not a free improvement to every workload. The experiment does not select 2 ms as
the product default or prove sustained capacity beyond the offered short bursts.

For the equal 30,000-change PostgreSQL batch runs, global WAL position growth was
about 1.20 MB and journal relation size about 1.16 MB. These are decimal units;
WAL observations can include unrelated/background activity. They do not establish
long-term disk growth, physical device writes or the final encoding's efficiency.

### Resident reads and concurrent loading

The unopposed fixed-root reader achieved 60.95–63.26 million reads/s. Alongside
10,000/s batched PostgreSQL writes it achieved 56.99–58.19 million reads/s; with
one simultaneous 128 MiB streamed load, 40.98–48.69 million reads/s over the write
phase. These observations show shared-resource interference even though ordinary
reads execute no SQL. This synthetic loop omits ID lookup, indexes and OQL.

Sampled read p99 was 0.1 microseconds for the first two conditions and 0.2
microseconds during concurrent loading. Timer quantization and sampling dominate
such tiny operations: some measured durations were zero. Do not treat these
figures as precise database request latency or a bound on every read.

During concurrent loading, batched writes completed about 9,999 changes/s with
zero rejections and p99 of 3.55–3.77 ms. Loading and installing the 128 MiB image
took 197–229 ms. The base was immutable; this did not exercise live checkpoint
capture, ordered-tail catch-up or replica Ready semantics.

### Loading a binary image into native memory

Standalone warm-cache transfers included allocation and installation of 128 MiB
of random payload in native memory. Connection opening and final hash validation
were outside timing. Both methods used the same checkpoint rows and reversed order.

| Loading path | Time for 128 MiB | Payload throughput | Managed bytes allocated during load |
| --- | --- | --- | --- |
| SequentialAccess + GetStream into native spans | 116–119 ms | 1,072–1,103 MiB/s | 1,112 |
| Binary COPY export through a byte array per chunk | 163–164 ms | 783–785 MiB/s | Approximately 128 MiB |

Both paths still allocate the 128 MiB native destination. The stream result is
not end-to-end zero-copy: native allocation, driver/network buffering and memory
movement remain. COPY's allocation cost belongs to the tested byte-array API path,
not every possible COPY implementation. The client lifetime peak working set was
about 195 MiB; that excludes PostgreSQL memory and is not a per-scenario peak.
No extrapolation to cold, remote or 5–10 GiB startup is justified by this sample.

**Decision impact:** the append-only baseline demonstrates commit-cost amortization
and low-allocation SequentialAccess/GetStream loading. It does not establish that
PostgreSQL needs an application recovery journal. This initial run did not measure
current-state UPDATE/UPSERT; its 10,000/s result must not be presented as that path's
capacity. The subsequent [unit comparison](#postgresql-current-state-units-and-journal-compaction)
measures those paths separately. The subsequent decision selects Q09.2's journal/base
strategy; its capture and retention policies remain open.
The subsequent decision selects PostgreSQL as the first Durable implementation in
[Q09.6](../decisions/persistence-backends.md#q09.6-durable-storage-backends) and
required reliable storage acknowledgment in
[Q09.1](../decisions/write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed).
Those product decisions do not extend the measured guarantees. Checkpoint/retention
requirements (Q09.2), realistic latency/load budgets (Q10.1) and full-engine,
long-running and deployment-specific measurements remain open.

## PostgreSQL current-state units and journal compaction

**Measured on 12 November 2026; bounded synthetic follow-up to Q09.2.**
The [prototype's unit modes](../../src/Prototypes/PostgresStorageComparison/README.md#current-state-units-versus-a-compact-journal)
own commands and methodology. Each scenario starts from the same 16 MiB image:
65,536 records of 256 bytes, mostly random filler, with 16 bytes changed per
mutation. Compare complete-unit replacement by prepared UPDATE / UPSERT with
32-byte journal changes. All methods batch at most 64 mutations / approximately
2 ms and wait for synchronous PostgreSQL commit before native publication.

Unit replacement uses one set-based statement per batch, reused payload buffers
and coalescing of changes to the same unit. The spread key permutation provides
little coalescing; a concentrated hot set could behave differently. The eight-second
10,000/s workload offers 80,000 mutations, so successful runs revisit records.
Rejected work is explicit; throughput includes draining the bounded queue.
UPSERT's measured load follows its conflict/update path. Insert rollback is tested
separately, not advertised as insertion throughput.

The environment matches the original software/hardware configuration, but these
are new runs under different transient machine conditions. No other active SQL
client was observed at each invocation's start. PostgreSQL autovacuum stayed on;
default TOAST compression was pglz, checkpoint timeout 300 s and max_wal_size
1,024 MiB. Server/background work and caches were not isolated. Comparisons below
use contemporaneous journal controls, not the earlier 2.75–3.34 ms latency result.

### Default PostgreSQL value storage

The EXTENDED-policy run lasted about 309 seconds (18:09:32–18:14:41 UTC). Two passes
reverse method/unit order; ranges are observations, not confidence intervals.

| Method and persisted unit | Completed changes/s | End-to-end p99, ms | Rejected out of 80,000 per pass |
| --- | --- | --- | --- |
| UPDATE, 256 B | 9,998 | 10.49–10.61 | 0 |
| UPSERT, 256 B | 9,997–9,998 | 12.12–16.52 | 0 |
| UPDATE, 4 KiB | 5,278–5,551 | 1,077.62–1,310.04 | 30,741–33,418 |
| UPSERT, 4 KiB | 5,460–5,730 | 1,243.18–1,464.52 | 29,580–31,187 |
| UPDATE, 64 KiB | 171–181 | 25,248.56–26,805.40 | 74,009–74,176 |
| UPSERT, 64 KiB | 214–226 | 21,233.02–21,575.89 | 73,344–73,856 |
| Journal, across all base-unit sizes | 9,996–10,000 | 7.51–80.54 | 0 |

The saturated paths must not be described as handling 10,000/s. Their high p99
mostly reflects queueing and drain, not the duration of one isolated SQL call.
Journal tail latency also varies materially; meeting offered throughput does not
establish a tight production latency SLO.

### Disabling compression for current-state units

The EXTERNAL-policy follow-up lasted about 215 seconds (18:15:56–18:19:32 UTC).
Only the current-unit column changes policy; the compact journal keeps EXTENDED.
PostgreSQL documents that EXTERNAL allows out-of-line storage without compression
attempts, whereas EXTENDED allows both; see [TOAST](https://www.postgresql.org/docs/current/storage-toast.html).
This tests one suspected cost, without changing durability or server-wide settings.

| Method and persisted unit | Completed changes/s | End-to-end p99, ms | Rejected out of 80,000 per pass |
| --- | --- | --- | --- |
| UPDATE, 4 KiB | 7,840–8,840 | 737.87–760.02 | 6,474–14,237 |
| UPSERT, 4 KiB | 7,769–9,151 | 621.03–722.37 | 4,868–14,093 |
| UPDATE, 64 KiB | 256–312 | 14,791.77–18,376.56 | 72,896–73,600 |
| UPSERT, 64 KiB | 419–481 | 10,191.53–11,688.10 | 71,272–72,000 |
| Journal, both base-unit sizes | 9,999 | 8.05–92.81 | 0 |

Disabling unit compression improved observed replacement throughput, but neither
larger-unit path sustained the offered rate. It does not prove compression is the
only bottleneck, or that compressed data would have the same outcome. Whole-unit
replacement still transmits and stores far more than the changed fields.

### WAL, recovery, maintenance and memory

For EXTERNAL units, global write-WAL growth per acknowledged mutation was about
4,847–5,174 bytes for 4 KiB replacement, 74,635–76,136 bytes for 64 KiB replacement,
and 37–42 bytes for the journal. These normalize for rejected work; they are not
physical device-byte counters. The default-policy 256-byte UPDATE/UPSERT cases
used about 12.5–16.8 MiB WAL for all 80,000 accepted changes, while journal controls
used 2.8–6.2 MiB. Global counters may include background work and verification.

The journal comparison also pays to construct a new base. The test stops writes
after each run, atomically copies the full 16 MiB current image into a new base and
removes its covered journal, then verifies recovery from that base. With EXTERNAL
units this took 595–702 ms, generated another 17.2–18.0 MiB WAL, and allocated
1,112 managed bytes during the measured compaction operation. Default-policy
compaction took 676–1,555 ms across its unit sizes and produced 17.2–21.3 MiB WAL.
The existing native image and a single reusable unit buffer are used; there is
no second full image for capture. A separate native destination is subsequently
allocated for verification, outside those compaction measurements.

Including compaction after every 80,000 changes, journal write-plus-compaction WAL
was roughly 20–28 MiB. This is more than the 256-byte direct-update cases: a journal
is not always the cheaper complete lifecycle. The test interval is not a proposed
checkpoint schedule. Less frequent compaction reduces its amortized work but leaves
a longer recovery tail; neither retention nor pause limits are selected here.

For the EXTERNAL runs, loading the 16 MiB base and replaying 80,000 changes took
146–268 ms; after compaction the base-only path took 84–165 ms. These warm-cache
small-image timings omit connection opening, indexes and final verification.
They cannot predict multi-gigabyte startup or a long retained tail.

Ordinary vacuum after EXTERNAL direct updates took 2.1–3.2 s for 4 KiB units and
4.7–8.2 s for 64 KiB units. Their allocated relations remained about 369–586 MiB
after maintenance, from initial 17–22 MiB relations. This is reusable-space
maintenance, not an OS-file-shrinking test. Journal base replacement plus removal
returned its relations to about 17–22 MiB; its following vacuum took 11–23 ms.
No repeated-cycle storage equilibrium or VACUUM FULL was tested.

Payload-buffer capacity is 16 KiB / 256 KiB / 4 MiB for direct updates, versus one
256 B / 4 KiB / 64 KiB unit buffer for journal seeding/maintenance. Every scenario
also owns the same 16 MiB native staging image and 1 MiB native read model; neither
is a production memory-layout recommendation. During 80,000-change journal runs,
the client allocated about 8.8–12.6 MiB cumulatively for encoding and workload
infrastructure, not that much additional live RAM. Client lifetime peak working
set was 126–128 MiB across the two invocations, including verification and earlier
scenarios; it is not a per-method peak and excludes PostgreSQL/OS cache memory.

Both measured runs completed all 30 scenarios with successful state verification
and zero dropped read samples. Both new smoke modes and the original smoke mode
passed. Reopened current state, exact journal acknowledgment membership/order,
rolled-back updates/insertions and post-compaction recovery were checked. Final
inspection found zero prototype databases, connections or temporary file journals.

**Decision impact:** the decision selects A, a recoverable base plus compact changes
in PostgreSQL, after this comparison. See the
[decision record](../decisions/checkpoint-and-journal.md#q09.2-postgresql-base-and-compact-journal). Its
write-traffic advantage is measured; it is no longer inferred from append alone.
Direct small records remain a viable simpler design. The application journal adds
replay/compaction responsibility while PostgreSQL continues to own physical
durability. The design also adopts automatic size/age triggers with a hard journal
limit. Neither decision accepts the prototype format, a capture/write-pause method,
numerical retention/queue thresholds or a production throughput SLO.

### Interpretation boundaries

The native read loop is the same tiny fixed-root probe, not GetById/OQL. In the
default-policy run its separate baselines were 13.42–14.21 million reads/s, while
writer scenarios ranged about 14.43–18.97 million reads/s. These inconsistent
baseline/loaded ordering observations show uncontrolled scheduling/frequency noise;
they do not establish that any storage method makes application reads faster.
Ordinary probe reads use no SQL. No read-latency samples were dropped.
The EXTERNAL run also showed noisy baseline/loaded ordering (10.29–10.54 versus
14.92–20.69 million reads/s); it does not resolve that limitation.

No run tests server crash/power loss, online checkpoint capture, long reader
snapshots, hot-key distributions, insert/delete churn under load, steady-state
vacuum, cloud/network latency, real document encoding, secondary indexes or
multi-gigabyte readiness. Full unit replacement is the tested implementation;
server-side byte patches and other representations were not benchmarked.

## Larger paused captures and pending-write memory

**Measured on 12 November 2026, 19:01:52–19:12:46 UTC.** Short write pauses were considered acceptable in principle, subject to larger
measurements before choosing a capture method. The method remains open. The runnable
[pause prototype](../../src/Prototypes/PostgresStorageComparison/README.md#larger-paused-captures-and-queue-memory)
extends the same local PostgreSQL 18.4 / Npgsql 10.0.3 environment. Its ignored raw
report is `.local/postgres-pause-measure.json`; the final smoke report is
`.local/postgres-pause-smoke-backlog.json`. No other active SQL client was observed
at invocation start; OS/server background work was not isolated.

### What the timing means

The image size is the complete resident dataset being saved, not the journal size
or the size of one update. For example, the 1 GiB case already has a 1 GiB native
image and a previously persisted base. The prototype does the following:

1. Finish 1,000 seeded writes and establish their committed boundary before timing.
2. Stop applying new writes. Keep a separate native read probe running against
   already committed roots. Start offering new write requests at 10,000/s.
3. Copy the stable image through a reused 1 MiB buffer into a new PostgreSQL base
   generation. Compute SHA-256 per chunk, use binary COPY, and durably commit each
   group of at most 32 chunks. The payload column uses STORAGE EXTERNAL.
4. Durably publish the complete generation and its committed boundary in a small
   manifest transaction. The previous base and journal remain recoverable until
   publication; their cleanup is outside timing.
5. Resume applying queued writes, with persistence acknowledgment before success.
   Keep offering new writes for three more seconds, then finish the admitted work.

**The reported pause is the interval from step 2 until step 5.** It is how long
new writes cannot execute in this candidate design. It is not startup time, a
single-query latency, or a pause of PostgreSQL itself. Native reads continue, but
they see the last committed state until writes resume. No memory gate spans SQL
I/O. The prototype performs the copying, hashing, transport and durable storage
work during this pause; it is much longer than a memory-copy-only operation.

Setup, initial native allocation/fill, writing the previous base, draining earlier
writes, full readback verification and later reclamation are outside timing. The
real engine's document/dictionary/index serialization and writer-barrier drain are
not represented. This is a concrete candidate's cost, not PostgreSQL's maximum
bandwidth or an optimized full-engine performance limit.

### Measured pause and request outcomes

There are two passes for every size and command size, with reverse order in the
second pass: 12 measured scenarios, plus an excluded warm-up. Each range below
covers both passes; it is an observed range, not a statistical confidence interval.

| Complete image | Command payload | Writer pause, seconds | Admission refusals per scenario | Expired before execution per scenario |
| --- | --- | --- | --- | --- |
| 128 MiB | 32 B | 3.74–5.28 | 4,612–20,038 | 0 |
| 128 MiB | 1 KiB | 4.19–5.17 | 25,578–35,338 | 0 |
| 512 MiB | 32 B | 18.49–22.89 | 152,170–196,155 | 0 |
| 512 MiB | 1 KiB | 15.31–21.28 | 136,670–196,415 | 0 |
| 1 GiB | 32 B | 31.97–34.54 | 286,958–312,635 | 20,232–32,768 |
| 1 GiB | 1 KiB | 30.36–38.54 | 291,070–369,019 | 8,097–16,384 |

Durable capture itself took 3.60–38.46 seconds. The measured writer pause also
includes 81–139 ms of probe finalization, counter collection and drain-probe setup,
reported separately as instrumentation. Manifest publication took 0.99–7.55 ms;
most of the time belongs to the preceding full-image write path. Producer lateness
p99 was at most 1.85 ms and the largest lateness was 36.05 ms. It does not explain
the multi-second write pauses.

The queue admits at most 32,768 requests and 16 MiB of command bytes. Thus 32-byte
commands hit the count limit; 1 KiB commands hit the byte limit at 16,384 requests.
At 10,000/s those capacities cover only about 3.28 and 1.64 seconds of arrivals.
Every measured pause exceeded both capacities. Later arrivals were refused before
allocating a retained request; they were not acknowledged and were not persisted.

An admitted request is also checked against a 30-second intended-arrival-to-start
budget when dequeued. If it is already too old, it fails before storage execution.
There is no timer-driven caller cancellation during capture: the failure may be
delivered after that deadline. Once a batch begins execution, a later deadline does
not turn a completed write into an uncommitted one. These are experiment settings,
not accepted production queue/timeout contracts.

Across all measured scenarios, 2,677,539 offered requests were accounted for:
573,400 committed, 2,026,658 refused admission and 77,481 expired before execution.
The 1,000 setup changes per scenario are outside these offered-work totals.
Low latency among successful requests alone can be misleading when many older
requests failed; refusal and expiration counts are essential to interpretation.

### Measured queue footprint versus a larger hypothetical queue

Before workload threads start, retained managed memory is calibrated using full
collections around empty/full queues. Each request owns its serialized byte array,
request object and TaskCompletionSource/Task. The empty preallocated queue retains
about 256 KiB, including its reference array. No full GC is forced during timing.

| Command payload | Approximate retained request object graph, excluding the queue array | Observed maximum requests | Estimated peak retained queue RAM |
| --- | --- | --- | --- |
| 32 B | 199.4 B | 32,768 | 6.48 MiB |
| 1 KiB | 1,190.7 B | 16,384 | 18.86 MiB |

These peak queue estimates combine measured retained bytes per request with the
observed high-water count and fixed container size. They are not an attribution
of the whole process working set. External caller state/continuations, transport,
in-flight batches, SQL buffers, allocator/GC headroom and the database image itself
are separate costs. Calibration is approximate; fractional bytes reflect the
heap-difference measurement, not a fractional allocation unit.

To estimate admission of **every** arrival during a pause, use:

```text
waiting requests = offered requests/second × pause seconds
approximate queue RAM = waiting requests × (calibrated request bytes + 8-byte reference)
```

Using each image size's observed pause envelope gives the estimates below. They
include one container reference per request, but exclude spare array capacity,
container headers, GC headroom and external callers. They are arithmetic estimates
for a different queue policy, not measurements of queues allocated in this run.

| Complete image | Observed pause envelope | Queue estimate for 32 B commands | Queue estimate for 1 KiB commands |
| --- | --- | --- | --- |
| 128 MiB | 3.74–5.28 s | 7.4–10.4 MiB | 42.7–60.4 MiB |
| 512 MiB | 15.31–22.89 s | 30.3–45.3 MiB | 175–262 MiB |
| 1 GiB | 30.36–38.54 s | 60.0–76.2 MiB | 347–441 MiB |

For example, a 35-second pause at 10,000/s creates about 350,000 pending requests
if all are admitted. A 32-byte command does not mean only 32 bytes of RAM per
pending operation: the calibrated request and one queue reference bring this
example to about 69 MiB. With 1 KiB commands it is about 400 MiB, before the
excluded costs. Enlarging the queue would also require revisiting the 30-second
wait budget; more RAM alone does not remove a long write pause.

### Resumption, memory outside the queue and correctness

For 128/512 MiB, every admitted paused request committed: the paused backlog was
processed in 0.29–0.42 seconds for 32-byte commands and 0.99–1.58 seconds for 1 KiB
commands, while new arrivals continued. The 1 GiB backlog settlement times of
0.13–1.25 seconds include expiration failures and must not be presented as successful
write throughput. New arrivals can keep a queue nonempty after the original backlog
has been processed. Time to an empty queue after arrivals stopped ranged from
about 1 ms to 2.84 seconds. This is a short post-capture observation, not proof of
sustainable recurring compaction or steady-state service capacity.

The capture uses one existing full native image and one reused 1 MiB serialization
buffer, with no second full capture image. Verification also streams through that
buffer with small root arrays. Client working set near resume was 201–261 MiB for
the 128 MiB image, 630–649 MiB for 512 MiB, and 1,144–1,172 MiB for 1 GiB. These
include the synthetic reader, metrics arrays, driver and retained runtime heap;
PostgreSQL and OS cache memory are excluded. Working-set differences are not a
measurement of queue size or a production engine memory-overhead ratio.

All 12 scenarios verified every active-generation chunk's identity, length and
SHA-256 after reopening a connection; verification took 0.52–5.87 seconds outside
the pause. The base's declared boundary, complete journal ordering/membership and
all reconstructed roots matched acknowledged writes. Refused and expired commands
were absent. Partial-generation publication refusal and rolled-back manifest
publication preserved old-base-plus-journal recovery. Native reads remained
coherent, with no dropped latency samples. Their tiny 1 MiB fixed-root probe is
not a scan of the large image or a full-engine GetById/OQL benchmark.

The Release build, final pause smoke and original smoke regression passed. Cleanup
found zero owned databases, prototype connections and temporary file journals.
No PostgreSQL crash/power-loss, network failure, uncertain-commit recovery, real
document encoding, index rebuild, long-run cleanup, remote managed service or
Linux/container behavior was tested. Both base generations and the journal were
retained until scenario cleanup, so this is not a bounded-retention implementation.

**Decision impact:** the simple paused streaming candidate avoids a second full
RAM image, but its measured pauses are tens of seconds at 512 MiB–1 GiB on this
setup. It is not yet a justified general v1 choice for frequent writes. A small,
rarely changed dataset has a different cost profile. Keep the method and numerical
budgets open; do not turn these measurements into a default or a claim that other
capture implementations cannot be faster.

## Background journal application with coalescing

**Main comparison measured on 12 November 2026, 19:41:46–19:56:09 UTC.**
This experiment compared an alternative to paused full-image capture before selecting a method. The [prototype instructions](../../src/Prototypes/PostgresStorageComparison/README.md#background-journal-application-with-coalescing)
own runnable commands, budgets and implementation details. Raw reports remain
ignored under `.local/`, including `postgres-apply-smoke.json` and
`postgres-apply-measure.json`. The same local PostgreSQL 18.4 / Npgsql 10.0.3
environment and durable settings were used; no other active SQL client was
observed at invocation start. OS/server background work was not isolated.

### What is being measured

The foreground appends a small change to PostgreSQL, publishes it in native memory,
then acknowledges success. A separate worker reads the next committed journal
prefix, loads the affected saved blocks, applies changes in order, and updates
each touched block once. The saved blocks and applied position/count commit in one
SQL transaction. New foreground writes continue throughout this work.

Two separate rates matter: how fast changes are durably accepted, and how fast
those accepted changes are incorporated into saved blocks. Accepted changes that
the worker has not yet applied remain recoverable in the on-disk journal. They
are not an unacknowledged RAM queue. No whole-arena write freeze is used here.

The size terms have distinct meanings:

| Quantity | Value in this experiment | Meaning |
| --- | --- | --- |
| Complete dataset | 128 MiB | All native/saved data; 65,536 fixed 2 KiB records |
| Changed data per operation | 16 B | A numeric value and its complement |
| Encoded journal command | 32 B | Sequence, key, value and complement |
| Saved block | 4 KiB or 64 KiB | The unit read and replaced by the background worker |
| Worker payload-buffer cap | 32 MiB | Maximum reusable saved-block payload capacity, separate from the arena and other memory |

Uniform writes cycle through all keys. Hot writes target 256 scattered keys 90%
of the time and the full-key permutation for the remaining 10%. Each measured
scenario offers 300,000 changes over 30 seconds at 10,000/s. Two reverse-order
passes produce 20 scenarios. JournalOnly has no background application. Immediate
applies one foreground batch at a time, already combining within that batch.
Coalesced gathers up to 128 journal batches, nominally every 250 ms when caught up,
and continues immediately on a full batch or unit-budget split. The comparison
includes larger batching and fewer round trips as well as repeated-change collapse.

### Foreground writes and background progress

Each range covers two passes, not a confidence interval. Approximate background
rate is the applied count at foreground completion divided by that foreground
interval. It includes initial accumulation delay. Lag samples during the run are
also needed to distinguish bounded delay from a continually growing tail.

| Pattern | Mode | Saved block | Foreground successes/s | Background applied changes/s | Unapplied changes when foreground finishes | Refused arrivals per scenario |
| --- | --- | --- | --- | --- | --- | --- |
| Uniform | JournalOnly | 4 KiB | 9,999–10,000 | 0 by design | 300,000 | 0 |
| Uniform | Immediate | 4 KiB | 10,000 | 3,329–4,624 | 161,273–200,115 | 0 |
| Uniform | Coalesced | 4 KiB | 9,905–9,944 | 3,772–4,105 | 175,179–183,973 | 1,680–2,857 |
| Uniform | Immediate | 64 KiB | 9,977–10,000 | 386–614 | 281,575–287,719 | 0–692 |
| Uniform | Coalesced | 64 KiB | 8,975–9,685 | 332–436 | 259,286–277,487 | 9,440–30,744 |
| Hot | JournalOnly | 4 KiB | 9,999–10,000 | 0 by design | 300,000 | 0 |
| Hot | Immediate | 4 KiB | 9,909–10,000 | 5,129–5,444 | 133,931–146,117 | 0–2,742 |
| Hot | Coalesced | 4 KiB | 9,878–10,000 | 9,776–9,967 | 997–3,041 | 0–3,666 |
| Hot | Immediate | 64 KiB | 8,846–9,670 | 392–470 | 251,279–278,351 | 9,895–34,628 |
| Hot | Coalesced | 64 KiB | 9,983–10,000 | 1,567–1,757 | 247,202–252,986 | 0 |

Hot/Coalesced/4 KiB is the only main-comparison background configuration that
maintained a small tail in both passes. Sampled maximum lag was 5,212–7,918 changes;
the oldest sampled unapplied commit was 398–720 ms old. During the final roughly
ten seconds, sampled pending counts changed by only +26 and +1,028, rather than
the tens of thousands seen in falling-behind cases. The remaining tail drained
in 0.09–0.20 seconds after the foreground finished. Its final 997–3,041 changes
represent about 31–95 KiB of encoded command payload, excluding SQL/batch overhead.

This does not prove unconditional 10,000/s service: one pass refused 3,666 of
300,000 arrivals (1.22%) when the 4,096-item foreground queue filled. The other pass
accepted all arrivals. All other background configurations retained substantial
unapplied tails even after the approximately ten-second post-load drain budget.
An executing transaction sometimes extended that stop interval to 12.61 seconds.
Failed capacity is not a recovery failure: the saved base plus remaining journal
still reconstructed every acknowledged change correctly.

### Acknowledgment latency and what coalescing saved

These are intended-arrival-to-success p99 values, including foreground queueing,
batch collection, durable commit and native publication. They are not the duration
of a single UPDATE and must be read together with refusal counts.

| Pattern | JournalOnly p99 | Immediate, 4 KiB | Coalesced, 4 KiB | Immediate, 64 KiB | Coalesced, 64 KiB |
| --- | --- | --- | --- | --- | --- |
| Uniform | 5.10–6.67 ms | 43.87–176.87 ms | 432.77–458.30 ms | 200.43–306.08 ms | 1,083.05–1,867.30 ms |
| Hot | 5.85–89.84 ms | 29.28–404.57 ms | 189.39–511.36 ms | 1,102.85–2,413.63 ms | 70.07–243.52 ms |

The Hot journal-only baseline itself varied substantially, so these are observations
on this shared local system, not clean causal slowdown factors. The hot/coalesced
4 KiB journal-commit p99 alone was 2.67–4.01 ms, while maximum commit calls took
61–776 ms. Individual outliers and the resulting queues can still make end-to-end
p99 much larger. A low SQL-call percentile
does not establish low application acknowledgment latency.

For Hot/Coalesced/4 KiB, 296,334–300,000 applied changes required 59,131–60,008 saved
block replacements: approximately five changes per replacement. Payload written
by the worker was 231–234 MiB, with the same payload read back from saved units.
By comparison, Immediate/Hot/4 KiB replaced almost one unit per applied change.
Uniform/4 KiB had essentially no repeated keys or shared units within its tested
application windows, so enlarging the batch did not provide that saving.

At 64 KiB, even reduced replacement counts still generated large read/write work.
The small 32-byte journal command does not make a 64 KiB saved-block replacement
equally cheap. This experiment identifies saved-unit granularity and workload
repeat frequency as material variables; it is not PostgreSQL's maximum throughput.

### Memory: reusable workspace versus process memory

The following accounts for the worker's retained workspace object/arrays/maps and
reusable byte-array allocations. It excludes driver state, temporary SQL/journal
arrays, latency statistics and caller/transport state. It is not total worker RAM.

| Mode / pattern | 4 KiB saved blocks | 64 KiB saved blocks |
| --- | --- | --- |
| Immediate, either pattern | 0.44 MiB | 4.07 MiB |
| Coalesced, uniform | 20.56–23.51 MiB | 32.28 MiB |
| Coalesced, hot | 3.22–3.48 MiB | 32.28 MiB |

The 32 MiB cap covers payload buffers; control arrays add overhead and scale with
the prototype's unit/key counts. No second full 128 MiB arena is allocated by the
worker. Unapplied history stays in PostgreSQL. A growing tail therefore did not
allocate one retained managed request per journal entry.

For Hot/Coalesced/4 KiB, worker allocation traffic over the whole run was about
14 MiB, distinct from its roughly 3.2–3.5 MiB retained workspace. Sampled whole-client
working-set peaks were 231–286 MiB and managed-heap peaks 62–115 MiB. Those include
the 128 MiB arena, the synthetic reader, experiment statistics, writer queue,
driver and runtime heap. They exclude PostgreSQL/OS cache memory; runtime heap
retention across scenarios prevents attributing their differences solely to the
worker. No full GC was forced during load.

### Read measurements and limits

In the main comparison, the tiny native read probe stayed coherent and dropped
no latency samples. Sampled p99 was 0.1–0.8 microseconds. However, even the two quiet
read baselines differed from 20.46 to 59.93 million reads/s. Loaded results also
varied widely and sometimes exceeded their baseline. Those observations cannot
establish a reliable percentage change caused by background application.
This prompted a focused same-processor follow-up; it is reported separately below
instead of treating scheduler/frequency variation as an engine improvement.

### Focused same-processor reader comparison

**Measured on 12 November 2026, 19:59:12–20:03:21 UTC**, in
`.local/postgres-apply-read-measure.json`, after `postgres-apply-read-smoke.json`.
Eight 20-second scenarios compare JournalOnly and Coalesced at 4 KiB for both key
patterns, in two reverse-order passes. Only the dedicated read-probe thread is
restricted to logical processor 0; other threads and PostgreSQL are unpinned.
The full dataset remains 128 MiB and offered input remains 10,000 changes/s.

| Pattern / pass | JournalOnly reads, million/s | Coalesced reads, million/s | Paired throughput difference | JournalOnly / Coalesced sampled read p99 |
| --- | --- | --- | --- | --- |
| Uniform / 1 | 45.53 | 44.05 | -3.26% | 0.2 / 0.2 microseconds |
| Uniform / 2 | 39.61 | 36.34 | -8.26% | 0.2 / 0.3 microseconds |
| Hot / 1 | 38.82 | 39.15 | +0.84% | 0.3 / 0.3 microseconds |
| Hot / 2 | 31.59 | 30.38 | -3.82% | 0.4 / 0.5 microseconds |

The quiet-read baselines were 41.07 and 37.93 million/s. Pinning reduced the large
variation seen in the main comparison but did not fix CPU frequency or isolate
the machine. The table is an observed paired comparison, not a universal slowdown
factor. All reads remained coherent, with no dropped latency samples. This probe
reads and checks one tiny native root under its gate; it does not measure primary
key lookup, string materialization, OQL, document copies or scans of the 128 MiB image.

Hot/Coalesced accepted all 200,000 arrivals in each focused pass, kept 1,196–2,351
changes pending at foreground completion, and drained them in 0.13–0.21 seconds.
Its maximum sampled pending age was 336–356 ms; reusable workspace accounting was
2.52–2.73 MiB. End-to-end write p99 was 6.59–9.97 ms. The corresponding JournalOnly
p99 values were 6.47 and 118.43 ms, again showing variability even without an applier.

Uniform/Coalesced still fell behind: approximately 4,950–5,248 changes/s applied,
95,034–96,587 changes pending at foreground completion, and about 70,000 left after
drain. One pass refused 4,414 arrivals; the other accepted all. These results
reinforce the dependence on update locality. The shorter duration, different run
history and reader placement mean improved write latency cannot be attributed
solely to CPU affinity. The successful hot-key follow-up does not erase the main
comparison's refused arrivals and larger latency observations.

### Verification and decision status

All 20 main and eight focused scenarios passed exact journal membership/order, applied-prefix/count,
saved-record and unchanged-payload checks. The base and remaining tail matched the
latest native values. Preflight verified nonempty-tail recovery, rollback of both
unit updates and their cursor, and a reopened worker continuing from its durable
position. Release build, apply smoke, pinned-reader smoke and the
shared-native-publication regression smoke passed. PostgreSQL was not crashed or stopped.
The final default-path smoke is `postgres-apply-smoke-final.json`; all ten scenarios
passed after the reader-control addition. Final cleanup found zero owned databases,
prototype SQL connections and temporary file journals. Documentation checks cover
local links, decision counts, UTF-8/whitespace and reference-source separation.

No journal cleanup was performed during load; autovacuum remained enabled. Thus
these runs do not prove hours-long space equilibrium, crash recovery, replica
loading, production documents/indexes or remote/container performance. At measurement time the method was still open. The subsequent decision selects
background application with coalescing for v1. The 250 ms window, saved-unit sizes
and memory limits remain experimental; these results do not establish stable
10,000/s for arbitrary workloads or select the final saved-unit format.

## Sustained comparison of compact saved units

**Measured on 12 November 2026, 20:24:22–20:37:27 UTC.** The background method is
accepted for v1. Following this comparison, the decision selects 256 bytes as the
baseline PostgreSQL saved-unit size; remaining numerical limits are open. The workload owner
clarifies that **10,000 changes/s is a peak**, not the required ordinary sustained
rate on every machine. Holding it for three minutes is a stress comparison. A
bounded temporary application tail is acceptable if durable writes succeed and
the worker catches up after the peak within the chosen recovery/resource budgets.
Sustained demand at that level calls for deployment capacity planning, including
vertical scaling; this run did not measure scaling gains.

[Runnable comparison](../../src/Prototypes/PostgresStorageComparison/README.md#sustained-comparison-of-compact-saved-units)
owns commands and resource budgets. Ignored raw reports are
`postgres-apply-units-smoke.json` and `postgres-apply-units-measure.json`.

### Sizes, workload and what a number means

- **128 MiB** is the resident writer dataset: 134,217,728 bytes, comprising
  524,288 records of 256 bytes. It is neither journal volume nor network bandwidth.
- **256 B / 1 KiB / 4 KiB** is one replaced binary saved unit in PostgreSQL.
  These units contain 1 / 4 / 16 records of the identical logical dataset.
  They do not select native allocation segment/page sizes.
- Each operation changes **16 bytes** and appends a **32-byte journal command**.
  Uniform keys visit the entire key space before repeating; there is no hot subset.
- Each case offers **1,800,000 changes over 180 seconds**, scheduled at 10,000/s
  independently of completion. Producer scheduling p99 was 0.015–0.209 ms, so
  this is clock-paced offered load with measured scheduling jitter.

The local PostgreSQL 18.4 / Npgsql 10.0.3 / .NET 10 environment and durable settings
match the earlier comparison. There was one ordered pass: journal-only control,
then 256 B, 1 KiB, 4 KiB. Each case starts from the same initial image. A short
excluded warm-up precedes measurement; caches are not flushed. Only the dedicated
native reader is pinned to logical processor 0. CPU frequency, OS/server activity
and run-order effects are not isolated. A few small read-only progress observations
were made during the run. This is not a statistical comparison or a cloud result.

Foreground batching remains at most 64 changes with approximately 2 ms collection
and a 4,096-change admission queue. Overflow is explicitly rejected **before commit**;
these requests are not acknowledged changes subsequently lost. Background application
uses at most 128 journal batches, a nominal 250 ms interval when caught up, and a
32 MiB reusable payload cap. Full batches continue immediately. Retained journal
rows are not deleted during load; PostgreSQL autovacuum remains enabled.

### Durable foreground writes

The rate below counts successful journal commit plus native publication, including
final foreground drain. p99 means 99% of successful requests completed within that
time, measured from intended arrival; refused requests are counted separately.

| Saved unit / control | Completed | Refused | Successful changes/s | Write p99, ms | Longest successful write, ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Journal only | 1,800,000 | 0 | 10,000 | 7.48 | 217.97 |
| 256 B | 1,800,000 | 0 | 10,000 | 7.92 | 182.07 |
| 1 KiB | 1,786,488 | 13,512 | 9,925 | 509.96 | 1059.74 |
| 4 KiB | 1,728,419 | 71,581 | 9,601 | 710.49 | 4323.36 |

The 256 B case accepted every arrival; its overall p99 was close to the control.
That does not mean every minute was equally smooth. The table below groups requests
by **intended-arrival minute**, including their eventual completion after that minute.

| Case | Minute 1: p99 ms / refusals | Minute 2: p99 ms / refusals | Minute 3: p99 ms / refusals |
| --- | ---: | ---: | ---: |
| Journal only | 8.16 / 0 | 7.33 / 0 | 7.10 / 0 |
| 256 B | 27.65 / 0 | 7.56 / 0 | 6.68 / 0 |
| 1 KiB | 6.01 / 0 | 648.92 / 13,512 | 6.53 / 0 |
| 4 KiB | 687.30 / 12,479 | 793.73 / 13,647 | 685.75 / 45,455 |

The 1 KiB refusals occurred entirely in the second minute, followed by recovery.
Its median remained 2.14 ms, while its worst successful request took 1.06 s.
For 4 KiB, refusals occurred in every minute and the worst request took 4.32 s.
These observations demonstrate interference/overload on this setup; they do not
identify its exact OS, GC or PostgreSQL cause. No causal profiling or vertical
scaling experiment was performed.

### Application lag while writes continue

A pending change is **already durably accepted**, but not yet incorporated into
saved units. It stays recoverable in the on-disk journal and increases later replay
work. It differs from the bounded queue of requests waiting for their first commit.
Minute boundaries use the closest 250 ms observation; the final column uses the
separately captured foreground-completion boundary.

| Saved unit | Pending at about 60 s | Pending at about 120 s | Pending at writer end | Largest sampled pending count | Oldest pending age: maximum, s |
| --- | ---: | ---: | ---: | ---: | ---: |
| 256 B | 520 | 1,135 | 2,628 | 7,193 | 0.721 |
| 1 KiB | 2,366 | 1,093 | 2,315 | 34,886 | 4.177 |
| 4 KiB | 298,829 | 595,932 | 865,398 | 865,398 | 92.386 |

256 B kept a small bounded observed tail throughout this run; the last 2,628 changes
were applied in approximately **63 ms** after input ended. 1 KiB temporarily reached
34,886 pending changes, then recovered; its last 2,315 changes drained in **122 ms**.
Its applier kept pace over the run with the **accepted** stream, which was smaller
than the offered stream because of foreground refusals.

4 KiB applied about **4,794 changes/s while input continued**. Its tail grew from
roughly 299,000 to 596,000 to 865,000 across the three minutes. At writer completion
the oldest pending commit was about 92 seconds old. After the nominal ten-second
drain window, **825,432** remained; actual drain elapsed was 10.85 s because a running
operation finishes before cancellation. This case was stopped with a valid
recoverable tail, not allowed to run indefinitely until it caught up. Its final
pending command payload was about 25.2 MiB before SQL/framing overhead.

The journal-only control intentionally applies nothing and ends with 1.8 million
pending changes. That is its measurement role, not failed recovery. Applied but
retained history must not be counted as pending replay.

### Native reads and memory

| Case | Native reads, million/s | Change versus control | Sampled read p99, microseconds | Retained worker workspace, MiB | Peak sampled client working set, MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Journal only | 17.59 | +0.0% | 0.4 | 0.00 | 261.3 |
| 256 B | 16.90 | -3.9% | 0.4 | 3.86 | 385.9 |
| 1 KiB | 17.35 | -1.4% | 0.4 | 9.47 | 377.0 |
| 4 KiB | 15.49 | -11.9% | 0.5 | 25.47 | 429.3 |

Reads are the synthetic locked value/complement probe, not document decoding,
GetById or OQL. All reads check integrity; one in 65,536 is timed. No samples were
dropped. The relative rates are observations from a single ordered pass; the
smaller loss at 1 KiB does not prove it intrinsically reads faster than 256 B.

Workspace includes retained unit buffers and change/key/unit lookup arrays.
Its reusable payload component was **0.99 / 8.00 / 24.42 MiB** for 256 B / 1 KiB /
4 KiB. Cumulative worker allocation traffic was **83.7 / 88.9 / 64.8 MiB**, respectively;
that is bytes allocated over time, not simultaneously retained RAM. The 4 KiB worker
processed fewer changes, so its smaller allocation total is not better efficiency.

Whole-client working set includes the 128 MiB writer image, separate 8 MiB native
reader data, managed gates, roughly 43 MiB of per-request measurement arrays,
sample buffers, driver state and runtime/retained allocator pages. No forced GC was
used between scenarios. Peak sampled managed heap was 76.6 / 96.9 / 102.7 / 244.9 MiB
in table order; it includes uncollected objects and is not retained workspace.
Subtracting process peaks does not isolate applier RAM. PostgreSQL and OS cache RAM
are excluded. There is no mandatory second full arena and the tail is not mirrored
as an unbounded in-process queue. This run does not establish indefinite memory
or disk equilibrium.

### Saved-unit traffic and coalescing

| Saved unit | Changes applied, including drain | Unit replacements | Payload read, MiB | Payload written, MiB | Global WAL delta, MiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| 256 B | 1,800,000 | 1,800,000 | 439.5 | 439.5 | 857.9 |
| 1 KiB | 1,786,488 | 1,786,488 | 1744.6 | 1744.6 | 1560.4 |
| 4 KiB | 902,987 | 858,146 | 3352.1 | 3352.1 | 6063.7 |

With uniform keys, 256 B and 1 KiB performed one unit replacement per applied change;
there were no same-key repeats inside the short application window. 4 KiB combined
some same-unit changes, reducing replacements by about 5%, but still moved much more
payload per applied change. These are summed SQL payload bytes in each direction,
not measured physical disk I/O or peak RAM. The 4 KiB row covers only the applied
subset. PostgreSQL WAL adds its own traffic; the global delta can include background
server work. The control's WAL delta was about 67.9 MiB.

### Verification and interpretation for peak demand

All four long cases passed saved-unit contents, unchanged filler, exact acknowledged
journal membership/order, applied-boundary/count and reconstructed-state checks.
Preflight at every saved-unit size verified nonempty-tail recovery, rollback of
both data and applied position, and worker reopen/resume. The four-case compact
smoke passed, as did the original journal smoke and all ten original-layout
background smoke scenarios after layout parameterization. Release build completed
with zero warnings/errors. Documentation checks passed for 30 Markdown documents,
339 local links, decision counts, UTF-8/whitespace and reference-source separation.
Final cleanup found zero owned databases, prototype SQL connections and temporary
file journals. No acknowledged mutation was lost and no torn native read was found.
The PostgreSQL service was not stopped; actual server-crash/uncertain-commit recovery,
general document operations, indexes and concurrent replica loading remain untested.

The accepted background method remains appropriate: a temporary durable tail during
a peak is not itself a failure. Size deployment capacity for ordinary traffic and
use the peak's duration, tail-growth rate and later spare application capacity to
bound catch-up/recovery time. Foreground rejection/latency is a separate criterion;
a larger disk journal cannot absorb a request that has not yet committed.

**Decision after this experiment:** the decision selects 256 B as the baseline
PostgreSQL saved unit for v1. It was the strongest measured option for uniformly
scattered tiny changes on this machine. The 1 KiB comparison caught up, but its
foreground stall remains relevant when interpreting other workloads or later changes. 4 KiB needs substantially more application
capacity, less demanding normal traffic, better locality or a different physical
implementation. This stress result does not exclude it for other workloads, and
it does not prove that CPU/RAM increases alone will fix storage limitations.
The accepted 256-byte baseline does not claim universal optimality, settle native
segment size or accept the remaining prototype timing/memory defaults.

## Primary index collection comparison

**Measured on 13 November 2026.** The runnable
[PrimaryIndexComparison](../../src/Prototypes/PrimaryIndexComparison/README.md) compares
Faster.Map 9.0.3 and Arc.Collections 2.2.0, with no custom hash-table implementation.
This is an isolated collection benchmark, not database throughput.

**Timing status: preliminary.** The shared workstation was reported severely
unresponsive during the run. A short inspection after completion found other active
test and IDE processes, but no remaining prototype worker. No system-wide profiling
established the cause or the overlap throughout the run. All timing values below
remain observations under uncontrolled interference, not grounds for a production
speed ranking. No further benchmarks were started after the report. Retained memory
repeated exactly across the three processes for each case.

### Setup and interpretation

Windows x64 build 26300; Intel Core i9-13900H; approximately 64 GiB physical RAM;
.NET SDK 10.0.404 and runtime 10.0.15. Each worker used workstation GC, disabled tiered
compilation and the normal OS scheduler without processor affinity. Only one worker
from this comparison ran at a time.

The full run completed 72 verified workers in 321 seconds: four maps, three key
kinds, two sizes and three fresh processes per combination. Each process measured
five shuffled passes per operation after warmup. Keys were unique deterministic
Int64, Guid or 20-character strings; values were 8-byte locators. String hits used
equal text in different objects. Package load factors were left at their defaults.

A MiB below means 1,048,576 bytes of index storage. Live-heap deltas after full GC
exclude precreated key objects, probe arrays, root objects and runtime baseline.
For string indexes this means text payload is excluded, while stored references
are included. The report does not claim total database or process memory.

### Retained index memory

Each row compares the same live entry count. String index storage matched Int64
storage on this x64 setup, excluding the additional string objects as described above.

| Entries | Collection | Public capacity | Int64 / String index, MiB | Bytes per entry | Guid index, MiB | Guid bytes per entry |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 100,000 | Arc UnorderedMapSlim | 131,072 | 3.50 | 36.70 | 4.50 | 47.19 |
| 100,000 | Faster DenseMap | 131,072 | 2.13 | 22.28 | 3.13 | 32.77 |
| 100,000 | Faster BlitzMap | 131,072 | 2.60 | 27.26 | 3.40 | 35.65 |
| 100,000 | Arc UnorderedMap | 131,072 | 4.50 | 47.19 | 5.50 | 57.67 |
| 1,000,000 | Arc UnorderedMapSlim | 1,048,576 | 28.00 | 29.36 | 36.00 | 37.75 |
| 1,000,000 | Faster DenseMap | 2,097,152 | 34.00 | 35.65 | 50.00 | 52.43 |
| 1,000,000 | Faster BlitzMap | 2,097,152 | 41.60 | 43.62 | 54.40 | 57.04 |
| 1,000,000 | Arc UnorderedMap | 1,048,576 | 36.00 | 37.75 | 44.00 | 46.14 |

At 100,000 entries, all four public capacities were 131,072 and DenseMap retained
the least storage. At one million, Faster maps had already expanded to 2,097,152,
while Arc maps remained at 1,048,576. UnorderedMapSlim then used the least memory:
28 MiB for Int64 versus DenseMap's 34 MiB; for Guid the comparison was 36 versus
50 MiB. This reversal reflects actual default capacity policies. It is not evidence
that either package has a universally smaller index or that bytes per entry remain
constant at arbitrary counts. Public capacity is not interchangeable with maximum
entry count before growth across these implementations.

### Temporary memory at the measured resize

These are the last growth events encountered on the way to one million entries.
The entry count is **immediately before** the single insertion causing growth; the
candidates do not resize at the same count. Int64 and String storage totals matched.

| Collection | Entries before growth | Old capacity -> new capacity | Int64 / String: old + new buffers, MiB | Guid: old + new buffers, MiB |
| --- | ---: | --- | ---: | ---: |
| Arc UnorderedMapSlim | 524,288 | 524,288 -> 1,048,576 | 42.00 | 54.00 |
| Faster DenseMap | 917,504 | 1,048,576 -> 2,097,152 | 51.00 | 75.00 |
| Faster BlitzMap | 838,860 | 1,048,576 -> 2,097,152 | 62.40 | 81.60 |
| Arc UnorderedMap | 524,288 | 524,288 -> 1,048,576 | 54.00 | 66.00 |

For example, UnorderedMapSlim's Int64 event retains about 14 MiB of old storage and
allocates about 28 MiB of replacement storage: **42 MiB together**, then about 28 MiB
after collection. The equivalent DenseMap event is 17 + 34 = **51 MiB**. These figures
exclude key text and objects, rather than describing the entire process.

The harness disables GC only during this insertion, using a bounded 512 MiB
allocation budget, and adds its measured allocation to the retained pre-growth index.
This prevents collection from hiding old buffers. The independent live-heap reading
matched that total in all 72 cases. The allocation budget itself is not index memory.
This is a controlled old-plus-new managed storage peak, not sampled RSS, total GC
commitment or the peak of a complete build with naturally timed collections.

At 100,000 entries the corresponding Int64/String peaks were 5.25 MiB for ArcSlim,
3.19 for Dense, 3.90 for Blitz and 6.75 for Arc. Guid peaks were 6.75, 4.69, 5.10 and
8.25 MiB in the same order. Peak storage also depends on the growth boundary.

### Observed operation timing: preliminary

All numbers below are **nanoseconds per operation**, lower is faster. Hit means an
existing-key lookup, miss means an absent-key lookup, and update replaces an existing
8-byte locator. Build includes empty-map allocation and all insertions/resizes, divided
by inserted count. It is not transaction latency or changing an arena object.

Each hit/miss/update value is the median of three process medians, each obtained from
five whole-pass averages. There are no per-request percentile claims. Build has one
measured full build per process, summarized by its median. Inputs, validation and
result serialization are outside the timed regions.

| Entries | Key kind | Collection | Hit, ns | Miss, ns | Update, ns | Build, ns/insert |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 100,000 | Int64 | Arc UnorderedMapSlim | 10.2 | 11.5 | 14.5 | 25.5 |
| 100,000 | Int64 | Faster DenseMap | 6.0 | 6.0 | 6.7 | 27.4 |
| 100,000 | Int64 | Faster BlitzMap | 10.6 | 9.6 | 14.0 | 48.8 |
| 100,000 | Int64 | Arc UnorderedMap | 12.4 | 13.7 | 16.9 | 47.1 |
| 100,000 | Guid | Arc UnorderedMapSlim | 14.6 | 17.7 | 23.1 | 34.0 |
| 100,000 | Guid | Faster DenseMap | 7.1 | 7.5 | 8.1 | 30.0 |
| 100,000 | Guid | Faster BlitzMap | 12.5 | 12.7 | 19.7 | 45.6 |
| 100,000 | Guid | Arc UnorderedMap | 13.4 | 15.3 | 17.3 | 42.2 |
| 100,000 | String | Arc UnorderedMapSlim | 129.7 | 113.5 | 153.9 | 60.4 |
| 100,000 | String | Faster DenseMap | 396.9 | 191.1 | 390.5 | 86.8 |
| 100,000 | String | Faster BlitzMap | 149.3 | 125.1 | 303.8 | 187.0 |
| 100,000 | String | Arc UnorderedMap | 174.2 | 153.0 | 166.3 | 78.6 |
| 1,000,000 | Int64 | Arc UnorderedMapSlim | 43.4 | 37.2 | 61.0 | 48.4 |
| 1,000,000 | Int64 | Faster DenseMap | 31.5 | 17.0 | 36.5 | 36.4 |
| 1,000,000 | Int64 | Faster BlitzMap | 34.1 | 22.6 | 52.6 | 73.9 |
| 1,000,000 | Int64 | Arc UnorderedMap | 48.5 | 45.5 | 58.8 | 54.5 |
| 1,000,000 | Guid | Arc UnorderedMapSlim | 47.7 | 53.5 | 70.3 | 52.1 |
| 1,000,000 | Guid | Faster DenseMap | 34.2 | 22.4 | 44.3 | 38.0 |
| 1,000,000 | Guid | Faster BlitzMap | 43.9 | 27.9 | 75.8 | 96.7 |
| 1,000,000 | Guid | Arc UnorderedMap | 54.9 | 42.6 | 67.8 | 64.7 |
| 1,000,000 | String | Arc UnorderedMapSlim | 565.0 | 427.0 | 566.6 | 140.2 |
| 1,000,000 | String | Faster DenseMap | 582.3 | 434.8 | 518.3 | 197.7 |
| 1,000,000 | String | Faster BlitzMap | 419.2 | 423.2 | 1072.4 | 415.8 |
| 1,000,000 | String | Arc UnorderedMap | 418.0 | 459.5 | 511.7 | 131.9 |

Variation is material, especially for strings. The ranges below are the smallest
and largest **process medians**, not confidence intervals or individual-request
extremes, at one million entries.

| Collection | Int64 hit, ns | String hit, ns | String update, ns |
| --- | ---: | ---: | ---: |
| Arc UnorderedMapSlim | 42.4 - 52.4 | 295.9 - 728.0 | 329.6 - 589.2 |
| Faster DenseMap | 29.7 - 48.1 | 551.5 - 2499.0 | 485.5 - 1797.6 |
| Faster BlitzMap | 32.8 - 34.6 | 416.4 - 507.5 | 1003.4 - 3157.7 |
| Arc UnorderedMap | 44.8 - 53.3 | 313.0 - 594.0 | 423.8 - 653.4 |

The timed lookup/update loops allocated zero managed bytes in every measured sample.
This excludes input creation, index construction, growth and result records. The
numeric timings suggest DenseMap is worth further evaluation, but the observed
interference prevents an accepted performance ranking.

Resize durations below time one growth-triggering insertion, in milliseconds. The
no-GC setup/teardown is excluded. These observations share the timing limitation and
must not become engine pause guarantees.

| Collection | Int64 resize, ms | Guid resize, ms | String resize, ms |
| --- | ---: | ---: | ---: |
| Arc UnorderedMapSlim | 4.36 | 5.64 | 14.07 |
| Faster DenseMap | 12.04 | 10.21 | 240.62 |
| Faster BlitzMap | 17.28 | 18.75 | 300.66 |
| Arc UnorderedMap | 7.29 | 8.24 | 13.79 |

### Verification and implications

Release build completed with zero warnings/errors before the comparison. The
standalone correctness command passed 108 map/key/size combinations. The 12-worker
10,000-entry smoke and all 72 full workers passed exact contents, count, missing-key,
update, deletion/reinsertion and resize checks. No worker timeout occurred. After
completion, inspection found no remaining PrimaryIndexComparison worker. Builds and
benchmarks were not repeated after the workstation slowdown was reported.

**Recommendation after the initial timing comparison:** retain UnorderedMapSlim and DenseMap as the leading candidates
for the next integration decision, with no production package selected yet.
UnorderedMapSlim has the smaller measured footprint at one million entries; DenseMap
has the smaller footprint at 100,000 and promising numeric timings. BlitzMap and
UnorderedMap remain measured alternatives; the current evidence does not establish
one winner for all key types and capacities.

The next selection gate is compatibility with the accepted engine contracts:
pre-commit capacity preparation, safe concurrent root acquisition and publication,
and lookup using native string representations without permanent text duplication.
The inspected APIs did not expose `EnsureCapacity` on these four concrete types;
constructors accept capacity, but safe growth of an existing index is not established
by this experiment. No custom growth mechanism was implemented. Default managed
string keys do not validate schema-defined OrdinalIgnoreCase or native UTF-8 lookup.

This experiment does not measure whole-engine RAM, sustained growth/shrink,
concurrent readers/writers, moving compaction, SQL durability or replication. A quiet
machine rerun is required before making a timing-based choice. The primary hash
structure decision is unchanged; standard .NET Dictionary remains excluded.

The subsequent bounded checks below resolve the initial shortlist through integration
properties, without claiming a timing winner or rerunning the full comparison.

## Primary index integration and selection

**13 November 2026: select Arc.Collections 2.2.0 UnorderedMapSlim for the first primary
index implementation.** The [decision owner](../decisions/index-structures.md) records
the package boundary, capacity strategy and remaining engine work. No custom hash
table, package fork or production engine was implemented in this investigation.

### Exact-version source inspection

The source commits match the assembly informational versions recorded by the original
benchmark: Arc `d9edfc9db902a8eea1505031b0f4cbfea90f921b` and Faster
`c8e847e835926d959aab82839c79f2444a4aa194`.

| Property | Arc UnorderedMapSlim | Faster DenseMap |
| --- | --- | --- |
| Key integration | Default equality and key GetHashCode; a value-type key can supply native content semantics | Pluggable struct hasher/equality; native content semantics are also possible |
| Available capacity | Allocated node slots; removals return slots to a free list | Insert threshold includes live entries and tombstones |
| Deletion maintenance | Unlinks and reuses nodes without table allocation | Can allocate replacement buffers for tombstone cleanup during Remove |
| Public advance reservation | No EnsureCapacity; constructor plus copying into an equivalent map is available | No EnsureCapacity; constructor/copy available, with additional tombstone maintenance to coordinate |
| Hardware requirement in constructor | No Vector256 acceleration requirement | Rejects construction when Vector256.IsHardwareAccelerated is false |
| Concurrency | Engine must synchronize structural mutation and lookup | Engine must synchronize structural mutation and lookup |

Sources: [ArcSlim](https://github.com/archi-Doc/Arc.Collections/blob/d9edfc9db902a8eea1505031b0f4cbfea90f921b/Arc.Collections/Collection/UnorderedMapSlim.cs),
[DenseMap](https://github.com/Wsm2110/Faster.Map/blob/c8e847e835926d959aab82839c79f2444a4aa194/src/Core/DenseMap.cs).
Arc's narrower maintenance behavior is a better fit for preparing resources before
durable commit. DenseMap can still be adapted; its favorable numeric timing is not
enough to justify more integration work and the hardware restriction for this v1.

### Bounded checks and observed results

The [integration and platform commands](../../src/Prototypes/PrimaryIndexComparison/README.md#bounded-integration-checks)
are separate from the large performance comparison. The run used the same .NET 10.0.15
Windows x64 environment, at most 600 live entries per map and two writer tasks. Test
workers ran sequentially at below-normal process priority, with a 20-second process
deadline; the final integration invocation completed in under one second including
its launcher. The runtime processor count was limited to two and tiered compilation
was disabled. No SQL server or external dataset was used.

| Check | Observed result |
| --- | --- |
| UTF-8 native key views | Both maps passed 28 text/comparison cases, including Unicode, empty text, embedded NUL and Ordinal/OrdinalIgnoreCase behavior checked against .NET |
| Lookup allocation | Both maps allocated 0 managed bytes across the measured warmed lookup loops |
| Descriptor size | The fixture's pointer/length/comparison value type occupied 16 bytes on x64 |
| Text relocation | Removing the old descriptor and installing the replacement before freeing old native text preserved lookup in both maps; single-threaded check |
| Forced collisions and deletion | Both preserved exact contents after inserting 600 same-hash keys and removing 200 |
| Allocation during those removals | Arc: 0 bytes; Dense: 17,488 bytes, with capacity unchanged at 1,024 slots |
| Arc preparation | Starting with 15 entries, reserving eight insertions built an equivalent 32-slot index; published count stayed 15 and pending keys were absent |
| Reserved publication | Eight additions after the simulated commit allocated 0 managed bytes |
| Failure/cancellation | Capacity-budget rejection left contents/credits unchanged; definitive pre-commit cancellation released credits |
| Reuse | 16 cycles deleting/reinserting 23 entries retained 32 slots and allocated 0 managed bytes in the warmed loop |
| Concurrent reservations | Two writers reserved eight slots each without overbooking; old records remained readable during the simulated commit wait; all 31 final locators were correct |
| Hardware-disabled process | With DOTNET_EnableHWIntrinsic=0, Arc lookup succeeded and Dense construction raised the expected NotSupportedException |

Both the initial and final bounded integration runs passed; the latter followed a
lock-scope correction so replacement copying does not hold the reader-excluding gate.
Release builds completed with zero warnings/errors. No large benchmark was repeated,
and no database engine speed claim follows from these checks. Disabling intrinsics
is a compatibility probe, not an execution test on Linux or ARM64.

### How reservation fits the write path

The prototype's capacity owner uses an unchanged Arc map. Index mutations serialize
under a mutation mutex; a separate reader/writer gate protects published access.
The acquisition order is mutation mutex then access gate; readers take only the gate.
Before journal submission, required slots include live entries, outstanding credits
and the new request. Growth constructs and fills a replacement while readers can use
the old map. A brief exclusive gate installs equivalent physical metadata and credits.
No lock spans the simulated storage wait. After the commit boundary, publication
consumes credits and does not trigger package growth in the tested scenario.

This avoids inserting invisible placeholder records into the live map. Only the
index metadata is copied; it does not create a second native arena or copy object
payloads. Large growth still costs O(n) work, delays index mutations and temporarily
retains old/new arrays. Its production duration and GC retention must be budgeted;
the small test does not establish a pause bound. Deleted slots are reused, but
shrinking the allocated arrays requires an explicit engine-owned rebuild.

### Scope of the selection

The package choice is settled for the first implementation. Remaining checks belong
to that implementation: complete root retention and delete/reinsert lifetimes,
moving/chunked native keys, explicit UTF-16 mode, WriteBatch publication, uncertain
commit reservations, memory accounting including GC retention, accepted traversal
semantics, and target-platform correctness/performance. Reopen the package choice
only if one of those reveals a concrete incompatibility.

The UTF-8 key fixture accepts at most 1,024 bytes and uses bounded stack decoding for
case-insensitive comparison. That limit is not a database requirement, and the fixture
is not the final large-string algorithm. Its raw pointer must remain protected until
the map entry is repaired or removed. Numeric capacity reservation and native key
lookup were tested separately; the integrated moving-arena protocol is not proven.

The earlier 28 MiB result describes one million 8-byte numeric keys plus 8-byte
locators. It is not the budget for 16-byte native text descriptors. Based on the
inspected node layout and that measured capacity, a 16-byte descriptor would require
about 36 MiB of Arc index buffers, plus the text in the arena; this is a layout-derived
estimate, not a new million-entry run. Public schema, persisted data and the replica
protocol remain independent of the package because physical indexes are rebuilt.

## Bounded secondary-index comparison

**13 November 2026:** the [standalone prototype](../../src/Prototypes/SecondaryIndexComparison/README.md)
compared Arc.Collections 2.2.0 OrderedMultiMap and Kaos.Collections 4.1.0 RankedMap.
The comparison recommended Arc for the first ordered secondary implementation,
with higher retained memory explicitly disclosed. **Subsequently accepted on
13 November 2026:** Arc OrderedMultiMap for v1, including that memory tradeoff.
The [decision owner](../decisions/index-structures.md#secondary-index-arc-orderedmultimap)
records the current contract; full engine integration remains unproven.

### Scope and resource use

The run used .NET SDK 10.0.404/runtime 10.0.15 on Windows x64 build 26300 and an
Intel Core i9-13900H. Every map held 10,000 entries: a 16-byte two-Int64 key and an
8-byte locator. `Unique` had 10,000 distinct keys; `Repeated` had 100 distinct keys
with 100 locators each. Input order was deterministic and shuffled. No production
data, native document arena, PostgreSQL, string key or custom collection was used.

Twelve timing workers and three preparation/check workers ran sequentially with
BelowNormal priority, workstation GC, runtime processor count two and tiered
compilation disabled. Each had a 20-second deadline and a 150 ms gap afterward.
The complete runner took about eight seconds; summed worker CPU time was 3.45 s.
The longest worker took 0.54 s wall time. No large/stress benchmark was run, no
deadline was reached, and no secondary-index worker remained afterward.

Each timing process warmed the type on 256 entries, then took five short samples
per operation after two operation warmups. Tables report ranges of the three
process medians. The small working set and shared workstation make timings
preliminary; they are not sustained throughput, p99 or engine latency claims.
Retained-memory and allocation results repeated identically across processes.

### Retained index memory and allocations

Retained bytes mean live managed heap delta after forced GC. They include the map's
keys, locators, metadata and spare capacity but exclude fixtures and database objects.
They are neither process RSS nor peak allocation. Allocated bytes include temporary
navigation objects that may already have been collected.

| Index / distribution | Retained bytes | Bytes per entry | Allocated while building |
| --- | ---: | ---: | ---: |
| Arc / unique | 880,056 | 88.01 | 880,056 |
| Kaos / unique | 381,856 | 38.19 | 2,465,072 |
| Arc / repeated | 880,056 | 88.01 | 880,056 |
| Kaos / repeated | 333,736 | 33.37 | 2,416,952 |

Arc uses about 2.3–2.6 times the retained memory here. The benefit of Kaos's compact
B+ tree is real in this fixture; choosing Arc does not remove that cost. These
figures describe one declared index, not a multiplier for total database RAM.

| Operation | Arc allocated bytes | Kaos allocated bytes |
| --- | ---: | ---: |
| Point hit or miss | 0 per lookup | 0 per lookup |
| Range traversal | 0 per query | 104 per query |
| Change a unique search key | 0 per change | 416 per change |
| Change a repeated search key | 0 per change | 447.33 per change, sample average |

The mutation measurements include finding the old key/locator. No permanent node
handle side table is included. Arc reuses the found node; Kaos removes the old pair
and inserts the new pair. Prepared nodes for new records are a separate experiment.

### Read, build and update time

All values in this table are **nanoseconds per operation**, not database requests.
A range query sums every matching locator. The unique range visits eight entries;
the repeated range visits 800. Compare maps within a row, not those two workloads
against each other. Point hits return one matching locator, not an entire group.

| Distribution / operation | Arc, ns/op | Kaos, ns/op |
| --- | ---: | ---: |
| Unique / initial insertion | 146.6–234.9 | 341.4–491.5 |
| Unique / hit | 79.2–86.4 | 126.9–208.5 |
| Unique / miss | 18.6–41.0 | 41.8–59.3 |
| Unique / range of eight entries | 65.6–92.2 | 198.4–300.0 |
| Unique / key change | 248.0–325.4 | 570.7–787.9 |
| Repeated / initial insertion | 53.0–75.4 | 300.2–713.8 |
| Repeated / hit | 28.6–67.8 | 115.0–142.1 |
| Repeated / miss | 9.6–19.6 | 49.4–72.9 |
| Repeated / range of 800 entries | 6,373.4–14,090.6 | 11,134.4–13,115.6 |
| Repeated / key change | 962.9–1,368.8 | 898.6–2,410.7 |

Short range queries reuse only 64 bounds, while point samples contain 4,096 queries;
cache warmth and noise can make the small range result appear faster than a point
lookup. This is not a claim that traversal does less work than finding one entry.
Duplicate-heavy range/update intervals overlap; no stable winner is established
there. Duplicate-group searches are part of both update adapters.

### Prepare first, publish after the simulated commit

Each map initially contained 10,000 unique entries. Prepare 32 key relocations,
eight deletions and eight insertions. Verify the old map remains exactly unchanged,
then publish and verify every resulting key/locator and its ordering. Times below
are **microseconds for the entire 48-change batch**, not per entry.

| Metric | Arc: detached nodes and mutation handles | Kaos control: full private index copy |
| --- | ---: | ---: |
| Preparation time, three processes | 3.9–9.1 µs | 1,250.1–1,464.4 µs |
| Preparation allocation | 1,920 bytes | 2,359,856 bytes |
| Additional retained memory before publication | 1,920 bytes | 259,952 bytes |
| Publication time | 7.6–15.8 µs | Reference assignment, below meaningful timing resolution |
| Managed allocation during publication | 0 bytes | 0 bytes |

Arc creates insertion nodes in a private staging map and detaches them; key/value
data remains separately in the prepared plan. Existing moves/removals use node
handles. The live index is never temporarily populated with placeholder records.
Kaos prepares a complete replacement containing all entries and changes, then swaps
a reference. Its sequential copy is more densely filled than the earlier shuffled
build, explaining the smaller copy size. Additional retained bytes are not the
transient allocation peak; the copy also created temporary navigation objects.

This control is intentionally not a proposed Kaos production strategy: copying
an entire index per batch is O(n), holds another index and requires coordination
against competing writes. The inspected package API offers no reservation of its
mutation paths or splits; its Capacity property controls tree branching factor.
Both paths still need engine synchronization. The measured zero-allocation reference
assignment does not prove atomic database publication or concurrent correctness.

### Correctness findings and decision boundary

Release builds succeeded with zero warnings/errors. All 12 measured workers passed
point/range results, counts, ordering and exact final key/locator checks. All three
preparation workers preserved old contents until publication and produced the exact
expected result with zero managed publication allocation. An initial harness check
caught use of cleared values on detached Arc nodes; the plan now stores those values
separately, and the complete reported run used that corrected adapter.

**A separate Kaos API check failed reproducibly:** inserting keys 0–299 and enumerating
`ElementsBetween(10, 299)` raised ArgumentOutOfRangeException instead of returning
290 records. `ElementsFrom(10)` with an explicit upper bound returned all expected
records. That lower-bound path is the measured Kaos adapter; the convenience-method
failure is recorded, not counted as a passing check. The inspected v4.1.0 source's
range iterator continues using the initial leaf's key count after advancing leaves.

Exact sources:
[Kaos RankedMap 4.1.0](https://github.com/kaosborn/KaosCollections/blob/2500a36dbae3f7844bd7abca644e0eafd070d979/Source/RankedMap/RankedMap.cs),
[Kaos NodeVector](https://github.com/kaosborn/KaosCollections/blob/2500a36dbae3f7844bd7abca644e0eafd070d979/Source/Btree/Btree.NodeVector.cs),
[Arc OrderedMultiMap 2.2.0](https://github.com/archi-Doc/Arc.Collections/blob/d9edfc9db902a8eea1505031b0f4cbfea90f921b/Arc.Collections/Collection/OrderedMultiMap.cs).
The Kaos source tag corresponds to the evaluated 4.1.0 package; newer repository
code was not substituted for it.

**Recommendation at the time of measurement, subsequently accepted:** prefer Arc's public node reuse and smaller integration scope for
v1, explicitly accepting its higher retained index memory. More large-scale timing
runs are not needed to distinguish these package integration properties. Subsequent
checks belong to the engine: native/composite keys, schema comparison rules, mutation
ownership across the commit boundary, reader/root lifetime, WriteBatch publication,
traversal during updates and budgeted reclamation. These results do not prove those
contracts, PostgreSQL performance or production readiness.

## Bounded root-entry storage comparison

**Measured on 13 November 2026.**
[RootEntryComparison](../../src/Prototypes/RootEntryComparison/README.md) compares managed
arrays of structs with native pages, using the same 32-byte synthetic entry and
4,096-entry (128 KiB) page. Both use a managed directory that doubles independently
of the existing entry pages. There are no database objects, indexes or working locks.
Neither entry nor page size is a selected production default.

The final run used Windows x64, .NET SDK 10.0.404/runtime 10.0.15, Release compilation,
workstation GC, one runtime processor, disabled tiered compilation and BelowNormal
priority. Twelve workers ran sequentially: two sizes, two backends and three fresh
processes per case, with backend order alternated. Each operation had two warmups
and seven samples of 262,144 entry accesses. Inputs are synthetic and deterministic.

### Memory and incremental growth

Values below are **bytes of table storage**, not the whole process. Managed storage
is allocation-counter accounting around owner/page/directory creation, with replaced
directories removed from the retained estimate. Native totals add requested page
bytes; native allocator overhead and GC spare capacity are not included.

| Metric | Managed pages | Native pages |
| --- | ---: | ---: |
| 32,768 entries: payload | 1,048,576 | 1,048,576 |
| Accounted retention | 1,048,904 | 1,048,704 |
| Additional allocation at growth | 131,248 | 131,224 |
| Accounted growth peak upper estimate | 1,180,152 | 1,179,928 |
| 262,144 entries: payload | 8,388,608 | 8,388,608 |
| Accounted retention | 8,390,728–8,398,392 | 8,389,184 |
| Additional allocation at growth | 132,144 | 132,120 |
| Accounted growth peak upper estimate | 8,522,872–8,530,536 | 8,521,304 |

At the larger size, adding one page changes capacity from 262,144 to 266,240 entries
and directory capacity from 64 to 128. The incremental peak is approximately **129 KiB**
in both cases, rather than another 8 MiB copy. Existing pages remain in place; only
the small directory is copied. Growth peaks conservatively include the old directory
until reclamation. These are allocation-based estimates, not sampled RSS peaks.

One managed large-case allocation estimate includes an extra 7,664 bytes, while all
three full-GC heap deltas and total build-allocation counts are identical. This is
an accounting discrepancy, not evidence of a different table layout. It is retained
in the reported range, not silently removed. The stable managed/native retained
difference in the full-GC deltas is 1,544 bytes at 262,144 entries; the two ordinary
allocation estimates agree. Native allocator overhead is unmeasured, so this does
not establish a net physical-memory saving for native placement.

Allocation plus zero-checking/touching the new page took 86.7–117.9 microseconds
(managed) versus 78.3–91.0 (native) at the smaller size, and 82.1–98.3 versus
96.6–128.5 microseconds at the larger size. These are three single-growth observations,
not tail-latency guarantees. The structural finding is bounded incremental growth.

### Access measurements

Each cell is the **range of three process medians, in nanoseconds per entry access**.
These are amortized times for warm loops, not latency of a database read/write.
Random operations use a shuffled full-table trace. Reads fetch location/revision;
updates change those two fields. Timed loops use specialized value-type accessors
with logical bounds checks, without virtual dispatch per entry.

| Entries | Operation | Managed pages | Native pages |
| ---: | --- | ---: | ---: |
| 32,768 | Sequential read | 1.08–3.11 | 0.93–1.02 |
| 32,768 | Random read | 1.87–3.70 | 1.22–1.46 |
| 32,768 | Random update | 2.24–5.26 | 1.63–2.28 |
| 262,144 | Sequential read | 1.20–1.59 | 1.26–1.81 |
| 262,144 | Random read | 3.64–4.25 | 3.15–4.45 |
| 262,144 | Random update | 4.86–5.53 | 3.36–4.73 |

Native is faster in the small-table access results and in the larger-table update
loop. Larger-table read ranges overlap. Earlier bounded runs showed substantial
scheduling variation; they are not pooled into this final dataset. These results
do not establish an overall engine throughput winner. Root gates, index lookup,
reader lifetime, publication and application managed-heap pressure are absent.

### Verification, resource use and recommendation

The final Release build passed without warnings or errors. All 12 final workers
passed read checksums, full-field verification before/after growth and updates,
fresh-page zeroing, existing-reference preservation across growth/full GC, invalid
index checks and disposal ownership checks. Timed loops allocated zero managed
bytes and had no recorded GC collections. Native pages are explicitly freed through
the table's disposal path; no performance worker remains running.

The final workers used 2.98 CPU seconds in total and 4.99 seconds of worker wall time,
excluding runner pauses/startup. Their maximum reported process working-set peak
was 37.21 MiB, including runtime overhead. During harness development, one earlier
worker hit the time guard and was terminated; that attempt is incomplete, not a
passing measurement. The final operation count was reduced fourfold without raising
the time or memory guards. Raw JSON/logs remain in ignored local storage.

**Recommendation subsequently accepted on 13 November 2026:** use managed struct pages for v1 root-control
storage. Both representations avoid a full-table copy on growth and have nearly
identical retained footprints in this experiment. Native's microbenchmark update
advantage is real in this sample, but does not establish a read-path or full-engine
advantage sufficient to require manual lifetime management for this metadata.
Managed pages keep their storage lifetime within .NET while preserving compact
records and a logical root locator. Root reuse still needs the accepted protection
protocol; managed backing does not implement that protocol automatically.

No larger stress run is needed to confirm the growth geometry. Production integration
must verify concurrent lookup/growth/removal, root protection, safe reuse, shutdown
and memory budgets. This placement decision does not replace the native data arena,
select a lock algorithm or claim performance under managed-heap pressure.

## Bounded replica bootstrap correctness

**Checked on 13 November 2026.**
[ReplicaBootstrap](../../src/Prototypes/ReplicaBootstrap/README.md) tests a candidate for
copying current native blocks while the master continues publishing changes.
It uses one Int32 scalar and an ordered Int32 list inside each 64-byte native block,
typed root keys, binary block copies and an ordered in-memory transaction tail.
There is no PostgreSQL, network transfer, persistent history or full staging arena
on either modeled node. This is a correctness experiment, not a startup benchmark.

### Candidate and the necessary additional metadata

The candidate registers retention at published position **S** and fences the current
root-control catalog range in the same short publication step. During the scan,
new identities are represented by complete Upserts in the tail. Fenced slots are
not reused and deleted roots retain only identity/revision metadata; their old
native data can be freed. Each copied root has its own coherent revision, and a
deleted root contributes an explicit deletion watermark rather than silent absence.

After the scan, the master fixes published boundary **R**. The child installs all
base frames, then processes every complete transaction in `(S, R]`, skipping only
effects already covered by that particular root's captured/current revision. A
WriteBatch can have one member already present in the base and another still needing
application. All its members must be accounted for before advancing processing.

The provisional arena can contain roots from different positions. It therefore
remains unreadable and has **no public applied acknowledgment until Ready at R**.
The internal replay cursor is not a coherent replica position during initialization.
Newer transactions remain queued for live application after this fixed boundary.
Once recipients acknowledge, delivery is released without an extra history window;
initialization deletion watermarks and catalog-retention claims are also removed.

The model uses monotonically increasing published transaction positions for root
revisions, including after delete/recreate. It assumes a valid publication prefix;
it does not select the production concurrent commit/publication scheduler or the
public revision-token encoding. Shared blocks, strings and variable/chunked roots
still require a compatible physical capture/lifetime design.

### Results

| Group | Checks | Result |
| --- | ---: | --- |
| Directed histories and failure boundaries | 20 | Passed |
| All interleavings of two copies/five ordered publications, in both catalog orders | 42 | Passed |
| Seeded histories, 24 publications each over six typed keys | 64 | Passed |
| Real writer progressing while a detached native-block copy awaits delivery | 1 | Passed |
| Deliberately broken replay rules | 3 | Expected counterexamples reproduced |

Successful catch-up histories compare complete key sets and values against an
independent logical oracle, including list contents/order. Rejection scenarios check
failure state, visibility and retained delivery instead. The 127 candidate checks
cover updates on both sides of capture, deletion, same-key recreation, new keys
beyond the fence, equal external IDs
of different entity types, and batches straddling capture times. Tests also cover
invalid batch preparation, duplicate transactions, incomplete batch frames, missing
base/tail ranges, changes after R, retention expiry and prompt cleanup after acknowledgment.
Live data and applied progress stay unchanged while only part of a live batch frame
has arrived. Initial state remains unreadable before full readiness.

The three negative controls demonstrate why weaker rules are insufficient:

| Incorrect rule | Reproduced outcome |
| --- | --- |
| Apply the entire tail without per-root coverage checks | A list append already present in the base is applied twice |
| Keep revisions only for live roots; omit deletion watermarks | A root changed and deleted before capture has no baseline for an earlier partial effect; catch-up fails |
| Skip a whole transaction using the largest captured root revision | One batch member stays old while another is new |

These are intentionally broken controls, not failures hidden among the passing
candidate results. Ordinary missing ranges/frames and expired delivery invalidate
initialization; they never produce successful partial readiness or a SQL fallback.
No candidate failure occurred in the measured run.

### Resource use and limits

Release restore/build succeeded with zero warnings/errors on .NET SDK 10.0.404,
runtime 10.0.15, Windows x64. Four focused workers ran sequentially with BelowNormal
priority, one runtime processor and workstation GC. The runner recorded **1.00 CPU
second total** and **2.52 seconds of worker wall time**, excluding runner pauses and
setup. Verification bodies took 72–90 ms per group; these times are not transfer or
engine-latency measurements. No deadline or memory guard fired.

The largest reported working-set high-water mark during verification was **30.90 MiB**,
including runtime overhead and measured before result serialization. Peak simultaneously
live modeled native blocks totaled **896 bytes**, including both nodes and prepared
replacements; managed fixtures, metadata and buffers are additional. All groups ended
with zero native blocks and no owned worker remains. These tiny workloads do not
estimate memory usage for a production database. Raw results/logs remain ignored.

The model intentionally serializes publication/copy steps with one short gate and
uses simple test metadata structures. It does not prove real root/index synchronization,
multi-writer ordering, concurrent compaction, multiple initializing children, shared
dictionary lifetime, schema migration or socket failure/framing behavior. Model writes
replace small blocks for verification; production targeted partial updates are unchanged.
Fixtures contain distinct roots per batch. The later
[duplicate-identity decision](../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch)
accepts that input restriction separately; these runs do not verify production SDK
validation of duplicate-root batches.

### Recommendation and subsequent acceptance

**Accepted for v1 on 13 November 2026:** use per-root capture watermarks together
with a bounded catalog fence/deletion-metadata retention and a complete-transaction
readiness boundary, following this experiment's recommendation. The
[decision record](../decisions/replication-and-hosting.md#initial-capture-and-readiness-handoff)
owns the accepted contract. Revisions of live objects alone are insufficient.
The bounded model supports this mechanism for the checked interleavings; it is not
production readiness evidence or a proof over all possible schedules. Acceptance
does not select the model's single publication gate or public revision encoding.
The later [master scheduling decision](../decisions/write-acknowledgment.md#ordered-commit-and-publication)
accepts concurrent independent preparation followed by sequential bounded
commit/publication groups; this prototype does not test that production pipeline.

Production work must integrate this protocol with native block ownership, shared
text/chunks, the accepted managed control arrays and published-prefix coordination.
Capture metadata and copy buffers need bounded admission in addition to the existing
delivery limit. Do not keep deleted object payloads or a permanent version history
to implement the temporary watermarks. Retention loss requires restarting full
initialization through the existing reload contract.

## Bounded arena lifecycle

**Measured on 13 November 2026.** The [protocol](arena-protocol.md) was written before
the [ArenaLifecycle implementation and detailed report](../../src/Prototypes/ArenaLifecycle/README.md).
The experiment combines native root/chunk allocation, managed control entries,
protected readers, private replacement preparation, in-place scalar updates,
deletion/recreation, shared UTF-8 text and bounded owner-aware compaction.

Seven directed scenarios passed, including two expected corruption detections.
Eight fixed-cardinality churn cycles passed logical, ownership and reference-count
audits. Three alternating process pairs exercised concurrent reads/writes with
compaction disabled/enabled; all workers reported zero outstanding native allocations.
These were low-priority, one-processor processes with paced work, a hard 2 MiB native
cap and wall/CPU/working-set guards. The final eight-worker set plus the additional
instrumented churn run used about 2.22 seconds of process CPU time in total.

The 384-root fixture initially occupied 376 KiB of native segments. After deleting
288 roots, capacity remained 376 KiB until compaction reduced it to 112 KiB for the
same 96 remaining roots: 88 KiB encoded live content, 6 KiB class slack, 10 KiB reusable
holes and an 8 KiB empty reserve. Moving 327 root/chunk/text blocks copied 65,664 bytes
and returned 264 KiB of segment capacity. The tiny quiescent packing phase measured
159 microseconds; it is not a production pause bound. Subsequent eight churn cycles
held 384 live roots at 384 KiB native capacity, including the reserve.

Concurrent p99 read-hit latency, including byte validation, ranged from 15.7–25.2 µs
without compaction and 11.7–23.7 µs with it. Full Save p99 ranged from 43.7–102.2 µs
and 56.6–99.1 µs respectively, without SQL. Enabled runs performed 579–774 actual
relocations. A 5.756 ms read outlier occurred with compaction disabled; percentiles
are not maximum bounds, and the overlapping samples do not establish a reliable
percentage overhead. The detailed report separates managed allocation traffic,
retained heap, native capacity and whole-process working set rather than treating
any of them as interchangeable memory measurements.

This supports continued implementation of the checked owner-protection protocol;
it does not select the fixture's segment/class sizes, striped gates or direct key
maps as production defaults. Production indexes and shared-key retainers, multi-core
execution, WriteBatch publication, durable/replay boundaries, general collection
layouts and compaction scheduling remain unverified. E01–E04 and the physical
foundation therefore remain open; no accepted behavior or decision count changes.

## Bounded arena and index integration

**Measured on 13 November 2026.** The subsequent
[Arc integration experiment](../../src/Prototypes/ArenaLifecycle/INDEXES.md) extends the
arena with the selected UnorderedMapSlim primary index and OrderedMultiMap secondary
index. Fixed numeric values are copied into keys; text uses protected stable
descriptors with explicit root, prepared and index ownership. This experiment uses
MemoryOnly publication, without PostgreSQL.

Seven new directed scenarios passed, including stale candidate handling, same-key
recreation, equal-value replacement under OrdinalIgnoreCase, duplicate-head deletion,
native String-key hashing and compaction with index-only text ownership. All text
predicates, bounded numeric ranges and full ordered traversal match a logical oracle
at quiescent checkpoints. The original directed/churn checks also pass. Sixteen
warmed prepared publications allocate zero managed bytes; this excludes preparation
and first-use synchronization resources.

For the same 384-root dataset, both indexes add **59.18–59.20 KiB of retained managed
memory** relative to the fixture lookup control. Both cases use the same 376 KiB of
native segments: indexes add no text payload copies. Deleting 288 roots followed by
327 relocations reduces native capacity to 112 KiB for the remaining 96 roots in
both cases. KiB means 1,024 bytes; managed retention, native capacity and whole-process
working set are separate measurements.

Three indexed off/on process pairs compare compaction on the same paced mutation
trace. With compaction enabled, p99 primary reads are 20.8–27.5 microseconds and full
Save p99 is 128.7–303.3 microseconds, without SQL. Enabled runs move 793–798 blocks,
including 32 text blocks each. **Observed maxima include a 63.390 ms Save and a
51.721 ms read.** Scheduling, lock waiting, GC and package work were not separated;
the experiment does not establish production tail latency or its cause.

The eleven-worker measurement/regression set used 3.625 CPU seconds, with process
working sets below 40 MiB. Workers ran sequentially, BelowNormal and on one logical
processor, with the existing hard 2 MiB native cap and time/working-set guards.
Every worker reported zero outstanding native allocations at exit.

The results support the selected packages for the checked ownership protocol.
General schema keys, long/chunked text, scalable gates, parallel preparation with
growth, WriteBatch, durable/replay publication and multi-core schedules remain
unverified. E01–E04 stay open; this adds evidence, not a new accepted contract.

### Follow-up: bounded tail attribution

The [instrumented tail investigation](../../src/Prototypes/ArenaLifecycle/INDEXES.md#bounded-tail-investigation)
reproduced a 62.382 ms Save without compaction and a 38.126 ms read with compaction.
All six final workload intervals had zero collections/GC pause and no observed
contended blocking acquisition. The Save stalled between comparisons while already
holding root/index protection; its total acquisition time was only 4.9 µs. The read
stalled inside a text-acquisition interval whose zero-time acquisition succeeded.
Thread cycle counts did not grow in proportion to the long wall times.

Scheduling or another OS/runtime execution stall is the leading explanation for
these new observations. No kernel scheduling/fault trace distinguishes the exact
cause, and the original two outliers cannot be attributed retroactively. Holding
protection during an execution stall can still delay other callers. The experiment
does not justify replacing the selected collections or claiming bounded production
latency. Calibrations distinguished a known gate wait, a sleep and a forced GC.
The final seven workers used 2.609 CPU seconds and a sampled peak below 39 MiB.

## Bounded WriteBatch, PostgreSQL and application recovery

**Measured on 13 November 2026.** The [WriteBatch report](../../src/Prototypes/WriteBatch/README.md)
extends the same native arena and Arc indexes with a database publication barrier,
multi-type Upsert/Delete, real PostgreSQL commit, compact operation results and
single-arena reopen. Eleven scenario groups pass in both MemoryOnly and Durable
modes. Four separate application workers exit abruptly before commit, after commit,
during publication and before reply; fresh processes recover the expected whole
state and validate same-token submission without duplicate application.

For 32-object batches, publication medians range from 0.104–0.290 ms with PostgreSQL,
with an observed maximum of 0.845 ms. Complete call medians are 1.725–4.263 ms;
storage waiting occurs outside arena protection. These are small paced samples,
not production latency guarantees. All 144 measured publication bodies allocate
zero managed bytes. The 256-root arena starts at 248 KiB of native segments; a
separate 32-member preparation probe reaches 288 KiB, including 30 KiB of prepared
root/chunk slots. Managed deltas at sampled preparation/commit points reach about
42 KiB; they are not a bound on every transient allocation.

The final 18 sequential low-priority workers use 9.969 CPU seconds and a sampled
process peak of 70.613 MiB. PostgreSQL server resource use is separate. The owned
schema is removed and verified absent; all normal worker exits report no remaining
native allocations. Locked restore and Release build pass without warnings/errors.

The experiment serializes preparation, uses a fixture binary base and bounded
journal, and does not implement the production 256-byte saved units/background
applier, expiry, replica transport, multi-core scheduling or general schemas.
Its application-crash checks do not test PostgreSQL/power failure or a lost network
commit response. Accepted behavior and decision counts are unchanged; E03/E06 gain
integration evidence rather than a production-completion claim.

## WriteBatch with 256-byte saved units and background application

**Measured on 14 November 2026.** The [saved-unit report](../../src/Prototypes/WriteBatch/UNITS.md)
integrates the same arena/index path with a bounded coalescing worker, 256-byte saved
payload rows, atomic base/applied-position/revision commits, separate journal cleanup
and receipts retained after covered payload removal. Sixteen unit-path groups and
22 previous-path regression groups passed. Nine abrupt application exits cover
foreground commit/publication and background unit/cursor/cleanup boundaries, with
whole-state recovery, safe retries and exact revision checks after resumed maintenance.

For 32-object batches, whole-Save medians were 1.972–2.147 ms with maintenance off and
2.216–2.614 ms on; publication medians remained about 0.070–0.073 ms. Complete Save
maxima were 6.424 ms off and 19.768 ms on. No latency guarantee follows from these
small samples. Background SQL requires no additional arena pause; its longest
observed 46.943 ms transaction is not an arena freeze. The 144 measured publication
bodies allocated zero managed bytes. Both on runs caught up through all 42 batches
and removed covered journal rows, while preserving receipts.

Native segment capacity peaked at 296 KiB in both modes; persisted maintenance alone
left native accounting unchanged. A separate two-by-32-member coalescing probe held
53,968 bytes of journal payload and 32,768 bytes of output unit payload, with a largest
sampled post-GC managed delta of 102,176 bytes. This is not a transient heap-peak bound.
On-run retained-heap deltas varied from 12,104 to 1,144,128 bytes and remain reported,
without a claimed attribution or long-running retention guarantee.

The 28 sequential low-priority, single-core workers used 23.953 CPU seconds and
75.219 summed worker wall seconds, with a sampled process peak of 72.832 MiB, excluding
PostgreSQL server resource use. Owned-schema cleanup and all native-release checks
passed. General formats, partial collection commands, receipt expiry, additional
retention consumers, network/server failure and cross-node visibility are unverified.
The result supports the accepted direction; it does not freeze the fixture layout,
numeric budgets or a production scope.
