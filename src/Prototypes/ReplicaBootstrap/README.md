# Bounded replica bootstrap correctness prototype

Test the initial-loading mechanism accepted for v1 on 13 November 2026, while master
writes continue. The accepted contract is owned by
[replica startup and hosting](../../../docs/decisions/replication-and-hosting.md#initial-capture-and-readiness-handoff).
The experiment transfers detached copies of small native blocks plus per-root revision
metadata, then replays a retained in-memory transaction stream. PostgreSQL, sockets,
schema generation and a second full arena on either node are not involved.

This is a protocol model, not the production engine or a throughput benchmark.
Production backing/index choices are unchanged: the model uses a simple metadata
list/map and one short publication gate to make interleavings explicit. It does not
test the subsequently accepted [concurrent preparation and ordered commit/publication
stage](../../../docs/decisions/write-acknowledgment.md#ordered-commit-and-publication)
or replace accepted root/index protection.
Model writes stage replacement 64-byte blocks to check validation/publication; this
does not change the accepted targeted in-place partial-update contract or introduce
a production copy-on-write scheme for every update.

## Run and resource budget

Requires .NET 10 SDK and PowerShell 7. No packages or external services. From the
repository's `src` directory:

```powershell
$env:DOTNET_PROCESSOR_COUNT = '1'
dotnet restore Prototypes/ReplicaBootstrap/ReplicaBootstrap.csproj
dotnet build Prototypes/ReplicaBootstrap/ReplicaBootstrap.csproj -c Release --no-restore -m:1 -nodeReuse:false -p:UseSharedCompilation=false
pwsh -NoProfile -File Prototypes/ReplicaBootstrap/Run-Bounded.ps1
```

The runner executes four focused groups sequentially at BelowNormal priority with
one runtime processor, workstation GC and tiering disabled. Each worker has a
10-second wall/3-second CPU budget and a sampled 192 MiB working-set guard. Only
the owned worker is terminated on failure. Run one group with `-Groups directed`
(or `generated`, `negative`, `threaded`). Raw JSON/logs are ignored under
`.local/replica-bootstrap-*`. No background load generator or unbounded loop exists.

Each native root block is 64 bytes: an Int32 number, a list count and up to 14 Int32
items. The catalog is capped at 64 slots and explicit batches at four distinct roots.
Pending delivery is capped at 128 transactions/64 KiB of modeled encoded content;
managed metadata/allocator overhead is additional. These are fixture limits, not
proposed production settings. Most checks are deterministic interleavings. One check
uses a real writer thread with event/join deadlines, without arbitrary sleeps.

## Tested protocol

1. At published transaction position **S**, register delivery retention and record
   the current catalog slot limit under the same short publication gate. Scan only
   that fenced range. New roots are represented by full Upserts in the retained tail.
2. Preserve identity/revision metadata of deleted fenced entries while capture needs
   them; do not reuse fenced slots. Free their native payload immediately after
   protected access permits it. A deletion frame carries a revision without old data.
3. Copy one root block and its revision coherently, release the gate, then deliver
   the detached bytes. Captured roots can belong to different published positions.
   No domain-object serialization is used to form the copied block image.
4. After the complete scan, record published position **R**. Install every base frame
   before replay. Replay every complete transaction in `(S, R]` in order, filtering
   each member against that root's captured/current revision. A global maximum base
   revision cannot stand in for per-root coverage.
5. Publish **Ready and applied position R together** only after the full range and
   complete batches are processed. Before that, the mixed-version arena is unreadable
   and has no public applied acknowledgment. Newer transactions remain pending and
   are applied as complete live batches after this fixed boundary.
6. Acknowledgment releases delivery immediately. Once initialization completes, remove
   temporary deletion watermarks and release the catalog-retention claim. A missing
   base frame, missing tail range or expired retention invalidates initialization;
   it cannot expose a partial database or fall back to SQL.

Revisions in this model are monotonically increasing **published transaction positions**,
including across delete/recreate of the same typed key. There is no counter reset on
reinsertion or durable image retained for each historical revision. This is a model
internal encoding, not an accepted public revision-token encoding. A correct
publication prefix is an assumption; concurrent commit-order scheduling is not tested.

The model defers reuse of all catalog holes during initialization for simplicity.
Only control metadata is retained for deleted roots. In a real allocator this retention
also needs a budget; no indefinite metadata growth is permitted. Whole-root copy buffers,
shared dictionary blocks and large/chunked owned trees still need integration work.

## Coverage and independent verification

- Directed cases cover updates before/after capture, deletion before/after capture,
  recreation under the same key, insertion of a new key beyond the fence, distinct
  entity types with equal external IDs, and WriteBatch crossing two capture times.
- Complete-frame checks keep live data/progress unchanged while only part of a batch
  has arrived. Incomplete initial batches never become Ready. Duplicates do not repeat
  collection appends. Missing ranges/frames and retention exhaustion fail explicitly.
- Enumerate all 21 interleavings of two captures and five ordered publications, in
  both initial catalog orders (42 histories). The publications append, delete,
  recreate, apply a cross-type batch and perform another partial update.
- An additional 64 deterministic seeds each publish 24 transactions over six typed
  keys, including batches and varied capture times. A separate logical oracle applies
  the submitted commands directly to ordinary values/lists; it shares neither the
  binary codec nor capture/replay rules. Compare complete key sets and all values.
- The threaded case delays delivery of an already-copied block while a writer updates,
  deletes/recreates and publishes a batch. It verifies detached data stability and
  that the capture gate does not remain held while delivery waits.
- Three deliberately broken replay modes are negative controls: unconditional replay,
  dropping deletion watermarks, and skipping transactions by the largest base revision.
  Their expected counterexamples are reported separately from passing candidate checks.
- Every scenario disposes native blocks; each group verifies zero remaining blocks.

Fixtures use at most one mutation per root in a batch, consistent with the later
[accepted input rule](../../../docs/decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch).
This model does not validate the production SDK's duplicate-input behavior.
The model's ready-state observation helper
reads multiple values under its test gate solely for assertions; it is not a new
public snapshot API. The independent oracle's small managed copies are test data,
not a second arena used by the transfer mechanism.

## Limits and decision status

This checks ordering and visibility on tiny blocks. It does not measure startup time,
prove lock-free/parallel engine correctness, test real sockets, validate schema changes,
or implement string-dictionary/chunk relocation, production native allocation,
multiple simultaneous initializing children or PostgreSQL crash recovery.

See [measurement evidence](../../../docs/design/prototype-evidence.md#bounded-replica-bootstrap-correctness)
for observations and the subsequent v1 acceptance. The mechanism is accepted;
production integration is not implemented or proven by this bounded model. Existing
memory-only replication and complete-readiness contracts stand.
