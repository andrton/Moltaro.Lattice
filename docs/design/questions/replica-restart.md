# Q08.1.3: Child state after process restart

**Accepted for v1 on 13 November 2026:** client replicas operate in MemoryOnly and
fully load their database state from the master after process restart, including
index preparation and catch-up before reads. The Durable master's persistence remains
in PostgreSQL. V1 children keep no engine-owned persistent replica or recovery spool.

The [decision record](../../decisions/replication-and-hosting.md#memoryonly-children-and-process-restart)
owns the contract, costs and alternatives. Child instances explicitly select MemoryOnly;
a Durable child request must not silently become volatile. The general Durable default
and the explicit MemoryOnly master mode remain unchanged.

A connection outage in a surviving child keeps the accepted local-read and incremental
resume behavior. An empty restarted child cannot become ready while its master is
unavailable. Single-arena reload and bounded read waiting remain in force.

See [question register](../questions.md#current-discussion).
Numerical reconnect/wait budgets keep Q08.1.3 partially open in the [question register](../questions.md).
