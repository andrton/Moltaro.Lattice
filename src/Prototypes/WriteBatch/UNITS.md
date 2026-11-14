# WriteBatch with a 256-byte base and background application

Bounded synthetic integration measured on **14 November 2026**. This extends the
[WriteBatch experiment](README.md), using the same native arena, selected Arc
indexes, preparation/publication barrier and foreground PostgreSQL commit path.
It checks the accepted maintenance mechanism; it is not a production provider.

## Protocol exercised

The fixture has 256 initial roots across two types. A persisted root contains a
length, its assigned revision and the existing independent binary value encoding.
It spans consecutively numbered **256-byte payload rows**, padded in the last row.
These are PostgreSQL saved units, not native segment sizes or SQL physical pages.
The unit directory key is `(stream, root slot, part)`; a root can grow, shrink,
disappear and be recreated. Text and payload bytes are actually persisted. Native
addresses and physical index images are not. This bounded slot/codec is not the
final general schema or shared-string disk representation.

1. Foreground commit inserts one complete logical WriteBatch and a compact result
   receipt in the same PostgreSQL transaction. The receipt holds operation ID,
   position, member count and a SHA-256 request fingerprint, not a duplicate payload.
   Native publication and successful completion follow the existing barrier protocol.
2. A worker inside the same process owns a separate PostgreSQL connection. It reads
   at most two **complete batches**, subject to a 64 KiB encoded-input bound. It
   applies operations in order, keeping the final full value/deletion per root and
   accounting for every assigned revision, including overwritten or deleted values.
3. Only affected roots' saved units are replaced, using set-based DELETE/INSERT.
   Growth/shrink removes obsolete parts. Updated units, the applied-through position
   and the revision high-water mark commit together in one SQL transaction.
   The changed-unit payload budget is 128 KiB. There is no second arena or full-base
   copy during maintenance. Full Save still rewrites its complete saved root.
4. A separate cleanup transaction deletes journal rows only through the committed
   applied position. Receipts remain available after that deletion. An SQL cursor-row
   lock serializes maintenance; foreground append does not acquire it. No arena,
   root or publication protection spans this I/O.
5. Recovery reads units, progress and the remaining journal in one repeatable-read
   SQL snapshot. SQL MVCC preserves that snapshot if maintenance commits or removes
   covered journal rows concurrently. Rebuild roots/indexes with saved revisions,
   retain the global revision floor even for an empty base, and replay whole tail
   batches before serving reads.

Maintenance uses a 20 ms accumulation wait for a partial/empty cycle and continues
without that wait after a full two-batch cycle. These constants are **experiment
budgets**, not selected production defaults. Receipts/positions are capped at 512
operations per stream; expiry is not implemented. Recovery and cleanup have no other
persisted consumers in this fixture. Replica delivery remains master-memory-only.

## Correctness and application crashes

All **16 unit-path scenario groups** passed, along with **22 regression groups**
for the previous MemoryOnly and binary-image SQL paths. The new cases include:

- Uncommitted units and cursor remain invisible; injected failures after unit writes
  and after cursor update roll back both and do not authorize cleanup.
- Two eight-member batches coalesce to eight final roots while preserving all 16
  ordered effects and their revisions. Changes include growth, shrink, deletion,
  recreation and equal keys in different types.
- Foreground reads, compaction and another committed write proceed while background
  unit updates remain uncommitted. Cleanup preserves the next unapplied batch.
- A captured recovery snapshot continues to see its matching old base and tail
  while another connection applies and deletes that tail.
- Maintenance can apply and prune a committed batch **before native publication**.
  If the foreground then faults, reopen still restores it from the base and a
  same-token retry returns its retained result without applying it again.
- Empty-base recovery retains the committed position and revision high-water mark.
  Reopen after maintenance preserves exact root summaries/revisions and index results.

Nine workers also terminate abruptly, without managed cleanup; each has a separate
recovery worker. Expected exit codes are 110–118.

| Exit boundary | Saved state on reopen | Required journal/receipt behavior |
| --- | --- | --- |
| Foreground before commit | Prior base | Uncommitted batch and receipt absent; retry applies once |
| Foreground after commit | Prior base plus complete tail | Whole batch restored; retry returns receipt |
| Foreground during publication | Prior base plus complete tail | No readable prefix; whole batch restored |
| Foreground after publication, before reply | Prior base plus complete tail | Retry does not apply again |
| Units written, cursor not updated | Old base/cursor | Full unapplied journal retained |
| Cursor updated, SQL transaction not committed | Old base/cursor | Full unapplied journal retained |
| Base/cursor committed, cleanup not started | New base/cursor | Covered journal may remain; replay skips it |
| Cleanup DELETE executed, not committed | New base/cursor | Covered rows remain; newer tail remains |
| Cleanup committed | New base/cursor | Covered rows gone; newer tail and receipts remain |

Every recovery also resumes application/cleanup and reopens again to verify identical
values, revisions and indexes. Recovery constructor time was **4.64–17.59 ms** for
this tiny fixture, excluding connection opening, pre-audit decoding and later audits.
This is warm-path behavioral evidence, not a large-database startup estimate.
These are application-process crashes, not PostgreSQL server, power or network-loss
tests. Ambiguous commit responses and receipt expiry remain untested.

## Latency and interference

Two fresh low-priority processes per mode, on one logical processor. Each performs
42 paced foreground batches, 14 each of sizes 1, 8 and 32; the first two of each
size are warm-up, leaving **12 measured samples per size/process**. Reads, indexed
queries and native compaction run concurrently. Off and on modes use the same unit
store and durable receipts. Off-mode catch-up occurs after the measured workload.
Forced-GC footprint probes are separate from these latency runs.

The following are **ranges of process medians in milliseconds**:

| Objects per batch | Whole Save, background off | Whole Save, background on | Protected publication, off | Protected publication, on |
| --- | ---: | ---: | ---: | ---: |
| 1 | 1.251–1.608 | 1.273–1.477 | 0.0061–0.0070 | 0.0071–0.0076 |
| 8 | 1.589–1.758 | 1.489–1.603 | 0.0227–0.0248 | 0.0224–0.0226 |
| 32 | 1.972–2.147 | 2.216–2.614 | 0.0696–0.0712 | 0.0716–0.0728 |

Background work adds no arena-wide freeze. Its SQL work still competes for CPU and
storage. The 32-member Save medians were about 12–22% higher in the paired on runs;
small-sample improvements for other sizes do not establish a speedup.

Do not interpret medians as latency limits. Largest complete Save was **6.424 ms off
and 19.768 ms on**. Largest protected publication was **0.923 ms off and 0.146 ms on**;
publication barrier acquisition reached **0.119 ms** in an on run. The protected
body timer ends just before unlock; it is not a bound on total reader waiting.
All **144 measured publication bodies allocated zero managed bytes**.

Concurrent read p99 was **20.5–27.8 µs off**, **18.3–56.8 µs on**; maxima were
**0.536 ms off**, **0.326 ms on**. Runs captured 416–560 reads and 26–35 indexed
queries each. One on-run indexed query reached **2.546 ms**; this limited experiment
does not attribute its cause or establish a tail SLO. No outliers are removed.

Both on runs applied all **42 batches / 574 mutations**, removed the covered journal
and ended with applied position 42 and zero journal rows. Each used 28 background
transactions; repeated roots reduced saved-root writes to **401–408**, with
**1,569–1,652 unit rows** written in total. Background-transaction medians were
**3.462–4.183 ms**, maxima **35.785–46.943 ms**, including first-use work. These are
SQL maintenance durations, **not pauses of the arena**. Cleanup took 47–61 ms in
aggregate. This paced run is not a 10,000-change/s capacity test or overload proof.

## Memory

**1 KiB = 1,024 bytes; 1 MiB = 1,048,576 bytes.** The timed workloads started at
248 KiB of native segments and peaked at **296 KiB in both modes**. Separate probes
verified that persisted application/cleanup alone leaves native arena accounting
unchanged; it never creates another arena.

The footprint probe applies two overlapping batches in one maintenance transaction:

| Members in each batch | Journal payload held | Final saved-unit payload | Largest observed managed retained delta |
| --- | ---: | ---: | ---: |
| 1 | 1,638 bytes | 1,280 bytes | 7,704 bytes |
| 8 | 12,992 bytes | 10,240 bytes | 34,152 bytes |
| 32 | 53,968 bytes | 32,768 bytes | 102,176 bytes |

Managed deltas are sampled after forced GC at prefix preparation, unit write and
cursor update. They include live arrays, codecs and driver objects, but **do not
capture every transient allocation peak**. Serialized arrays and driver buffers
are additional to the named payload budgets; these limits are not a unified heap cap.

Across 42 foreground batches, total managed allocation traffic including the harness
was about **2.12 MiB off** and **5.46–5.59 MiB on**. Background ApplyOnce accounted for
3.19–3.32 MiB of that traffic. These are allocated bytes over time, not simultaneous
memory use. Post-workload retained-heap deltas were 10,008 bytes off and **12,104 /
1,144,128 bytes on**. The larger observation is retained in the evidence; this run
does not isolate its driver/runtime/harness ownership or prove a long-run retention
bound. Maximum sampled whole-worker working set was **72.832 MiB**.

## Reproduction and resources

Use the [existing build and connection-file instructions](README.md#commands-ownership-and-limits), then:

```powershell
& ./Prototypes/WriteBatch/Run-Bounded.ps1 -Mode UnitChecks -ConnectionFile .local/batch-postgres-connection.txt
& ./Prototypes/WriteBatch/Run-Bounded.ps1 -Mode Units -ConnectionFile .local/batch-postgres-connection.txt -Repeats 2
```

`UnitChecks` runs the focused unit-path checks only. `Units` also runs old-path
regressions, a separate memory probe, paired off/on measurements and nine crash/reopen
pairs. PostgreSQL was 18.4 with fsync, full_page_writes and synchronous_commit enabled.
Only a unique run-owned schema in local PostgreSQL is created and dropped; no server
or unrelated process is stopped. The runner verifies schema absence after cleanup.

Workers remain sequential, BelowNormal, on one logical processor, with workstation
GC and existing 15-second wall/5-second CPU/192 MiB sampled process guards. Native
segments retain the 2 MiB fixture cap. The 28-worker measured run used **23.953 CPU
seconds and 75.219 summed worker wall seconds**. PostgreSQL server resource use is
not included. Every normal exit reported zero outstanding native bytes; the OS
reclaims abruptly terminated workers. All guards and expected exit checks passed.

Ignored raw evidence: `.local/write-batch-20261114-064854-871`. The preliminary
correctness run `.local/write-batch-20261114-064804-211` is not pooled into timings.
Release build passed without warnings/errors. Final owned-schema removal passed.

## Engineering conclusion

The accepted direction remains viable for implementation: a complete-batch journal,
bounded coalescing into 256-byte units, atomic base/cursor publication, separate safe
cleanup and receipts that outlive removed journal payloads. No full-arena pause or
second in-memory database is needed for this tested maintenance path.

This does not select the fixture directory/codec, budgets or SQL statements as final
production formats. General schema/dictionary metadata, partial collection edits,
parallel preparation, unified resource accounting, receipt expiry, additional
retained consumers and replica transport remain outside this check. It does not
measure or implement cross-node read-after-write guarantees.
