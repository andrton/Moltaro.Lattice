# Mutation preparation and physical publication

**Accepted on 13 November 2026 under Q16:** prepare bounded changes before
durable commit; publish small fixed-field assignments in place under root protection,
and install prepared replacement blocks where variable payload changes require them.
Do not clone the complete owned tree for every explicit partial update.

[Decision owner](../../decisions/physical-storage.md#targeted-mutation-preparation-and-publication) · [Durable acknowledgment](../../decisions/write-acknowledgment.md) · [Memory admission](../../decisions/write-admission.md) · [Accepted write coordination](master-write-order.md)

## Existing contracts

Readers see whole roots. Durable success follows PostgreSQL commit and coherent local
root/index publication; no root/index gate spans I/O. Preparation memory is bounded,
and ordinary capacity refusal happens before journal submission. Save supplies the
whole final object; partial updates use a separate explicit API. Automatic diff and
change tracking remain excluded. These contracts are not being reopened.

The accepted choice is targeted preparation for explicit partial updates and full
replacement for Save. The alternatives below explain its costs. Concrete reservation,
scheduling and failure-state mechanisms remain engineering work.

## A. Targeted preparation and protected publication — accepted

For an explicit assignment to a fixed-size field, stage the new value, validation
results and required index work rather than a full copy of the root's owned tree.
For changed variable values, prepare necessary replacement blocks/chunks privately.
Unchanged owned payloads stay in place. Collection restructuring may require more
than one changed chunk; a one-element edit is not a constant-copy-cost guarantee.

For full Save/Replace, prepare a complete replacement from the supplied final value
without comparing it with the old tree to infer edits. This path can temporarily
require both old and new owned data. Dictionary strings may reuse shared entries
through the existing dictionary policy; that is not automatic object diffing.

1. Validate, establish conflict order and prepare the mutation. Account for retained
   inputs, the command/journal representation, replacement blocks, index work and
   string-store lifetime holds. Acquire actual publication resources before commit;
   an unbacked byte-budget promise alone does not eliminate allocation failure.
2. Commit the logical mutation to the PostgreSQL journal without root/index gates.
   The published object remains unchanged while readers continue to access it.
3. Under exclusive root protection and the coherent index-publication protocol,
   assign prepared fixed values, install replacement descriptors or a replacement
   root, update revision/index state and retire displaced storage. Release protection
   before completing the successful operation.

The master must preserve the prepared root state against conflicting logical writes
until publication, using scheduling rather than a reader-blocking gate over I/O.
Many callers may submit concurrently. This requirement does not choose global versus
per-root commit scheduling. Compaction may change physical locations while a write
waits: publication must resolve current locations through the protected entry and
descriptors, not dereference addresses cached before the storage wait.

Expected validation/capacity failures must be resolved before journal submission.
Publication should not need ordinary allocation, user callbacks or new validation
that can reject a committed operation. Unexpected post-commit failure requires
stopping inconsistent service and recovering committed state; it cannot be reported
as a clean rejection. Exact failure transitions remain a separate engineering task.
Likewise, an uncertain PostgreSQL commit response cannot release dependencies on the
assumption that the transaction definitely failed; follow the recovery protocol.

Old replaced blocks are reclaimed after all relevant retainers finish. No permanent
history is introduced. In-place assignment is engine-internal after durable commit;
application copies and read-only borrowed views do not become writable arena access.

## B. Prepare a full replacement owned tree for every mutation — not selected

Even an explicit scalar update builds a complete private replacement tree, then
publishes its root location after commit. Shared dictionary strings can still be
reused, but ordinary owned collections and payloads are copied. Unchanged-subtree
sharing would add a different ownership/lifetime design and is not assumed here.

The old root remains readable during preparation and I/O. A prepared-root switch
simplifies data installation, but index maintenance and safe reclamation remain;
publication is not reduced to an uncoordinated pointer assignment.

## Consequences

| Aspect | A: targeted preparation | B: full replacement per mutation |
| --- | --- | --- |
| Small partial writes | Copy/stage affected values and required structural work | Copy the owned tree even when one scalar changes |
| Ordinary reads | Same protected access; readers wait for final publication | Same protected access; readers wait for root/index publication |
| Publication pause | Work depends on assignments, descriptor changes and indexes | Root installation is simpler; index changes can still dominate |
| Temporary memory | Bounded plans and changed storage; full Save can still duplicate the object | Old plus new owned tree for each prepared mutation, before buffers/index workspace |
| Complexity | Separate fixed-value and block-replacement paths, with carefully prepared publication | More uniform replacement path, but copying and reclamation of complete trees |

A is selected for small frequent updates and bounded memory. Measure end-to-end latency,
read pauses and peak bytes for scalar patches, collection edits and full Save separately.
Neither approach changes PostgreSQL acknowledgment or guarantees a measured speedup.
Existing component benchmarks do not implement either complete publication protocol.
