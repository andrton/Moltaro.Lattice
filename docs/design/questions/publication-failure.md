# Recovery scope after a committed publication failure

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted on 13 November 2026 under Q09.2:** if a confirmed durable mutation
or WriteBatch cannot be coherently published in memory, put the affected Lattice
instance into a recovery-required state. Refuse new local reads/writes and recover
through controlled close/reopen from PostgreSQL, using one arena. Do not terminate
the embedding application process automatically.

[Decision owner](../../decisions/checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure) · [WriteBatch](../../decisions/document-model.md#atomic-writebatch-within-one-database) · [Accepted operation outcomes](operation-outcomes.md)

## Exact failure being considered

PostgreSQL has confirmed commit. Local publication then fails after it may have
changed root data, indexes or lifetime metadata. The durable batch must survive,
but continuing from an unproven partially installed arena can expose inconsistent
state. The existing contract already prohibits exposing partial results or reporting
a clean rollback. The accepted boundary is the failed Lattice instance, recovered
through close/reopen; exact lifecycle and failure-state mechanics remain engineering
work.

This question does not change ordinary validation/memory refusal before commit,
bounded waiting for temporary PostgreSQL unavailability, or transient background
applier retries. Those cases have their own accepted contracts and do not imply a
corrupted published arena. A lost commit response remains an unknown outcome; its
resolution/operation-status protocol is a separate open detail, not proof of rollback
or permission for blind application resubmission.

## A. Stop serving the instance and recover on reopen — accepted

Mark the instance recovery-required before releasing publication protection in a way
that could admit another access to partial state. Every access path must honor that
state, including requests already waiting at gates. Stop new mutation admission and
new publication/progress announcements. Do not acknowledge the failed publication as
a successful completed operation or as a definite rollback.

Quiesce instance-owned preparation, storage, applier and replication activity. In-flight
PostgreSQL transactions must settle or be accounted for before a new recovery view is
made readable; an old transaction must not commit later outside the recovered order.
Do not free native segments while protected users or background work can still
dereference them. Existing independent owned copies remain owned by their callers.

The host closes/disposes the failed engine and reopens it after the cause permits
recovery, or restarts its service using the existing deployment lifecycle. Release
the old arena safely before installing the recovered one; rebuild indexes, replay
all committed changes/batches and become Ready only after verification. A failure to
recover leaves the instance unavailable instead of starting an unbounded retry loop.
Exact lifecycle APIs and shutdown budgets remain engineering work.

This stops one Lattice instance, not every instance in the process and not PostgreSQL.
It does not mandate killing a worker that embeds the library. Ordinary same-store
Durable restart preserves history. Already initialized children can keep their last
coherent local state under the accepted disconnected-read contract, then resume or
fully reload under the existing history rules when the master returns.

## Broader recovery alternatives

The accepted recovery unit is the Lattice instance. Rebuilding selected tables while
other work continues is not a v1 requirement. Removing the partition abstraction does
not change the stop/close/reopen contract or permit access to an unproven arena.

## Costs and rationale

| Aspect | Accepted instance recovery |
| --- | --- |
| Healthy operations | No extra persistence round trip; state-check cost needs measurement |
| Fault availability | Local service stops until coherent recovery |
| Recovery work | Restore the database and rebuild indexes |
| Memory | One arena plus bounded buffers; old users drain before release |
| Complexity | One recovery lifecycle with explicit in-flight work and publication boundaries |

A is selected for v1 to make durable recovery demonstrable before adding live partial
repair. It is a rare failure path, not the normal small-write mechanism. Inject failures
after commit and at root/index publication boundaries; verify no partial read escapes,
all committed changes survive reopen, old references are not reused unsafely and no
second full arena appears. This document selects no new retry, failover or automatic
process-termination subsystem. Operation identity and resolution of unknown commit
results follow the separately [accepted contract](operation-outcomes.md).
