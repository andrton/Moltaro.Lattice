# MemoryOnly experiment: historical scope

**Superseded as the production milestone scope on 13 November 2026.** The first
production milestone requires PostgreSQL durability and one database per instance.
Physical architecture must be settled before its milestone/sprint plan is written.
See the [engine foundation](engine-foundation.md) for the current work.

The material below is retained as an isolated MemoryOnly experiment proposal, not an
approved production release plan. Its datasets, baselines and numerical hypotheses
may support focused engineering checks. Its single-writer load profile is a benchmark
setting, not a limit on concurrent application write callers. Deferred PostgreSQL
below does not apply to the production milestone. The current engine has no partition
abstraction; the experiment uses the same single-database context.

The [accepted production workload](performance-requirements.md#accepted-primary-workload-and-first-milestone-access-scope)
also requires secondary indexes and direct indexed access without OQL. Any index
deferral below belongs only to this historical experiment.

[Project overview](overview.md) · [Database design](../decisions/database-scope.md) · [Decision register](questions.md)

## Question to answer

Does packed native storage earn enough memory reduction over a reasonably optimized
managed representation to justify its ownership, allocation and concurrency complexity?
A failed experiment is a reason to simplify or reconsider the representation before
adding persistence, networking or a query language.

The example is directory data: users, groups and roles. These are stored domain
records, not a login service. Passwords, tokens, personal records, authentication,
authorization enforcement and administration screens are outside the experiment.
All identities and strings are generated from a published deterministic seed.

## Scope boundary

| Include in this slice | Defer |
| --- | --- |
| Embedded .NET engine, explicit MemoryOnly | Separate server and all network transport |
| One local database | Cross-database coordination or a multi-database management layer |
| Small declared schema: Int64 keys, Boolean, UTF-8 String and ordered key-reference Lists | Full scalar catalog, arbitrary nesting, Map/Set, schema migration and code generator |
| Runtime schema-bound field access plus hand-written benchmark adapters | Stable production SDK and generated POCO/view packages |
| One primary index per entity type, key lookup and .NET enumeration | Secondary indexes, OQL, joins and query planner |
| Insert, whole-document Replace and Delete through one writer | General partial-command language, multi-document or external transactions |
| Owned copies and short protected read-only views | Long-lived views, graph-wide snapshots and asynchronous borrowed access |
| Native segments, allocation/reuse and bounded incremental movement experiment | Full large/chunked collection machinery and tuned production allocator |
| Plain strings; one measured dictionary variant for explicitly selected fields | Automatic string mode switching and broad compression choices |
| Correctness checks and a console benchmark harness | Polished control UI, deployment packaging and operational feature set |

A single primary index is sufficient for the first slice. Permission traversal is
ordinary .NET code: User -> Group references -> Role references. There is no referential
integrity subsystem. Missing reference targets are explicit absence, never substituted
with fabricated records. The generic engine does not embed directory-specific policy.

The prototype opens empty and is populated from a synthetic fixture. It has no disk
recovery promise. A test input fixture is not a new persisted Lattice format.

## Synthetic workload and fair baselines

Use the same canonical records, input order, seed and operation trace for every backend.
The fixture generator is part of the first implementation step, not an existing tool.

| Record | Fields in the initial fixture |
| --- | --- |
| User | Id, Name, DisplayName, Department, Enabled, ordered GroupIds |
| Group | Id, Name, ordered RoleIds |
| Role | Id, Name, ordered PermissionCodes |

Names and IDs are synthetic. Include ASCII and valid non-ASCII UTF-8 strings, unique
names, repeated departments/permission codes, empty Lists, duplicate List references,
and bounded strings/Lists. A second high-cardinality text profile prevents a misleading
result that only works when almost every string is identical. Reference targets exist
in the normal fixture; deletion/missing-target cases are separate correctness scenarios.

- Smoke: 1,000 users, 32 groups, 16 roles; short deterministic mutation traces.
- Normal: 100,000 users, 1,000 groups, 128 roles.
- Scale: 1,000,000 users, 10,000 groups, 512 roles; explicit bounded benchmark run.
- Use eight group references per user, four role references per group and eight
  permission codes per role for the normal/scale profile; vary cardinality separately.

Compare three representations:

1. Straightforward POCO records, Dictionary key indexes and ordinary collections.
2. A reasonably optimized managed baseline: compact key arrays, shared immutable text
   where appropriate, no accidental duplicate payloads and the same read/write guarantees.
3. Native arena, first with Plain text and then with explicit dictionary fields.

The optimized managed baseline is the primary comparison. Do not claim a win solely
against avoidable allocations, a global string-intern pool with unbounded lifetime, or
an unsafe baseline that lets read results mutate unexpectedly. The native primary index
may be managed; its complete retained cost is included in native-backend totals.

Owned-copy reads compare equivalent owned results; protected reads compare equivalent
field access/checksums. Include the same synchronization and mutation consistency.
Measure key reads, field projection, full owned copy, full enumeration, graph traversal,
load/build and Insert/Replace/Delete. Enumerations preserve the existing coherent-record
and no-repeated-key contract; any key snapshot or workspace counts toward memory.

## Proposed numerical go/no-go targets

These are provisional experiment gates, not historical measurements or a product SLA.
Record CPU, RAM, OS, runtime, build settings, thread count and all limits with each run.
Use the same host, sequential fresh processes and reversed backend order. Numerical
gates apply to both text-cardinality profiles unless stated otherwise.

| Measure | Target for deciding whether to continue |
| --- | --- |
| Retained data memory | Native total <= 75% of the optimized managed baseline at 1,000,000 users; report the straightforward baseline separately |
| Read throughput | At least 80% of optimized managed throughput for equivalent key/projection/scan operations |
| Read latency under writes | p99 key lookup below 1 ms at the normal profile with 1,000 completed replacements/s; report the managed ratio too |
| Full local initialization | At most 5 seconds for the scale profile in a fresh process, including fixture read/decode, schema validation, arena fill and primary-index build |
| Bounded churn | After warmup and at least ten fixed-live-count replacement/delete/reinsert cycles over ten minutes, quiescent retained data memory <= 110% of the post-warmup level, with no continuing upward trend |
| Peak memory | At most 1.5 times the post-load backend footprint during the bounded mutation/compaction scenario, plus explicitly reported fixed harness/runtime costs; no second full arena |
| Protected hot path | Zero managed allocation per operation after warmup for fixed-field/key view reads; owned copies and string materialization are measured separately |
| Correctness | No torn record, stale freed-memory access, duplicate scan key, lost completed mutation or incorrect reference resolution in the defined scenarios |

Retained data memory is live/retained managed storage plus all allocated native segments,
indexes, gates, dictionaries and backend work buffers. Do not subtract allocator slack
or count only live payload. Report absolute bytes per user and total private/working-set
memory as separate measurements; managed GC counters alone cannot measure a native win.
Compare equal final logical states and release common generator/input staging before
retained measurements. Include staging in initialization and peak-memory measurements.

Fixture generation is outside the initialization timer; reading and decoding its output
is inside. Record file bytes and cache conditions. Fresh process does not mean cold OS
cache. Process launch/runtime startup is reported separately. No network or replica
startup target is inferred from this local loading target.

Use one writer and four readers in the concurrent profile. A 10,000 changes/s burst
is a separate bounded stress probe, not a prerequisite for ordinary directory traffic.
Report completed operations, offered rate, refusals and p50/p95/p99; batch-average loop
time is not a request percentile. Limit an individual scale worker to a 2 GiB budget
and stop explicitly if it cannot fit; do not silently reduce the fixture or omit overhead.

## Implementation sequence and stopping points

1. **Fixture and managed baseline.** Build the public seeded generator and shared
   operation trace. Establish reference results, realistic baseline memory and timing.
   Fail early if the fixture or measurement method cannot distinguish the hypotheses.
2. **Small native arena and key path.** Implement the schema subset, relative locations,
   bounded allocation/free-slot reuse, primary index and Insert/Replace/Delete/Get.
   Use configurable prototype limits such as a 64 KiB root budget and bounded Lists;
   these are experiment settings, not new release defaults. Reject unsupported types
   and oversized writes before modifying stored data. Native memory must be released
   on disposal and partial initialization failure.
3. **Read ownership and concurrent publication.** Implement owned copies and short
   protected runtime views under the root document gate. Exercise one writer with
   multiple readers; update the root and index coherently. No gate spans I/O, await or
   external waits. Verify saved copies remain unchanged and protected views never
   outlive their scope. Keep the same contract in both managed baselines.
4. **Churn, movement and text sharing.** First test reuse without movement. Then move
   live blocks in bounded maintenance steps while readers and writes run, updating
   owner/index locations before reclaiming old storage. Exercise readers at each
   relocation boundary and fixed-live-data churn. Add an explicit dictionary variant
   with stored-reference accounting and reader-safe release; measure its high-cardinality
   overhead too. Large chunked collections remain outside this bounded slice.
5. **Compare and decide.** Run focused correctness checks, then normal and scale
   profiles sequentially. Record failures as failures. Continue only if the correctness
   gates pass and memory/read tradeoffs meet the stated targets; otherwise simplify,
   retune the measured representation or choose the managed approach before expanding.

Do not implement PostgreSQL, replication or OQL to make this experiment look complete.
The critical proof is safe storage under mutation and movement, not feature count.
The existing segment microbenchmark measures neither concurrent compaction nor a real
record allocator; it cannot replace steps 2-4.

## Decisions needed now versus parked work

The experiment uses one database with entity type + key identity. This matches the
current [database scope](../decisions/database-scope.md); its MemoryOnly delivery scope
remains historical because PostgreSQL is required for the first production milestone.

The narrow schema avoids floating-point ordering, nullable Map keys and malformed
Unicode round trips: generate valid text and reject invalid/out-of-subset input at
this experimental boundary. This does not settle the broader text/type policies.
Physical sizes, safe gate implementation, index layout and allocator/reclaimer choices
are implementation experiments inside this slice, with bounded configurable settings.

Persistence retention/recovery, backup operations, replica budgets, transport, schema
migration scope, additional OQL semantics, administration tooling and full release
performance envelopes are parked. No new architecture questionnaire is required to
start fixture/baseline work. Existing accepted contracts remain the broader design
reference, not a requirement to implement every feature before this first test.
