# Epic 3. After the first milestone

**Status:** outline. Opened on 18 November 2026 by the owner's decision, to hold what
belongs to neither [Epic 1](epic-1-core.md) nor [Epic 2](epic-2-identity-storage.md).
Nothing here is designed or scheduled; each requirement gets its decision record before
it gets a sprint.

## Goal

What an application needs from the database beyond the first production milestone: to
learn of changes without polling, to find what refers to an entity, and to ask questions
the explicit index search does not answer.

## Scope

**Included, as requirements below.** A subscription to changes; the lookup of the
entities that refer to a given entity; indexes on the members of a collection, as far as
that lookup needs them.

**Candidates, taken from "Outside this milestone" of Epic 1; each needs the owner's
decision before it becomes a requirement here.** A query language with its parser and
planner; joins; automatic choice of an index; snapshots across calls; lazy conversion of
mixed schemas; generic continuation by the last key; maintenance commands.

**Not here.** A partition layer and the election or promotion of a master, which are not
part of the design; external transactions (Q09.3 stands).

## Requirements

| # | Level | What is needed | What needs it | Today |
| --- | --- | --- | --- | --- |
| R1 | Must | An application subscribes, on a child as on a master, to the changes of the database and is told of each committed change after the node has applied it — which entity type, which key, whether it was written or deleted, at which position — without polling. | A service that reads from a child and must react to changes made elsewhere | Not in the contract. The node already applies changes in order, each transaction at one position (Epic 1, S11) |
| R2 | Must | Given an entity, an application gets the entities that refer to it, quickly and without reading everything: through every reference field of the schema whose target is that entity's type. | Finding what depends on a changed entity, for example to notify it | Partly planned: a reference stores the key of its target (S18) and a declared index on a reference field finds the entities holding a given key (S14, S18). Not planned: a reference inside a collection, and one call across all referring types and fields |
| R3 | Should, for R2 | An index over the members of a collection: an entity is found by any element of a List or Set field. | R2 when references are kept in a collection | Outside the first milestone |

## Open questions

1. R1: does a notice carry only the identity of the change or the entity as well; what a
   subscriber that is slower than the changes gets (the database does not hold a lock or
   unbounded memory for it — principle 9); what it gets when a child has reloaded and
   cannot say what changed (a notice "everything may have changed" is the honest answer);
   whether a subscription can start from a position; whether it filters by entity type.
2. R2: one call for all referring types and fields, or the application names each index;
   whether the declaration of a reference field asks for the lookup or every reference
   field has it.
3. Which candidates of the scope become requirements, and in which order.

## Sprints

None yet. The epic is planned after Epic 2, by the development process.
