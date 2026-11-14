# Q13: Remaining mutation details

Q13 is partially accepted. Its [decision record](../../decisions/mutation-commands.md#q13-how-can-a-document-be-updated)
owns the complete mutation contract and alternatives.

The [milestone contract](../milestone-1-engine-contract.md) and
[operation protocol](../operation-retry-protocol.md) now specify the engineering
baseline, including no-op accounting and retry expiry. The older unresolved-work
list below identifies implementation responsibilities, not another approval queue.

The latest accepted boundaries are:

- [Save submits a complete final document](../../decisions/mutation-commands.md#full-document-save-and-explicit-partial-updates-accepted-for-v1);
  partial updates use a separate explicit API, with no automatic change tracking.
- [Mutable input stays unchanged until async completion](../../decisions/mutation-commands.md#stable-write-inputs-until-completion-accepted-for-v1),
  including error/cancellation completion. The engine cannot depend on that mutable
  input after completion; requesting cancellation alone does not release it.
- [Operation identity and retained results](../../decisions/mutation-commands.md#operation-identity-and-retained-results-accepted-for-v1)
  provide authoritative result lookup and safe same-token resubmission within bounded
  retention, with an accepted [configurable 15-minute default](operation-outcomes.md#accepted-safe-repeat-window)
  from first master admission. Expired tokens are rejected; automatic retry is not selected.
- [One operation per type/key in an explicit WriteBatch](../../decisions/mutation-commands.md#repeated-roots-inside-an-explicit-writebatch)
  is accepted: duplicate identities reject the entire batch before commit. Local
  edits before Save, separate ordinary writes and whole-batch token retries are distinct.
- [Caller cancellation follows the dispatch boundary](../../decisions/mutation-commands.md#cancellation-at-the-commit-boundary):
  cancel before dispatch; afterward complete the engine path within its own finite
  failure deadlines and report the actual outcome. MemoryOnly uses publication handoff.
- [One scalar assignment per target path in an explicit partial update](../../decisions/mutation-commands.md#repeated-scalar-targets-in-an-explicit-partial-update)
  is accepted: repeated targets reject the mutation, or its containing WriteBatch,
  before commit. Ordinary Save and ordered collection commands retain their contracts.

The [collection input contract](../../decisions/schema-and-types.md#collection-input-contract)
now settles retained Map-key spelling and whole-collection duplicates. Those are no
longer unresolved mutation questions. Explicit [database WriteBatch](write-batch.md)
and its duplicate-root rejection rule are accepted.

## Unresolved mutation work

- The exact SDK and transport shapes, including how accepted Insert/Replace/Upsert
  intent is expressed by a complete-value submission.
- Remaining validation/error semantics and conditions beyond the accepted revision
  check. The accepted cancellation boundary, atomicity and uncertain-commit
  guarantees remain in force.
- Exact token, request-binding, status and expiry protocols for the accepted bounded
  resubmission guarantee; no blind automatic retry is implied.
- Command encoding, no-op accounting and the implementation of bounded preparation,
  coherent root/index publication, recovery and replica application.

Exact signatures and encoding are engineering work when they preserve the accepted
behavior. Changes to observable guarantees require a separate decision. The existing
storage prototypes are not a production mutation implementation.

Active push of binary logical changes is accepted: full documents for full writes,
explicit partial effects, no invalidation or automatic Save diff. Q08.1.2 accepts local
reads during lag/disconnection and explicit master-progress checks. Q08.1.3 accepts
bounded resume/full reload and one arena with bounded read waiting. The
[remaining discussion is tracked in the question register](../questions.md#current-discussion). Child restart
is accepted as MemoryOnly/full loading from the master.
Moving that discussion does not mark Q13 complete or select any unresolved detail.

## Accepted repeated scalar target policy

**Accepted on 14 November 2026 under Q13: option A.** This concerns the separate explicit partial
update API, not ordinary Save or repeated local property assignments. For example,
one submitted partial update might contain both `Set("Name", "First")` and
`Set("Name", "Second")` for the same root and scalar field. Syntax is illustrative;
the SDK shape is not selected here. Root-level atomicity already prevents readers
from seeing intermediate assignments.

**A: one final scalar assignment per target path — accepted.** Reject the entire
partial mutation before commit if its submitted assignments repeat a scalar target,
even with equal values. If that mutation belongs to an explicit WriteBatch, the
whole batch fails validation. The caller submits the intended final value once.

**B: ordered repeated assignments — not selected.** Validate and apply the submitted scalar commands
in order during preparation; the last assignment determines the published value.
An invalid intermediate assignment rejects the whole mutation/batch. This lets
callers concatenate scalar command lists but makes their ordering part of the API.

| Effect | A: reject duplicate scalar targets | B: ordered scalar assignments |
| --- | --- | --- |
| Reads and retained storage | Unchanged | Unchanged |
| Write preparation | Validate uniqueness; bounded temporary target tracking | Validate each assignment and preserve its order |
| Temporary memory | Proportional to the bounded submitted target set | Proportional to the bounded command list and preparation needs |
| Complexity | Simple final-assignment contract; caller resolves duplicate intent | More ordering and intermediate-validation cases |

Neither alternative promises a measured performance advantage or changes ordinary
Save: repeated edits to a detached copy still produce one final document. Ordered
collection commands such as multiple List inserts/removes remain supported and are
outside this duplicate-scalar rule. Duplicate root entries in WriteBatch and retries
of an operation token already have separate accepted contracts. Schema-bound target
identity, encoding and validation implementation remain engineering work.
