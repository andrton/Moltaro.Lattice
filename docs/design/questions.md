# Design register and implementation handoff

**Milestone 1 handoff: no unresolved architecture selection currently blocks
planning of the first production engine milestone.** The register retains
49 accepted topic records and nine specified engineering areas. [Unique indexes](../decisions/unique-indexes.md)
close the actual consumer R1 gap. This is design closure, not implementation or
production readiness; required release checks remain explicit.

The R1 reference records the design discussion that led to the accepted Unique
contract. Consumer drafts can change; they do not override the engine baseline.
The Identity package was adapted to the accepted indexes, Unique indexes and atomic
WriteBatch on 14 November 2026; see [Epic 2](../work/epic-2-identity-storage.md).

[Architecture](index.html) · [Decision records](../decisions/README.md) · [Retry protocol](operation-retry-protocol.md)

## Current discussion

Milestone and sprint planning is tracked in [Epic 1](../work/epic-1-core.md). Start with the
implementation contract, which fixes ownership, binary layout, allocation/lifetime,
index preparation, write scheduling, PostgreSQL base/journal/results, TCP replication,
schema migration, initial limits and the deterministic reference workload.

The latest completed check is [OperationRetry](../../src/Prototypes/OperationRetry/README.md):
13 directed PostgreSQL scenarios, nine Unique-index scenarios and two abrupt
process-exit/recovery checks passed together in the final bounded run.
The protocol persists first admission before executing effects, so receipt cleanup
and a restart cannot silently make an old operation new. Its small pooled samples
are not full-engine latency or throughput evidence.

The consumer-required Unique decision is accepted. Other remaining tasks are
implementation, integration, resource tuning and release verification. A newly
identified missing guarantee or material contradiction must be reopened explicitly;
do not relabel a real blocker as tuning, and do not reopen settled product choices
merely to select a method name or a measured configuration value.

## Implementation handoff

The table replaces the former nine-topic open-decision list. The broad OQL-only
rules remain deferred beyond milestone 1; direct index access uses the fixed shared
comparison/binding contract. Backup scheduling and deployment-specific RPO/RTO belong
to infrastructure and do not block engine implementation planning.

| ID | Specified area | Implementation / verification remaining |
| --- | --- | --- |
| Q07.3.6 | [Typed binding and comparison](milestone-1-engine-contract.md#index-preparation-and-traversal) | The shared comparers exist and the direct-index facade binds keys and bounds by the explicit numeric allowlist, every key family verified; OQL stays deferred |
| Q08.1.3 | [Replication and session consistency](milestone-1-engine-contract.md#replication-transport-and-initial-loading) | Implement framing, capture, applied ACK, bounded RAM tail/reload and fixed-position waits; validate actual multi-process behavior |
| Q09.2 | [Durable base/journal](milestone-1-engine-contract.md#independent-base-journal-and-recovery) and [operation protocol](operation-retry-protocol.md) | Integrate exact codecs, 256-byte units, receipts/expiry and recovery; fault and resource tests |
| Q09.4 | [Restore/deployment boundary](milestone-1-engine-contract.md#diagnostics-backup-and-deployment-boundary) | Exercise isolated restore/activation and history change; infrastructure owns backup scheduling/retention |
| Q10.1 | [Fixture](milestone-1-engine-contract.md#reproducible-first-fixture) and [acceptance targets](performance-requirements.md) | Implement equivalent managed/reference harness, record hardware and verify ordinary/burst/memory/readiness gates |
| Q13 | [Write coordinator and revisions](milestone-1-engine-contract.md#write-coordinator-and-revision-semantics) | Integrate typed/dynamic/remote facade, explicit partial/WriteBatch, no-op accounting and retry status; test failures |
| Q14 | [Bounded settings](milestone-1-engine-contract.md#initial-bounded-settings) | Implement reservations and option validation; verify exhaustion, cancellation and recovery; tune within accepted guarantees |
| Q16 | [Native layout](milestone-1-engine-contract.md#native-layout-and-allocation), [collections](milestone-1-engine-contract.md#collections-and-strings) and [schema](milestone-1-engine-contract.md#schema-identity-migration-and-facade-integration) | Implement selected codecs, descriptors/chunks/hash layouts and schema binding; binary/lifetime validation |
| Q17 | [Protection and compaction](milestone-1-engine-contract.md#reader-protection-publication-and-compaction) | Implement claims/generations, bounded movement/shrink and diagnostics; verify fixed-live-data churn and read safety |

## Accepted decisions

A settled contract is not a claim of implemented behavior. The nine specified
engineering areas above remain separate and are not counted twice. Detailed rationale belongs to the
linked topic owner.

| ID | Decision record | Accepted contract |
| --- | --- | --- |
| Q01 | [Atomicity and explicit database WriteBatch](../decisions/document-model.md#q01-what-must-change-together) | Accepted: ordinary per-root consistency plus atomic WriteBatch across entity types in one database, without a limit of its own since the owner's decision of 15 November 2026; complete-object batch saves use Upsert with one transaction boundary; lock-free application-side construction, readers available during I/O, atomic publication without a barrier that drains readers since the amendment of 22 November 2026, and preserved recovery/replica boundary; no shared multi-call/scan snapshot |
| Q02 | [Flat records or schema-bound owned documents](../decisions/document-model.md#q02-flat-records-or-owned-documents) | Accepted: schema-bound hierarchical documents with exclusive mutable-child ownership |
| Q02.1 | [Non-owning references and imperative .NET graph traversal](../decisions/document-model.md#q02.1-can-typed-references-support-imperative-graph-algorithms) | Accepted: one primary key, references by key without referential integrity, generated value views |
| Q02.2 | [Array ordering](../decisions/document-model.md#q02.2-must-arrays-preserve-their-supplied-order) | Accepted: preserve supplied sequence and duplicates; physical contiguity not promised |
| Q03 | [Schema evolution policy](../decisions/schema-and-types.md#q03-how-should-the-schema-change) | Accepted: explicit migration with temporary unavailability of affected data in the first version. Scope confirmed 14 November 2026: on the master all writes pause and reads of unaffected types continue; every child reloads fully; migration is for a Durable master only |
| Q03.1 | [Initial numeric types and fractional precision](../decisions/schema-and-types.md#q03.1-how-should-numeric-fields-be-typed) | Accepted: per-field types, including one-byte and two-byte integers |
| Q03.2 | [Nullability and absent field values](../decisions/schema-and-types.md#q03.2-when-may-a-field-have-no-value) | Accepted: Nullable=false by default; null only for explicitly nullable fields |
| Q03.3 | [List, Map and Set collection abstractions](../decisions/schema-and-types.md#q03.3-should-the-schema-support-list-map-and-set) | Accepted for v1: arena views; List preserves order/duplicates; Map/Set enforce uniqueness without ordering guarantees |
| Q03.4.1 | [Scalar and primary-key type catalog](../decisions/schema-and-types.md#q03.4.1-scalar-and-primary-key-type-catalog) | Accepted: eight fixed-width integers, Float32/Float64, Decimal, Boolean, String, Guid, temporal types and Binary; eight integer types/String/Guid for keys; eight integers for enum/Flags backing |
| Q03.4.2 | [Collection declarations and input rules](../decisions/schema-and-types.md#q03.4.2-nested-and-collection-declarations) | Accepted: complex List/Map values; scalar/enum/reference identity except Binary; explicit nullability, non-null Map keys, first-representation Set coalescing, atomic rejection of duplicate bulk Map keys and preserved existing key spelling on set-by-key; full replacement uses its input |
| Q03.5 | [Ordinary enum fields and .NET enum bindings](../decisions/schema-and-types.md#q03.5-should-schema-fields-support.net-enums) | Accepted: numeric storage, Int32 default, explicit supported underlying type; only declared values |
| Q03.6 | [Flags combinations and unknown-bit validation](../decisions/schema-and-types.md#q03.6-should-flags-allow-combinations-of-declared-bits) | Accepted for v1: explicit Flags mode, all combinations of declared bits, 0 as the empty set; unknown bits are errors |
| Q03.7 | [Enum and Flags comparison/order](../decisions/schema-and-types.md#q03.7-how-should-enums-and-flags-be-ordered) | Accepted: numeric order of the declared underlying type for comparisons, sorting and ordered indexes |
| Q03.8 | [Enum aliases and canonical names](../decisions/schema-and-types.md#q03.8-can-enum-members-have-aliases) | Accepted: aliases with one explicit canonical name when several names share a code; original alias is not stored; naming remains schema/application responsibility |
| Q03.9 | [Foundations and timing for deferred schema conversion](../decisions/schema-and-types.md#q03.9-when-should-schema-conversion-run) | Accepted: evolution foundations in v1, migrations with affected data paused; mixed layouts and lazy conversion in v2 |
| Q04 | [String identity and comparison](../decisions/strings.md#q04-how-should-strings-be-compared) | Accepted: Ordinal by default; explicit OrdinalIgnoreCase per field; preserve original text |
| Q04.1 | [Physical string encoding](../decisions/strings.md#q04.1-how-should-strings-be-encoded-in-storage) | Accepted: database creation setting; UTF-8 default, explicit UTF-16 option; no per-field or dynamic selection |
| Q04.2 | [String deduplication policy](../decisions/strings.md#q04.2-when-should-equal-strings-share-storage) | Accepted: Plain default, explicit Dictionary per string field; no automatic switching in v1 |
| Q04.3 | [String dictionary scope](../decisions/strings.md#q04.3-where-should-identical-strings-share-storage) | Accepted: one logical dictionary per database for opted-in fields; no cross-database sharing in v1 |
| Q04.4 | [String reclamation, ID reuse and retention budgets](../decisions/strings.md#q04.4-how-should-unused-strings-be-reclaimed) | Accepted: stored-reference counts and deferred safe reclamation; stable generational handles, lifetime accounting and initial limits are specified in the milestone contract |
| Q04.5 | [Text input validity](../decisions/strings.md#input-validity-and-preservation) | Accepted: well-formed Unicode in both UTF-8 and UTF-16; reject malformed strings before commit without character substitution; preserve valid text exactly |
| Q05 | [Managed versus native bulk storage](../decisions/memory-and-access.md#q05-should-the-arena-use-managed-or-native-segments) | Accepted: native bulk segments with managed C#/.NET control code; bounded comparison completed |
| Q06 | [Owned ID results and internal DSL access](../decisions/memory-and-access.md#q06-what-do-id-reads-and-dsl-execution-return) | Clarified: engine protects copying/evaluation; owned results outlive internal protection; no caller-held lock required |
| Q06.1 | [Logical read-gate granularity and publication protocol](../decisions/memory-and-access.md#q06.1-should-one-gate-cover-an-entire-owned-record) | Accepted: one logical gate per root document and all owned mutable blocks; physical gates, acquisition order and publication are specified in the milestone contract |
| Q06.2 | [Separate public borrowed-view access](../decisions/memory-and-access.md#q06.2-should-v1-expose-a-protected-view-to-application-code) | Accepted for v1: owned results plus a separate short synchronous protected read-only view; no escape, I/O, await/yield, external waits or nested database calls |
| Q06.3 | [Runtime access without generated C# classes](../decisions/memory-and-access.md#q06.3-can-records-be-used-without-generated-classes) | Required in v1: schema-driven record/field access, including protected reads without whole-document materialization; API shape remains illustrative |
| Q07.4 | [Unique secondary indexes](../decisions/unique-indexes.md) | Accepted for milestone 1: explicit Unique on single/composite keys; any-null keys exempt; final-state master validation rejects the complete conflicting operation/WriteBatch before effects commit |
| Q07 | [Search, ordering and single-field/composite indexes](../decisions/queries.md#q07-how-should-filtering-ordering-and-indexes-work-together) | Accepted for v1: filtering and ordering by one or several fields; single-field and composite indexes explicitly declared in the schema; primary hash table without standard .NET Dictionary; ArcSlim selected; Arc OrderedMultiMap accepted for ordered secondary indexes with its memory tradeoff; key/lifetime integration is specified and requires release verification |
| Q07.1 | [.NET enumeration and queries without a suitable index](../decisions/queries.md#q07.1-should-queries-run-without-a-suitable-index) | Accepted: enumerate entity collections from .NET; OQL scans by default when no suitable index is available, with a per-query opt-out |
| Q07.1.1 | [Query execution-time and temporary-memory limits](../decisions/queries.md#q07.1.1-should-query-execution-have-resource-limits) | Accepted: database defaults, per-query overrides and explicit failure on exhaustion; initial defaults and accounting boundaries are specified in the milestone contract and require resource tests |
| Q07.2 | [Query boundaries and result shape](../decisions/queries.md#q07.2-what-can-a-v1-query-return) | Accepted: whole documents or selected fields from one document in v1; joins, combined cross-document results and computed result fields in v2, for OQL and selection APIs |
| Q07.2.1 | [Filtering owned collections in OQL](../decisions/queries.md#q07.2.1-should-oql-filter-inside-owned-collections) | Accepted: Any, Contains, ContainsKey and Map-entry predicates inside owned document collections; no automatic element indexes |
| Q07.3.1 | [Null semantics in query predicates](../decisions/queries.md#q07.3.1-how-should-null-affect-query-predicates) | Accepted: two-valued predicates, explicit null tests, null-aware equality, false range comparisons on null and defined collection behavior |
| Q07.3.2 | [Null placement in ordered results](../decisions/queries.md#q07.3.2-where-should-null-appear-in-ordered-results) | Accepted: nulls last for both directions by default, with explicit NULLS FIRST/NULLS LAST per sort key |
| Q07.3.3 | [Equal-key order and explicit unique ordering](../decisions/queries.md#q07.3.3-how-should-equal-sort-keys-be-ordered) | Accepted: explicit sort keys only; ties unspecified; caller adds the primary key for unique order |
| Q07.3.4 | [Result limiting and pagination](../decisions/queries.md#q07.3.4-how-should-v1-limit-and-page-results) | Accepted: Limit/Offset in v1; generic continuation by the last sort key deferred to v2; no snapshot across calls |
| Q07.3.5 | [Traversal during concurrent changes](../decisions/queries.md#q07.3.5-what-may-a-traversal-observe-while-records-change) | Accepted: coherent records, no repeated primary-key identities per pass, unchanged matches included in a successful full pass, mutable membership and no common snapshot |
| Q08 | [Replica startup: full load before reads or partial availability](../decisions/replication-and-hosting.md#q08-can-a-child-load-everything-before-accepting-reads) | Accepted: binary arena-block transfer from master memory, fenced capture of root slots (the temporary deletion metadata and watermarks were withdrawn by the owner on 17 November 2026: a child skips a state it already holds at a later sequence), complete-transaction catch-up and local indexes before Ready/applied acknowledgment at a fixed published position; MemoryOnly children have no PostgreSQL connection or demand paging; startup time still measured. Added 14 November 2026: children that start together load in parallel, up to a configurable limit of 10 by default |
| Q08.1.1 | [Live replication delivery and payload](../decisions/replication-and-hosting.md#q08.1.1-how-are-live-changes-delivered) | Accepted: active push of binary logical mutations and shared master/child arena application; full documents for complete writes, explicit partial effects and ordered Delete; no child SQL commit, invalidation or automatic full-Save diff |
| Q08.1.2 | [Local reads and replication status](../decisions/replication-and-hosting.md#q08.1.2-what-may-a-lagging-or-disconnected-replica-read) | Accepted: intact unconstrained local reads during lag/disconnection; explicit master/applied progress comparison; no stale-age cutoff or ordinary per-read master coordination. The later session-consistency contract adds bounded waits for supplied minimum positions and local application before child-facing write success |
| Q09.1 | [Durable write acknowledgment](../decisions/write-acknowledgment.md#q09.1-when-does-a-durable-write-succeed) | Accepted: wait for reliable storage acknowledgment and coherent local publication; concurrent independent preparation, per-object conflict ordering and sequential saving/publication of short bounded groups; internal PostgreSQL group commit does not add shared read atomicity |
| Q09.3 | [External transaction integration](../decisions/external-transactions.md#q09.3-transactions-spanning-lattice-and-application-sql) | No participation in external transactions in v1; internal Durable commits and single-document atomicity unchanged |
| Q09.5 | [Persistence mode selection](../decisions/persistence-modes.md#q09.5-how-is-persistence-mode-selected) | Accepted for v1: select one mode at local instance open time; Durable by default, explicit MemoryOnly; no live mode switching |
| Q09.6 | [First Durable storage backend](../decisions/persistence-backends.md#q09.6-durable-storage-backends) | Accepted: implement PostgreSQL storage first; MemoryOnly stays SQL-independent; a future file backend has no selected delivery date |
| Q11 | [Embedded engine versus mandatory standalone hosting](../decisions/replication-and-hosting.md#q11-can-the-master-run-inside-the-application-process) | Accepted: embedded library and standalone service share one engine; no mandatory replica for an embedded master; worker/master plus scaled reader replicas is a supported design scenario |
| Q12 | [One database per engine instance](../decisions/database-scope.md#q12-one-database-per-engine-instance) | Accepted: no partition abstraction; database-owned schema/data/indexes/dictionary, type + key lookup, references and WriteBatch within the database, full-database replicas |
| Q15 | [Configured master and child connection](../decisions/replication-and-hosting.md#q15-configured-master-and-child-connection) | Accepted: one environment-managed master, configured URL on each child, reconnect with compatible resume or full reload; no engine election, child promotion or fault-tolerance orchestration. Changed 14 November 2026: one store has one master at a time and the engine enforces it — a master takes the leadership of its store at open, a second master is refused while the first lives, and a master that lost the leadership commits nothing more; a child's open has a finite configurable timeout; the replication port is protected by the deployment, not by the engine |
| Q18 | [Control and maintenance tools](storage-and-maintenance.md#accepted-administrative-scope) | Accepted first-release scope: diagnostic DI services and host-mounted HTTP endpoints for local/master status; automatic maintenance remains. Manual maintenance commands, backup/restore launch commands, CLI/GUI and a mandatory separate management host are excluded. Infrastructure launches backup/restore; exact API/protocol implementation remains work |
| Q19 | [Physical index lifecycle](../decisions/index-lifecycle.md#q19-should-physical-indexes-be-persisted-or-rebuilt) | Accepted: definitions in the schema; every node builds primary and declared secondary indexes locally before readiness; no physical Lattice index images persisted or transferred in v1 |

## Engineering work

These are responsibility owners for the receiving planner, not a sprint plan or
another list of approval questions. The implementation contract fixes their baseline.

| ID | Owner | Required work |
| --- | --- | --- |
| E01 | Native allocation and collections | Implement class/region free lists, descriptors/chunks, compact/hash collections and bounded shrink/compaction |
| E02 | String storage | Implement Plain/Dictionary, immutable stable text handles, stored/prepared/index reference accounting and safe reuse |
| E03 | Lifetime and publication | Implement database/root/catalog/text/allocator order, entry claims/generations and atomic publication/fault transitions |
| E04 | Index integration | Wrap selected Arc structures, reserve growth/nodes/scratch, implement shared comparison and budgeted live traversal |
| E05 | Schema/bindings | Compile stable IDs/layouts, typed/generated/dynamic bindings, versioned formats and explicit eager migration |
| E06 | PostgreSQL provider | Implement independent base/256-byte units, journal/applier/recovery, durable admission/results and bounded retirement |
| E07 | Transport/lifecycle | Implement framed TCP, capture/remapping, live application/ACK, resume/reload, forwarded writes/status and readiness |
| E08 | Public facade and diagnostics | Complete existing facade against contracts, including tokens/status, revisions, WriteBatch, dynamic/protected views, explicit indexes and resource errors |

## Deferred scope

OQL, joins, cross-call graph snapshots, external transactions, automatic failover,
partitions, lazy mixed-schema conversion, generic keyset continuation and manual
maintenance UI/API are not first-milestone prerequisites. Planning may not defer
accepted durability, indexes, memory lifetime/compaction or replica consistency.

The [verification handoff](milestone-1-engine-contract.md#verification-handoff-and-closure)
is mandatory release work. No production readiness, implementation date or sprint
plan is claimed by closing this register's design choices.
