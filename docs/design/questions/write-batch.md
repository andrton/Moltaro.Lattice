# Explicit atomic write batch within one database

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted for v1 on 13 November 2026:** a bounded atomic WriteBatch in one database.
Application-side construction takes no engine locks. Readers remain available during
preparation and storage I/O; the database closes access for the complete batch's
publication. Conflicting database writers wait during execution. Cross-database
batches and external transaction participation remain excluded from v1.

[Decision owner](../../decisions/document-model.md#atomic-writebatch-within-one-database) · [Accepted internal group commit](../../decisions/write-acknowledgment.md#per-object-coordination-and-internal-group-commit) · [Engine foundation](../engine-foundation.md)

## Two different kinds of batch

| Mechanism | Who defines the group | Accepted guarantee |
| --- | --- | --- |
| Internal group commit — accepted | The engine groups independent prepared writes | Shared PostgreSQL commit; ordinary roots still publish individually, without a shared read snapshot |
| Explicit WriteBatch — accepted | The application submits one group in one database | All members validate and commit together; no readable intermediate publication of that group |

Internal group commit must not silently convert unrelated calls into an application
transaction. Conversely, an explicit WriteBatch must retain its logical boundary even
if the storage writer combines it with other work in a PostgreSQL transaction.

## Accepted API scope

The application constructs a batch of supported mutations on owned values of
different entity types, then submits one Commit operation. Ordinary complete-object
batch saves use [Upsert semantics](../../decisions/mutation-commands.md#atomic-batch-save-with-upsert-semantics):
create an absent type/key and fully replace an existing one from the supplied value.
The caller does not need to split the batch into inserts and updates. Individual
members do not perform separate commits. A name such as BeginBatch may create a local builder;
it must not acquire database locks while application code computes or awaits work.
SDK names remain illustrative. Save stays a full replacement without diff; explicit
partial commands keep their existing semantics.

Validate the entire batch in its owning database, including schema/existence rules,
expected revisions and total preparation budget. A definitive pre-commit failure in
any member rejects the whole batch with no published changes. Ordinary outcomes such
as deleting an already absent key retain their existing success/absence semantics.
Repeated commands for one root need explicit ordered semantics or validation rules;
those details are not implicitly selected here.

The batch is a write transaction. Reads used to construct it are not automatically
part of a snapshot or a serializable read/write transaction. Existing optional
revision conditions can reject stale inputs. No arbitrary application callback runs
inside the engine's protected publication scope.

## Accepted database coordination

1. On submission, schedule exclusive logical write ownership of the database and
   drain earlier conflicting writes before final validation/preparation. New writes
   in that database wait within existing admission budgets. This scheduler state
   is not a reader-excluding lock held across I/O; ordinary reads remain available.
2. Prepare all affected values/blocks, index work and lifetime reservations within
   the total batch budget. Do not modify published objects yet. No second database
   arena or permanent historical versions are required.
3. Commit the complete logical batch to the PostgreSQL journal in one transaction,
   without holding root/index/database read-write gates across I/O. Readers continue
   to observe the old published data during this wait.
4. Acquire an exclusive database publication barrier after existing protected users
   drain. Apply every member and affected index/revision update, then release the
   barrier and complete the batch. Reads wait during this publication interval.

All arena read/publication paths, including ordinary writes, index traversal and
maintenance, must cooperate with the barrier. Ordinary per-root operations need a
short shared database admission around protected access; the batch uses exclusive
publication access. Exact lock order and gate implementation remain E03 work. The
database's logical write ownership prevents conflicting commits while preparation
is in progress. Readers remain available until the short publication barrier;
independently opened databases are outside this coordination scope.

MemoryOnly uses the same preparation and publication boundary without a PostgreSQL
wait. Preparing a batch does not reserve the whole database's data twice; peak
temporary memory follows prepared mutations, full Save replacements and metadata.

## Atomic publication does not freeze a multi-call calculation

The barrier prevents a protected access from running while only some batch members
have been installed. It does not make separate reads share one point in time: a
caller can read A before Commit and B after Commit. A scan taking protection one root
at a time can likewise cross publication and retain results from both sides.

The accepted contract preserves the existing scan/no-global-snapshot contract. A
consistent multi-object read would need an explicit bounded read scope or copy
operation; it is not silently added by this WriteBatch. Do not describe ordinary
multi-call reads as observing the entire batch atomically.

## Durability, failure and replication consequences

- Definitive validation/admission/rollback failure means none of the batch commits.
  A lost commit response is an unknown outcome, not proof that none committed. Batch
  identity/status and safe resubmission need a defined recovery contract.
- After durable commit, a publication failure cannot become an ordinary rollback.
  The accepted [failure-recovery policy](../../decisions/checkpoint-and-journal.md#instance-recovery-after-committed-publication-failure)
  stops the affected instance and recovers through close/reopen; never expose the
  half-applied batch. Its concrete state machine still needs tests.
- Journal replay must recover the whole committed batch before serving affected data.
  Saved-state applied positions, compaction/retention and initial image capture must
  preserve batch boundaries or use an explicitly proven equivalent protocol.
- Replication must carry the logical batch boundary. A child prepares and publishes
  the complete batch under its own barrier; insufficient memory
  or missing payload must not expose a prefix or advance applied progress past it.
  Master success still does not wait for child freshness under the existing contract.

## Alternatives and costs

| Aspect | Bounded WriteBatch — accepted | Application-held interactive transaction |
| --- | --- | --- |
| Application flow | Collect known writes, submit once | Read/compute/write while a transaction remains open |
| Read/write latency | Database writers wait during execution; readers wait during final publication | A lock-based v1 design can block readers/writers for application/network delays |
| Temporary memory | Bounded aggregate preparation; full Save may retain old and new data | Longer resource retention; supporting nonblocking reads may require additional versioning |
| Complexity | Group validation, atomic publication, grouped replay/replication and a barrier on all access paths | Also isolation semantics, transaction lifetime, timeouts, rollback and user-held resource management |

This is a material v1 extension, not merely a PostgreSQL batching setting. Its added
read-path synchronization and pause/memory costs require an integrated prototype.
Cross-database transactions, long application-held locks and general read snapshots
are outside the accepted package. The [duplicate-root rule](../../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch)
now rejects the entire explicit batch before commit when a type/key occurs twice.
SDK signatures, numerical limits and the concrete failure/recovery mechanisms remain
engineering and Q13/Q14 work. [Operation identity and retained results](operation-outcomes.md)
are accepted separately, with one token for each explicit WriteBatch.
