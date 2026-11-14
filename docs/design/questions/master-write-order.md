# Object write coordination and internal group commit

**Accepted on 13 November 2026 for v1:** concurrent independent preparation followed
by sequential saving and publication of short bounded groups, with per-object
conflict coordination. Internal PostgreSQL group commit retains individual ordinary
publication boundaries. The [decision record](../../decisions/write-acknowledgment.md#ordered-commit-and-publication)
owns the accepted scheduling contract; this page explains the tradeoff.

[Engine foundation](../engine-foundation.md) · [Accepted database WriteBatch](write-batch.md)

## Object protection and storage batching are distinct

A pending mutation must not lose the predecessor state against which it was validated.
Coordinate conflicting writers through scheduling/ownership until publication.
Readers can still use the previous published object during the PostgreSQL wait.
The root/index read-write gates are acquired for protected preparation/publication,
not retained across storage I/O.

Several prepared independent mutations may share one durable PostgreSQL commit.
Each ordinary API call still completes after its own coherent local publication.
A reader may observe one published root and another not yet published root from the
same internal storage group. Internal grouping is not a public multi-object transaction
or a stable snapshot for a query. MemoryOnly skips the PostgreSQL wait.

The number of calling threads is unrestricted by this authority contract. Required
resource budgets still bound accepted work and preparation. Do not prepare dependent
same-root mutations independently against stale state merely to fill a storage batch;
resolve predecessor dependencies or separate their execution.

## Accepted scheduling baseline

One bounded ordered stage completes a foreground storage group and publishes its
logical transactions before the next group proceeds. Independent preparation may
continue concurrently. Many callers can submit work; readers retain access to
published state during storage I/O. An explicit WriteBatch remains one atomic unit,
while ordinary operations sharing a storage transaction publish independently.

The [accepted initial-loading protocol](../../decisions/replication-and-hosting.md#initial-capture-and-readiness-handoff)
needs a trustworthy published position. If two independent writes complete out of
order, the larger assigned number alone cannot certify that all earlier changes
are installed. The bootstrap prototype assumes this order; it does not implement
concurrent PostgreSQL commit scheduling. The later scheduling acceptance selects
one ordered stage; production integration still has to establish the complete prefix.

### A. Concurrent preparation, ordered commit/publication — accepted

This makes the persisted order, arena publication and replica progress easier to
reconcile. Independent preparation can run during the current storage wait, within
the existing admission budget. Commit/publication remains a serialization point:
an expensive group delays later groups, and bounded batching amortizes storage
round trips without establishing a full-engine rate or latency guarantee.

### B. Multiple independently committing groups — not selected for v1

Allow more than one storage group to execute concurrently, then coordinate their
publication and the highest complete prefix. This may improve write throughput
when concurrent I/O helps the actual workload. It also requires bounded tracking
of out-of-order completions, aborted/uncertain earlier groups, conflicts and
publication dependencies. A later completed group cannot make a missing earlier
position disappear. Keeping several prepared/committed groups can increase peak
temporary memory without reducing the latency of a blocked prefix.

| Cost | A: ordered commit/publication | B: concurrent committing groups |
| --- | --- | --- |
| Reads | Local reads continue during storage I/O; short publication gates remain | Same read contract; more concurrent work can contend for CPU/index access |
| Writes | One ordered stage can limit throughput and delay later groups; batching and concurrent preparation help | Potential I/O overlap; actual gain depends on storage and conflict mix |
| Temporary memory | Easier bound for one active commit group plus admitted preparation/queue | More in-flight groups and out-of-order completion bookkeeping to bound |
| Complexity | Simpler prefix, failure and handoff coordination | Additional ordering, failure and recovery coordination |

The decision selects no numerical queue/group sizes, artificial batching delay or
performance promise. A bounded engine-path check must measure reads and writes
together before release defaults are chosen. The asynchronous coordinator's worker
count, fairness, reservations and concrete conflict ordering remain engineering work.
Foreground group commit remains separate from the background saved-state applier.
Existing storage-only measurements do not establish full-engine performance.

## Accepted explicit application transaction

The requested scenario is one submitted group of related objects that must all
commit or none commit. Q01 now accepts a bounded [WriteBatch within one database](write-batch.md),
with explicit group validation, publication and replay/replication boundaries. Separate
reads and scans do not gain a shared snapshot. [Operation identity and bounded
retained results](operation-outcomes.md) are also accepted.
