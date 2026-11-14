# Q08.1.3: Read availability during full reload

**Accepted for v1 on 13 November 2026, including bounded read waiting.** Full reload
replaces the affected storage using one arena after safely draining its users. A second
full arena is explicitly excluded. New reads may wait within configured time, request-
count and retained-memory limits until full data/index installation and catch-up finish.
If ready in time, execute the ordinary read; otherwise report explicit timeout,
cancellation, capacity or reload failure. Expired/cancelled waiting calls must not
execute later. Waiting does not hold document locks or retain the discarded arena.

The illustrative **30-second example is not a fixed default or measured reload duration**.
Numerical budgets remain to be checked. This bounded-wait correction supersedes the
earlier immediate-error/no-wait recommendation. Intact local reads during ordinary
disconnection or incremental catch-up remain unchanged; caller-owned copies stay valid.

The [decision record](../../decisions/replication-and-hosting.md#one-arena-and-bounded-read-waiting)
owns the complete contract, failure boundaries and costs. Q08.1.3 remains partially
open for numerical history/wait limits. Child process-restart storage is now settled:
explicit MemoryOnly and full loading from the master.

[MemoryOnly child restart](replica-restart.md) is accepted. See [question register](../questions.md#current-discussion).
See the [question register](../questions.md) for all remaining decisions.
