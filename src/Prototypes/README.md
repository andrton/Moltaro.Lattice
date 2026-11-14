# Synthetic prototypes

These standalone experiments measure individual costs. None is the database engine
or evidence that its concurrent allocator, replication or recovery is complete.

The twelve source projects and their measurement notes were retained during the
14 November 2026 design transfer. They are outside `Moltaro.Lattice.slnx`, are not
shipped packages, and do not replace production tests. No private datasets, connection
files, raw run results or build outputs were transferred.

Run the commands below and in each experiment's README from the repository's `src`
directory. Relative `Prototypes/...` paths and ignored `.local/...` run/connection
paths are relative to `src`. Package versions and lockfiles remain pinned per
experiment to preserve the measured dependency versions. This is an intentional
historical experiment boundary, not the package policy for production projects.
The local build properties inherit the repository's settings and disable packaging.

Some older commands are substantial performance runs. Inspect their documented
budgets and select a small correctness mode first; copying the sources does not
authorize a full benchmark sweep. The latest small check can run without PostgreSQL:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet build Prototypes/OperationRetry/OperationRetry.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
./Prototypes/OperationRetry/Run-Bounded.ps1 -UniqueOnly
```

Current work is the [physical engine foundation](../../docs/design/engine-foundation.md) for a
first production milestone with PostgreSQL durability and one database per instance.
The [MemoryOnly experiment](../../docs/design/first-prototype.md) is a historical scope proposal,
not that release plan. Existing benchmarks can support focused mechanism checks.

| Existing experiment | Measures | Important limitation |
| --- | --- | --- |
| [LayoutComparison](LayoutComparison/README.md) | Packed numeric rows versus columns | Managed numeric arrays; no allocator, strings or concurrency |
| [StringEncodingComparison](StringEncodingComparison/README.md) | UTF-8/UTF-16 storage and access | No dictionary reference counting or reclamation |
| [SegmentStorageComparison](SegmentStorageComparison/README.md) | Managed/native segments and replacement | Single-threaded; not reader-safe moving compaction |
| [ReplicaStreamComparison](ReplicaStreamComparison/README.md) | Full-image TCP streaming on loopback | No indexes, simultaneous updates or complete replica readiness |
| [PostgresStorageComparison](PostgresStorageComparison/README.md) | Durable batches, unit updates and background journal application | Synthetic storage protocol; not full-engine persistence or crash recovery |
| [PrimaryIndexComparison](PrimaryIndexComparison/README.md) | Faster.Map/Arc retained and resize memory, read/write costs, bounded native key and pre-commit capacity probes | Full root lifetime/arena integration remains untested; first-run timings are preliminary; ArcSlim selected for the first implementation |
| [SecondaryIndexComparison](SecondaryIndexComparison/README.md) | Bounded Kaos RankedMap/Arc OrderedMultiMap retained memory, reads, key changes and preparation before simulated commit | Numeric keys, 10,000 entries, single-threaded; Arc accepted with higher retained memory; engine concurrency and native-key integration remain untested |
| [RootEntryComparison](RootEntryComparison/README.md) | Managed struct pages versus native pages: storage accounting, read/update access and incremental-growth peak | Managed struct arrays accepted for v1 control entries; synthetic 32-byte entries and page sizes are not production defaults; root locks and concurrent reclamation remain untested |
| [ReplicaBootstrap](ReplicaBootstrap/README.md) | Correctness of native-block initial capture with concurrent logical changes, deletes/recreation and WriteBatch; per-root watermarks, catalog fence and readiness handoff | Mechanism accepted for v1 after 127 checks and three expected negative controls; tiny fixed-layout blocks, no network, shared strings/chunks or production parallel publication |
| [ArenaLifecycle](ArenaLifecycle/README.md) and [Arc integration](ArenaLifecycle/INDEXES.md) | Native lifetimes/compaction plus real Arc primary and composite secondary indexes, protected shared text, candidate handoff and bounded latency/memory measurements | Original fixture groups plus seven index scenarios and three indexed off/on pairs; one core and coarse gates, Int32 primary path with separate string-hash checks; no WriteBatch, SQL, replication or general schema/collection engine |
| [WriteBatch](WriteBatch/README.md) | Multi-type atomic Upsert/Delete on the same arena/indexes, real PostgreSQL commit, safe retry, faulted-instance reopen and four application-crash boundaries; bounded pause/memory measurements | Serialized preparation, 256 synthetic roots, batches of 1/8/32; fixture base/journal rather than the production 256-byte base/applier; no replication, general schemas or network-ambiguity test |
| [WriteBatch saved-unit integration](WriteBatch/UNITS.md) | The same path with 256-byte saved units, background coalescing, atomic base/cursor commits, safe cleanup, retained receipts, concurrent recovery snapshots and nine application-crash boundaries | Fixed fixture codec, full-root Upsert/Delete, bounded single-core off/on samples; no general schemas, receipt expiry, network ambiguity or cross-node visibility |
| [OperationRetry](OperationRetry/README.md) | Durable admission/results, duplicate execution prevention, bounded expiry/retirement and two process-crash boundaries; 13 directed groups | Tiny SQL effect model; injected clock; no native arena, full codec, network fault proxy or throughput claim |

Run commands from the repository's `src` directory. Each experiment documents its .NET SDK, resource
limits and commands. PostgreSQL-backed modes need a local server; MemoryOnly modes do not.
Public input is synthetic. Credentials, raw results and build output are not committed.
Use the [evidence record](../../docs/design/prototype-evidence.md) to distinguish observations
from design hypotheses. The historical MemoryOnly proposal describes a comparison using the same generated
records and operation trace against straightforward and optimized managed baselines.
