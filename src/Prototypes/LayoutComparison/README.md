# Row versus column layout

## Question and boundaries

Compare two representations of the same 64-byte numeric record:
one array of structs versus nine arrays of columns. This first experiment isolates
memory layout. Neither layout is the Moltaro.Lattice arena; both use managed arrays.

The experiment measures:

- Random full-record reads using precomputed slot positions. Every field contributes
  to an observable checksum. This is not a key-index lookup.
- A filtered scan reading three fields. Reported time is per visited input row,
  not per matching row.
- Random updates of two fields, with repeated positions allowed. The same assignments
  repeat across samples; there is no equality/no-op optimization.

Both representations have the same payload. This experiment does not measure strings,
indexes, segmentation, concurrency, native memory, schema/code generation, persistence,
networking or an external reference implementation. It cannot establish a universal winner or a production latency SLO.
The column layout assembles a value-type record for full reads; that does not allocate
a separate managed object.

## Run

Requires the .NET 10 SDK. No third-party packages or external services are used.
Run from the repository's `src` directory:

```powershell
dotnet build Prototypes/LayoutComparison/LayoutComparison.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet Prototypes/LayoutComparison/bin/Release/net10.0/LayoutComparison.dll row 250000 50000
    dotnet Prototypes/LayoutComparison/bin/Release/net10.0/LayoutComparison.dll column 250000 50000
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
```

Run each representation in a fresh process, sequentially. Repeat in reversed order
and with a different row count before drawing a conclusion. Raw JSON may be redirected
under .local; it must not be committed.

## Method and correctness

Positions come from Random with seed 20261112. The program validates the initial
full-state checksum, selected records and filter result against values generated
directly from the input formula. It validates the complete state after updates too.
Any mismatch fails the process.

Each operation has one initial call and five warmup calls, followed by seven samples
of twenty invocations. Tiered compilation is disabled by the documented command.
Results report the median, minimum and maximum sample time per item. These are
batch averages, not individual request percentiles.

Allocation counts cover the synchronous measured loop, excluding setup, delegates
and reporting. PayloadBytes counts only the numeric arrays' contents.
ApproximateRetainedManagedBytes is a forced-GC heap delta around construction;
it is approximate, excludes the position array and is not process RSS or container
memory. Runtime, OS, architecture and logical processor count are emitted with results.

Measurements and interpretation belong in the canonical
[prototype evidence](../../../docs/design/prototype-evidence.md).
