# Bounded operation-retry protocol experiment

Measured on 14 November 2026 using .NET 10 and local PostgreSQL with fsync,
full_page_writes and synchronous_commit enabled. No server setting was weakened.
The experiment implements the SQL state machine in the
[operation protocol](../../../docs/design/operation-retry-protocol.md), independently of the
existing arena/WriteBatch experiments. It is not a production SDK or a load test.

## What is exercised

Two durable transitions: compact Admitted metadata, then journal/effects/Committed
result in a single transaction. A per-database control-row lock serializes admission,
execution and retirement. Receipts bind a history/generation/UUID token to SHA-256
request contents. An injected seconds clock permits deterministic expiry checks
without waiting fifteen minutes. Production callers cannot supply that clock.

The fixture applies non-idempotent integer effects to one or two SQL rows so duplicate
execution is detectable; these additions are test effects, not new Lattice arithmetic
commands. Its SQL object rows stand in for applied effects. It does not implement the
production 256-byte base, native arena, replication or group commit. Earlier
[WriteBatch](../WriteBatch/README.md) and [saved-unit](../WriteBatch/UNITS.md) experiments
cover those separate publication/storage boundaries.

Thirteen directed scenario groups passed:

1. Lost admission response preserves the original first-admission deadline.
2. Lost committed response resolves to the original result without duplicate effects.
3. Concurrent identical admission/execution converges on one result.
4. Changed payload under the same token is rejected, including concurrent admission.
5. A failure between two effects rolls back both; reopen preserves admission and can resume.
6. Configuration changes apply prospectively; closed generations refuse new IDs.
7. Expired pending/committed operations cannot execute on repeat.
8. A nondecreasing clock floor and retirement boundary survive physical receipt deletion.
9. Repeating an absent Delete preserves an object inserted afterward.
10. Definitive rejection remains terminal on repeat.
11. Retirement waits for a real PostgreSQL transaction started before expiry;
    the check observes its SQL lock waiter before releasing the controlled commit.
12. Retirement boundary and receipt deletion roll back/commit atomically.
13. Missing receipts report Unknown; new history rejects old tokens.

Two additional worker processes exit abruptly: **91 before effects COMMIT** and
**92 after COMMIT before the reply**. Fresh recovery processes observe Admitted and
Committed respectively, preserve expiry, and verify exactly one complete effect after
repeated resubmission. The PostgreSQL service itself is never terminated.

## Cost measurements and interpretation

After two warmup operations, twelve paced one-root operations were timed. A complete
sample includes connection acquisition, session settings, admission transaction and
effects/result transaction. No arena publication, socket replication or child wait
is included. These are medians/maxima of a small sample, not p99 or throughput evidence.

| Connection handling | Admission median / max | Complete two-stage call median / max |
| --- | ---: | ---: |
| Initial diagnostic run: fresh authenticated connection for each transition | 75.018 / 156.856 ms | 141.409 / 199.223 ms |
| Final bounded pool, maximum four connections, warmed samples | 2.568 / 11.545 ms | 7.403 / 13.672 ms |

The first run included opening/authenticating a new SQL connection twice per call.
Changing only connection reuse produced the second run; production uses a bounded
provider-owned pool. Retain the first numbers as evidence of that configuration
cost, not as a product write-latency result. The accepted <=50-ms child-write target
still needs the complete implementation under the reference load.

Final run: seven sequential workers, **14.049 seconds summed wall time**, **2.125
seconds worker CPU**, largest sampled worker working set **44.465 MiB**. The paced
loop allocated 404,464 managed bytes including warmup; final collected managed heap
was 593,904 bytes. Worker CPU/working set excludes the existing PostgreSQL server.
No retained-memory-per-receipt or 10,000-operations/s claim follows from this fixture.

Both runs passed correctness and verified deletion of their owned schemas. Each run
creates a random `lattice_retry_<run-id>` schema, touches no other data, and cleans up
in the runner's finally block. Credentials and raw results remain in ignored `.local`.

## Reproduce

From the repository's `src` directory with the .NET 10 SDK and a local PostgreSQL connection string
in an ignored text file:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet build Prototypes/OperationRetry/OperationRetry.csproj -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false
./Prototypes/OperationRetry/Run-Bounded.ps1 -ConnectionFile .local/batch-postgres-connection.txt
```

The runner uses one logical processor and BelowNormal priority on Windows; each
worker has a 45-second wall / 8-second CPU / 192-MiB sampled working-set budget.
SQL commands have a three-second timeout and a two-second lock timeout. Workers
and the owned schema are cleaned up on test failure. No load-test window is needed
for this small mechanism check.

Network response loss is modeled by discarding the result, not by a faulting TCP
proxy. Physical power/server crashes, production clock discipline, partial packet
delivery, complete canonical schema encoding, active-coordinator cancellation,
native publication and multi-pod integration remain release checks. The experiment
does not prove those behaviors and does not authorize silently weakening their contracts.

## Unique-index extension

The default bounded runner includes these checks alongside the PostgreSQL protocol
and crash/recovery checks. The final combined run passed all 13 protocol scenarios,
nine Unique scenarios and both process recovery scenarios, then removed its SQL schema.

After the explicit Unique decision, nine small checks passed over Arc.Collections
2.2.0 OrderedMultiMap: duplicate insertion, whole-batch rejection, within-batch
collision, atomic key swap, delete/reclaim, composite scope, any-null exemption,
OrdinalIgnoreCase equality and concurrent writes to different roots with exactly
one winner. The validator checks the projected end state under an ordered writer;
it does not clone the full index.

The fixture has at most 32 managed slots. It does not implement native preparation,
node reservation, PostgreSQL persistence or replica publication for unique indexes.
Those are integrated engine acceptance checks. The first passing worker took
0.730 seconds wall, 0.141 seconds CPU and 29.828 MiB sampled working set.

Run this extension alone without a PostgreSQL connection file:

```powershell
./Prototypes/OperationRetry/Run-Bounded.ps1 -UniqueOnly
```
