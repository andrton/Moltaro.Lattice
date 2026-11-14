# Packed string encoding comparison

Compare the same text in a contiguous UTF-8 byte array or UTF-16 char array, with
identical offset/length descriptors. This is an encoding experiment, not a string
dictionary, database engine or decision about managed versus native storage.

## Run

Requires the .NET 10 SDK; no third-party packages or external services. From the
repository's `src` directory:

```powershell
dotnet build Prototypes/StringEncodingComparison/StringEncodingComparison.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
$previousTiering = $env:DOTNET_TieredCompilation
try {
    $env:DOTNET_TieredCompilation = '0'
    dotnet Prototypes/StringEncodingComparison/bin/Release/net10.0/StringEncodingComparison.dll utf8 ascii
    dotnet Prototypes/StringEncodingComparison/bin/Release/net10.0/StringEncodingComparison.dll utf16 ascii
} finally {
    $env:DOTNET_TieredCompilation = $previousTiering
}
```

Run each encoding in a fresh process, sequentially, then reverse the order. Repeat
with `cyrillic` and `cjk`. Raw JSON belongs under the ignored .local.

## Method and limits

Each corpus has 4,096 distinct strings: a fixed language-specific prefix followed
by five ASCII digits. The Cyrillic and CJK corpora also contain ASCII separators;
their payload ratios do not describe pure Cyrillic or CJK text.

The same seeded random sequence contains 8,192 positions. Three warmup invocations
precede five samples of eight invocations per operation. Results include median,
minimum and maximum batch time per read, plus maximum managed allocation per read.
The small working set is repeatedly visited. Timing includes loop, interface call
and checksum overhead; allocation-heavy samples vary substantially.

Three operations are measured:

- Materialize a new CLR string from stored text.
- Copy/decode into one preallocated char buffer.
- Compare exact text against one pre-encoded needle. This workload is mostly
  misses with a long common prefix; it is not an index lookup or ignore-case test.

Every stored value is checked against the source using Ordinal equality, including
its owned string, copied chars and needle comparison. Additional cases cover empty
text, embedded NUL, distinct normalization forms, supplementary scalars and BMP
characters. Each measured batch must match an independently derived checksum.
The UTF-8 encoder rejects malformed input; malformed-input behavior is not a timed
operation or a selected database contract.

PayloadBytes counts encoded text only. SlicePayloadBytes counts two Int32 values
per string. Neither includes array/object headers, source CLR strings, positions,
needle preparation or buffers. Source strings remain allocated during the run;
these numbers are not retained-heap or process-memory measurements. Setup and
reporting are excluded from per-read allocation counts.

Writes, dictionaries, indexes, OrdinalIgnoreCase, concurrent mutation, persistence
and networking are absent. The experiment does not establish database latency or
a universal encoding winner. Recorded results and interpretation are in
[the architecture discussion](../../../docs/decisions/strings.md#q04.1-how-should-strings-be-encoded-in-storage).
