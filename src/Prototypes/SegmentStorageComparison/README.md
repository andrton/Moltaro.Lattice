# Managed and native segment comparison

Compare identical packed records in managed byte arrays and native zeroed
allocations. One shared workload owns all record access; this is a bounded,
single-threaded allocator experiment, not the Moltaro.Lattice engine or a concurrency
proof. No third-party packages or external services are required.

## Run

Requires the .NET 10 SDK. From the repository's `src` directory:

```powershell
dotnet build Prototypes/SegmentStorageComparison/SegmentStorageComparison.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet Prototypes/SegmentStorageComparison/bin/Release/net10.0/SegmentStorageComparison.dll managed 256
    dotnet Prototypes/SegmentStorageComparison/bin/Release/net10.0/SegmentStorageComparison.dll native 256
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
```

Repeat in reverse order, sequentially in fresh processes. A 16 MiB run is the
correctness smoke. Payload is explicitly limited to 16..512 MiB, in multiples of
4 MiB. Raw JSON belongs under the ignored .local directory. Each
process disposes its native allocations, including partial construction failure.

## Method

- 4 MiB segments contain 32-byte records: four little-endian Int64 values.
  No CLR references or per-record objects occur in the payload.
- Both backends expose bounded spans; shared code uses BinaryPrimitives. Timings
  include interface dispatch, segment lookup, bounds checks and checksum work.
- New byte arrays and NativeMemory.AllocZeroed start with zeroed bytes. Setup
  includes allocation and writing every record, so pages are touched in both.
  Native pages may be zeroed lazily by the allocator/OS; calloc and CLR array
  allocation need not perform equal physical work before the writes.
- Seed 20261112 supplies 131,072 random positions over the entire payload.
  Repeating these positions warms a subset; this is not uniformly cold random I/O.
- Each operation has three warmup invocations and five samples. Random reads
  and updates run eight invocations per sample; scans run two. Reported ns/item
  are batch averages, not individual-request latency percentiles.
- Every field is verified before operations, after updates and after a complete
  segment replacement pass. Read/scan/update checksums are checked against
  expected values; duplicate random positions intentionally repeat idempotent
  writes. Replacement must preserve all changed and unchanged records.
- Replacement allocates, copies and installs each segment once. Native storage
  frees the old segment immediately after copying because no readers exist.
  Managed storage drops its reference and leaves collection to GC. This measures
  segment churn, not per-record update or safe concurrent reclamation.

## Memory and interpretation

Forced full collections occur before setup and after initialization, outside
timed phases, to estimate retained managed storage. RetainedManagedBytes excludes
native payload: always consider PayloadBytes and process working/private memory
alongside it. Process memory is a snapshot including runtime and helper data,
not a precise arena-overhead measurement or a peak-memory measurement.

Replacement reports managed allocated bytes and GC collection counts during its
one pass, followed by heap/process snapshots without a forced collection. These
are end-of-pass observations, not measured peaks. There is no forced GC in that
pass. Native payload allocations are not counted by GC.GetAllocatedBytesForCurrentThread.
Neither backend uses an application-level segment pool. Allocator caches, delayed GC and
OS behavior mean neither free nor dropping an array guarantees immediate RSS
reduction. Native storage has explicit using/Dispose ownership here; the prototype
does not rely on finalization or model GC memory-pressure integration.

The default project GC mode is retained and reported; it is not a selected engine
GC configuration. The run does not cover server GC under request pressure, Linux,
container limits, multi-gigabyte heaps, strings, indexes, record reclamation,
concurrent readers/writers, pinning, memory mapping, persistence or networking.
Comparing these backends does not select a row/column layout or a binary format.
Recorded results and the accepted allocation decision belong in
[Q05](../../../docs/decisions/memory-and-access.md#q05-should-the-arena-use-managed-or-native-segments).
