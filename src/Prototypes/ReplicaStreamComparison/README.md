# Full memory image over a local stream

This bounded experiment asks how quickly an existing memory image can be sent
through one TCP connection into a fully retained destination. It uses two separate
arenas in one process and Windows loopback in the recorded runs. It does not
measure a cross-node network or complete database initialization.

## Run

Requires .NET 10; no third-party packages or external services. From the repository's
`src` directory:

```powershell
dotnet build Prototypes/ReplicaStreamComparison/ReplicaStreamComparison.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
dotnet Prototypes/ReplicaStreamComparison/bin/Release/net10.0/ReplicaStreamComparison.dll 8
dotnet Prototypes/ReplicaStreamComparison/bin/Release/net10.0/ReplicaStreamComparison.dll 5120
```

The argument is total image MiB, a multiple of 4 from 4 through 10,240. Both the
source and destination remain resident: the 5 GiB run needs approximately 10 GiB
of arena payload plus runtime/socket overhead. Check available memory before a
large run. Run sequentially in fresh processes. Raw JSON belongs under ignored
.local. The listener binds only loopback on an ephemeral port and
closes on completion; transfer has a 60-second cancellation limit.

## Method

Source preparation allocates and fills 4 MiB segments before transfer timing.
Segments contain deterministic bytes and their ordinal in both ends. Transfer
timing includes connect, destination allocation/zeroing/first touch, full stream
receipt and sender completion. No full staging copy is allocated at the receiver.

Verification scans every destination byte after transfer and checks both segment
ordinals. Its elapsed time is reported separately and together with transfer.
The payload is deliberately simple; no compression is enabled. Correctness fails
the process on mismatched bytes. The small run checks the path before large runs.

No schema, records, indexes, checkpoint capture, concurrent writes, catch-up,
container limits, TLS, real network link or application query readiness exists
in this experiment. Both ends share a process and runtime. Reported arena payload
is a known byte count, not measured process RSS or peak memory. Source preparation
is not an implementation of a coherent snapshot of a changing database.

## Recorded local baseline

On 12 November 2026: Windows x64, .NET 10.0.15, Intel Core i9-13900H, approximately
29 GiB free RAM before starting. One 8 MiB smoke run and two sequential 5 GiB runs
passed complete payload verification.

| Phase | 5 GiB run 1 | 5 GiB run 2 |
| --- | --- | --- |
| Source allocation and filling, outside transfer | 1.03 s | 1.10 s |
| Transfer plus destination allocation | 10.79 s | 8.94 s |
| Complete destination verification | 1.29 s | 1.30 s |
| Transfer plus verification | 12.08 s | 10.24 s |
| Transfer payload throughput | 0.463 GiB/s | 0.559 GiB/s |

These two runs are a baseline, not a percentile or a physical network ceiling.
They do not establish 5–10 second database readiness. Cross-node tests with actual
selected data, indexes, concurrent changes and simultaneous child startup remain
necessary. The accepted startup lifecycle and its limits are in
[Q08](../../../docs/decisions/replication-and-hosting.md#q08-can-a-child-load-everything-before-accepting-reads).
