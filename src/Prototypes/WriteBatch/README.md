# Atomic WriteBatch on the arena and PostgreSQL

Bounded synthetic experiment, measured on 13 November 2026. It reuses the
[ArenaLifecycle](../ArenaLifecycle/README.md) native allocator, root/chunk/text
ownership and the selected Arc.Collections 2.2.0 primary and secondary indexes.
One application path prepares and publishes a batch in MemoryOnly or after a real
PostgreSQL transaction. This is a mechanism prototype, not the production SDK.

The subsequent [256-byte base and background-application extension](UNITS.md)
integrates coalescing, atomic saved-unit/cursor updates, separate journal cleanup
and retained receipts, with nine application-crash checks. The measurements below
remain the original binary-image experiment from 13 November.

[Engine foundation](../../../docs/design/engine-foundation.md) · [Arena protocol](../../../docs/design/arena-protocol.md) · [Atomic batch contract](../../../docs/decisions/document-model.md#atomic-writebatch-within-one-database)

## Implemented boundary

`BatchEngine` owns one arena, a short database publication barrier and an ordered
writer admission permit. Its Read, indexed Query and compaction entry points all
participate in that barrier. The underlying arena is private. Existing arena locks
still protect roots, indexes, shared strings and allocation. The nested order is
database barrier -> root -> catalog/index -> text -> allocator.

The experiment serializes preparation as well as commit/publication. The writer
permit owns ordering across I/O; it is not an arena protection lock. Readers and
compaction continue during preparation and PostgreSQL work. Future concurrent
preparation must participate in the same publication/admission protocol; this
experiment does not implement or measure that scheduler.

1. Validate the entire batch: one operation per type/key, valid values, member and
   conservative native preparation limits. Check primary capacity before preparing.
2. Prepare all Save replacement blocks and index nodes/text references. A Delete
   records its target; writer ordering keeps the predecessor stable. A preparation
   failure disposes all plans without changing published objects or indexes.
3. Durable mode inserts the complete encoded batch and its result receipt in one
   logged PostgreSQL transaction, with `synchronous_commit=on`, and waits for commit.
   There is no database/root/index protection gate across this I/O. MemoryOnly omits it.
4. Acquire exclusive database publication protection, draining existing protected
   readers. Apply every member through the existing arena/index paths and advance
   the applied position only after the last member. Then release the barrier.
5. Complete the call. If publication fails, mark the instance faulted **before**
   releasing the barrier; further read/query/write/compaction access is rejected.
   Discard that arena and restore a new one from the stored base plus committed batches.

The barrier is a prototype ReaderWriterLockSlim, not a newly selected production
lock representation. Separate reads may straddle a successful batch publication;
there is no public multi-object snapshot or application-held transaction lock.
Test-only hooks deliberately suspend work at boundaries; application callbacks are
not part of the proposed engine API.

The fixture has 256 roots across User and Role types, including equal numeric keys
in different types. A bounded internal slot encoding maps `(type, key)` to the
existing primary index. Both types share the arena and string-bearing secondary
index. This is not a partition or a general schema/key encoding. Supported batch
members are full Upsert and Delete; the tests include replacement, insertion and
deletion in one transaction. Payloads normally range from 128 to 1,536 bytes.

## Persistence and failure model

Each run creates a uniquely named schema in the local `postgres` database. Existing
schemas/data and server configuration remain untouched. The runner drops only its
owned schema in `finally`, including after expected application crashes, and verifies
its absence. No PostgreSQL service/backend is forcibly stopped.

The initial base is an independent binary fixture image; each journal row contains
one whole transaction, operation UUID, ordered position, member count and binary
effects. Encoding includes typed identities, numeric values, actual UTF-8 text and
actual payload bytes; decoding validates every byte against the fixture schema.
Native addresses and physical index images are absent. Recovery rebuilds the arena
and indexes, then applies complete journal batches before serving reads.

A stored position/member-count result is returned for a same-token repeat, with
request fingerprint validation. MemoryOnly retains a compact SHA-256 fingerprint
and result rather than old encoded payloads. Durable receipts remain colocated with
the retained journal in this experiment. Receipt expiry, journal pruning, a full
result-status API and provider failure reconciliation are not implemented here.
The journal is capped at 512 batches before commit; it is not an unlimited recovery
buffer. No new retention policy is selected.

The base is a fixture blob, **not** an implementation of the accepted 256-byte saved
unit layout or background applier. Testing those components together with this path
remains separate work. PostgreSQL WAL settings are checked, not weakened: the measured
server was **18.4**, with fsync, full_page_writes and synchronous_commit all on.

## Correctness and crash checks

Eleven scenario groups passed in each mode:

- Whole-batch rejection for repeated type/key identities and an invalid last member.
- Native preparation admission refusal and cleanup after an injected allocation
  failure following two prepared members, with full ownership/index audits.
- Pre-commit rollback leaves old data, indexes and operation receipt unchanged.
- A held pre-commit boundary permits reads of old values and compaction.
- A held publication midpoint blocks both Read and indexed Query; compaction skips
  the barrier and the applied position does not advance over the visible prefix.
- Publication drains a reader that entered before it requested the barrier.
- Same-token retry returns the original result without changing object revisions;
  changed input under the same token is rejected.
- Concurrent callers targeting the same roots preserve operation order.
- Three fault locations stop the instance: after commit, after the first published
  member, and after publication before caller completion. Durable reopen restores
  the full batch and a repeat does not apply it twice. A reader already waiting at
  the failed publication midpoint receives the instance-fault error after release,
  rather than reading the partly installed state.

In addition, four **separate application processes terminate abruptly**, without
managed cleanup. A fresh process reopens each corresponding stream:

| Exit boundary | State found on reopen | Same-token submission after reopen |
| --- | --- | --- |
| INSERT sent, before COMMIT | Old base; no journal row or receipt | Applies the batch once |
| COMMIT returned, before publication | Complete committed batch | Returns retained result |
| First member published, remainder pending | Complete committed batch | Returns retained result |
| Publication complete, reply not returned | Complete committed batch | Returns retained result |

Exit codes 92–95 are intentional and checked by the runner. Recovery audits all
roots, both indexes, string ownership and retry revisions. These are application
crashes and injected exceptions, not PostgreSQL/power/storage-loss tests. Lost network
commit responses and failure during the recovery process itself are not exercised.

## Latency measurements

Windows x64, .NET 10.0.15, local PostgreSQL 18.4. Two fresh worker processes per mode
each run 42 paced batches: 14 per size, with the first two per size excluded from
batch timing statistics. Thus each size has 12 measured samples per process, 24
per mode. Concurrent protected reads, indexed queries and bounded compaction run
on the same logical processor. Test-hook delays and forced GC memory probes are
excluded from these timings.

The table shows **ranges of process medians in milliseconds** and the largest
observed publication interval across both processes. Complete calls include input
encoding/fingerprinting, receipt lookup, preparation, memory accounting, storage
where applicable and publication. SQL time includes BEGIN/INSERT/COMMIT, not only
the final flush. The publication interval measures protected work through the point
just before releasing the barrier; the unlock call is outside that timer.

| Mode | Objects per batch | Complete call, median ms | SQL transaction, median ms | Publication, median ms | Publication maximum, ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| MemoryOnly | 1 | 0.072–0.108 | — | 0.005–0.011 | 0.026 |
| MemoryOnly | 8 | 0.310–0.406 | — | 0.043–0.067 | 0.206 |
| MemoryOnly | 32 | 1.083–1.321 | — | 0.220–0.278 | 0.853 |
| PostgreSQL | 1 | 1.439–2.234 | 0.847–1.703 | 0.011–0.017 | 0.044 |
| PostgreSQL | 8 | 1.776–3.979 | 0.950–2.543 | 0.041–0.111 | 0.414 |
| PostgreSQL | 32 | 1.725–4.263 | 0.743–1.995 | 0.104–0.290 | 0.845 |

Publication also waited for existing protected access: the maximum measured wait
was **8.8 µs**. New readers can wait during that drain as well as during publication;
the hold column is not a universal upper bound on reader latency. Concurrent read
p99 was 21.3–46.9 µs in MemoryOnly and 19.5–21.1 µs with PostgreSQL; corresponding
read maxima were 451.5 and 305.5 µs. Each process captured 400–440 reads and 25–27
queries. Smaller PostgreSQL samples do not establish better reading performance.

The largest complete SQL-backed call was **7.763 ms**. With twelve samples per
size/process, p95/p99 batch values effectively select the maximum; the table uses
medians and maxima instead of implying a stable tail percentile. Scheduling variation
is visible, no outliers are discarded, and these runs establish no throughput SLO.
All **144 measured publications allocated zero managed bytes** in the publication
body. Preparation, input encoding, driver work and first-use synchronization are
outside that observation.

Reopening 256 base roots plus zero/one eight-member journal batch took **88.7–105.5 ms**
in the four fresh recovery workers. Connection opening and subsequent full audits
are outside that interval; first-use recovery/JIT work is inside it. This tiny test
validates recovery behavior, not startup time for a large database.

## Memory measurements

**1 KiB = 1,024 bytes.** Native arena capacity, managed retained heap and whole-process
working set are separate measurements. Before mutation, the fixture uses **248 KiB**
of native segments. The separate memory probe performs forced collection at defined
boundaries; it is not mixed with latency measurements.

| Batch size | Prepared native root/chunk capacity | Native segment capacity while prepared | MemoryOnly managed delta at observed preparation/commit points | PostgreSQL managed delta at those points |
| --- | ---: | ---: | ---: | ---: |
| 1 | 1.625 KiB | 264 KiB | up to 1,016 bytes | up to 18,840 bytes |
| 8 | 13 KiB | 272 KiB | up to 19,120 bytes | up to 19,896 bytes |
| 32 | 30 KiB | 288 KiB | up to 42,184 bytes | up to 42,960 bytes |

Prepared root/chunk capacity does not include text entries internally registered as
live with prepared owners. Segment capacity includes these, class slack, holes and
reserve. Managed deltas include reachable plans, key work, encoded bytes and applicable
driver state; sampling after GC does not capture every transient serialization peak.
Some small MemoryOnly deltas become negative after a later collection (−440 bytes
before commit and −960 after completion for size 1); these are heap/lifetime
observations, not negative memory use or a hard budget proof.

The paced timing workers peaked at **296 KiB of native segments** and finished at
288–296 KiB, with 42–46 relocations. Managed allocation traffic, including the harness,
was approximately 1.90–1.92 MiB in MemoryOnly and 2.07 MiB with PostgreSQL across all
42 batches. It is not retained memory growth. The native preparation admission cap
is 64 KiB, assuming a separate 64-byte text slot per Upsert; the arena has its existing
hard 2 MiB segment cap. These fixture limits are not selected production defaults
and do not implement the final unified managed/native resource budget.

## Commands, ownership and limits

Prerequisites: .NET 10 SDK, Arc.Collections 2.2.0 through ArenaLifecycle, and Npgsql
10.0.3 only in this prototype. NuGet versions use central package management and a
lock file. ArenaLifecycle grants this sibling experiment internal access through
InternalsVisibleTo; it acquires no PostgreSQL dependency.

For SQL checks, provide an ignored local connection file such as
`.local/batch-postgres-connection.txt`. Only localhost, 127.0.0.1 and ::1 are accepted;
the login must connect to `postgres` and create/drop its owned schema. Connection
strings are never written into reports or command-line arguments.

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet restore Prototypes/WriteBatch/WriteBatch.csproj --locked-mode --disable-parallel
dotnet build Prototypes/WriteBatch/WriteBatch.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
& ./Prototypes/WriteBatch/Run-Bounded.ps1 -Mode All -ConnectionFile .local/batch-postgres-connection.txt -Repeats 2
```

`-Mode MemoryOnly` needs no server or connection file. `PostgresOnly` runs only the
SQL-backed scenarios. Workers run sequentially, BelowNormal, with one logical
processor, workstation GC, a 15-second wall/5-second CPU guard and sampled 192 MiB
process guard. The final set of **18 workers used 9.969 CPU seconds and 14.997 summed
worker wall seconds**, with a sampled process peak of **70.613 MiB**. PostgreSQL server
CPU/memory is not included; only small bounded SQL statements are issued. Every
normally exiting worker reported zero outstanding native allocations. Abrupt exits
have no managed cleanup report; the OS destroys their address space.

Final ignored evidence: `.local/write-batch-20261113-212227-073`. Earlier exploratory
runs `.local/write-batch-20261113-212007-605` and `.local/write-batch-20261113-212015-250`
are not pooled into the table. Locked restore and Release build passed with zero
warnings/errors. All four expected crash exits and their recovery checks passed,
and final schema removal was verified. No owned worker remains running.

The final-source correctness rerun is recorded separately in
`.local/write-batch-final-checks-20261113-213206-751`: both eleven-group suites,
including the waiting-reader fault case, and owned-schema cleanup passed. It adds
no latency samples to the table above. Its four workers used 2.156 CPU seconds,
and the Release build completed with zero warnings/errors.

The ignored `ownership.json` records the run token and connection-file path for
cleanup if the runner itself is externally terminated. Cleanup accepts that same
token through the executable's `cleanup` mode; it must never target unrelated schemas.

## What remains unverified

This supports the accepted atomic publication/commit/reopen mechanism. It does not
complete the production engine: parallel independent preparation and reservations,
growth under contention, multi-core scheduling, general key/schema/collection formats,
partial-update commands inside batches, operation expiry, network ambiguity,
background base application/retention, and replication integration remain work.
The [unit extension](UNITS.md) adds bounded base/application/cleanup evidence; general
production persistence and the other listed boundaries remain unimplemented.
The single coordinator, fixed capacities, binary fixture encoding and numerical
limits are experiment choices, not newly accepted product decisions.
