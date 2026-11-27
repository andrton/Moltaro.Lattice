# Acceptance

The epic is done when all of the following hold.

**Behavior.**

1. Every requirement above is delivered. No entry point of an accepted feature throws
   `NotSupportedException`, and no test of `Moltaro.Lattice.Tests` is skipped.
2. The tests that were written ahead — the skipped tests of `IndexTests`,
   `UniqueIndexTests` and `WriteBatchTests` — pass with their assertions unchanged. So do
   the tests written in each sprint of this plan. One assertion of an older test changes,
   by the owner's decision of 15 November 2026 on the position of a write that changes
   nothing: the brief of S12 names it. A second follows from the same rule, by the owner's
   word of 22 November 2026: the brief of S17 names it. The four tests that assert a refusal,
   such as `Durable_persistence_is_not_implemented_yet`, are gone.
3. The placeholder is gone. The samples run to the end against the engine, and the code in
   `README.md` compiles.
4. Nothing that Epic 2 needs from the core is missing: its R1, R2, R7 and R13.

**Release checks.** Each row of the
[verification table](../../design/milestone-1-engine-contract.md#verification-handoff-and-closure)
is closed by one sprint; the sprints listed as contributing deliver their part earlier.

| Area | Evidence required | Closed in | Contributing |
| --- | --- | --- | --- |
| Q07.3.6 Binding and comparison | Every scalar and key family, overflow, null, NaN, Unicode and composite ranges agree between key lookup, index search, typed access and access by name | S20 | S07, S14, S15, S19 |
| Q08.1.3 Replication | A real topology of 5–10 processes; a child loads while single writes and batches continue; gaps, duplicates, disconnection and a change of history; bounded buffers | S35 | S10–S11, S12, S33–S34 |
| Q09.2 Base and journal | Foreground, applier, cleanup and reopen interrupted at every boundary; operation expiry; bounded storage; the recovered state equals the acknowledged one | S32 | S28–S29, S30–S31 |
| Q09.4 Restore | Restore into an isolated target; a damaged or incompatible target is refused; children reload on the new history | S36 | S27 |
| Q10.1 Targets | The same data in an optimized managed reference; the ordinary and the burst profile; no prototype timing reused | S41 | S25, S40 |
| Q13 Writes and retry | Typed, by-name and remote submission agree; cancellation, an unknown outcome and a repeat never apply an effect twice | S35 | S20, S26, S28–S29 |
| Q14 Bounded resources | Each budget exhausted in a test; protected readers drain; nothing queued runs after its cancellation; no unbounded wait | S39 | S08, S09, S10–S11, S14–S15, S17, S21–S22, S24, S26, S29, S30–S31, S32, S33–S34 |
| Q16 Layout and formats | Binary round trips for every type; malformed input refused before use; ownership and reference counts audited | S37 | S05, S10–S11, S13, S18, S19, S30–S31, S32 |
| Q17 Lifetime and compaction | Churn with a constant live set, including deletes and reinserts. Move data referenced by indexes and preserve primary and secondary search results. Retained memory stabilizes. | S23 | S04, S13 |

**Numeric gates.** The targets are those of the
[performance requirements](../../design/performance-requirements.md), on the
[reference fixture](../../design/milestone-1-engine-contract.md#reproducible-first-fixture):
121,000 roots — 100,000 users, 10,000 groups, 1,000 roles, 10,000 settings — generated
with seed 42.

| # | Gate | Target | Measured in | Early figure in |
| --- | --- | --- | --- | --- |
| G1 | Retained storage memory | At most 75% of the optimized managed reference with the same data, indexes and metadata, after load and after churn | S25, and again in S41 on the final build | — |
| G2 | Small local read | p99 at most 1 ms for reads by identifier and by exact index value, at 10,000 reads/s per ready child, required position already applied, while 200 changes/s arrive. Range reads are reported as their own class | S40 | S25 on one node |
| G3 | Small write through a child | p99 at most 50 ms for single-entity writes, from the call to application on the calling child, durable commit and admission included, at 200 logical changes/s in total. Batches are reported as their own class | S40 | S12 through a child in MemoryOnly; S28 on a Durable master alone |
| G4 | Readiness of a new child | At most 10 s from its open call to Ready, under the ordinary load, the other children reading; 5 runs, each within the target | S40 | S35 |
| G5 | Peak memory of a node | At most 1.5 times its steady ready-state memory, for the master and each child, during ordinary mutations with compaction and during the start of one child | S40 | S23 on one node |
| G6 | Burst | 10,000 changes/s offered for 30 s, then the ordinary profile restored within 60 s at 200 changes/s; every submitted operation resolved as completed, refused or uncertain; no accepted write lost | S41 | — |
| G7 | Deployment profile | Every gate above holds with 5 children and with 10 | S40, S41 | S35 for correctness |

Rules for every measurement:

- Gates are judged on the reference machine, the owner's workstation. The master, every
  child and PostgreSQL run as separate containers with declared limits of CPU and memory.
- A run has a manifest written before it starts and approved by the owner: hardware,
  operating system and virtualization, .NET and PostgreSQL versions, the limits of every
  container, network placement, concurrency, warm-up and duration. A result without its
  manifest is not evidence.
- Before a gate run the program checks the machine: enough free memory for the sum of the
  limits, a quiet processor for a stated time, no foreign containers. When the check
  fails the run does not start; there is no way to measure anyway.
- Offered, admitted, completed, refused and uncertain operations are reported separately,
  with p50, p95, p99 and the maximum. Slow or failed operations stay in the denominator.
- Component measurements are reported next to the totals. A prototype timing is never
  copied into a result.
- An early figure that already misses its gate is reported to the owner in that sprint.
  The sprint does not close on it silently.
- No measurement runs in the default test run, and none is launched to write or review
  this plan.
- A figure a brief calls *asserted* — a size, an allocation count — is a test of the
  default lane and fails the build when it does not hold.
- A figure a brief calls *reported* is produced by a named scenario of the measurement
  program and entered in [measured results](#measured-results) with the manifest it ran
  under. Manifests are committed files of the measurement project; raw output is not
  committed.
- There are four manifests. The development manifest, written in S05, names
  the machine, the operating system and the versions; the figures of the sprints up to
  the gates run under it without containers, are for comparison between sprints only,
  and need no approval. The single-node manifest of S25 and the topology manifest of S40
  govern the gates. The burst has a manifest of its own, written in S41, with its resource
  budget and its baseline window. The owner approves the single-node, the topology and the burst manifest, each before the
  first run it governs.
- A manifest is not changed after a result was seen. A new one needs a new approval, and
  the results under the old one stay in the table.

**Added by the planner on 19 November 2026,** after the first start of a rented machine,
which grew from four planned hours to more than seven by additions:

- Everything for one start of a rented machine is one queue that runs on the machine
  itself; nothing waits for a session on the workstation.
- A start has a plan written before it: the series, their order, the expected length and
  what each decides. A series added during a start needs a reason the planner agrees to.
  The plan names what each line really runs: the scenario, the builds and the rounds.
- On a machine of N cores the judged case of many readers uses at most N/2 readers.
- A suspected fall is confirmed only by a series with at least three builds rotating,
  among them a second build of one of its commits as the gauge, and is called only where
  three rounds agree against the gauge; the lines of one run are not independent
  evidence. A fall of 5 to 7 % reported from two builds and three rounds did not exist.
- A start comes after the review and the breaker of its sprint are closed.
- A single run on the workstation selects nothing: it gives a direction and a listing,
  and a choice between builds is made by a series.
- Figures from a rented machine are a new series and are never compared in absolute
  terms with those of the workstation.
- Nothing about the machine goes into the repository beyond "a rented ARM64 machine, 16
  cores". How it is reached, and the raw outputs of every start, are kept in a note
  outside the repository, which the coordinator keeps and the planner knows of. The owner
  is asked only to start the machine and to stop it; the coordinator reaches it by that
  note and tells him at once when a queue has ended.
- The output of every repeat of the test suite is kept, so that a failure is never
  without a name.
- A comparison across trees is judged only when the trees rotate (the planner, 23 November
  2026, at the close of S17): a fall of S16 measured with its trees adjacent in one order
  did not reproduce once they rotated (K1 of the [refactoring backlog](../refactoring.md)).

## Measured results

Filled by the coordinator when a sprint closes. S42 moves the table into the product
documentation before this document is removed.

| Sprint | Figure | Value | Manifest | Date |
| --- | --- | --- | --- | --- |
| S05 | Bytes retained per user of the scalar form (100,000 users; live bytes and class slack of the arena), against 66 bytes of logical field sizes | 176 bytes, 2.67 times the logical size: 162 live and 14 slack; the fixed part of the root is 105 bytes | development | 15 November 2026 |
| S05 | Bytes retained per setting of the scalar form (10,000 settings), against 160 bytes of logical field sizes | 288 bytes, 1.80 times the logical size: 288 live and no slack; the fixed part of the root is 96 bytes | development | 15 November 2026 |
| S05 | Native capacity obtained per entity, with what no counter shows (Q31) | 209.72 bytes per user (5 segments), 419.43 per setting (1 segment) | development | 15 November 2026 |
| S06 | Read by key of one of 100,000 users of the scalar form, by class, one thread; 1,000,000 reads after 100,000 of warm-up, three runs | p50 400 to 600 ns, p99 2,000 to 3,300 ns alone; p50 600 to 1,100 ns, p99 1,300 to 5,400 ns with one writer replacing entities | development-2 | 15 November 2026 |
| S06 | The same read by name | p50 1,000 to 1,200 ns, p99 2,600 to 4,100 ns alone; p50 1,400 to 1,500 ns, p99 3,500 to 4,900 ns with one writer | development-2 | 15 November 2026 |
| S07 | Read by a String key of one of 10,000 settings of the scalar form, by name, one thread; 1,000,000 reads, three runs | Ordinal: p50 400 to 500 ns, p99 1,600 to 2,000 ns. Without case, by the case table, in the stored spelling and in upper case: p50 400 to 500 ns, p99 1,600 to 2,100 ns; with the comparison of .NET before the table, p50 400 to 500 ns, p99 1,600 to 1,900 ns | development-2 | 15 November 2026 |
| S08, part 1 | Reads by key in total, by class, of 100,000 users, without a writer; windows of 2 s, three runs | 1 thread 2.0 to 2.3 M/s, 2: 4.4 to 5.0, 4: 8.4 to 9.3, 8: 14.2 to 14.7, 16: 16.2 to 17.6 (one disturbed run left out); on the protection of S06 1.9, 2.6, 2.4, 2.1, 2.1 | development-3 | 16 November 2026 |
| S08, part 1 | Writes a second of one writing thread beside 20 readers of its own database, medians of five runs | replaces 408 k/s (S06: 757 k/s); inserts and deletes 640 k/s (S06: 19 k/s); inserts with growth 421 k/s (S06: 8 k/s); reads beside them 14.2 to 14.7 M/s in total (S06: 1.7 to 1.8) | development-3 | 16 November 2026 |
| S08, part 1 | The same writes beside N readers of its own database, divided by those beside N readers of another database of the process; N of 1, 2, 4, 8, 16, 19; three runs | 0.72 to 1.42 in two runs; in the third, a disturbed one, one point at 0.68; the median of every point at least 0.74 | development-3 | 16 November 2026 |
| S08, part 1 | Bytes of the primary index per entry at 100,000 and at 1,000,000 entities | 22.3 and 17.8, for an Int64 key, a composite Int64 key and a String key alike; the map of the package took 36.7 and 29.4 for Int64, 47.2 and 37.7 for String | development-3 | 16 November 2026 |
| S08, part 1 | The insert that grows the primary index from 131,072 entries; an ordinary insert takes 0.2 to 0.5 µs | 1.3 to 1.9 ms, median 1.56 ms, in the implementer's runs; 2.3 to 8.0 ms on a loaded machine in the review | development-3 | 16 November 2026 |
| S08, part 2 | One pass of an enumeration over 100,000 users, by class, without a writer and without limits; median of a series, three runs | 41 to 53 ms on a machine under other load; 43 to 49 ms in part 1, where a pass remembered nothing and failed when the index changed | development-3 | 16 November 2026 |
| S08, part 2 | The same pass beside a writing thread, and with the index growing once during it (about 132,000 entities) | beside replaces 59 to 60 ms; beside inserts and deletes 59 to 61 ms; with a growth 88 to 90 ms in the implementer's first runs, 195 to 240 ms on a loaded machine later; in part 1 the two last failed | development-3 | 16 November 2026 |
| S08, part 2 | Memory a pass holds per key it has returned, at 100,000 keys | 21.0 bytes for an Int64 key, 41.9 for a Guid, 68.2 for a String of 13 characters; between half and all of that over one doubling of the table | development-3 | 16 November 2026 |
| S08, part 2 | Reads by key in total, by class, without a writer, the branch against main in alternating runs | 16 threads 14.7 to 15.6 M/s against 14.9; 20 threads 14.9 to 16.2 against 15.6 to 17.1: no difference | development-3 | 16 November 2026 |
| S09 | Completed writes a second of 1, 4 and 16 calling threads, replaces; three alternating runs against S08 | 1.19 to 1.27 M/s, 0.43 to 0.63 M/s, 0.58 to 0.67 M/s; on S08 1.79 to 1.91 M/s, 1.17 to 1.25 M/s, 1.08 to 1.19 M/s | development-3 | 17 November 2026 |
| S09 | The same for inserts and deletes | 1.45 to 1.74 M/s, 0.36 to 0.74 M/s, 0.62 to 0.71 M/s; on S08 3.10 to 3.30 M/s, 2.10 to 2.26 M/s, 2.06 to 2.18 M/s | development-3 | 17 November 2026 |
| S09 | p99 of one write with 16 calling threads; bytes of managed memory allocated per write | 60 to 74 µs against 320 to 700 µs on S08 (1.6 to 2.7 ms beside 16 readers in the review); 160 bytes a replace and 136 an insert or delete for one caller, as on S08, and 350 to 362 with 4 callers, 538 to 543 with 16 | development-3 | 17 November 2026 |
| S09 | Reads by key in total on 16 threads, alone and beside 1, 4 and 16 writing callers | 19.2 M/s alone against 18.1 to 18.4 on S08; 15.9 to 17.3, 14.7 to 15.5 and 13.8 to 14.7 beside the callers, against 16.9 to 17.9, 16.1 to 17.4 and 15.8 to 16.5 on S08; readers of another database of the process fall alike | development-3 | 17 November 2026 |
| S09, part 2 | Completed writes a second of 1, 4 and 16 calling threads, replaces; alternating rounds against S08 and S09, the rounds in which the machine ran at its ordinary speed | 1.59 to 1.63 M/s, 1.04 to 1.10 M/s, 0.91 to 1.03 M/s; on S08 1.86 to 1.92, 1.25 to 1.32, 1.16 to 1.23 M/s; on S09 1.21 to 1.29, 0.38 to 0.67, 0.57 to 0.64 M/s | development-3 | 17 November 2026 |
| S09, part 2 | The same for inserts and deletes | 2.35 to 2.58 M/s, 1.75 to 1.81 M/s, 1.54 to 1.80 M/s; on S08 3.01 to 3.04, 2.05 to 2.22, 1.95 to 2.21 M/s; on S09 1.72 to 1.79, 0.30 to 0.69, 0.60 to 0.68 M/s | development-3 | 17 November 2026 |
| S09, part 2 | p99 of one write for 1, 4 and 16 calling threads, replaces, alone; bytes of managed memory allocated per write | 1.3 µs, 94 to 99 µs, 0.95 to 1.0 ms (S08: 1.3 µs, 75 to 83 µs, 0.59 to 0.71 ms); 160 bytes a replace and 124 an insert or delete for any number of callers (S08: 160 and 136; S09: up to 546) | development-3 | 17 November 2026 |
| S09, part 2 | Reads by key in total on 16 threads, alone and beside 16 callers that replace | 17.5 to 19.6 M/s alone; 14.9 to 15.7 M/s beside the callers, against 15.1 to 15.3 on S08 and 13.9 to 14.4 on S09 | development-3 | 17 November 2026 |
| S09, part 2 | One write of an entity of 32 MiB: how long it holds the lock of the writes | p50 2.6 to 2.7 ms, p99 5 to 7 ms | development-3 | 17 November 2026 |
| S09, part 2 | Small writes of one caller beside a caller that writes entities of 32 MiB one after another | 1,500 to 4,300 writes/s, p99 from microseconds to 16 ms, the longest wait 100 to 112 ms; without the give-way of a large write 900 to 1,600 writes/s, p99 up to 64 ms; on S08 0.57 to 0.91 M/s with a longest wait of 3 to 7 ms, on S09 1.24 to 1.44 M/s with 0.2 to 0.4 ms | development-3 | 17 November 2026 |
| S10 | Time from the open of a child to Ready, 100,000 users of the scalar form, master and child in one process over loopback; five loads | 58 to 301 ms, median 184 ms; the first loads include the compilation of the code | development-4 | 17 November 2026 |
| S10 | Bytes of one such load on the wire, next to the stored bytes | 15,410,260 bytes from the master and 108 from the child: 154.1 bytes per entity, against 162 live and 176 retained per entity in the arena of the master; the child stores what the master stores | development-4 | 17 November 2026 |
| S10 | Memory of one such load | an arena of 20 MiB on each side; 7.8 MiB of managed memory allocated during a load, 7.5 MiB held after it (the index and the root entries); the working set of the process 128 MiB before the first child and 166 MiB after the fifth | development-4 | 17 November 2026 |
| S10 | Reads by key and writes of a master without a child, the branch against its base in alternating rounds | reads on 20 threads 17.2 to 17.6 M/s against 14.9 to 16.3; one caller replacing 1.38 to 1.48 M/s against 1.49 to 1.53; 16 callers inserting and deleting 1.58 to 1.70 M/s against 1.62 to 1.65: within the spread of the rounds | development-3 | 17 November 2026 |
| S11 | Time from the open of a child to Ready, 100,000 users, while the master takes 200 writes a second and 16 threads read it; children in the master's process; four runs (measured before the tail of blocks) | one child 440 to 580 ms, two at once 530 to 703 ms; 78 to 128 transactions in (S, R], `LoadEnd` to Ready 0.1 to 4.5 ms (26.1 ms once), 0 to 5.1 % of the states skipped, peak of `PendingDeliveryBytes` 14.4 to 22.6 KB; following for 2 s the children apply 198 to 200 transactions a second, as the master writes | development-4 | 18 November 2026 |
| S11 | The same load at the full rate of one caller | Ready in 0.96 to 1.12 s, one child or two; 40.5 to 51.3 thousand transactions in (S, R], `LoadEnd` to Ready 300 to 533 ms, up to 94 % of the states skipped (the copy already held a later state), peak of `PendingDeliveryBytes` 11.7 to 15.4 MB; following, the master writes 276 to 340 k/s and the children apply 290 to 358 k/s | development-4 | 18 November 2026 |
| S11 | Read p99 of the master during a load at the full rate, 16 readers | 4.1 to 5.4 µs with no load, 5.1 to 6.6 µs during one load, 4.6 to 6.2 µs during two | development-4 | 18 November 2026 |
| S11 | Reads by key beside a following child, one caller writing, per reader thread, below what the machine gives (it keeps at most 9 to 10 of its 20 logical processors busy at sustained speed) | 2 readers: 708 to 766 k/s on the base, 742 to 813 k/s with no child, 704 to 864 k/s with a child, p99 2.7 to 4.3 µs throughout; 4 readers with the child in its own process: 566 to 763 k/s against 616 to 769 k/s. A following child does not slow a read. The rows of 16 readers are a capped machine and decide nothing; the first round after a load with the child inside the master's process shows the child's own heap promoted in that process, a property of the measurement, not of a master's read | development-4 | 18 November 2026 |
| S11 | Writes a second of a master with following children, with the tail of blocks and after the review; alternating rounds of one day | one caller: 364 to 510 k/s with no child, 377 to 415 k/s with one child in the process, 451 to 474 k/s with one in its own process, 375 to 398 k/s with two; 16 callers: 245 to 270 k/s with one or two children, as with none; a write allocates 160 bytes with no child and 164 to 182 with children; with no child nothing is encoded or retained and the branch equals its base | development-4 | 18 November 2026 |
| S11 | The tail of blocks of 1 MiB against one object and one array for each transaction (the trial that was kept) | allocation per write with a child 446 to 462 bytes against 165 to 182; one caller with one child 331 to 383 k/s against 382 to 437 k/s, write p99 14.6 to 15.9 µs against 9.0 to 12.3 µs, pause of the collector per 2 s 25 to 143 ms against 6.9 to 9.2 ms; 16 readers beside 16 callers with a child: writes 60 to 77 k/s against 127 to 150 k/s, reads 2.02 to 2.52 M/s against 2.51 to 2.98 M/s. Tried without gain and not kept: acknowledgments that do not release, a sender that spins before it waits, a larger gen0 budget. Not built: a pool of tail memory and encoding outside the lock (S33) | development-4 | 18 November 2026 |
| S11 | One write of 32 MiB with a child following | p50 2.5 to 2.6 ms with no child, 27.6 to 34.8 ms with one; small writes beside large ones 3.7 to 17.9 k/s with no child and 293 to 592 a second with one, p99 42 to 101 ms: they wait while the large write is copied into the tail under the lock. The capture's copy of one root of 32 MiB inside the protection: median 10.4 to 14.9 ms. Carried to S33 | development-4 | 18 November 2026 |
| S11 | One acknowledgment that releases a million transactions of 64 bytes, under the lock of the writes | 4.0 to 19.6 ms when every header was read, 0.03 to 0.46 ms with the release by whole blocks | development-4 | 18 November 2026 |
| S12 | The mechanism that wakes waits for a position, before and after its replacement (waiters ordered by target, the lowest target published): the clock alone with 100 and 10,000 waiters; and a child with 10,000 waiters while 16 callers write to its master | alone: 0.95 to 2.1 million publications a second and 0.8 to 1.1 KB allocated per publication before, 175 to 177 million and nothing allocated after, at every count of waiters; on a child: the master wrote 373 to 394 k/s before and 668 to 706 k/s after, the collector paused 515 to 551 ms in 2 s before and 8 to 11 ms after; a read with a satisfied session on a child p50 0.5 to 0.6 µs both ways | development-4 | 18 November 2026 |
| S12 | One small replace forwarded through a child, one caller, loopback; and its parts | p50 50 to 116 µs, p99 87 to 364 µs (the first round of a process is the slow one). Parts: the write on the master p50 0.7 to 1.3 µs; a round trip of 64 bytes on loopback p50 23 to 28 µs; from the end of the master's write to the end of the child's wait p50 25 to 59 µs. The gate of a write through a child is 50 ms | development-4 | 18 November 2026 |
| S12 | 16 callers forwarding through one child; and the same beside 4 callers and 4 readers of the master | 50 to 78 k writes a second, p50 196 to 295 µs, p99 0.45 to 1.3 ms; beside the master's own load 15 to 28 k/s forwarded (p99 1.8 to 5.1 ms) while the master wrote 315 to 473 k/s and its reads kept p99 1.6 to 2.8 µs (1.4 to 1.7 µs with no forwarding) | development-4 | 18 November 2026 |
| S12 | Reads by key on a child, 4 threads, beside 16 callers forwarding through it, the process at its cap of 10 processors | 3.6 to 5.6 million a second, p99 1.4 to 2.1 µs, against 4.9 to 8.4 million and 1.0 to 2.0 µs alone; no code of a read changed, the fall is the processors the callers take | development-4 | 18 November 2026 |
| S12 | Small forwarded writes beside forwarded writes of 32 MiB through the same child (one connection) | p50 56 to 63 µs, p99 3.5 to 5.2 ms, longest 214 to 349 ms against 4.8 to 16 ms alone; an Applied of a master's write arrives up to 77 to 98 ms late. Carried to S33 with Q52 | development-4 | 18 November 2026 |
| S12 | The close of a master and of a child with 16 callers forwarding | master: median 2.4 to 3.3 ms after the frame Closed (1.9 to 3.3 ms before it), longest 7.5 ms; child: median 1.7 to 3.1 ms, longest 18 ms | development-4 | 18 November 2026 |
| S12 | Allocation per forwarded write | about 3.2 KB on the child and 1.1 KB on the master; not reduced in S12, a figure to return to when G3 is measured | development-4 | 18 November 2026 |
| S12 | A master with no child against the commit before the implementation of S12, alternating rounds | no fall with 4 and 16 callers, readers, and readers beside writers; allocation per write unchanged (160 and 124 bytes); one caller: replace 1.39 to 1.52 M/s against 1.46 to 1.57 M/s, inside the spread between runs (the order of the runs changed the sign) | development-4 | 18 November 2026 |
| S13 | Read by key of an entity whose field is stored as Dictionary against the same field as Plain out of line, by class | 1 thread 1.57 to 1.93 M/s against 1.22 to 1.62 M/s; 16 threads 7.9 to 9.2 M/s against 6.3 to 8.1 M/s: not slower. Against a Plain value small enough to lie in the root the two cross at 65 bytes | development-5 | 18 November 2026 |
| S13 | Reads beside a writer that allocates in the size class of the entries, 7 readers and the writer on 8 processors, 16 rounds of each, Plain and Dictionary alternating, the machine 27 to 50 % busy with other work | reads beside the writer over reads alone: Plain median 0.875 (0.75 to 1.01), Dictionary 0.86 (0.64 to 0.97), the ratio of the medians 0.98; reads alone 8.17 M/s Plain and 9.96 M/s Dictionary, beside the writer 7.30 and 8.41 M/s; the writer 795 k/s beside Plain readers and 704 k/s beside Dictionary readers. With 16 readers: 0.93 and 0.95 | development-5 | 18 November 2026 |
| S13 | Writes of an entity with a text of 9 bytes, the field as Dictionary against Plain | 12 to 20 % fewer writes a second as Dictionary; a write whose text is already stored costs 60 to 110 ns more, one whose text is new about 330 ns more (reads win over writes; the advice is in the decision on strings) | development-5 | 18 November 2026 |
| S13 | Memory of 100,000 users of the fixture with one field of 16 distinct texts of 9 bytes | 16.0 MB as Dictionary against 17.6 MB as Plain, 9.1 % less | development-5 | 18 November 2026 |
| S13 | Memory for each value when all 1,000,000 texts are distinct, 9 bytes each | 163 bytes as Dictionary against 80 as Plain; the table takes 51.4 bytes for each distinct entry | development-5 | 18 November 2026 |
| S13 | A child that loads 100,000 such users | the bytes on the wire are those of Plain (15,410,268); the child holds 16 entries; Ready about 0.12 µs later for each Dictionary value | development-5 | 18 November 2026 |
| S13 | Bytes the arena holds while one text of 1 MiB is replaced 100 times with no read in progress | 101 MiB as Dictionary against 4.0 MiB as Plain before the fix of the review; after it the test holds Dictionary to at most twice Plain | development-5 | 18 November 2026 |
| After S13 | Moves of the epoch for each 10,000 writes, counted by a probe, before the fixes and after | replaces 78 and 78, with a read held or not; deletes of an absent key beside a held read 0 and 78, one look for a window; 99 inserts to 1 replace, pure inserts, nothing retired: 0 and 0. No time was measured | — | 18 November 2026 |
| After S13R, part 1 | The spread of the method, measured for the first time: two builds of one commit (8c66279) against each other, read-by-key-threads, four rounds with the order of the builds rotated, 13 to 28 % of the machine busy with other work before a run. Later judgements of a paired ratio refer to it | the second build over the first, by pairs: 1 thread alone 1.00, 0.97, 1.32, 1.05; 1 thread beside a writer that replaces 1.05, 0.97, 1.06, 0.98; 16 threads alone 0.96, 1.05, 1.00, 1.02; 16 threads beside the writer 0.96, 1.06, 1.00, 1.01. Medians within 2.5 %, pairs within about 6 %, one outlier | development-5 | 19 November 2026 |
| After S13R, part 1 | Read by key of the reference fixture, the commit before S13 (8c66279) against main after part 1 of S13R (40f6a8c), in the same four rounds: closes the read of S13, of the immutable schema and of the second trigger of the drain (the fixes after S13) and of part 1 | head over each of the two builds of the base, medians: 16 threads alone 1.035 and 1.040 (9.13 and 9.16 against 9.51 M/s), every pair at or above 1.00; 16 threads beside the writer 1.026 and 1.025 (8.48 and 8.54 against 8.83 M/s); 1 thread alone 1.05 and 0.95 (2.47 and 2.63 against 2.50 M/s: the head lies between the two builds of the base); 1 thread beside the writer 0.96 and 0.98 (2.37 and 2.39 against 2.25 M/s), pairs 0.83 to 1.08. No fall; the case of 1 thread beside a writer is inside the spread of four rounds and is measured again with eight | development-5 | 19 November 2026 |
| After S13R, part 1 | Writes, write-callers, 8c66279 against 40f6a8c, three rounds | medians of the paired ratios: 1, 4 and 16 callers that replace 1.01, 0.99, 0.99 (1.44 to 1.46 M/s for one caller); that insert and delete 1.05, 1.03, 1.03; allocation for a write unchanged, 160 and 124 bytes. Writers beside 16 readers of the written database 0.96, 1.15, 1.11 (replace) and 1.09, 1.01, 1.13 (insert and delete); those readers 1.03, 1.00, 0.97 and 1.02, 0.955, 1.03. No fall | development-5 | 19 November 2026 |
| After S13R, part 1 | A child, the commit before part 1 (645fa43) against 40f6a8c, three rounds | following-children, writes a second: one child following 0.98, 1.05, 1.07 for 1 caller, 16 callers, 16 callers beside 16 readers; two children 1.03, 1.05, 0.98; with the children in processes of their own 1.01 to 1.07 in two rounds, the third disturbed (0.63 to 0.76 in four lines of one run). child-under-writes at the full rate of one caller: a child applied 1.24 to 1.33 M transactions a second against 1.21 to 1.27 M. child-load: single loads take 36 to 334 ms in both builds, three runs decide nothing; measured again with the case above | development-5 | 19 November 2026 |
| S13 | Read by key of the reference fixture, no Dictionary field, the commit before the implementation (8c66279) against the head, 8 rounds in the order base head, head base, each on 8 processors, the machine 19 to 77 % busy with other work. Left open by this series; answered by the series of 19 November 2026 below | head over base, by pairs: 1 thread 1.38, 0.65, 1.17, 0.69, 0.87, 1.33, 0.79, 1.03, median 0.95; 16 threads median 1.09 (0.78 to 1.34, and one pair whose base run was disturbed). Medians of the eight runs: 1 thread 1.72 M/s base and 1.49 M/s head; 16 threads 6.69 and 7.56 M/s. The pairs swing by more than the difference looked for, so the series decides nothing; on a Plain read the code differs from the base in one place, the test of the length where the base tested the kind, one comparison as before | development-5 | 18 November 2026 |
| S13R, part 2 | The spread of the method on a rented ARM64 machine, 16 cores: two builds of one commit against each other. The figures of this machine are a new series and are never compared in absolute terms with the rows of the workstation | one thread within ±5 % (one case +8 to 10 %); 8 threads ±10 %; 16 threads ±16 %, the machine saturated; writes ±6 % | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Read by key of the reference fixture, the cases left open above: case 2 of S13, and one reader beside a writer | closed: 12 rounds, the median of the ratio head over base 0.98 to 1.00 | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | The load of a child, child-load | no difference between the builds | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Read by key of text after A6 | a first series of two builds and three rounds showed 5 to 7 % slower; a series of four builds did not reproduce it: ratios about 1.00 for A6 as committed, A6 as fixed and main. Not measurable on this scenario, ±10 % a pair | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Writes after D1, write-callers, eight rounds with the builds rotating; the median of the ratio to main (40f6a8c) for one caller and for 16 that replace, one caller and 16 that insert and delete | the first cut, 670617c: 0.967, 0.981, 0.967, 0.960; the head before the attempts, 0151c9f: 0.979, 0.985, 0.987, 0.968; 70fcc30: 0.999, 0.992, 0.974, 0.967; babd343: 0.979, 0.976, 0.974, 0.965. A second series of the same form with 0d63331: 670617c 0.996, 0.970, 0.984, 0.974; babd343 0.983, 0.974, 0.991, 0.981; 0d63331 1.014, 1.000, 1.011, 1.001; beside 16 readers the writes of 0d63331 0.99 to 1.03 and the reads 1.00 to 1.04. The first cut cost 2 to 3 % on a write; written where it lies, the change costs nothing measurable. The rows of 4 callers scatter 0.85 to 1.13 a round and are not judged | rented-arm64-1 | 19 November 2026 |
| S13R, part 2 | Writes with children following, following-children, four rounds, five builds rotating (main, 670617c, 42b6317, 0151c9f, babd343), the children in processes of their own started through the executable | 16 callers that replace, over main for 670617c, 42b6317, 0151c9f and babd343: no child 0.969, 0.934, 0.953, 0.945; one child 0.988, 0.950, 0.978, 0.966; two children 0.946, 0.969, 0.947, 0.951; one child in its own process 0.958, 0.944, 0.971, 0.957; two in their own processes 0.958, 0.948, 0.972, 0.966; reads beside the writers 0.97 to 1.01. An earlier series of two builds and three rounds had shown 0.94 and 0.91 with a child following. No fall belongs to a child: the rows with a child lose what the rows without lose, the cost of the first cut of D1, which 0d63331 removes | rented-arm64-1 | 19 November 2026 |
| S14 | Memory and the build of secondary indexes, index-build, on a rented ARM64 machine, 16 cores, the engine at 11adfeb (before the last fixes of the review), two or three runs of each case within a few per cent: bytes of the arena for an entry of an index and for an entity; the time of one caller to insert 100,000 entities of a type with 0, 1, 3 and 6 Int64 indexes | 17.6 bytes an entry with ascending keys and 25.1 with random keys, the same with 1, 3 and 6 indexes; 112 bytes an entity without indexes. The build 104 ms with no index; 254 to 327 ms with one; 546 to 770 ms with three; 980 to 1,440 ms with six, the first figure with ascending keys and the second with random ones | rented-arm64-1 | 19 November 2026 |
| S14 | Writes a second of one caller, index-writes, a type with 0, 1, 3 and 6 indexes; and of eight callers | a replace that changes every indexed key 952 k, 210 k, 85 k, 47 k; a replace that keeps every key 814 k, 648 k, 436 k, 307 k; insert and delete 1,385 k, 436 k, 166 k, 85 k. Eight callers, the replace that changes every key: 408 k, 79 k, 41 k, 26 k | rented-arm64-1 | 19 November 2026 |
| S14 | The same writes of one caller as the time of a write; what a write allocates and takes | a replace that changes the key 1.05 µs with no index and 4.8, 11.8, 21.3 µs with 1, 3 and 6, about 3.5 µs an index; an insert or a delete 0.72 µs and 2.3, 6.0, 11.8 µs, about 1.85 µs an index. Managed bytes a write unchanged by indexes. Blocks of the arena taken for an insert, a replace and a delete with one index 7.6, 13 and 4, about one of them used | rented-arm64-1 | 19 November 2026 |
| S14 | Exact search by the full key, index-search, 1 and 8 readers: an Int64 key, distinct keys, through the class, alone, beside a writer that changes indexed keys, and by name; the first item under one of 16 keys; a text key, Plain, Ordinal, distinct | Int64: 450 k a second on 1 thread and 2.9 M on 8; beside the writer 400 to 415 k and 2.4 to 2.6 M; by name 445 k and 2.7 M. The first item under one of 16 keys 776 k and 4.4 M. Text of 8 bytes 163 k and 1.2 M; of 100 bytes 119 k and 0.86 M. For scale, the read by primary key of the same items 1.5 M and 12 M. Managed bytes a search 720 through the class and 936 by name, against 88 for a read by key | rented-arm64-1 | 19 November 2026 |
| S14 | Read by primary key beside writes to indexes, index-read-by-key, through the class: a type with 6 indexes alone and beside a writer that changes every indexed key; the same type with no index | 1 reader: 1.53 M a second alone and 1.38 M beside the writer with 6 indexes, 1.49 M and 1.44 M with none; 8 readers: 11.9 M and 10.3 M with 6 indexes, 11.6 M and 10.7 M with none | rented-arm64-1 | 19 November 2026 |
| S14 | The prototype of the study on the same machine, bare trees and no engine, 8 readers, three runs: the copy-on-write tree against the tree changed in place. Not a figure of the engine | exact search alone 15.6 M a second and 15.2 M; beside a writer of the same index at full speed 10.4 M against 14.5 M; ranges forwards 6.2 M and 5.8 M alone, 3.7 M against 5.4 M beside the writer. One index write beside the 8 readers, ns: an insert 840 against 525, a delete 842 against 619, a replace 1,762 against 1,197. The bare tree alone: a replace that changes the key 1.14 µs, an insert 0.52 µs, a search 2.1 M a second on a thread | rented-arm64-1 | 19 November 2026 |
| S14 | The forms of a text key in the prototype on the same machine, 8 readers, random keys, searches a second at keys of 8, 32 and 100 bytes. Not a figure of the engine | a copy 10.1 M, 7.1 M, 4.6 M; a shared text entry 4.6 M, 4.3 M, 4.0 M; a shared entry with a prefix 9.2 M, 9.0 M, 9.2 M | rented-arm64-1 | 19 November 2026 |
| S14, part 2 | Exact search by the full key, index-search, 1 thread, through the class, alone; three builds rotating, three rounds | Int64 796 k a second (main 464 k), text of 8 bytes 503 k (161 k), of 100 bytes 288 k (117 k); 416, 456 and 640 bytes a search (720, 760, 944). The gauge, a second build of main, 0.98 to 1.02 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | A replace that keeps every indexed key, index-writes, 1 caller, against the same type without indexes | 0.96, 0.94 and 0.86 of its rate with 1, 3 and 6 indexes (main 0.80, 0.56, 0.39): about 30 ns an index, against about 320 on main; the type without indexes 805 k writes a second on every build | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The upkeep of one index for a replace that changes the key, index-writes, 1 caller, against the replace of the bare tree of the prototype in the same start, 1,153 ns | 1,465 ns with 1 index, 1.27 times the bare tree (main 3,579 ns, 3.10); 1,313 and 1,258 ns an index with 3 and 6 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | Writes and reads by key of the reference fixture's users, a type with three indexes, write-callers and read-by-key-threads, eight rounds | a replace keeping every key 1.72 times main with 1 caller, about 1.5 with 4 and 16; an insert and a delete 2.50 and about 1.96; reads by key up to 8 threads 0.96 to 1.07 of main, the gauge 0.98 to 1.04 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The size of a node, 2 KiB against 1 KiB, index-search, index-writes, index-build, a second build of the head as the gauge | 8 readers beside a writer: reads 1.04 (gauge 0.99), the writer 0.95 (0.94); a replace that changes the key 0.97 to 0.98 (0.99 to 1.00); 16.8 and 23.7 bytes an index entry against 17.6 and 25.1 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | Text keys of 100 and 200 bytes, index-long-text, five rounds: a prefix of 8 bytes beside a shared key, and the references of a text held for each entry, against the head | the prefix: a search 1.26 and 1.30 with distinct heads, 0.89 and 0.93 with a common head, a replace that changes the key 1.17 and 0.98; the references for each entry: a replace that changes the key 1.45 to 1.51, an insert and a delete 1.9 to 2.1 with distinct heads, a search unchanged. The gauge 0.99 to 1.02 | rented-arm64-1 | 20 November 2026 |
| S14, part 2 | The harness of B2, the new (main) against the old (f3d767f) on the same engine, three rounds and one pair of the gauge | read-by-key-threads up to 8 threads 0.96 to 1.05, write-callers 0.98 to 1.03, index-search 0.99 to 1.07; index-read-by-key 0.96 to 1.03 but for 0 indexes, 1 thread, alone, 0.89 [0.94 0.87 0.89], whose gauge pair is 1.07 | rented-arm64-1 | 20 November 2026 |
| S15, part 1 | Exact search, index-search, 1 thread alone, against 5cc64c8 (main after S14R), the first of three starts, four builds, three rounds | Int64 through the class 0.88, by name 0.92; text of 8 bytes 0.87, of 100 bytes 0.92; the first item of 6,250 equal keys 0.52, its bytes 416 to 840; S14R itself loses nothing there | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | The walk, index-search, the first start: a range of 100, a page at Offset 10,000, the rounds of a step | a range of 100 about 9,800 a second alone, 0.89 forwards and 0.84 in reverse beside a writer, about 62,000 with 8 readers; the page at Offset 10,000 404 a second against 10,405 for the first page, the price of remembering the identities passed over; counting under `ReadMemoryLimit` not visible for an entity of 112 stored bytes; 3.22 descents at a fence for a range of 100; `EntriesPerRound` 64, 256 and 1,024 not apart outside the gauge, 256 kept | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | Writes and reads by key, the upkeep, forwarded writes, the first start | writes and reads by key lose nothing to the sprint; a type without indexes loses nothing (F7); the upkeep of one index 1.20 to 1.26 times the bare tree of the same rounds; the forwarded write loses nothing after F12; F9 not explained by how a step is called | rented-arm64-1 | 21 November 2026 |
| S14R | Inserts and deletes of the reference fixture's users, write-callers, against 22b8ee1, as merged and with two fixes, the first and second starts of S15, part 1 | as merged 0.93 to 0.96; with V1 0.96 to 0.99; with V2 1.01 to 1.08, taken and merged (PR 37); V2 on index-long-text 8 to 14 % faster on a replace that changes a long key and on an insert and a delete with distinct heads | rented-arm64-1 | 21 November 2026 |
| S15, part 1 | Exact search at the close, the third start, against 5cc64c8, 1 thread alone, class and by name | Int64 0.966 and 0.957; text of 8 bytes 0.899 and 0.911; text of 100 bytes 0.951 and 0.964; the first of many 0.826 and 0.869; beside a writer one entity 0.99 to 1.06. Targets of S14, part 2 at the head: 692 k against 700 k, 426 k against 450 k, 258 k against 200 k, with 5cc64c8 reading 730 k and 474 k | rented-arm64-1 | 21 November 2026 |
| S16 | Unique indexes, unique-writes, three rounds of the head and its gauge: a refused insert, the price of the check, the replace that keeps every key, the load of a child | an insert refused for a held value 143 k a second, a `DuplicateKey` refusal 104 k, an accepted insert and delete 650 k; one Int64 index unique against not 0.89 for an insert and a delete, 0.84 for a replace that moves the key, about one descent of the tree; the fixture's users with their unique index 1.00 with one caller and 0.97 with 16 on a replace that keeps every key; the load of a child 572 ms with the walk against 566 ms | rented-arm64-1 | 22 November 2026 |
| S16 | F6, G2 and the record of a write: index-writes and write-callers against 57be01b, five builds rotating | with 1, 3 and 6 indexes 1.02 to 1.13, the gauge 0.95 to 1.02; a type without an index 0.996 to 1.013; the upkeep of one index 1.08 to 1.18 times the bare tree (57be01b 1.20 to 1.30); the fixture's users, a replace 1.02 to 1.06, an insert and a delete with one caller 0.984, the gauge 0.990, every run of the head below the runs before, its cause not found | rented-arm64-1 | 22 November 2026 |
| S16 | Reads by key and the exact search against 57be01b | reads by key at 1 and 8 readers 0.96 to 1.08, the gauge 0.97 to 1.06; the exact search on one thread 0.96 to 0.99, the gauge 0.96 to 1.01; Int64 737 k, text of 8 bytes 443 k (target 450 k, missed as before), of 100 bytes 269 k | rented-arm64-1 | 22 November 2026 |
| S17 | How long ordinary reads are held by the publication of a batch of 1, 32, 1,000 and 10,000 entities, readers of its members on the master, the start of part 2 | none over 100 µs for 1 and 32; up to the commit for 1,000 (1.8 ms, the longest read 2.1 to 2.6 ms); for 10,000 at most 2.1 to 3.2 ms against a commit of 17.7 ms; readers of other entities are not held | rented-arm64-1 | 23 November 2026 |
| S17 | The pool of a waiting batch against what its publication takes, batch-writes, the bound by levels, batches of 1, 32, 1,000 and 10,000; the check on the final state | random keys 4.67, 1.07, 1.28, 1.05; ascending keys 14.00, 3.06, 3.06, 1.38; the fixture's users 6.57, 3.69, 2.85, 1.85, 58.7 MiB at 10,000 where the linear bound of part 1 held 341.8 MiB; the check 1.75 to 1.80 µs a member for one unique index, 5.2 to 5.45 µs for three | rented-arm64-1 | 23 November 2026 |
| S17 | Writes at the close, unique-writes, index-writes, write-callers and following-children, nine trees rotating, against b9a9074 (`main` before S17) and the gauge of f6d9cf3 | the sum of S17, the head (cb70b9f) against b9a9074, on index-writes 0.910 to 0.981 on 8 of 24 lines and on unique-writes 0.933 to 0.958 on 5 of 17 with one gain of 1.082 (K2); to the write path (f6d9cf3) 0.916 to 0.954 on 7 index-writes lines; the write path no fall and no gain on index-writes; following-children 0.953 to 1.014, none called; the users inserted and deleted by name, one caller, the head 0.990 of 57be01b, none called (K1 closed); at the head single writes 652 to 696 k a second and the upkeep of one index 1,396 ns, against 1,298 at b9a9074 | rented-arm64-1 | 23 November 2026 |
| S18, part 3 | Historical comparison of `following-children` and `forwarding-costs` against part 2 | [Paired results, measured during S19](sprints/s19.md#s19-arm64-results) | rented-arm64-1 | 24 November 2026 |
| S18 | `root-bytes` for full fixture users and groups | [208.01 and 112 retained bytes per entity, including size-class slack](sprints/s18.md#s18-measured-bytes) | Workstation, [confirmed on rented-arm64-1](sprints/s19.md#s19-arm64-results) | 24 November 2026 |
| S19 | Full fixture retention, Map lookup and write-lock cost, and the scalar and nested index controls | [Bytes, timings and paired controls](sprints/s19.md#s19-arm64-results) | rented-arm64-1 | 24 November 2026 |
| S20 | Change one field of the full fixture User | [43 bytes for the partial effect, 199 for Replace](sprints/s20.md#forwarding-and-sparse-replay) | [Deterministic wire test](../../../src/Moltaro.Lattice.Engine.Tests/Replication/Transactions/SparseUpdateTests.cs) | 24 November 2026 |
| S20I | Position-clock and child application rates | [Exact-parent clock-waiter comparison](sprints/s20i.md#clock-waiter-costs) | development-5 | 25 November 2026 |
| S20R | Fixed scalar Map pairs | [Owned read and whole Replace](../refactoring.md#r2-fixed-scalar-pair-comparison) | Isolated paired driver in the S21 ARM64 queue | 25 November 2026 |
| S21 | `protected-views` entry, writer during a held view, owned and protected List traversal | [Windows](sprints/s21.md#implementation-review-and-windows-measurements) and [ARM64](sprints/s21.md#arm64-results) | development, rented-arm64-1 | 25 November 2026 |
| S21I | `protected-views` Int64 List traversal at 1,000 and 100,000 elements | [Windows](sprints/s21i.md#windows-x64-results) and [ARM64](sprints/s21i.md#arm64-results), against the exact parent | development, rented-arm64-1, six rotated orders | 25 November 2026 |
| S22 | Typed and by-name protected scalar reads | [Windows x64 scalar comparison](sprints/s22.md#windows-x64-scalar-comparison) | One and four readers, paired windows and by-name controls | 25 November 2026 |
| S23 E07 | 100-byte forwarded replaces at the write admission correction | [Latency and allocation](sprints/s23.md#write-admission-cost) | development-5, exact base and implementation head | 25 November 2026 |

For S20 the Release test itself captured and counted the two complete wire
effect bodies and asserted their size ordering. It supplied the reported byte
counts in place of a measurement-program scenario and manifest.

The figures of S14 come from the scenarios `index-build`, `index-writes`, `index-search`
and `index-read-by-key`, run on the engine at 11adfeb, before the last fixes of the
review; the rows of the prototype are the bare trees of its study on the same machine.
The figures of S14, part 2 are of the head 9e34443 against `main` at 3229ba1, each series
with a second build of the same commit rotating in its rounds as the gauge; the raw outputs
are kept outside the repository.
No acceptance of S14 set a target for the cost of a write or of a search; the record of
S14 says what is weak in them, and S14, part 2 is about them.

The figures of S09 come from the scenario `write-callers`. Writes fell against S08: by
a third to a half for one caller, and several callers write about half of what one writes
where S08 lost about a third. The cause is the work of the coordinator on every write —
two locks taken several times where S08 took one, the queue of an identity, and for
callers that wait a hand-over between threads — and not the number of places of
preparation: with 16 or 64 places instead of 2 nothing was gained. What the path gives
for it: the order of calls, cancellation before dispatch, a p99 of a write with many
callers that is lower, and groups for the storage step of S27. Reads by key did not fall:
beside writing callers they are about 10% lower, and so are readers of another database,
so it is the share of the machine. The requirements are 200 writes a second, 10,000 at
the peak and a p99 of 50 ms; the lowest rate measured is 233,000 a second. The owner
decided on 17 November 2026 that S09 closes with these figures and that S09, part 2 puts
the write path on a proven source at once.

The figures of S09, part 2 come from the scenarios `write-callers` and `large-writes`,
measured after a restart of the machine in alternating rounds; in some rounds the machine
ran at about a third of its speed, and the rows give the rounds in which it did not.
Against S09 the write path is faster for every number of callers, by a third for one and
by 1.5 to 3 times for 4 and 16, allocates what S08 allocated, and keeps what S09 added:
the order of calls, cancellation before dispatch and groups for the storage step. Against
S08 it is 14 to 17% lower for one caller, and for several by the same share, since all
of a write runs under the one lock. The implementer measured where the 70 to 90 ns a
write go: about 10 ns outside the engine call, and the rest in what a write carries since
S09 and S08 did not have — the record of the write, the group, the storage step and
publication as a step of its own; none of the simple removals gave a measurable gain, and
the one that did, a second path for a write that completes in its hold, was not taken.
The Acceptance line "one caller not lower than S08" is therefore not met; the owner
decided on 17 November 2026 that the path is accepted as it is and that optimization
stops here. Reads did not fall. What a large entity costs is new: a caller that writes
entities of 32 MiB one after another leaves another caller about a thousandth of its
small writes, because the lock of .NET is not fair and lets the caller that just released
it take it again for up to about 100 ms; a write larger than the limit of a group gives
its processor up once after it releases the lock, which raises the small writes about
2.5 times and bounds nothing. A first-in-first-out gate in front of the lock was
measured and dropped: with 16 callers beside 16 readers on 20 processors no write
completed in less than 10 ms. This is the input of Q52. Two reviewers measured that,
while a storage step is busy, admitting a write walks the list of the writes that wait:
with 40,000 waiting, 0.6 to 0.8 ms an admission; it never happens in MemoryOnly and is
carried to S27.

The figures of S08, part 2 come from the scenario `enumerate`. Remembering the keys costs
about a quarter of a pass, about 110 ns an entity; with a limit set a pass also takes
timestamps, about 13 ms per 100,000 entities. A step after a growth walks the new table
from its start in rounds of 256 words of the index, so that no round stays inside the
protection of reads for more than a bounded piece of work: 0.14 to 2.6 ms at the longest
with 1,000,000 keys returned, against 117 to 269 ms before the rounds. A seeded hash of
text after Marvin32 of .NET is not slower than the hash it replaced: 24 to 26 ns against
27 to 28 ns for a key of 13 characters compared without case, and 9 to 10 ns for Ordinal
before and after, in the implementer's benchmark and not under a manifest.

The figures of S08, part 1 come from the scenarios `read-by-key-threads` and
`primary-index`. The machine has cores of two kinds and carried 15 to 25% of other load
on that day, and the same series varied up to twofold between runs, so the figures say
how the protections compare and no more. The replaces of a writing thread beside 20
readers are lower than on the protection of S06 because the readers now run: beside
as many readers of another database the same thread is as slow. The growth of the index
takes longer than with the hash of S07, which spread consecutive integers evenly; the
mixed hash spreads them as it spreads any keys. Per instance the protection costs 16 KiB
for the table of epochs and about 0.8 KiB of padding, and 16 KiB per segment of 4 MiB
for the marks of retired blocks; the 1,025 locks of S06, about 100 KiB, are gone.

The figure of S07 comes from the scenario `read-by-text-key`; its keys are 13 ASCII
characters, and no key outside ASCII was measured by it. One hash and one equality of
such a key cost about 40 ns by the table against 29 ns by .NET, and 94 ns against 73 ns
for a key of 15 characters that starts in Cyrillic, in the implementer's own benchmark,
not under a manifest. The table and `OrdinalIgnoreCase` of .NET 10 disagree on 16
scalar values under ICU, 1,031 under NLS and 6 under invariant globalization; in every
case the table holds equal what the runtime holds different. The table adds 17 KiB to
the core assembly. A long key outside ASCII compared without case reads slower by the
table than it did by .NET, in the probe of the review and not under a manifest: a key
of 1,000 UTF-16 units of mixed text 6.4 µs against 3.9 to 4.1 µs, and one of 100,000
units 0.63 ms against 0.36 to 0.42 ms in UTF-16 and 1.02 ms against 0.77 ms in UTF-8;
keys of 13 characters and long ASCII keys read as before.

The figures of S06 come from the scenario `read-by-key`
(`dotnet run -c Release --project Moltaro.Lattice.Measurements -- read-by-key development-2`,
from `src`). They are given as the range of three runs, and a single run can lie well
outside it — the review measured a p50 of 1,700 ns by class alone in one of two runs —
because the runs differ more
than the series do: the processor of the development machine has cores of two kinds and
the threads are not pinned. The manifest `development-2` is the development manifest
with a `Run` sentence that is true of a series with a writer; the figures of S05 stay
under `development`. Reads by key on several threads were measured by the review of S06
and not under a manifest: they do not scale with threads, which is entry Q42 of the
[open questions](../open-questions.md), reported in S08 and judged with the figures of S25.

The figures of S05 come from the scenario `root-bytes` of the measurement program
(`dotnet run -c Release --project Moltaro.Lattice.Measurements -- root-bytes development`,
from `src`) and were the same on Windows and on Linux. They are for comparison between
sprints, not a gate: G1 is judged in S25 against the managed reference. What they show
already is where the bytes of a small entity go — the 32-byte header of the root block
and a 16-byte descriptor for every String, 64 bytes of descriptors for the 57 bytes of
text a user has — and the coordinator reported it to the owner with the close of S05.
