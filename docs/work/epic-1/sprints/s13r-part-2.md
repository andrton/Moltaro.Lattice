# S13R, part 2 · Refactoring that owes a measured figure

**Carried out and reviewed on 19 November 2026.** The entries of the
[refactoring backlog](../../refactoring.md) that owed a figure, each commit with its figure,
measured on a rented machine (see the measured results). Each step was reviewed by one
independent reviewer with one question, "is the behavior what it was"; no review found a
P1.

1. **D1, with A2 in its wider cut** (670617c): one struct for a prepared change,
   `PreparedChange` with `ChangeKindEnum`, and the sealed `ChangePublisher`, which
   reserves, releases and publishes the changes of one publication over a span. The
   first cut cost 2 to 3 % on a write; 70fcc30, babd343, 0d63331 and f4ae9f3 removed
   that cost: the change is written straight into the record of the write, not built
   aside and copied. The review of the third step of D1 named three findings of P3 and
   P4, fixed in f4ae9f3.
2. **D4** (42b6317): the retired memory notes whether anything was retired since the
   previous publication, and `AfterWrite` takes no argument.
3. **D2** (55b48a7): one shape for a write refused for memory on a child, and both
   callers ask the retired memory. A P2 of its review: the switch of the tests, moved to
   `ReadEpochs` as the entry proposed, kept a reference there that hollowed the
   weak-reference probe of "a closed database retains nothing"; it lives in
   `RetiredMemory` (4b784ef).
4. **D3** (5a88dd4): the check of a received layout is removed. The reviewer showed its
   condition by reading: the layout that reaches the check is always taken from the
   node's own schema, never selected by received bytes.
5. **A6** (37071e5, 0151c9f): the primary keys of a type leave `EntityTable`. A P2 of its
   review: a sealed class for the keys added dependent loads to a read by key; they are a
   readonly struct `PrimaryKeys` of 24 bytes held by value in the table, and the
   comparison stays in `EntityTable.KeyEquals`.
6. **T1 with B3** (808b619): `TextSlot` is 24 bytes and keeps no state; the arena says
   whether the block of an entry is prepared (`Arena.IsPrepared`).
   `ValueBlocks.TryStoreText` takes the slot as a number and writes the header of the
   block; `ValueBlocks.TextSlotOf` beside it reads it.

**Checked by the coordinator on f4ae9f3:** the build with 0 warnings, the format clean,
2,492 tests with 2,336 passed, 156 skipped and 0 failed, the samples in Debug and in
Release, 20 repeats of the suite in Release, all clean. One failure is not explained:
in the tenth of ten repeats in Release on 0d63331 one test failed, and the run did not
keep its name. It did not appear again in 40 more repeats with their output kept, 20 on
0d63331 and 20 on f4ae9f3. At the time the workstation also ran a build and the full
suite of two agents. From now on the output of every repeat is kept.

**Not done:** A9 and B2, taken into S14R; B2 before the scenarios of S15 are written.
