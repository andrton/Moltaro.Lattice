# Q03.4.2: Collection input rules

**Accepted for v1 on 13 November 2026:** option A in full. Q03.4.2 is closed.

The [decision record](../../decisions/schema-and-types.md#collection-input-contract)
owns the complete rules, rationale, costs and rejected last-entry-wins alternative:

- Collection, List-element, Map-value and Set-element nullability is explicit per
  position and false by default. A nullable Set can contain one null member.
- Map keys are always non-null; nullable key declarations are invalid.
- Lists preserve order and duplicates. Bulk Set input coalesces equal members,
  preserving the first representation under schema equality.
- Duplicate equivalent Map keys in whole-value input reject the complete mutation,
  even if their associated values are equal. Stored data/revision remain unchanged.
- Explicit Map set-by-key retains existing key spelling while replacing the value.
  Insertion uses supplied spelling. Full document/collection replacement uses its
  validated input and does not merge the former collection's key spelling.

Existing type eligibility, root atomicity, input lifetime and memory budgets apply.
[Shared floating-point rules](../../decisions/queries.md#floating-point-special-values)
are accepted: equivalent NaNs and signed zeros follow the same Map/Set duplicate
policies as other equal values. Q12 now accepts
[same-database reference context](../../decisions/database-scope.md). Physical collection algorithms are engineering work. No implementation or
benchmark was performed. The next proposal concerns
[external transaction integration](external-transactions.md).
