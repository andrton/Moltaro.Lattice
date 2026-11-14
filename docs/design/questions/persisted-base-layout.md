# Persisted base layout and live-arena locations

> Historical discussion. The current status is in the [decision register](../questions.md); the [milestone 1 contract](../milestone-1-engine-contract.md) specifies the mechanisms left open here.

**Accepted on 13 November 2026 for Q16/E06:** encode the PostgreSQL base as
versioned binary records and owned payloads in provider-addressed saved units,
independent of live native segment/offset locations. Reconstruct the arena on load.
Do not persist an image of the allocator's current memory placement as the base.

[Physical foundation](../engine-foundation.md) · [Native layout](../../decisions/physical-storage.md) · [Journal and base](../../decisions/checkpoint-and-journal.md)

The accepted contract is maintained in [Physical storage](../../decisions/physical-storage.md#independent-persisted-base-and-arena-reconstruction).
This note retains the rationale and the alternative considered.

## Decision scope

The foreground compact journal, background coalescing applier, 256-byte saved-unit
baseline, allocated native arena and local index reconstruction are already accepted.
This decision separates saved record/payload placement from live native placement.
It does not reconsider the storage backend, mmap, journal strategy or ordinary
acknowledgment. Exact packing/addressing is still explicitly open under Q16/E06.

For example, a root moves from native segment 3 to segment 8 during compaction.
Its key, field values and revision do not change. Must the saved base follow that
physical relocation, or can its own addresses remain unchanged?

## A. Provider-addressed binary data, independent of RAM placement — accepted

The saved representation identifies a root by type/key and describes its encoded
fields and owned payloads using provider-local locations. Schema/layout versions
and the applied-through boundary remain recoverable. Native addresses, runtime root
gates, index nodes, free lists and arena slack are not authoritative persisted state.

A conceptual provider root directory can locate the saved units for a record;
variable payloads can span several units. These are internal storage locations, not
a second public object key or a universal runtime handle table for owned blocks.
The exact SQL layout, directory encoding, headers and unit-packing algorithm require
specification and experiments. The accepted 256-byte baseline is unchanged.

The background worker reads the committed journal, resolves the affected saved data,
coalesces compatible changes and updates the required units/metadata with its applied
position atomically. Whole Save remains a whole logical replacement; explicit partial
changes need not rewrite every saved unit of a large record. Variable growth, collection
restructuring and metadata can still amplify writes and must be measured. No foreground
UPSERT of the full saved record or one giant SQL blob per root is selected here.

On reopen, stream the coherent base through bounded buffers, allocate native blocks,
install fields/descriptors and reconstruct required dictionary/runtime metadata.
Then replay the retained tail and build physical indexes before Ready under the
existing recovery contract. Loading can write directly into native representation;
it does not require constructing the whole database as CLR objects first. Shared
scalar codecs may be reused; independent addressing does not require unrelated
implementations of every data type.

No second full arena or resident copy of the saved base is required. Temporary
decoding, lookup and replay buffers still have costs and need explicit bounds.
Persisted directory metadata need not become another complete in-memory index merely
because it exists in PostgreSQL; the concrete loader/applier design must prove this.

Pure native relocation changes neither the logical journal nor PostgreSQL base
locations. Disk packing/reclamation is a separate provider operation. Conversely,
provider-local relocation must preserve its own references and recovery boundaries;
it does not force an arena move. Fragmentation is still possible in both stores.

## B. Preserve an arena-shaped base with matching relative locations — not selected

Save native segments or a compatible memory image with the same relative locations,
plus sufficient metadata to reconstruct runtime ownership. Native process pointers
and synchronization objects still cannot be restored as working live state verbatim.
The journal/applier remains responsible for maintaining a recoverable image.

This may reduce field decoding and pointer reconstruction during startup through
bulk copying of compatible regions. It does not remove journal replay, validation,
native allocation or local index building. Actual readiness improvement is unmeasured.

If the image must match the current live layout, compaction must propagate physical
moves and descriptor repairs into the image consistently, adding maintenance writes
and ordering dependencies. If the image keeps an older independent placement instead,
that design needs remapping and its own placement lifecycle; it is no longer a direct
copy of the current arena. Holes/slack can increase saved/transfer size unless the
format includes an additional packing/reconstruction mechanism.

B does not inherently require a second arena, but couples the persistent format more
closely to allocation layout and version changes. Its startup benefit must justify
that coupling without weakening recovery, memory limits or compaction progress.

## Costs and rationale

| Aspect | A: independent saved representation | B: matching arena-shaped image |
| --- | --- | --- |
| Resident reads | Existing native/index access; no base decoding per read | Existing native/index access |
| Foreground writes | Existing journal commit; base mapping remains background work | Existing journal commit, plus an image-maintenance protocol |
| Background work | Saved-unit lookup, encoding and coalescing; pure RAM moves need no SQL changes | Layout-preserving image updates can add physical relocation traffic |
| Memory | One arena plus bounded loader/applier workspace and required metadata | One arena is possible; layout/relocation bookkeeping still needs a budget |
| Startup | Reconstruct native representation, replay tail and build indexes | Potentially more bulk copying; replay/validation/index work still remains |
| Disk | Encoded live data and provider metadata; independent reuse/packing needed | May preserve arena holes/slack or need a separate packing mechanism |
| Complexity | Explicit saved-to-live mapping; allocator and durable placement evolve independently | Less reconstruction only if layouts stay compatible; tighter allocator/recovery coupling |

Option A is accepted for v1: bounded memory and frequent small updates should not force native
compaction to become durable application traffic. The cost is a real loader/codec and
provider-addressing design, not a claim that two representations are free or always
smaller. No comparative full-engine performance result exists yet.

Validate cold restore time and peak memory, dictionary/collection reconstruction,
small-update write amplification and background lag under concurrent native compaction.
Compare the same logical workload and verify state after interrupted recovery. Before
implementation, specify the root directory, block addressing, delete/reuse rules,
schema/version metadata and coherent base/tail capture. Initial replica loading uses
the separate [binary arena-block transfer direction](../../decisions/replication-and-hosting.md#initial-replica-state-representation);
its exact image/capture/handoff implementation remains E07 work. It does not change
this independent saved representation or the accepted live logical mutations.
