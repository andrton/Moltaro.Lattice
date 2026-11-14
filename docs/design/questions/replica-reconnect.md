# Q08.1.3: Reconnect, retained history and cross-node visibility

**Partially accepted for v1 on 13 November 2026:** option A. Resume from coherently
applied progress while the complete required binary logical tail is retained and
compatible; otherwise fully reload the database. History and
per-child work are bounded. A slow or disconnected child cannot pin replication
history indefinitely. Full reload uses one arena with bounded readiness waiting for
reads; a second full arena is excluded. Numerical history/wait limits will be selected
from measurements; the stated 30-second wait example is not a fixed default. V1 children
explicitly use MemoryOnly and fully load from the master after process restart; the
Durable master retains PostgreSQL persistence for its own recovery.

[Pending delivery is in master RAM](../../decisions/replication-and-hosting.md#pending-delivery-in-memory)
in both modes. Release changes once all participating recipients acknowledge them;
there is no additional retention window or accumulation for future children. New
children load full state from the master's arena. Reconnect uses only a still-present
RAM tail; there is no PostgreSQL catch-up path. Limits bound outstanding delivery,
not a mandatory duration for retaining already delivered changes.

The [decision record](../../decisions/replication-and-hosting.md#q08.1.3-how-does-a-replica-recover-after-falling-behind)
owns the recovery contract, alternatives and costs. It preserves master recovery
input, the hard application-journal budget and the distinction between replication
history and provider crash recovery. No automatic writer failover is introduced.

## Remaining Q08.1.3 work

- [Session consistency](../../decisions/session-consistency.md) is accepted: implement
  bounded minimum-position waiting and context propagation; no all-replica barrier.
- [Child process-restart storage](replica-restart.md) is accepted: explicit MemoryOnly
  and full loading from the master, with no durable child recovery image.
- [Single-arena reload and bounded read waiting](replica-reload.md) are accepted;
  numerical wait/count/memory budgets remain open.
- Numerical pending-delivery byte/age budgets and per-child work limits, measured
  together with master writes, reads, catch-up and simultaneous reloads.
- Recovery scheduling, capture/trim coordination and concrete protocol/API details;
  implementation choices belong to E07 where they preserve the accepted contract.

Numerical history/wait budgets keep Q08.1.3 partially open in the [question register](../questions.md).
Protocol/scheduling mechanics remain engineering work. See [question register](../questions.md#current-discussion); the initial full-state capture
method also remains separate.

## Cross-node write visibility accepted

**Accepted on 14 November 2026:** calling-child application before forwarded-write
success, and fixed minimum-position propagation across related requests on any pod,
with bounded waiting. Do not wait for all replicas or require sticky routing.
The [session-consistency decision](../../decisions/session-consistency.md) owns the
contract and facade handoff. The expected deployment has roughly five to ten children.
The following preserves rationale; it does not claim a tested replication engine.

### Selected: carry a minimum applied position

Return a history identity and published transaction position with write completion.
The originating child waits until its coherent applied prefix reaches that position.
A subsequent related request carries the position to whichever child serves it;
that child waits only if behind, with a deadline and explicit failure on exhaustion.
Compare positions only within the same history. A rebuilt/restored database must
not silently accept a token from an incompatible history.

The target is fixed: waiting for position 120 does not start waiting for 121 when
another write arrives. The [explicit local position wait](../../decisions/session-consistency.md#explicit-local-position-wait)
accepts the supplied history/position and waits for local data/index application.
It requires no master-status round trip when the target is already known. Capturing
a current master position through status is a separate action. Neither approach
chases a moving latest state, prevents later writes or creates a shared snapshot.

This provides read-your-writes across related requests, not latest-global-state
guarantees for unrelated readers that supply no position. A host SDK/middleware can
carry the token in application request context; it does not require an external
session database. Exact wire/API packaging awaits the application interface review.
Sticky routing can reduce waits, but failover to another pod still needs the token.

Costs: small context metadata and bounded waiters, potential lag delay for a read
on a different child, and a clear host integration contract. It does not await
unrelated slow replicas, duplicate the arena or introduce distributed write voting.
The existing ordered replication and applied-prefix tracking provide the mechanism.

### Not selected: wait for all serving replicas before caller success

After master publication, await coherent application through the operation's position
on every required serving replica. Fan-out is parallel; completion is limited by the
slowest required child. The wait must be outside arena locks and must not stall the
master's ordered publication stage. A timeout after durable commit means visibility
was not confirmed, not that the write rolled back.

This can make later ordinary reads on those serving replicas observe the write or
newer state without a propagated request token. It requires an explicit membership
and read-admission protocol. A removed/disconnected child cannot both be excluded
from the required acknowledgments and continue serving unrestricted stale reads
under this guarantee. New children need a coordinated admission boundary; a loading
or TCP-connected child is not automatically a serving member. Readiness probes alone
are not a proof that no stale in-flight/direct request can reach an excluded child.

The existing permissive offline-read policy would therefore need an explicit scope
change for strict serving reads. Small replica count does not remove that tradeoff.
Quorum acknowledgment is insufficient if an arbitrary unacknowledged child can serve
the next read. No such membership/strict-read change is selected here.

### Other approaches and established examples

Session affinity alone does not cover pod restart, reassignment or new requests from
unrelated clients. Refreshing just one object cannot silently skip the ordered prefix,
its indexes or a multi-object WriteBatch. A fresh copy returned by the master can be
an application value, but is not proof that the replica's database is synchronized.
Refresh before a later write also cannot prevent a concurrent update; use the already
accepted revision precondition when that lost-update protection is needed.

[PostgreSQL synchronous replication](https://www.postgresql.org/docs/18/warm-standby.html#SYNCHRONOUS-REPLICATION)
documents waiting for application on configured synchronous standbys with
`remote_apply`. [MongoDB causal sessions](https://www.mongodb.com/docs/manual/core/read-isolation-consistency-recency/#causal-consistency)
provide read-your-writes across members under their required read/write concerns.
These illustrate the two families of guarantees, not dependencies or ready-made
implementations for Lattice. A single Lattice master can use its ordered history
position; this discussion does not introduce multi-master vector clocks or consensus.
