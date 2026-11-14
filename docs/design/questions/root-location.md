# Root lookup during relocation

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted on 13 November 2026 under Q16:** use one stable control entry
per root object lifetime. It stores the current relative location and the state
needed to coordinate protected access and lifetime. Root data and owned blocks may
move; the entry remains a safe lookup target while operations retain that lifetime.

[Decision owner](../../decisions/physical-storage.md#stable-control-entry-per-root) · [Memory and access](../../decisions/memory-and-access.md) · [Accepted preparation policy](mutation-preparation.md)

## The question

A lookup can find an object's location just before a compactor moves it. The engine
must protect the transition from index lookup to root access, not only the bytes
after the reader has started using them. A lock stored solely inside a movable block
does not by itself make that transition safe.

Root-level atomicity, short protected reads and relative segment/offset addressing
are already accepted. This decision places root location/lifetime coordination
in the stable entry. The steps below outline the required acquisition/lifetime protocol;
its concrete gate and reclamation implementation remains engineering work. It does not reopen a universal handle table for every owned block or introduce
a second public key. Logical object identity remains type + primary key.

## A. Stable root control entry — accepted

```text
Database / type / primary key -> primary index -> root control entry
Root control entry -> current location (segment, offset) -> root bytes
Root / owned parent -> relative payload descriptor -> owned payload bytes
```

The control entry has current location, revision and lifetime/access state.
[Managed arrays of structs](../../decisions/physical-storage.md#managed-arrays-of-structs-for-control-entries)
are accepted for v1, with growth by additional arrays and an independently grown
directory. The locator identifies the logical slot and root lifetime, not a pinned
CLR address. Exact fields, byte widths, array capacity and gate implementation
require engineering/measurement. A separate managed object and lock per root is
not selected; the native data arena is unchanged.

1. Lookup retains the control entry under short index/lifetime protection. Release
   index protection before waiting for the root gate.
2. Acquire root protection, validate that the retained lifetime is still live, then
   read the current location. Do not carry an unprotected arena pointer through this
   transition. Deletion yields absence/retry according to the operation's contract.
3. Copy or evaluate under the root gate. A borrowed view remains within this scope.
4. Release root protection and the lifetime retention when finished.

For a root move, the compactor takes exclusive root protection, relocates data and
updates the entry's location. No protected reader may still use the old bytes when
they are reused. Moving owned payloads still updates their owner descriptors.
Dictionary entries retain their separate shared-text lifetime protocol.

Secondary indexes can retain the same safe root locator rather than a raw arena
address. This avoids rewriting their locators solely because the root moved; indexed
field changes still require coherent index maintenance. Any structure that actually
retains a physical location must still be updated safely.

Deletion retires the old root lifetime. Recreating the same key must not let a retained
old locator refer to the replacement object. Retention plus generation checks or an
equivalent proven reuse protocol must cover slot reuse; a generation number alone is
not sufficient. The entry can be reclaimed after its retainers finish; no permanent
historical versions or durable control-entry IDs are required. Local control entries
are rebuilt on load; logical persistence/replication never depends on their addresses.

## B. Direct root locations with independent key-based gates — not selected

Keep root locations directly in indexes and coordinate access through gates resolved
from the logical key before using a location. One possible compact mechanism maps
keys to a bounded array of locks. Unrelated keys can then share a lock and wait for
one another. Index candidates must be revalidated under the appropriate protection.

Moving a root requires repairing every actual physical locator under coherent
protection. This retains the accepted relative-address model without A's stable root
entry, but it does not remove revision metadata, synchronization or lifetime checks.
An implementation using per-key gate entries also needs to count their memory cost.

## Costs and rationale

| Aspect | A: stable root entry | B: direct locations and key-based gates |
| --- | --- | --- |
| Reads | Location resolution through control metadata; potentially another dependent memory access, depending on index layout | May avoid separate locator indirection, but still resolves gates and revalidates lookup; shared gates may cause unrelated waits |
| Ordinary updates | Uses the same root coordination; index changes remain necessary | Similar logical work; physical gate choice affects contention |
| Root movement | Change the entry's location; indexes using the stable locator need no address repair | Repair all physical locators; movement cost depends on index representation/count |
| Memory | Per-root control metadata and retained entries; cost must include index integration and spare capacity | Can use a bounded shared gate array; revision/lifetime metadata and index locators still consume memory |
| Complexity | Explicit root lifetime and movement point, plus safe entry allocation/reuse | Less indirection, but more coupling between lookup, gate selection, index repair and relocation |

A is selected for the first implementation because it centralizes safe root acquisition
and relocation while keeping owned payloads directly addressed. No fixed entry size
or measured speed advantage is claimed. Compare total metadata bytes and read/update
latency, including concurrent compaction, deletion/reinsertion and slow readers.

This decision does not provide a snapshot across objects or extend borrowed-view
lifetimes. Exact lock order across root/index/dictionary operations, entry reuse and
publication failure transitions still require a complete engineering design.

## Implementation task: safe lookup, deletion and entry reuse

**Clarified on 13 November 2026:** the retention algorithm is an E03 implementation
task, not a question awaiting product approval. No counter, epoch or generation-based
algorithm has been accepted by removing it from the approval queue. The stable-entry
and per-root protection contracts above remain in force.

Required behavior for a read racing with deletion:

- If deletion publishes before the read obtains protected access to that root
  lifetime, the read observes absence; a query discards that stale candidate.
- If the read already holds protected access, actual removal/reclamation waits
  for that access to end. Copy reads return their owned result normally.
- Published deletion removes the object from subsequent lookup. Recreating the same
  primary key starts a new lifetime; an already retained old candidate must never
  silently become that replacement object or another allocation.

Implement safe lookup-to-root acquisition, current-location resolution, deletion,
relocation and reuse as one coherent protocol. Release index protection before
waiting for the root gate, and never hold an arena/index gate across I/O. A lifetime
hold is not a snapshot or a requirement to keep data at a fixed physical address.
Shared text continues to follow its separate accepted ownership/protection contract.

The engine implementation owns the choice of retention bookkeeping, metadata layout,
gate order and safe reuse mechanism. Verify primary lookup and secondary candidates
against deletion/reinsertion, compaction, delayed access and exceptional cleanup with
small bounded concurrency checks. Measure per-root metadata, read overhead and
deferred memory; only a material change to the accepted behavior or resource contract
needs further product discussion. No new prototype or performance result is claimed.
