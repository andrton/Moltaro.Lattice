# Backup and restore

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md)
now specifies the mechanisms and starting values described as open, partially accepted or
illustrative below, and the [decision register](../design/questions.md) owns the current
status. The earlier notes are kept as the decision's history; they do not reopen what was
specified. "v1" below is wider than the first production milestone, whose scope is in
the [planning handoff](../design/planning-handoff.md).

Accepted Moltaro.Lattice contracts, rationale and alternatives. No backup service,
restore tool or recovery experiment is implemented by this record.

[Decision index](README.md) · [Question register](../design/questions.md) · [Backup requirements](../design/storage-and-maintenance.md#backup-and-restore-q09.4)

## Q09.4: Backup and restore

**Partially accepted:** v1 Durable-master backups use PostgreSQL/deployment-native
mechanisms. Lattice validates and opens the restored durable state; it introduces
no custom Durable backup archive. Restore uses an isolated replacement PostgreSQL
target, preserves former storage until verification and switches with one Lattice
arena. Restore activates a new Lattice history and requires full child reload; an
ordinary restart of the same continuous Durable history preserves its identity.
Infrastructure executes backup/restore; v1 exposes no Lattice commands to launch
those jobs. Concrete deployment procedure, recovery objectives, retention and engine
activation/validation details still require specification and verification.
MemoryOnly export and partial portability are separate scope, not supplied by this policy.

[Backup/restore control](../design/storage-and-maintenance.md#accepted-backup-and-restore-control)
was accepted on 14 November 2026: deployment-owned execution, with a verified Lattice
restore-activation/reopen path and diagnostics. Ordinary same-history reopen remains
distinct from explicit restore activation. This does not change the capture/restore boundary.

### Durable backup responsibility

**Accepted on 13 November 2026:** option A for v1. PostgreSQL/deployment
mechanisms capture and restore a consistent recoverable storage boundary. Lattice
validates the restored metadata and recovery inputs, loads saved state, replays the
required application journal, rebuilds local physical indexes and becomes ready only
after coherent initialization. A raw live-arena copy or saved base without its required
journal is not sufficient. This responsibility decision does not select a PostgreSQL
backup command, recovery-point/time guarantee, additional service or automatic schedule.
The later [restore destination decision](#restore-destination) selects the target policy.

#### Scope and a recoverable boundary

This question concerns the existing PostgreSQL-backed Durable master. It does not
select persistence for MemoryOnly instances or make a volatile child a backup source.
A child's readable state can lag and is not by itself an independent durable backup.
Explicit MemoryOnly export, partial exports and portability to another future storage
backend remain separate scope; A does not claim to provide them.

A recoverable artifact must cover the required schema/format metadata, saved base,
applied-through metadata, dictionaries or equivalent recovery data and the necessary
committed Lattice journal tail at a consistent storage boundary. Index definitions
belong in that state; physical Lattice indexes are rebuilt locally after recovery.
The accepted PostgreSQL application-journal retention and atomic base/position updates
must remain sufficient when background application runs during capture.

The current saved base alone is not an independent backup. Neither is copying a live
arena or exporting unrelated tables at unrelated times. A backup recovers the state
covered by its capture/recovery boundary, not necessarily the latest successful write
at the time someone later requests restoration.

#### A. PostgreSQL-native backup, Lattice recovery — accepted for v1

Use a correctly configured PostgreSQL backup procedure or the corresponding managed
deployment capability to capture and restore the durable storage. Lattice then follows
its ordinary recovery path: validate compatibility/completeness, load the saved base,
replay the required application journal, rebuild its indexes and become ready only
after coherent initialization. V1 Durable backups introduce no Lattice-owned portable archive.

PostgreSQL documents distinct [backup techniques](https://www.postgresql.org/docs/current/backup.html).
Production recovery may use physical base backups with the required archived WAL or
the deployment's managed recovery facility. Exact configuration depends on the chosen
recovery objectives and environment; this acceptance selects the responsibility boundary,
not a complete operations procedure or a mandatory new external service.

A logical export is another tool, not a substitute for every production backup need.
[pg_dump](https://www.postgresql.org/docs/current/app-pgdump.html) provides consistent
exports and can use archives restored by pg_restore, but its documentation cautions
against treating it as the default regular production backup solution in general.
It exports one PostgreSQL database, not all cluster-global objects. A bounded logical
export/restore experiment can validate a recovery path without selecting it as the
production backup policy.

[Base backup plus archived WAL](https://www.postgresql.org/docs/current/continuous-archiving.html)
can support point-in-time recovery when the required WAL sequence is available.
That physical recovery concerns the PostgreSQL cluster; a Lattice application journal
is different from PostgreSQL WAL. Logical dumps do not become PITR backups by adding
WAL files. A complete deployment procedure must account for its actual restore scope.

- **Reads:** native Lattice reads keep their ordinary path; capture can still compete
  for shared CPU, memory bandwidth and storage resources. No read-latency result exists.
- **Writes:** appropriate online PostgreSQL methods need no full Lattice arena freeze,
  but I/O/CPU load, retained storage and schema-operation coordination can affect commit
  latency. No zero-impact or fixed-pause promise is made.
- **Memory:** no second Lattice arena is required. PostgreSQL and backup/restore
  processes still need buffers/workspace, and Lattice recovery still builds its arena
  and indexes within the agreed budgets.
- **Complexity:** reuse the storage system's capture/restore mechanics; Lattice still
  needs a tested recovery contract, compatibility checks and clear completion/failure
  behavior. An administration UI does not automatically configure a working backup.
- **Limits:** artifact format and restore scope depend on PostgreSQL/deployment.
  Co-located databases/tables and cluster-global objects may require a broader restore
  procedure. This choice does not silently mandate one PostgreSQL database per instance
  or promise single-database restoration.

#### B. Lattice-owned archive and export/import — not selected for Durable v1

Define and implement a versioned archive containing a complete recoverable Lattice
state with its consistency boundary, metadata, completeness validation and interruption
rules. Build corresponding import and compatibility handling.

A portable archive could support instance/database selection and future providers,
but those capabilities require explicit scope and implementation. It is not enough
to serialize ordinary per-record reads, which have no shared database snapshot.
Capture could stream from a consistent PostgreSQL view or another defined mechanism;
B does not inherently require a second arena or a full-duration writer pause.

- **Reads/writes:** export serialization, capture and retention add work. Effects
  depend on the capture method; no inherent throughput advantage is established.
- **Memory:** bounded streaming is possible, but buffering, version retention and
  import/index workspace must be designed and measured.
- **Complexity:** own the archive format, its evolution, consistent capture, validation,
  failure cleanup and import, in addition to the ordinary persistence provider.
- **Benefits:** a product-owned portability/selection boundary instead of exposing
  PostgreSQL backup artifacts. These benefits do not make recovery automatically faster.

#### Selection rationale and remaining decisions

The decision selects A for the first PostgreSQL-backed Durable release. It keeps the recovery
responsibility concrete while avoiding a second persistent format before the engine
and storage provider exist. B is valuable when portable/partial export or MemoryOnly
capture becomes an explicit delivery requirement.

Still decide recovery-point/time objectives, backup cadence/retention/destination,
operator-managed master stop/start and history-activation mechanics after restore,
schema-migration coordination, permissions/environment prerequisites and control-tool
delivery scope. Do not expose partial restoration or let an old child silently treat
a restored older history as continuation of its former master state.

Validate restoration in an isolated target independently of the original live process,
including concurrent writes/background application during capture, required journal
tail, metadata/index reconstruction and interrupted/incomplete artifacts. Measure
capture impact, restore-to-readiness time and memory/disk use. Existing journal replay
checks do not prove backup recovery. No backup, restore, infrastructure configuration
or benchmark is executed by this acceptance.

### Restore destination

**Accepted on 13 November 2026:** option A for v1. Restore into an
isolated replacement PostgreSQL target and retain the former target until the result
is verified. Stop the former writer and drain protected users, release its arena,
then load and validate the restored state with one replacement arena before readiness.
This accepts a master availability gap; it adds neither a second full arena nor
automatic failover or automatic rollback. Exact orchestration and recovery budgets
remain engineering and deployment work.

#### What a target means

A target is the independently restorable PostgreSQL storage unit required by the
chosen backup technique, not necessarily one new SQL database. A logical archive can
be restored into a separate database using the appropriate
[pg_restore procedure](https://www.postgresql.org/docs/current/app-pgrestore.html).
Physical base backup/WAL recovery restores a PostgreSQL cluster, as described in the
[continuous-archiving documentation](https://www.postgresql.org/docs/current/continuous-archiving.html).
Managed restore facilities have their own target/endpoint boundaries. No specific
deployment or command is selected by this question.

Restoration recovers a chosen captured/recoverable boundary. Changes written to the
old master after that boundary do not automatically appear in the replacement. This
is not a live migration or a data-merge procedure. Recovery-point objectives and any
later WAL coverage must be explicit; preserving a second disk target is not proof
that all previously acknowledged writes are present in the restored history.

#### A. Isolated replacement target, preserve the former target — accepted for v1

Prepare and restore a separate PostgreSQL target without overwriting the original.
If the old service is still usable, it may continue operating during provider-level
restore, subject to shared resource budgets. If the original has already failed,
this option does not create availability that no longer exists.

Perform the checks possible at the storage level: restore completion, required
objects/recovery inputs and format/schema compatibility. Those checks do not prove
full Lattice startup, index construction or application-level correctness.

To complete the switch without two full Lattice arenas:

1. Stop admission to the old authoritative writer, resolve/drain in-flight work and
   prevent it from restarting as a competing writer. Drain protected arena users
   before releasing old native data. Master stop/start is an operator responsibility under Q15.
2. Release the old arena, then open the restored target with the one replacement
   arena. Load the base, replay the required application journal, rebuild indexes
   and perform engine-level validation before exposing readiness or accepting writes.
3. Retain the former disk target under an explicitly bounded operational policy
   until the restore outcome is verified. Its eventual cleanup is separate work.

There is an availability gap while the replacement arena initializes. Full engine
validation cannot be completed alongside the old full arena under the single-arena
constraint. The old disk target may make a failed attempt easier to abandon or retry,
but it is not assumed to contain correct/healthy data merely because it was preserved.

Before new writes are admitted to the replacement, returning to a usable former
target can be an explicit recovery choice after stopping the failed replacement.
After new writes have succeeded there, switching back blindly can lose them; rollback
needs a separate data/recovery decision. This decision does not add automatic rollback.

Replicas must not interpret a restored older/divergent history as continuation just
because stored sequence numbers happen to match. The later [history decision](#replica-history-after-restore)
requires a new identity and full child reload after restore. The environment supplies
one master under Q15; exact activation/reload coordination remains implementation work.
Existing local stale-read and single-arena reload contracts remain; there is no
zero-downtime guarantee.

- **Reads:** if healthy, the old service can remain useful during SQL restoration.
  Master reads are unavailable during its arena replacement; children follow their
  existing local-read/reload contracts. No duration is measured.
- **Writes:** old writes may continue during preparation if the selected recovery
  objective permits it, then stop for cutover and engine initialization. No second
  active writer is allowed, and later old-history writes are not silently merged.
- **Memory:** one full Lattice arena at a time. A separate PostgreSQL instance may
  require additional provider memory, so this is not a zero-extra-process-memory claim.
- **Disk/resources:** retain both storage targets plus backup/recovery workspace.
  Shared I/O and CPU can affect the old service; provider-level scope may also contain
  co-located data, so placement and budgets matter.
- **Complexity:** provision and validate a target, manage endpoint/configuration
  switching and preserve/retire the former target. Easier to retry preparation without
  having already overwritten the former storage.

#### B. Restore over the existing target while stopped — not selected for v1

Stop the affected writer and required PostgreSQL users, release the old Lattice
arena, then replace the existing storage through the chosen provider procedure.
Reopen Lattice only after provider recovery and coherent engine initialization.
No partially restored tables or half-built indexes may become readable.

- **Reads/writes:** affected service is unavailable throughout provider restore and
  Lattice initialization. Children cannot turn incomplete restored state into ready data.
- **Memory:** one Lattice arena; no extra provider instance is inherently required,
  although restore buffers/workspace still consume memory.
- **Disk/resources:** usually avoids keeping two complete target stores, but still
  needs the backup and technique-specific working space; it is not zero-extra-disk.
- **Complexity/recovery:** fewer target-switching steps, but the previous storage is
  overwritten. A failed restore can require repeating it or using another backup;
  there is no preserved former target to reopen. Both alternatives need ownership,
  history compatibility and readiness coordination.

#### Restore destination rationale and evidence boundary

The decision selects A for v1. It preserves former disk storage until verification while
keeping one full Lattice arena, and separates restore preparation from overwriting
the former storage. B could reduce temporary disk needs, but it is not selected as
a supported v1 restore workflow.

Before implementation, define restore completion checks, operator-managed switching,
history-activation/reload mechanics, old-target retention and recovery-point/time objectives.
Test provider restore failure, engine/index initialization failure, interruption at
the switch and rejection of competing writers. Distinguish rollback before and after
new successful writes. Measure total disk/provider memory, peak native arena memory
and time to readiness. No target, restore, endpoint change or benchmark is created here.

### Replica history after restore

**Accepted on 13 November 2026:** option A for v1. Restoring from a
backup establishes a new Lattice history identity and requires full reload of the
affected child subscriptions. Ordinary master restart against the same continuous
Durable history preserves its identity and remains eligible for bounded resume.
This is small current metadata, not retained historical versions of documents.
The accepted MemoryOnly master state-loss/reset rule remains unchanged.

#### Why a position alone is insufficient

A child applied changes through position 1000. The master is restored to position
800. The child can contain later documents, deletions or field values absent from
the restored master. Sending new changes from 801 cannot undo all those differences.
If the restored master eventually reaches 1000 again, equal numbers still do not
prove equal histories or data. The existing reconnect contract already forbids
using incompatible history; this decision selects the restore-specific policy.

A history identity distinguishes the branch of changes. It is replication metadata,
not a document key, document revision, native address or a collection of historical
object versions. Positions are comparable only within a compatible history and scope.

#### A. New history on restore, full child reload — accepted for v1

The explicit restore activation establishes and durably records a fresh Lattice
history identity before accepting writes or advertising the restored stream as ready.
Do not keep the copied backup identity as authority for resuming old children. Exact
identifier representation, scope and interrupted-activation handling belong to E06/E07;
an activation retry must not accidentally expose mixed identities or create endless
reloads. A normal crash/process restart after activation retains the established
identity when it recovers the same continuous durable history.

The restore path must distinguish historical journal replay needed to reconstruct
the saved state from the new history advertised to children. Do not blindly relabel
old journal records or assume that this decision selects a sequence-number reset.
The recovery/stream boundary and retained-tail eligibility need explicit implementation.

On observing an authoritative master with a different history, a child reports the
mismatch and enters the accepted full-reload path for its database.
It stops admitting reads against the superseded state, drains protected users,
releases/reuses old storage, then loads data, builds indexes and catches up before
readiness. There is one full arena; waiting reads obey the accepted time/count/memory
limits and fail explicitly when those limits or recovery fail.

A disconnected child cannot know that restoration occurred. Until it learns of the
new history, its existing local stale-read contract still applies, with no claim of
current freshness. Detection does not revoke copies already returned to application
code. This decision adds no instantaneous global invalidation or synchronized snapshot.

The old master must be stopped/prevented from competing before replacement activation,
as required by the accepted restore destination. A new history ID identifies state;
it does not select or elect a master. Q15 uses a configured URL and environment-owned
master lifecycle, without a fencing or takeover subsystem. A restored older branch
must not merge discarded writes from a child.

- **Reads:** ordinary local reads keep their current path. Each affected child has
  a reload interval after detecting the new history; no duration is promised.
- **Writes:** ordinary durability/acknowledgment is unchanged. Restored-master writes
  do not wait for every child to reload. Concurrent reloads can consume master CPU,
  memory bandwidth and network capacity and indirectly affect write latency.
- **Memory:** a small amount of history metadata plus bounded transfer/index workspace;
  no second full arena and no permanent versions per mutation. Exact overhead is unmeasured.
- **Network/startup:** transfer the full subscribed restored scope and rebuild local
  indexes, even for a child whose old state might have been a reusable prefix.
- **Complexity:** reuse the accepted mismatch/full-reload path. Durable identity
  activation, reconnect validation and interrupted-recovery behavior still need tests.

#### B. Prove a reusable history prefix, otherwise reload — not selected for v1

Attempt incremental continuation for a child only when retained lineage and mutation
history prove that its applied state is a compatible prefix of the restored branch
and the complete required tail is available. Children ahead of the chosen restore
point, divergent children and unprovable cases still fully reload. Numeric positions,
timestamps or matching document counts alone are not proof.

- **Reads/network:** an eligible child can avoid a full reload and its read outage;
  others pay the same reload cost as A.
- **Writes:** no child acknowledgment barrier is inherently required, but lineage,
  replay eligibility and retained-history bookkeeping add work of unmeasured cost.
- **Memory/disk:** may reduce transfer/rebuild workspace in eligible cases, while
  requiring additional bounded proof metadata and retained history. No second full
  arena or unlimited history is inherently necessary.
- **Complexity:** define branch ancestry, exact prefix proof, scope/schema compatibility,
  trimming rules and interruption handling. No automatic merge or rollback of child
  changes is implied. The likely benefit depends on how often restoration occurs
  and how many surviving children are actually reusable.

#### Restore history rationale and evidence boundary

The decision selects A for v1. It keeps restoration predictable and uses the existing
full-load mechanism. Restore pays a full reload; normal same-history Durable restarts
remain eligible for bounded resume. B is not selected for v1; future optimization
would need measured recovery benefits to justify protocol and retention work.

PostgreSQL also distinguishes histories after archive recovery using
[timelines](https://www.postgresql.org/docs/current/continuous-archiving.html#BACKUP-TIMELINES).
That is an analogy, not an implementation dependency: Lattice needs its own logical
history contract, including for logical restore. Its identity is not a PostgreSQL
timeline or WAL position and is not inferred from the server address.

Before implementation, verify a child ahead of the backup, equal positions on
different branches, unavailable/slow children, restore-activation interruption and
ordinary same-history restart. Measure full reload under concurrent writes and
multiple children within bounded resources. Preserve the [accepted revision-token
identity guarantee](mutation-commands.md#recovery-and-replica-requirements): old tokens
must not accidentally match after restore. Exact token encoding and pending-client-
write handling across activation remain Q13/E07 implementation work; this decision
does not authorize automatic replay of old requests into a new history. No protocol
or restore workflow is implemented or measured here.

See [question register](../design/questions.md#current-discussion).
