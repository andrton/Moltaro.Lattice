# Bounded root-entry storage comparison

Compare managed arrays of structs with a native paged table. Both contain identical
synthetic 32-byte root-control entries: location, revision, generation and three
integer bookkeeping fields. These fields are inert data, not an implemented lock,
retention protocol or accepted production layout. Root objects and indexes are absent.

## Run and resource limits

Requires .NET 10 SDK and PowerShell 7. No packages, database or external data.
Run from the repository's `src` directory:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet restore Prototypes/RootEntryComparison/RootEntryComparison.csproj
dotnet build Prototypes/RootEntryComparison/RootEntryComparison.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
pwsh -NoProfile -File Prototypes/RootEntryComparison/Run-Bounded.ps1
```

The runner executes 12 fresh workers sequentially: two representations, two counts
(32,768 and 262,144), three repetitions with alternating backend order. Repetitions
may be reduced to one or two; counts are fixed in the program. Each worker uses one
benchmark thread, BelowNormal priority, workstation GC, runtime processor count one
and disabled tiered compilation. These settings do not establish CPU affinity or an
OS CPU quota. The runner pauses 150 ms between workers and checks every 50 ms for
10-second wall, 5-second CPU and 256 MiB working-set limits, killing only its own
worker on failure. The memory guard is sampled, not an instantaneous OS reservation.

Maximum table payload is 8.125 MiB including one growth page. The random trace is at
most 1 MiB; .NET runtime/JIT/GC and process overhead are additional. There is no
background stress generator or no-GC-region reservation. JSON and worker logs go
under ignored `.local/root-entry-*`. A failed worker stops the run.

## Equal geometry and measurement

- Each page contains 4,096 entries (128 KiB). The directory starts at four slots
  and doubles. Both representations use a managed directory; its entries are
  managed array references or native addresses. Growth adds one zeroed page and
  may copy the small directory; it never copies all existing entry pages.
- Native pages use `NativeMemory.AllocZeroed`/`Free`. Managed pages are ordinary
  `RootEntry[]`. Both validate the logical index. Managed element access retains
  normal array bounds checks; the native offset is bounded by the common geometry.
- Value-type accessors specialize the timed generic loops. The measurement excludes
  virtual dispatch per entry, index lookup, root synchronization, storage and network.
  Accessors are short-lived borrowed values; concurrent growth/disposal is unsupported.
- Before memory measurement, initialize JIT paths and allocate the deterministic
  shuffled trace. Fill/touch every record. Count managed allocations around each
  directory/page allocation and owner construction; subtract superseded directories
  from owned storage. Add requested live native bytes for **accounted retention**.
  This includes managed headers/alignment but excludes native allocator overhead
  and GC spare space; it is not RSS. Allocation-counter intervals can include
  incidental runtime allocations, so this is an upper estimate of owned storage,
  not an exact heap-object-size API. Cross-check against the full-GC heap delta and
  repeated samples; report discrepancies. Process private/working-set deltas are
  separate, noisy observations, especially for small tables.
- Growth crosses both a page and directory boundary. Measure allocation and touching
  the complete new page. The conservative **accounted growth peak** is pre-growth
  accounted retention plus newly allocated managed bytes and added native bytes,
  assuming no old directory is reclaimed during the step. This is an allocation-based
  upper bound, not a sampled process peak. Also record table-owned retention after growth and
  the process lifetime peak working set (runtime included, before final serialization).
- Read location/revision sequentially and in a shuffled full-table pass; update
  location/revision in the same shuffled order. A sample performs 262,144 accesses
  through repeated passes over the same trace. Use two warmups and seven samples per
  operation; report nanoseconds per entry access and fresh-process median ranges.
  This is a warm, single-threaded microbenchmark, not end-to-end database throughput.
- Verify read checksums, every field before/after growth and every final update.
  Retained references to old entries must survive directory growth and a full GC.
  Verify fresh-page zeroing, rejected out-of-range accesses, allocation-free timed
  loops and idempotent disposal. Native ownership is released on exceptional exits.

## Decision boundary

This isolates storage placement and incremental growth. It does not prove production
lock safety, root deletion/reuse, concurrent publication, compaction coordination,
performance under managed-heap pressure or total engine memory. Larger entry layouts,
page-size tuning and pooled synchronization state may change the tradeoff.

Results and the rationale for the accepted managed placement belong in the
[measurement evidence](../../../docs/design/prototype-evidence.md#bounded-root-entry-storage-comparison).
The [decision owner](../../../docs/decisions/physical-storage.md#managed-arrays-of-structs-for-control-entries)
accepts managed arrays of structs for v1 control entries. The synthetic entry/page
sizes and benchmark accessors are not selected production layouts or implementations.
