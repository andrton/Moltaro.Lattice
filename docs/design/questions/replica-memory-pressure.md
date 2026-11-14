# Q14: Child replica memory pressure

**Accepted for v1 on 13 November 2026:** option A. An initialized child that cannot
prepare its next change within budget pauses application, releases partial preparation
and keeps intact local state readable under ordinary limits. Expose pressure and lag;
bound incoming/prepare work and resume from the missing position in order when capacity
permits. Master write acknowledgment does not wait for this child.

The [decision record](../../decisions/write-admission.md#child-replica-memory-pressure)
owns the contract, alternatives and costs. Memory pressure alone does not initiate
full reload. Missing/incompatible history still requires full reload under the accepted
reconnect contract; inadequate capacity must not cause an endless discard/reload loop.
No skipped changes, false applied progress, persistent child spool or second arena.

This covers controlled pre-publication resource failure with intact installed data,
not corruption, partial publication or arbitrary process-wide allocation failure.
Numerical receive/work/retry budgets and remaining startup/recovery details are open.
No implementation or new performance result is claimed. Q14 stays partially accepted
in the [question register](../questions.md).
See [question register](../questions.md#current-discussion).
