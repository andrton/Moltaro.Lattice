# Q15: Configured master and child connection

**Scope clarified on 13 November 2026:** a child connects to its
configured master URL. V1 includes no fault-tolerance orchestration, master election,
child promotion or automatic standby takeover. The environment runs one authoritative
master and owns process start, stop and restart.

An initialized child keeps local reads while disconnected and attempts to reconnect.
When the master returns with the same continuous history, resume from retained
compatible changes or fully reload if the required tail is missing. A new/restored
history requires full reload. A replacement process using the same durable history
does not itself create a new history. The URL alone never proves history identity.

The [decision record](../../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection)
owns the contract and its boundaries. Single-arena reload, bounded read waiting,
MemoryOnly child startup and existing write outcomes remain unchanged. No extra
master-coordination service or fault-tolerance framework is required for v1.

Q15 is closed in the [question register](../questions.md). Transport details and
numerical budgets remain engineering work. No runtime change or benchmark was made.
