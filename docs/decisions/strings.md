# String representation and lifetime

**Current implementation baseline:** the [milestone 1 contract](../design/milestone-1-engine-contract.md#collections-and-strings)
now specifies stable text handles, reference accounting, protection, relocation,
replica remapping and initial resource limits. Earlier open-mechanism notes below
record the decision's history; they do not reopen those specified mechanisms.
Their implementation and integrated resource/lifetime checks remain required.

Accepted Moltaro.Lattice decisions, rationale and alternatives. Decision IDs are
retained from the original discussion. Acceptance applies to the stated contract;
explicitly unresolved details are not silently approved.

[Decision index](README.md) · [Question register](../design/questions.md) · [Architecture](../design/index.html)

- [Q04](#q04-how-should-strings-be-compared)
- [Q04.1](#q04.1-how-should-strings-be-encoded-in-storage)
- [Q04.2](#q04.2-when-should-equal-strings-share-storage)
- [Q04.3](#q04.3-where-should-identical-strings-share-storage)
- [Q04.4](#q04.4-how-should-unused-strings-be-reclaimed)
- [Q04.5](#input-validity-and-preservation)

## Q04: How should strings be compared?

**Accepted on 12 November 2026:** use exact, case-sensitive Ordinal equality
by default, with OrdinalIgnoreCase available as an explicit per-field comparison
policy. Preserve the original text in both modes. Do not implicitly trim whitespace,
change case, remove accents or normalize Unicode sequences on storage.

This question selects string equality behavior. It does not choose UTF-8 versus
UTF-16 storage, dictionary scope, physical string IDs or the full search/sort API.
[Q07.3.6 now also accepts](queries.md#string-ordering-and-query-overrides) using the
field policy for ordering and ranges, without a per-query override in v1. Both
storage encodings produce the same logical order; equivalent case-insensitive
values remain tied without a hidden spelling tie-breaker.
Encoding is selected in Q04.1; dictionary policy and scope are accepted in Q04.2
and Q04.3; Q04.4 accepts stored-reference counts and deferred reclamation.
The .NET ordinal comparison
modes are the intended semantic reference, not a requirement to allocate a CLR string
for every stored-value comparison.

Examples:

| Values | Ordinal | OrdinalIgnoreCase |
| --- | --- | --- |
| Main and main | Different | Equal |
| main and main followed by a space | Different | Different |
| Empty string and null | Different, under accepted nullability | Different, under accepted nullability |

### A. Exact Ordinal equality only

- **Benefit:** the smallest initial comparison contract. Different case or text
  sequences remain distinct. Application code can rely on exact identifier matching.
- **Reads/search:** no case mapping or linguistic rules are needed for equality.
  This is a simple candidate for direct comparison of the chosen encoding.
- **Writes:** no case-insensitive comparison/index path is maintained.
- **Memory:** no additional case-insensitive search key is needed. Normal text,
  dictionary and index costs still apply.
- **Implementation:** fewer comparers and test cases; applications needing
  case-insensitive lookup must wait for an explicit extension or model a separate
  search value themselves.
- **Limit:** too restrictive for some reusable configuration/reference-name fields.

### B. Ordinal by default; explicit OrdinalIgnoreCase fields — accepted

- **Benefit:** exact matching remains the default, while a schema can state that
  a particular identifier/name ignores case. Callers receive the stored original
  spelling rather than an automatically lowercased replacement.
- **Reads/search:** retrieving the original value need not transform it. Equality
  filtering and lookup for ignore-case fields need the corresponding comparison
  and hash behavior. Case handling may add CPU work; no slowdown ratio is known.
- **Writes:** hashing/index maintenance for those fields uses the selected policy.
  Indexed query predicates and index keys must agree on equality. If a unique
  index is declared, Main and main conflict under the ignore-case policy.
- **Memory:** a comparer does not inherently require a second full stored string.
  A materialized folded search key could accelerate some operations but adds
  memory and write work. That optimization is not selected here.
- **Implementation:** two equality/hash paths, corresponding generated query
  binding and tests. The schema selects the path; there is no requirement for
  culture discovery or reflection on each record access.
- **Limit:** ignoring case is not language-aware matching, accent removal,
  whitespace trimming or automatic Unicode normalization.

### C. Language-aware comparison policies

- **Benefit:** useful when matching or ordering text must follow a specified
  language's rules, rather than identifier-style equality.
- **Reads/search:** collation rules can require more work than ordinal matching.
  User-facing sorting and database identity need separate, explicit requirements.
- **Writes:** maintaining collation-dependent indexes can require generating or
  updating search/sort keys.
- **Memory:** precomputed collation keys consume space; comparing on demand trades
  that space for runtime work. Exact cost depends on the selected implementation.
- **Implementation:** define culture, options and comparison-version compatibility
  across nodes and persisted indexes. The current process/OS culture must never
  silently choose database identity semantics.
- **Limit:** larger initial contract with no established requirement yet. This
  option is deferred by the decision, not prohibited for future extensions.

Microsoft recommends explicit Ordinal/OrdinalIgnoreCase for non-linguistic
matching; these modes are not driven by the current thread culture. See
[the .NET string comparison guidance](https://learn.microsoft.com/en-us/dotnet/standard/base-types/best-practices-strings).

### Equality is not lossless deduplication

Under the accepted preservation contract, a dictionary may share identical stored text.
An ignore-case field may consider Main and main equal for lookup, but their stored
representations remain distinct when original spelling must be returned. Merging
both into one spelling would change the value observed by the caller.

Consequently, distinct StringIds can compare equal under an ignore-case policy.
Comparing IDs alone is not a complete ignore-case predicate. Dictionary ownership,
query indexes and comparison metadata have separate responsibilities. Text storage
mode does not determine field comparison semantics.

### Compatibility and remaining details

The writer, replica applier, query evaluator and indexes must use compatible
comparison rules. Effective case-mapping behavior/version belongs to the eventual
compatibility contract; a culture-independent mode is not permission to ignore
engine/runtime-version differences. Changing a field's comparison policy may
merge formerly distinct unique keys, so validate conflicts and rebuild affected
indexes through the accepted controlled migration path.

Visually identical text can have different Unicode character sequences. No
implicit normalization is proposed. [.NET normalization](https://learn.microsoft.com/en-us/dotnet/api/system.string.normalize)
is a separate transformation with explicit forms. Input validation and encoding
rules, including handling invalid sequences, remain part of Q04.1.

The eventual ordering contract must be explicit too: raw UTF-8 byte ordering is
not generally identical to .NET UTF-16 Ordinal ordering. Choosing a storage encoding
must not accidentally choose incompatible range-index or sort semantics. Detailed
ordering, prefix and substring operations remain in Q07.3.

**Decision:** B is accepted. Ordinal equality is the default; OrdinalIgnoreCase is
an explicit per-field setting. Preserve original text and keep lossless dictionary
deduplication separate from case-insensitive search. The concrete JSON property
spelling, encoding, dictionary policy and full ordering API remain separate details.

<a id="q04.1-how-should-strings-be-encoded-in-storage"></a>

## Q04.1: How should strings be encoded in storage?

**Accepted on 12 November 2026:** support UTF-8 and UTF-16, selecting
one stored-text encoding for the entire database when it is created. UTF-8 is the
default. UTF-16 is an explicit creation setting. Do not select encoding dynamically
or separately for individual fields.

This setting is independent of plain versus dictionary storage. Both modes can
support direct access without creating a CLR string for every value. Materializing
an owned string or filling a caller buffer remains explicit. Supporting two codecs
does not require retaining two encodings of every value; an automatic decoded-copy
cache is not selected by this decision.

Persist the encoding in database metadata so opening an existing database and
installing a replica use the stored choice. A child must not reinterpret its image
using a conflicting local setting. Changing an existing database's encoding requires
an explicit conversion under Q03, not changing a config value and reopening the
same bytes. UTF-16 byte order and the exact creation-config grammar belong to the
binary-format/API design and remain unspecified.

This is not a claim that one encoding is universally smaller or faster. The
creation setting lets .NET-heavy text workloads select UTF-16 without requiring
mixed encodings within one database. It does not decide managed versus native
allocation, deduplication, read lifetimes or persistence acknowledgment.

### Representation costs

The following sizes count text payload only: no handles, lengths, dictionary
entries, allocation headers, indexes or alignment.

| Text | UTF-8 | UTF-16 |
| --- | --- | --- |
| ASCII letters, digits and punctuation | 1 byte per character | 2 bytes per character |
| Common Cyrillic letters | 2 bytes per character | 2 bytes per character |
| Common CJK characters in the Basic Multilingual Plane | 3 bytes per character | 2 bytes per character |
| One supplementary Unicode scalar, such as U+1F600 | 4 bytes | 4 bytes |

These are Unicode encoding properties, not compression ratios for an entire
database. See the [Unicode encoding FAQ](https://www.unicode.org/faq/utf_bom.html).
A .NET string uses UTF-16 code units; see
[.NET character encoding](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding).
An ordinary string cannot wrap an arbitrary arena slice: creating a new owned CLR
string from either packed representation allocates and fills a string. A borrowed
view or reused destination is a different access contract, still subject to Q06.

### A. UTF-8 for stored text — default in the accepted database setting

- **Memory:** compact for ASCII-heavy keys and values. Cyrillic text alone has
  little payload advantage; common CJK text can take more space than UTF-16.
  Deduplication and per-value metadata can matter more than the encoding choice.
- **Reads/search:** exact equality can compare validated UTF-8 bytes directly.
  Encoding a query parameter once can amortize its conversion across candidates.
  Returning CLR strings requires decoding; a reused char buffer removes per-read
  allocation but still requires decoding. Ignore-case matching needs a correct
  comparer, not byte equality or an ASCII-only lowercase shortcut.
- **Writes:** incoming CLR strings need validation and encoding. Existing UTF-8
  input may avoid transcoding but still needs validation. Write costs have not
  been measured by the current prototype.
- **Disk/network:** when the same text representation is persisted or transferred,
  payload savings or increases carry through. Total traffic also includes metadata
  and the eventual change protocol; no traffic reduction ratio is promised.
- **Implementation:** one internal encoding and explicit conversion boundaries.
  Queries should avoid materializing text they do not return; no second decoded
  copy is retained automatically.

### B. UTF-16 for stored text — explicit database creation option

- **Memory:** twice the ASCII payload, often similar for common Cyrillic, smaller
  for common CJK. Packed UTF-16 still avoids one CLR string object per stored value.
- **Reads/search:** char-based processing is more direct in .NET. Copying to a char
  buffer avoids decoding; creating a CLR string still allocates and copies.
  Larger ASCII payloads can increase memory traffic. Exact and ignore-case search
  remain separate operations; no general query-speed winner is established.
- **Writes:** CLR input can be copied without transcoding, with validation if the
  engine requires well-formed Unicode. UTF-8 input needs decoding instead.
- **Disk/network:** stored/transferred UTF-16 has the same language-dependent size
  tradeoff. A separate UTF-8 wire format would add another conversion boundary.
- **Implementation:** convenient for .NET text APIs, but persist/wire byte order
  must be defined. This does not eliminate ownership and reclamation work.

### C. Explicit encoding per field — not selected

- **Benefit:** choose UTF-8 for ASCII-heavy fields and UTF-16 for text that benefits
  from direct char access or smaller UTF-16 payloads.
- **Reads/writes:** avoid some unfavorable conversions, but cross-field comparisons,
  dictionary sharing and query binding must understand encoding compatibility.
  Schema-bound generated paths could avoid a branch per value; that is unmeasured.
- **Memory:** can select the smaller representation without retaining both copies,
  but extra metadata and separate dictionary domains can offset the savings.
- **Implementation:** two codecs throughout storage, accessors, checkpoints,
  replication and tests. Changing encoding may require the accepted migration path.
  Recommend deferring this until a representative workload justifies the complexity.

### Consequences of selecting once per database

All fields and dictionaries in one database agree on their text encoding. The
engine can bind encoding-specific readers, writers and comparers when opening the
database; this avoids a requirement for per-record encoding discovery. Whether
generated paths remove all dispatch cost must be measured, not assumed.

Both codecs still need correct equality/hashing, persistence and replication paths
and corresponding tests. This is more implementation work than UTF-8 alone, but
less mixed-format complexity than per-field selection. Q04's Ordinal and
OrdinalIgnoreCase behavior and original-text preservation apply to either mode.

### Bounded local evidence

[StringEncodingComparison](../../src/Prototypes/StringEncodingComparison/README.md) compares
packed byte/char arrays containing 4,096 distinct short values, with identical
offset/length descriptors and randomized reads. It checks every value against
the source and tests empty strings, embedded NUL, combining characters and
supplementary Unicode scalars. Two processes per encoding/corpus were run in
opposite encoding order, with five measured samples per process.

| Synthetic corpus | UTF-8 payload | UTF-16 payload | CLR string, UTF-8 ns/read | CLR string, UTF-16 ns/read |
| --- | --- | --- | --- | --- |
| ASCII prefix and digits | 140 KiB | 280 KiB | 26.1–28.0 | 19.1–23.5 |
| Cyrillic prefix, separators and digits | 224 KiB | 256 KiB | 52.1–54.4 | 19.6–22.7 |
| CJK prefix, separator and digits | 192 KiB | 160 KiB | 62.9–63.5 | 25.5–27.6 |

Times show the range of the two process medians, not a confidence interval or
database latency. Environment: Windows x64, .NET 10.0.15, Intel Core i9-13900H,
tiered compilation disabled. Descriptors add 32 KiB to either layout. Source
objects are retained during the experiment, so payload counts are not process
memory measurements. Individual timing samples vary, especially when allocating.

Materializing a CLR string allocated the same bytes in both layouts: 96, 88 and
64 bytes per read for these respective corpora. Reused-buffer reads and prepared
exact comparisons measured zero per-read managed allocations in both. Reused
buffer reads of the Cyrillic corpus took 20.8–20.9 ns for UTF-8 decoding versus
4.1–4.2 ns for UTF-16 copying. This is evidence of conversion cost, not an argument
that every database read must convert a string.

The experiment uses a small hot working set and mostly unsuccessful exact equality
checks. It does not measure indexes, OrdinalIgnoreCase, writes, dictionaries,
concurrent readers, native allocation or total database memory. Its results support
explicit materialization and workload-dependent choices, not a universal speed claim.

### Input validity and preservation

**Q04.5 — accepted for v1 on 13 November 2026: option A.** Both UTF-8 and UTF-16
storage modes accept only well-formed Unicode. Reject malformed input explicitly
before Durable commit or MemoryOnly publication, without character substitution.
The creation-time encoding setting remains unchanged.

This applies wherever the schema stores String values, including primary keys,
owned fields and collection values/keys. Plain and Dictionary storage use the same
input domain. A CLR string containing an unpaired surrogate fails validation;
accepted strings keep their original character sequence, including case, whitespace
and normalization form. Empty strings remain valid; null follows the separate
accepted nullability rules. Validation does not normalize text or merge different
valid spellings.

Validate supplied text during mutation preparation. A rejected mutation leaves
stored data and revision unchanged; an invalid member prevents an explicit atomic
WriteBatch from committing. The same valid text can round-trip through either
encoding without replacement. This does not add live encoding switching or require
revalidating every stored string on ordinary reads. Chunked codecs must preserve
validation state across chunk boundaries rather than reject a valid split sequence.

For example, taking half of an emoji's UTF-16 surrogate pair produces a CLR string
that is not well-formed text. The distinction and strict encoding support are
described in [Microsoft's character-encoding documentation](https://learn.microsoft.com/en-us/dotnet/standard/base-types/character-encoding-introduction#well-formed-encoding).

| Choice | Behavior | Expected costs, not benchmark results |
| --- | --- | --- |
| A — accepted | Reject malformed text before commit in either storage mode; preserve valid input without replacement, case changes or normalization | Validate incoming text during preparation, combining this with encoding where possible. Work scales with the supplied text; no permanent duplicate or repeated validation on ordinary reads is required. Standard strict codecs keep the implementation small. |
| B — not selected | Preserve every possible CLR UTF-16 code-unit sequence, including unpaired surrogates | Raw UTF-16 can retain it, but standard UTF-8 cannot represent it losslessly. Supporting the same domain in UTF-8 would require a custom representation/escaping and corresponding read/write paths, with additional format complexity and potentially more bytes or conversion work. |

Silent character replacement is prohibited: it changes the caller's data and can
change string keys. The existing string-encoding prototype uses a strict UTF-8
encoder; it does not establish production mutation validation. Implementation checks
must cover supplementary characters, isolated high/low surrogates, split chunks,
keys/collections and unchanged data after rejection in both modes. No new benchmark
or runtime implementation is supplied by this acceptance.

**Decision:** one creation-time encoding per database is accepted, with UTF-8 as
the default and UTF-16 explicitly selectable. Persist and replicate that choice.
There is no per-field or adaptive encoding policy. Q04.5 settles input validity;
exact codec integration, UTF-16 byte order and creation-config grammar remain
engineering details. Dictionary policy is owned by Q04.2-Q04.4.

<a id="q04.2-when-should-equal-strings-share-storage"></a>

## Q04.2: When should equal strings share storage?

**Accepted on 12 November 2026:** choose string storage explicitly per
field, with Plain as the default and Dictionary enabled explicitly for selected
fields. V1 does not switch these modes automatically. This is independent of
Q04.1: the entire database still uses the single encoding chosen at creation,
whether UTF-8 or UTF-16.

Plain means no content-based sharing between independent stored occurrences is
required. Dictionary means exact text is stored once within a dictionary domain,
with occurrences referencing its entry. Q04.3 accepts a database as that domain;
Q04.4 accepts stored-reference counts and deferred reclamation, with exact ID
lifetime rules still open. No global immortal intern pool is selected.
This text-sharing mechanism is distinct from Q03.3's Map collection abstraction.

The later [accepted index-key ownership](index-structures.md#search-key-ownership-copied-fixed-values-and-shared-text)
lets a record and its indexes share the same stored immutable text through protected
stable references. Plain still does not deduplicate independent stored occurrences;
Dictionary retains its explicit content-sharing policy. Each retained index or
prepared mutation participates in ownership, and relocation/reclamation must protect
active dereferences. The exact descriptor and reader-protection protocol remain work.

For example, a repeated category or region name can benefit from dictionary
storage. Mostly unique descriptions, generated tokens or long unique identifiers
may pay dictionary overhead without enough shared text to compensate. The relevant
factor is repetition within the eventual dictionary domain, not the field name.

### A. Explicit per-field choice, Plain by default — accepted

- **Memory:** fields without useful repetition avoid the extra deduplication
  dictionary. Repeated text fields can share their encoded payload. Both modes
  still require location/length or reference metadata, and exact savings depend
  on the physical representation and cardinality.
- **Reads:** Plain can read its referenced text directly; Dictionary adds entry
  resolution, potentially benefiting memory locality when many values reuse the
  same entry. Exact ID equality may help within one compatible dictionary, but
  is not a universal comparison or an OrdinalIgnoreCase implementation.
- **Writes:** Plain does not require a content-sharing lookup. Dictionary needs
  lookup and lifetime bookkeeping; a hit can avoid allocating/copying another
  text payload. Neither mode is assumed universally faster under update churn.
- **Configuration/implementation:** the schema author selects the policy from
  the known workload and measurements. Two storage paths must be supported, but
  no automatic profiling or representation switching is required.
- **Limit:** opted-in fields share text within the database selected as the
  dictionary domain in Q04.3. Equal text in different databases is not shared.

### B. Dictionary storage for every string field

- **Benefit:** repeated values can share storage without a per-field opt-in.
  There is one string-storage policy for the application to understand.
- **Memory:** mostly unique values retain their text plus dictionary lookup and
  lifetime metadata. Very short values can cost more to index than to duplicate.
- **Reads:** entry resolution applies to all values. Returning an owned CLR string
  still requires a separate materialization/ownership policy; a byte dictionary
  does not automatically provide preallocated CLR strings.
- **Writes:** every field pays the content-sharing lookup and entry management,
  including fields whose values rarely repeat. Hits may still save copying.
- **Implementation:** fewer selectable storage policies, but the dictionary and
  reclamation machinery remain necessary and their costs affect all text.

### C. Automatic policy selection and switching

- **Benefit:** the engine can adapt to repetition without manual schema choices.
- **Memory:** observations/statistics and a transition between layouts consume
  additional space. During conversion, old and new representations may coexist.
- **Reads/writes:** profiling and conversion add work; avoiding pauses while
  preserving readers, indexes and replication introduces more coordination.
  Sampling may choose poorly for changing workloads.
- **Implementation:** requires thresholds, stable switching rules and measured
  behavior under changing cardinality and churn. This is a larger first-version
  contract than explicit selection and is not recommended for v1.

### Preservation and validation boundaries

Dictionary entries must preserve the original text. Main and main remain distinct
stored spellings even on an OrdinalIgnoreCase field. Sharing text never removes
duplicate array positions or changes Q02.2's sequence order.

No performance ratio is claimed. The existing encoding prototype does not measure
dictionary metadata, churn or reclamation. A focused comparison should vary value
length, distinct-value ratio, cross-field repetition and updates, including full
metadata and lifetime costs. An explicit initial policy does not prevent those
measurements from informing which fields should enable sharing.

**Decision:** Plain by default, with explicit Dictionary storage per
field; no automatic mode switching in v1. Q04.3 accepts one dictionary per
database; Q04.4 accepts counts and deferred safe reclamation. The illustrative
JSON's StringStorage property remains an example rather than a finalized schema grammar.

<a id="q04.3-where-should-identical-strings-share-storage"></a>

## Q04.3: Where should identical strings share storage?

**Updated on 13 November 2026:** one logical string dictionary belongs to the database,
shared by every field that explicitly selects Dictionary storage. [Q12](database-scope.md)
removes the former partition layer. Plain remains the default. This selects the
sharing boundary, not a hash-table layout, refcount algorithm or globally stable ID.
Each node has its own local representation; reading text never requires a network lookup.

### A. One dictionary per database — accepted

For example, opted-in Region and DeliveryZone fields containing North can reference
one immutable encoded entry, even across entity types. A Plain field retains its
own text. Collection key/value/element declarations select their policy explicitly.

- **Memory:** shares repeated text across participating fields and records. Entry
  references, lookup/lifetime metadata and spare capacity still cost memory. Mostly
  unique text can cost more than Plain; participation remains explicit.
- **Reads:** resolve the entry in the local database. Shared hot entries may improve
  locality, but dictionary size and access patterns need measurement.
- **Writes:** exact-text lookup and reference accounting accompany changes. Reuse can
  avoid payload copies; it does not remove synchronization or reader-lifetime work.
- **Replication/lifecycle:** the full database and required string state load together.
  Deleting a field must preserve entries used by other records, indexes or readers.
  Independent database instances do not share a process-global immortal intern pool.

### B. Separate dictionary per field — not selected

Narrower dictionaries can have smaller working sets and independent rebuilds, but
repeat the same text across fields and add lookup structures. Whether a smaller
structure is faster is workload-dependent. V1 retains one logical database dictionary;
internal allocation/layout is still engineering work, not a per-field mode switch.

### Text identity and lifetime boundaries

Sharing uses the exact stored text under Q04/Q04.1. Equal spellings may share
across fields even when their query comparison policies differ; the comparison
policy belongs to the field, not to the shared text entry. North and north remain
different dictionary entries even when an OrdinalIgnoreCase lookup treats them
as equal. A StringId is not a lexical sort key or an ignore-case equality key.

Dictionary entries are immutable. Updating a field changes its reference; it
must not edit a shared text entry in place and thereby change other records.
An entry does not become a new historical version on every update. Existing text
can be reused, and unused entries must be reclaimable under safe lifetime rules.
The following requirements explain the scope choice. Q04.4 now accepts counts
and deferred safe reclamation; its detailed implementation remains open:

1. An old entry remains live while another record or retained storage consumer
   still needs it. Removing one field occurrence does not imply immediate deletion.
2. After the last stored reference is removed, readers, index references and
   checkpoint/transfer users must no longer depend on its bytes or identifier
   before reuse is safe. An owned copy is independent only when it owns or safely
   retains its text too. Merely reaching a zero record-reference count is not
   enough to prove safe reclamation.
3. Temporary retired entries need a retention budget and a defined response to
   stalled consumers. Counts and deferred reclamation are accepted; read
   scopes/epochs, compaction timing and ID reuse/generation rules remain open.
4. Reclaiming an arena entry permits space reuse; it does not promise immediate
   reduction of process memory, checkpoint files or WAL. Disk history and truncation
   remain Q09 work. No permanent per-update string history is required.

The accepted scope is for a database-owned encoded-text store. Using CLR String.Intern
would add a different lifetime: Microsoft notes that interned strings are unlikely
to be released until CLR termination and must first be materialized as strings.
That is unsuitable as the default reclamation mechanism for changing database
data; see [String.Intern performance considerations](https://learn.microsoft.com/en-us/dotnet/api/system.string.intern?view=net-10.0#performance-considerations).

A local StringId is meaningful only with its dictionary identity and valid
lifetime. Equal integer IDs in different databases need not denote equal text.
Persistence/replication must carry enough dictionary definitions or remapping
information before a record using an ID becomes readable; process addresses and
unqualified local IDs cannot be treated as portable text. Whether replicas retain
the master's IDs or translate them remains an implementation/protocol decision.

No prototype establishes the final dictionary's performance. Measure repetition
within and across fields, metadata cost, update churn, peak retained memory and full
database startup. Q04.4 retains stored-reference counts and deferred safe reclamation;
exact ID-reuse mechanisms and retention budgets still need definition.

**Decision:** one database-owned logical dictionary shared by all explicitly opted-in
fields. No automatic switch from Plain and no process-global text sharing are added.

<a id="q04.4-how-should-unused-strings-be-reclaimed"></a>

## Q04.4: How should unused strings be reclaimed?

**Accepted on 12 November 2026:** track stored ownership with reference
counts and defer reclamation of unused entries until readers and other legitimate
storage users can no longer access them. This accepts the lifetime strategy;
it does not select a lock-free algorithm, reader epochs, exact ID format or
pressure thresholds. Incremental cleanup in bounded batches remains an
implementation direction to measure. No collector is implemented yet.

There are two different questions: whether current stored values still use a
string, and whether a reader or retained storage generation can still access its
old bytes. A zero count for current records answers only the first. The scope
choice in Q04.3 does not make immediate deletion at zero safe.

### A. Stored-reference counts plus deferred reclamation — accepted direction

1. A dictionary entry records how many stored occurrences own it, including
   occurrences in owned collections. Independently retained index or generation
   references must also participate in an explicit ownership/lifetime protocol.
   The dictionary's own lookup entry is not an immortal owner of its payload.
2. An update acquires the new string reference and releases the old one as part
   of the record's validated publication. If the field keeps the same entry,
   there is no net count change. Updates to unrelated numeric fields do not
   create another string version. Failed mutations must not leave leaked counts
   or published references to unowned entries.
3. When no current stored owner remains, the entry becomes a reclamation
   candidate. Existing readers or retained storage users can still keep it alive.
   A live-lookup removal/reacquisition protocol must prevent a newly acquired
   reference from racing with reclamation. A queued zero-count entry cannot be
   freed blindly if it has become live again.
4. After safe reader completion and release of other owners, the allocator may
   reuse its bytes and, under a separately defined rule, its identifier. Small
   batches amortize cleanup work without requiring a full database scan for
   every change. Neither a fixed cleanup interval nor an immediate return of
   memory to the OS is promised.

For example, if 100 records refer to North, changing one to South leaves 99
stored references to North. Changing the last occurrence makes North eligible
for retirement. A reader already viewing North must finish its protected access,
optionally after making an owned copy, before that storage can be reused.
A sequence of arbitrary replacements does not need permanent history, but several
retired values can coexist while readers are active.

The proposed fast read path registers a short internal read scope rather than
updating every string's ownership count on every property read. The engine ends
this scope before returning an owned result. Scopes still require synchronization;
their physical form, dictionary lifetime-tracking domain and memory ordering
remain open alongside Q06.1's accepted root-document gate.
An epoch/grace-period scheme is one candidate, not an accepted implementation.
The goal is to wait for readers that could hold a retired entry, not necessarily
for every reader in the process to become idle simultaneously. Coarse tracking
can conservatively retain more entries, so a long reader may delay unrelated
retirements within the tracked domain.

Separating removal from safe reclamation is also the principle described in
[the Linux RCU overview](https://docs.kernel.org/RCU/whatisRCU.html#rcu-overview).
That reference explains the lifetime distinction; it does not establish correct
.NET publication or require Linux RCU as a Moltaro.Lattice dependency. Lifetime protection
does not itself make a multi-field record read coherent; Q01 still requires a
separate correct publication/read protocol.

- **Memory:** one ownership counter per dictionary entry, reader/lifetime
  metadata and a temporary retired-entry list. Counts need not be stored on every
  occurrence in addition to its existing StringId. Actual counter width and total
  overhead remain unmeasured. Delay and churn determine temporary retention.
- **Reads:** avoids a mandatory increment/decrement per string getter in this
  candidate. Entering/exiting a read scope is not free; its cost must be measured.
  Owned copies can leave the scope once all copied data is independent.
- **Writes:** adjust counts for changed ownership and enqueue reclamation work.
  The single local writer can own those adjustments; this does not imply an
  atomic read-modify-write on every count or allow a background reclaimer to
  mutate counters unsafely. Cleanup and allocation can still affect latency.
- **Complexity:** requires correct publication, failure cleanup, safe retirement
  and balanced ownership across record/index/checkpoint/replication paths.
  Recovery must reconstruct or validate counts consistently with restored state;
  persisting transient reader state is not required.

### B. Periodically trace live references and sweep unused entries

Instead of maintaining a count on each stored ownership change, scan the
database's live roots and retained users to discover referenced dictionary
entries, then reclaim unreferenced ones under a safe reader protocol.

- **Memory:** uses mark/scan metadata and retains garbage between collections.
  An incremental or concurrent pass may need additional mutation tracking and
  conservatively retain entries for another pass.
- **Reads:** no per-string ownership counter update. A stop-the-world scan delays
  readers; avoiding that pause requires a correct concurrent snapshot/barrier
  protocol. This is not automatically a synchronization-free alternative.
- **Writes:** avoids ordinary per-update count maintenance, but scans revisit
  live reference slots even if only a small fraction changed. Pausing mutations
  simplifies a scan at the cost of writer/apply latency; concurrent scans require
  coordination so an insertion cannot be missed and collected incorrectly.
- **Complexity/use:** can suit infrequent bulk rebuilding. It replaces exact
  incremental accounting with traversal/barrier work and potentially large pauses.
  It is less attractive as the primary cleanup strategy for frequent updates.

Both approaches need safe handling of active readers. Ordinary .NET GC does not
identify unused string slices inside a still-live arena buffer. This responsibility
would also exist with managed segments; Q05 selects native bulk storage.
Keeping every entry until database unload is not proposed: a long-lived
database with continually new text would grow with historical updates.

### Retention limits and identifier safety

Memory roughly comprises live entries, entries retained for active users,
dictionary/reader metadata, allocator slack and in-flight work. For intuition,
20 MiB/s of retired payload held for 2 seconds gives about 40 MiB before overhead;
this is arithmetic under an assumed steady rate and delay, not a measured bound.
A stalled reader, checkpoint or transfer can invalidate that delay assumption.

Reclamation must have a finite budget; it cannot promise unlimited writes,
arbitrarily long borrowed access and bounded memory simultaneously. On pressure,
the engine must stop admitting work that would exceed its budget and coordinate
release of existing users. Waiting for capacity or returning a resource-limit
failure must happen without a half-committed record and without unbounded queues.
Exact limits, wait/fail semantics and master/child flow control remain open.
Timeouts can request cooperative cancellation; they do not prove that a raw view
or span can no longer be used and cannot authorize freeing memory underneath it.
No lock may remain held during network or disk I/O.

StringId reuse must not let a stale reference resolve to unrelated text. Safe
reuse needs proven lifetime completion and an explicit identity rule; a generation
tag is one candidate, not a substitute for lifetime safety. Exact ID layout,
wraparound handling, lookup-entry revival and compaction protocols remain open.
Compaction must preserve active borrowed access or wait for a safe boundary.

Disk WAL/checkpoint retention remains Q09; reclaiming an entry in RAM alone does
not shrink a durable log. A child can use the same local ownership mechanism,
but incoming changes and checkpoint/transfer consumers still require bounded
retention and the ordering rules of their owning subsystems.

A focused prototype should check last-reference removal during a read, reuse
after retirement, same-text reacquisition, failed writes, repeated replacements
and a deliberately delayed reader. Compare allocation, retained/peak bytes,
read/write latency and cleanup work under equal workloads. Existing encoding
measurements do not prove this protocol's safety or performance.

**Decision:** stored-reference counts with deferred safe reclamation are accepted
for v1. Exact read-scope mechanics, cleanup batching, ID format and pressure
thresholds/actions remain explicit follow-up decisions, not implicit acceptance
of an epoch or lock-free design.
