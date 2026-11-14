# Q08.1.2: Freshness and disconnected reads

**Accepted for v1 on 13 November 2026.** An initialized child continues reading its
intact, coherently installed local state during lag or disconnection. Replication
status is available separately. There is no immediate-freshness/read-your-write
guarantee, automatic stale-age cutoff or ordinary per-read master coordination.

The contract also requires an explicit status query comparing master progress with the
replica's applied progress in the same database history. It reports caught up
through the observed boundary, behind, or unknown/incompatible; numeric lag is only
valid where positions define it. An unreachable master does not imply zero lag.
This is a point-in-time observation, not a permanent equality or data-integrity proof.

The [decision record](../../decisions/replication-and-hosting.md#q08.1.2-what-may-a-lagging-or-disconnected-replica-read)
owns the contract, status comparison, costs and alternatives. Exact protocol/API and
observation mechanics remain engineering work.

[Q08.1.3 bounded resume or full reload](replica-reconnect.md) is now accepted.
[Single-arena reload with bounded read waiting](replica-reload.md) is also accepted.
[MemoryOnly child restart](replica-restart.md) is accepted. See [question register](../questions.md#current-discussion).
See the [question register](../questions.md) for the remaining decisions.
