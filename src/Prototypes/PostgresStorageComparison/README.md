# PostgreSQL storage comparison

A bounded, synthetic .NET 10 experiment for Q09.2, Q09.6 and Q10.1. It measures
ordered binary change batches, current-state UPDATE/UPSERT at different unit sizes,
native resident reads, binary-image transfer, paused journal compaction, and larger
base captures with bounded pending-write queues, and background journal application
with bounded coalescing workspaces.
It is not the database engine, its storage format or its provider API.

[Performance requirements](../../../docs/design/performance-requirements.md) · [Backend decision](../../../docs/decisions/persistence-backends.md) · [Evidence](../../../docs/design/prototype-evidence.md)

## Prerequisites and commands

- .NET 10 SDK and local PostgreSQL; the initial target is PostgreSQL 18.
- A login able to connect to `postgres`, create/drop a database and read the used
  settings/WAL positions. Only localhost, 127.0.0.1 and ::1 are accepted.
- PostgreSQL `fsync` and `full_page_writes` must already be enabled. The experiment
  refuses weaker settings and never changes global configuration. Its own write
  sessions use `synchronous_commit=on`.
- Npgsql 10.0.3, centrally versioned within this isolated prototype. This does not
  add an SQL dependency to the core or other prototypes.

Store the connection string in an uncommitted local text file, for example
`.local/postgres-connection.txt`:

```text
Host=127.0.0.1;Port=5432;Username=<local-role>;Password=<local-password>;Database=postgres
```

From the repository's `src` directory in PowerShell:

```powershell
dotnet build Prototypes/PostgresStorageComparison/PostgresStorageComparison.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll smoke .local/postgres-connection.txt .local/postgres-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll measure .local/postgres-connection.txt .local/postgres-measure.json
```

Use a new report filename for every run; existing reports are not overwritten.
Run smoke before measure. Commands run sequentially, not alongside other load
experiments. Connection strings never appear in the report. Raw JSON stays ignored.

## Resource ownership and budgets

Each invocation creates a randomly named `lattice_probe_*` database. Existing
databases and server configuration remain untouched. Normal completion and handled
errors drop only that database. File journals have unique names in the OS temporary
directory and are removed after each scenario. The report names the temporary
volume; compare its storage hardware with the PostgreSQL data volume.

The workload has an eight-minute cancellation budget, a 45-second budget per write
scenario and a 20-second SQL command timeout. Cancellation is observed between
operations; an in-flight synchronous storage call can delay cleanup. A forced process
termination can leave its temporary database/file and requires owner-directed cleanup.
The prototype never stops or crashes an existing PostgreSQL service.

Smoke uses a 4 MiB checkpoint, 1,000 mutations/s, one-second scenarios and one pass.
Measure uses a 128 MiB checkpoint, rates of 1,000/5,000/10,000 mutations/s,
three-second scenarios and two passes. Each rate compares PostgreSQL/file journals
with single-mutation commits and batches capped at 64 mutations / approximately
2 ms collection time. The second pass reverses backend, rate and batch order.
These are exploratory inputs, not product SLOs or a sustained-capacity certification.
The bounded queue holds 4,096 mutations; excess arrivals are explicitly rejected.

The live dataset has 65,536 fixed roots, each holding a value and its complement
in 16 native bytes, with a managed gate per root. A reader visits pseudo-random
roots continuously. The writer changes a deterministic sequence of keys, persists
a batch, then updates each root under its gate. No gate spans I/O. The gate does
not make all roots in a batch an atomic application transaction.

## What the experiment measures

### Writes and resident reads

Each encoded mutation contains a sequence, key, value and complement: 32 bytes.
One prepared PostgreSQL INSERT stores one complete binary batch in a logged table,
committing it synchronously. File storage appends the same bytes with a 16-byte
batch header and calls `Flush(true)` per batch. This compares commit frequency
under declared flush guarantees, not identically implemented recovery systems.
These original smoke/measure modes do not compare UPDATE/UPSERT; the separate unit
modes below do. PostgreSQL's default payload storage/compression remains enabled.

An independent producer schedules offered arrivals from a monotonic clock. Reported
end-to-end latency starts at intended arrival and ends after commit and native
publication. Producer scheduling lateness, rejection count, queue high-water
observation and drain time remain visible. Timer precision can exceed the nominal
2 ms collection budget; actual collection latency is recorded. Throughput includes
drain time and must be read together with rejected work and latency.

Resident-read throughput includes locking, synthetic key generation and integrity
checking. One in 4,096 reads measures latency, with at most 500,000 samples; dropped
samples are counted. Stopwatch overhead is included. This is a tiny fixed-root
read, not GetById lookup cost, OQL, string decoding, collections or an index scan.
Baseline readers run without the writer. Per-process allocation/CPU observations
include workload infrastructure; PostgreSQL server CPU and memory are not included.
Peak working set is the client's lifetime maximum, not a resettable scenario peak.

Global WAL position differences may include unrelated server activity and background
work. They are not precise per-operation physical disk I/O. Journal relation size
includes PostgreSQL storage/index overhead; file size includes its explicit headers.
Measure only interpretable runs on known storage with competing work disclosed.

### Binary state loading

Checkpoint rows contain deterministic random 1 MiB chunks to avoid an all-zero
compression advantage. Generation, hashing and initial COPY insertion are a setup
measurement, not a pure storage write benchmark. Standalone loads compare:

1. `SequentialAccess` and `GetStream`, reading into native destination spans.
2. Binary COPY export, decoding each `bytea` into a managed byte array before copying
   to the native image. This deliberately measures this concrete API path; it does
   not establish a lower bound for every possible COPY implementation.

Load timing includes native allocation, query/transfer, driver handling and native
installation. Connection opening is outside timing. Full SHA-256 validation runs
after timing. Both methods verify every chunk, size and identity. No shared-buffer
or OS cache flush occurs: these are recently written, warm-cache data.

A separate highest-rate PostgreSQL batch scenario loads one full state image on
another connection, starting about half a second into the writes. This reveals
one bounded interference event, not continuous replication or checkpoint capture.
Its process-wide allocation observation includes concurrent writer work. The loaded
base is immutable; there is no changing-base capture, tail replay or Ready protocol.

## Correctness checks and interpretation

Every write scenario reopens the persisted journal, verifies exact acknowledged
sequence membership and ordering, validates all encoded fields and compares the
replayed final state with every resident root. Concurrent reads verify that the
value/complement pair is coherent. An explicit rollback/reconnect check proves that
rolled-back rows are absent. Any mismatch fails the invocation.

Reconnect/readback does not test server crash recovery, power loss, uncertain commit
outcomes or backups. The file journal lacks a production checksum/recovery protocol.
No result establishes a production durability guarantee beyond the tested calls.

Other missing costs include schema binding, variable data, string dictionaries,
primary/secondary indexes, allocator churn, checkpoint generation/retention cleanup,
WAL truncation, remote TLS/network latency, managed-service limits, Linux/Kubernetes,
replica catch-up and multi-gigabyte readiness. The three-second scenarios touch at
most 30,000 distinct roots; hot-key contention and repeated changes to one root are
not measured. Three-second bursts also do not prove
steady-state vacuum/checkpoint behavior or bounded lifetime disk growth.

Use these measurements to choose the next experiment and discuss acknowledgment
latency. They cannot alone select the final backend, batch delay or database SLO.

## Current-state units versus a compact journal

Run the additional modes sequentially after building:

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll units-smoke .local/postgres-connection.txt .local/postgres-units-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll units-measure .local/postgres-connection.txt .local/postgres-units-measure.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll units-smoke-external .local/postgres-connection.txt .local/postgres-units-external-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll units-measure-external .local/postgres-connection.txt .local/postgres-units-external-measure.json
```

The same isolated-database ownership, synchronous commit, eight-minute invocation
budget, 45-second write-scenario budget and new-report requirement apply. Unit modes
refuse to start when another active SQL client is observed; they do not reserve the
server or eliminate background interference. They never change global PostgreSQL
configuration, stop its service or access application tables.

The image contains 65,536 records of 256 bytes: 16 MiB total. Deterministic random
filler avoids an all-zero compression advantage; two Int64 fields (16 bytes) change
per mutation. All methods use the same key sequence, initial bytes, resident-reader
probe, 64-change / approximately 2 ms batches and 4,096-entry admission queue.
Updates follow a permutation of the 65,536 keys, repeating after one complete pass.
This includes repeated updates in an eight-second 10,000/s run that keeps up; it
does not model a concentrated hot-key distribution.

The three methods are:

| Method | Ordinary commit | Recovery input |
| --- | --- | --- |
| Update | One prepared UPDATE FROM unnest of arrays containing distinct changed units | Current units |
| Upsert | One prepared INSERT SELECT unnest with ON CONFLICT DO UPDATE | Current units |
| Journal | The original prepared INSERT of a compact 32-byte-per-mutation batch | Seeded units plus ordered batches |

Current-state units are 256 bytes, 4 KiB or 64 KiB. Changes to the same unit inside
a batch are combined before sending it. There is one SQL statement/commit per
batch, not one round trip per unit. Upsert throughput measures its conflict/update
path because keys already exist. A separate transaction exercises insertion and
rollback; bulk insert/delete churn is not a throughput result of this experiment.
No server-side patch function or partial-byte update is implemented.

`units-smoke` offers 1,000/s for one second, one pass: nine measured scenarios.
`units-measure` offers 10,000/s for eight seconds, two passes: 18 measured scenarios,
with reversed unit/method order on the second pass. Three one-second warm-up
scenarios are excluded from both reports. No OS/database cache flushing occurs.

The `-external` variants use the same experiment for 4 KiB and 64 KiB units with
`current_units.payload` set to PostgreSQL STORAGE EXTERNAL before seeding. This
allows out-of-line storage but disables compression attempts for this column.
The journal column retains its default EXTENDED policy; its encoding does not
change. The two larger sizes give six smoke and 12 measured scenarios. The small
256-byte values do not need TOAST compression and are not repeated in this variant.
This controls a specific suspected cost observed in the default-policy run, not
a new production setting or a claim about all compressible document workloads.

Current-state writes reuse up to 64 managed payload buffers: 16 KiB, 256 KiB or
4 MiB depending on unit size. Journal mode uses one unit buffer for seeding and
maintenance. Each method maintains one 16 MiB native writer-owned image in addition
to the existing 1 MiB native read probe. It is an experimental staging image, not
a proposed requirement to duplicate the complete production arena. Shared probe
records publish only after commit; no root gate spans SQL I/O. Reported allocated
bytes measure allocation traffic during writes, not retained RAM or PostgreSQL
server memory. Initial buffers and image capacity are reported separately.

After writes, recovery allocates a separate 16 MiB native destination, streams all
units through GetStream, and replays the journal where present. Timing excludes
connection opening and subsequent full verification. Every recovered field is
checked against acknowledged mutations and resident records; all image bytes are
compared. Journal membership/order is checked before its removal. UPDATE rollback
and UPSERT insert rollback are checked after reopening a connection.

Journal maintenance then stops writes and SQL readers, and atomically replaces
the base through TRUNCATE + binary COPY and removes the covered journal in one
SQL transaction. It reuses the existing native image and one managed unit buffer;
no second full native capture image is allocated by this compaction step. Recovery
is verified again from the replacement base with an empty journal. A separately
allocated recovery destination verifies the result; that is outside compaction
timing/allocation observations. This is a paused prototype, not online capture,
continuous retention, a production format, or permission to pause production writes.

All methods finish with ordinary VACUUM (ANALYZE) on owned tables. The report records
write payload bytes, global WAL growth, relation sizes before/after, recovery time,
paused compaction duration/allocations/WAL, and vacuum duration. WAL is global and
can include background work; write-WAL intervals include readback verification.
Compaction WAL excludes the subsequent vacuum. Relation size is allocated storage,
not live data size or physical device traffic. No VACUUM FULL is run.

This is a bounded cost comparison. It does not test simultaneous SQL snapshots,
online base construction, crash interruption, inserts/deletes under load, long-run
vacuum equilibrium, remote service limits, indexes, real document encoding or
multi-gigabyte datasets. A journal can win the write path while still adding
recovery and periodic maintenance costs. Compare those costs explicitly.

Primary API references: [Npgsql performance](https://www.npgsql.org/doc/performance.html),
[COPY](https://www.npgsql.org/doc/copy.html),
[PostgreSQL commit durability](https://www.postgresql.org/docs/current/wal-async-commit.html),
[TOAST and updated values](https://www.postgresql.org/docs/current/storage-toast.html).

## Larger paused captures and queue memory

Short write pauses were considered acceptable in principle, subject to measurements
before selecting a capture method. These modes test one candidate; they do not accept it.

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll pause-smoke .local/postgres-connection.txt .local/postgres-pause-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll pause-measure .local/postgres-connection.txt .local/postgres-pause-measure.json
```

Use fresh report filenames and run sequentially. Both modes refuse another active
SQL client at startup. They share the isolated database and durable settings above.
Their invocation budget is 15 minutes, scenario budget 120 seconds, capture budget
60 seconds and SQL command timeout 20 seconds. Cancellation is checked between
synchronous operations. No existing PostgreSQL service is stopped or reconfigured.

Smoke covers 4/16 MiB at 1,000 arrivals/s in one pass. Measure covers 128/512/1,024 MiB
at 10,000 arrivals/s in two passes, reversing size and command order in the second.
Both use 32-byte and 1,024-byte commands and exclude a 4 MiB warm-up. Each scenario
initializes one native image, persists an old base, then durably seeds 1,000 changes.
Deterministic random bytes avoid an all-zero compression advantage. Setup is outside
the measured pause. The earlier writer is already drained; production barrier wait,
real document serialization, dictionaries and index policy are not measured.

During the pause, a single writer streams the stable image through a reused 1 MiB
managed buffer into a new generation. Binary COPY commits at most 32 chunks per SQL
transaction; payload uses STORAGE EXTERNAL. Each chunk has a SHA-256 checksum. A
separate transaction publishes the complete generation and its boundary. The old
base and complete journal remain until scenario cleanup: reclamation is outside
timing. This differs from the earlier unit prototype's TRUNCATE replacement.

An independent producer offers writes throughout capture and for three seconds
after resumption. Each admitted request retains a byte array, request object and
TaskCompletionSource/Task. Extra command bytes contain deterministic random data;
they contribute to queue/journal traffic although the applied mutation changes
only a 16-byte root. Commands are not coalesced. Admission is bounded by 32,768
requests and 16 MiB of command payload, checked before request allocation. Overflow
is rejected explicitly. A 30-second intended-arrival-to-execution budget expires
work before persistence; no timeout implies an accepted write. These are test
settings, not selected production limits or retry semantics.
Expiry is checked when dequeuing, so the failure can be delivered after 30 seconds;
the prototype has no timer-driven caller cancellation while capture is running.

After resumption, the same synchronous journal sink persists batches capped at
64 commands / approximately 2 ms collection time, followed by native publication
and completion. Reports separate settlement of the requests present at resume
from the first completely empty queue. The latter may never occur while input
continues, even after the original backlog is gone. Rejections and expirations
must accompany latency and drain results. End-to-end latency starts at intended
arrival and includes the pause; producer lateness is recorded separately.

Before load, a full-GC retained-heap comparison calibrates an empty preallocated
queue and a full queue for each command size. The measured live bytes per item
and observed high-water count estimate queue RAM. This is an estimate, not an
exact process-peak attribution: external callers, async continuations, transport,
SQL buffers, in-flight batches and GC heap headroom are separate. Process working
sets and allocation traffic are recorded separately. No forced GC runs inside
timed capture/drain. Lifetime peak working set cannot be reset per scenario.

The reported writer pause includes instrumentation between durable publication
and actual drain start; durable capture and that instrumentation interval are
also separate fields. Full generation readback occurs afterward, not in the
pause. Every chunk's identity, length and checksum is checked through a reopened
connection. Base boundary, journal sequence/membership and all recovered root
values must match acknowledged writes. Partial-generation publication refusal
and rolled-back manifest publication both preserve old-base-plus-journal recovery.
These checks do not simulate a PostgreSQL crash, power loss or uncertain commit.

An independent 1 MiB native fixed-root probe continues reading during capture and
drain and checks coherent value/complement pairs. It is not a scan of the larger
image or a full-engine query benchmark. The image is synthetic, caches are warm,
the server is local, and PostgreSQL/OS memory is excluded from client working set.
The prototype allocates no second full capture image; verification reuses the
1 MiB buffer and small root arrays. Statistical arrays, two read-probe buffers,
driver buffers and retained managed heap still contribute to process memory.

## Background journal application with coalescing

The background method and short bounded batches with immediate catch-up are
accepted for v1. The SQL format and remaining numerical settings stay experimental;
the 256-byte saved-unit baseline is accepted separately. The foreground
continues durably appending changes and publishing them in native memory. A worker
thread in the same process uses a separate PostgreSQL connection to read the next
committed journal prefix and its affected saved units. It applies all changes in
order, then persists each affected unit once. Unit updates
and the applied sequence/count share one SQL transaction. No native gate spans I/O,
and the background worker does not read a second complete arena image. The accepted
production policy permits continued healthy appends and paced retries during a
transient applier failure. This prototype instead stops the experiment on worker
failure; its rollback/reopen checks do not validate that production failure policy.

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-smoke .local/postgres-connection.txt .local/postgres-apply-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-measure .local/postgres-connection.txt .local/postgres-apply-measure.json
```

Choose fresh reports and run sequentially on the same isolated local-database
fixture. The invocation budget is 25 minutes; each scenario has its offered-load
duration plus 90 seconds of cancellation headroom.
The existing 20-second SQL command timeout applies. Cancellation is checked between
operations; an executing SQL transaction can finish after cancellation. A worker
drain is requested for at most ten seconds after the foreground finishes, then the
worker stops at an operation boundary. An in-flight call can extend this interval.
The server is never stopped or reconfigured, and application databases are untouched.

The full dataset is 128 MiB: 65,536 fixed 2 KiB records with deterministic random
filler. Every change assigns a sequence value and complement in 16 bytes; its
journal command is 32 bytes. Persisted units group records into 4 KiB or 64 KiB
payloads with STORAGE EXTERNAL. These sizes are saved units, not journal records or
the total dataset. Both unit sizes use identical logical changes and total data.
Foreground readers use the existing separate 1 MiB fixed-root integrity probe.

Two deterministic access patterns are compared:

- Uniform: a permutation of all 65,536 keys, repeated after a complete pass.
- Hot: 90% of changes cycle through 256 keys spread across the image; the remaining
  10% follow the full-key permutation. This deliberately tests substantial reuse,
  rather than assuming every workload benefits from combining repeated changes.

Each pattern compares these modes:

| Mode | Foreground work | Background work |
| --- | --- | --- |
| JournalOnly | Journal append, then native publication | None during the scenario; a same-pattern baseline for interference |
| Immediate | Identical append/publication | Read one foreground journal batch at a time; combine changes within that batch |
| Coalesced | Identical append/publication | Gather up to 128 journal batches, with a nominal 250 ms interval between starts when caught up |

Both background modes write each touched unit once per application transaction.
The comparison includes batching frequency, round trips and coalescing together;
it is not an isolated measurement of coalescing CPU cost. A full batch or unit-budget
split causes immediate continuation rather than another 250 ms wait. Catch-up after
the writer finishes also skips that accumulation delay.

Foreground batches contain at most 64 changes with approximately 2 ms collection,
using the existing journal sink and a bounded 4,096-change input queue. Excess
arrivals are rejected and counted; successful latency ends after durable commit
and native publication. The background worker does not delay acknowledgments until
base updates complete. Rejected arrivals remain gaps in the sequence; the applied
count is stored separately and must not be inferred by subtracting sequence IDs.

The application workspace has at most 32 MiB of reusable saved-unit payloads,
plus bounded change arrays, unit maps and key markers. Buffers are allocated lazily
and retained for reuse. The smaller Immediate mode can use at most 64 unit buffers.
The slot map and key markers scale with this experiment's unit/key counts; the
32 MiB cap applies to reusable payload buffers, not all memory for arbitrary datasets.
The worker selects a complete prefix of journal batches that fits its unit budget;
it never advances the cursor past skipped input or partially applies a journal row.
Saved units are read in one bounded SQL query and updated in one set-based UPDATE,
not one network round trip per object. This prototype models assignments only;
general increments, lists, creation/deletion and schema metadata are not implemented.

Smoke offers 1,000 changes/s for two seconds, with ten measured scenarios and an
excluded warm-up. Measure offers 10,000/s for 30 seconds in two reverse-order passes:
20 scenarios, each offering 300,000 changes. Setup and seeding precede timing.
Quiet native-read probes are also recorded. No OS/database cache flushing occurs.

Every 250 ms, the sampler records published/applied counts, pending count, oldest
pending commit age, client working set and managed heap size. Counts are coherent
per published progress object, but the two independently advancing streams are
sampled separately; tiny transient negative differences are clamped to zero.
The exact progress pair observed at foreground completion is reported separately.
Lag during sustained arrivals must be distinguished from eventual completion after
arrivals stop. The original writer queue and the durable unapplied journal are
different backlogs. A baseline with no worker intentionally leaves its whole journal
unapplied; that is not a failed correctness check.

Memory output separates retained workspace allocations, reusable payload capacity,
worker allocation traffic, and sampled whole-client managed/working-set values.
Workspace allocation accounting includes its retained arrays/maps and lazily created
byte arrays. It excludes SQL/driver state, temporary parameter/journal arrays, latency
samples, foreground caller state and PostgreSQL memory. It is not the worker's total
RAM. Whole-client samples include the 128 MiB arena and experiment infrastructure;
sampling is not an exact process peak and the prototype forces no GC during load.

After threads stop, verification reopens one Repeatable Read transaction, validates
exact journal membership/order, the applied boundary/count, every saved record and
all unchanged filler bytes. Base state plus the remaining journal must match native
publication. Preflight exercises a nonempty tail, rollback of both saved units and
cursor, and a reopened applier continuing from its durable position. This does not
simulate a PostgreSQL crash or uncertain commit outcome. The older smoke mode is a
regression check for the shared native-record publication overload.

No journal deletion or VACUUM is requested during workload timing; autovacuum remains
enabled. All history remains until owned-database cleanup. Thus results measure
application lag separately from physical cleanup, and do not prove long-run storage
equilibrium. Global WAL includes other/background server work. Real document encoding,
indexes, concurrent replica loading, remote SQL, Linux/Kubernetes and hours-long
steady-state behavior remain outside this bounded experiment.

### Focused reader-affinity follow-up

The main comparison showed large quiet-reader throughput variation. These additional
modes repeat paired JournalOnly/Coalesced comparisons at 4 KiB for both key patterns,
pinning only each dedicated native reader thread to Windows logical processor 0.
The previous affinity is restored when that thread exits. Foreground writers,
background appliers, PostgreSQL and other applications retain their normal affinity.
This is an optional local measurement control, not a product hosting requirement.

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-read-smoke .local/postgres-connection.txt .local/postgres-apply-read-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-read-measure .local/postgres-connection.txt .local/postgres-apply-read-measure.json
```

The focused smoke has four measured two-second scenarios at 1,000/s. The measured
follow-up has eight 20-second scenarios at 10,000/s in two reverse-order passes.
Each includes the same saved-state, cursor and journal verification; ownership,
timeouts and ten-second drain budget are unchanged. It requires Windows and fails
explicitly if reader affinity cannot be set. It does not fix CPU frequency, isolate
the machine, control PostgreSQL scheduling or replace full-engine query benchmarks.

### Sustained comparison of compact saved units

These modes compare the accepted coalescing method at **256 B, 1 KiB and 4 KiB**
persisted units. The current decision selects 256 B as the PostgreSQL v1 baseline;
the three-size matrix remains intact for reproducibility. Every scenario uses the
same 128 MiB image, now made of 524,288 fixed 256-byte records. This permits every record to fit in the smallest
unit: the units contain 1, 4 or 16 records. The changed value/complement still totals
16 bytes and the journal command remains 32 bytes. This changes the logical record
width/count relative to the earlier 2 KiB-record experiment, so its results are not
a repeat of that exact workload.

```powershell
$env:DOTNET_TieredCompilation = '0'
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-unit-smoke .local/postgres-connection.txt .local/postgres-apply-units-smoke.json
dotnet Prototypes/PostgresStorageComparison/bin/Release/net10.0/PostgresStorageComparison.dll apply-unit-measure .local/postgres-connection.txt .local/postgres-apply-units-measure.json
```

Use new report paths. Smoke runs four three-second scenarios at 1,000/s. Measure
runs one three-minute journal-only control followed by three three-minute coalesced
variants at 10,000/s, in ascending unit-size order. Each offers 1.8 million changes. The 10,000/s rate represents peak demand; the long
constant plateau is deliberate stress, not a mandatory normal-load requirement.
The deterministic uniform permutation visits all 524,288 keys before repeating;
there is no hot-key concentration. One pass limits total foreground load to twelve
minutes; setup, verification and draining add time within the 25-minute invocation
budget. Per-scenario cancellation headroom is 270 seconds for the measured mode.

All cases retain the same foreground batching/queue, 128-journal-batch application
limit, nominal 250 ms interval, 32 MiB reusable payload cap and ten-second drain
budget. Full batches continue immediately. The control leaves its entire journal
unapplied by design. SQL EXTERNAL storage and durable commit settings are unchanged.
Preflight checks atomic rollback, nonempty-tail recovery and worker reopen at every
unit size. Setup and complete verification are outside offered-load timing.

The dedicated reader is pinned to logical CPU 0. Its separate integrity-probe data
now holds 8 MiB of native value pairs plus managed per-key gates. All reads check
integrity; one in 65,536 is timed so the bounded 500,000-sample array covers the longer
run. `UnsampledAfterCapacity` must be checked. This measures a tiny locked native
read, not an index lookup or document/query API. Other threads and PostgreSQL retain
normal scheduling; a single ordered pass is not a statistical interference estimate.

`ArrivalWindows` divides requests by intended-arrival minute, reporting completed,
rejected and end-to-end latency. The 250 ms progress samples independently show how
many changes actually reached the journal and saved state over time. A tail at the
last instant is not by itself evidence of growing lag; inspect its trend and oldest
age throughout all three minutes, then distinguish catch-up after arrivals stop.

Whole-process RAM includes the 128 MiB writer image, separate reader data/gates,
per-request timing/acknowledgment arrays, sample buffers and driver state. Those
measurement arrays grow with the configured run length, not with application lag.
Workspace allocation accounting excludes transient driver/SQL costs and is not
whole-worker or PostgreSQL memory. No full journal RAM copy or forced GC is used.
No logical journal deletion occurs during load; autovacuum remains enabled. Thus
this checks sustained application for minutes, not indefinite disk equilibrium,
full document behavior, concurrent replica loading or server crash recovery.
