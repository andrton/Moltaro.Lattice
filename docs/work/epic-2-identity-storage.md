# Epic 2: Storage for ASP.NET Core Identity

**Status:** the contract of the package exists; the stores are not implemented · 14 November 2026 · follows [Epic 1](epic-1/README.md)

## Goal

A package in this repository, `Moltaro.Lattice.AspNet.Identity`, that lets a standard
ASP.NET Core Identity application keep its users and roles in a Lattice database.

The application keeps using `UserManager`, `SignInManager` and `RoleManager` unchanged. Only
the storage registration differs: `AddLatticeStores()` where an application would otherwise
call `AddEntityFrameworkStores`. The package is general: any application can use it, with
its own user and role classes and its own schema.

This is the first real consumer of the database.

**The package adapts to the database, not the reverse.** The scope of the engine is set by
its [accepted design](../design/planning-handoff.md): declared indexes, unique indexes,
collections, atomic write batches and durable persistence are part of the first milestone
whether or not this package uses them. This document says which of them the package relies
on. It does not narrow or extend the engine.

## Scope

**What is replaced.** ASP.NET Core Identity is split into parts, and only the last one
depends on Entity Framework. Checked against .NET 10.0.15:

| Part | Contains | Depends on Entity Framework |
| --- | --- | --- |
| `Microsoft.Extensions.Identity.Core` | `UserManager`, `RoleManager`, validators, password hashing, the store interfaces | No |
| `Microsoft.Extensions.Identity.Stores` | `IdentityUser`, `IdentityRole` and the related data classes; `UserStoreBase`, `RoleStoreBase` | No |
| `Microsoft.AspNetCore.Identity.EntityFrameworkCore` | The stores that keep everything in a relational database | Yes — this is the part the package replaces |

**Included:**

- A user store and a role store on `UserStoreBase` and `RoleStoreBase`.
- A default declaration of the Identity data, which the application may use as it is or
  change, and the check that the application's schema has what the stores need.
- Registration in dependency injection, including the choice of a named database.
- A sample and a test suite that checks the stores through `UserManager` and `RoleManager`.

**Not included:**

- Sign-in pages, tokens, cookies, OpenID Connect: these are above the stores and stay as they are.
- Passkeys, in the first version.
- Moving an existing application from a relational database to Lattice.
- Anything specific to one application.

## What exists

The contract of the package, in `src/Moltaro.Lattice.AspNet.Identity`:

| Part | State |
| --- | --- |
| `identity.schema.json` | The default declaration: seven entity types with their fields and indexes, five of the indexes unique |
| `LatticeIdentitySchema.CreateDescriptor<TUser, TRole>()` | Works: returns the default declaration bound to the application's classes |
| `LatticeIdentitySchema.Validate<TUser, TRole>(schema)` | Works: refuses a schema that lacks an entity type, a field or an index, and lists everything it found |
| `AddLatticeStores(databaseName)` on the Identity builder | Works: the stores are registered and resolved, for the default or a named database; the schema of that database is checked when a store is created |
| `LatticeUserStore`, `LatticeRoleStore` | Declared on the base classes; every operation throws `NotSupportedException` and names, in a comment, what it will do in the database |
| Tests | 10 run and pass; 15 scenarios through `UserManager` and `RoleManager` are written and skipped |
| Microsoft's specification tests | In the repository, as published for ASP.NET Core 10.0.15: 112 tests, connected to the Lattice stores by `LatticeSpecificationTests` and skipped |
| Sample | Written; stops at the first store operation |

## Design

**The base classes do most of the work.** `UserStoreBase` implements every store interface —
passwords, lockout, stamps, the authenticator key, recovery codes — on top of about thirty
members that touch storage. The package implements only those. They work in terms of the
data classes that come with Identity: a user, a role, a role of a user, a claim, a login, a
token. The application's user class derives from `IdentityUser<string>` and needs nothing
from Lattice.

**Each of those is an entity type.** The user and the role are bound to the application's
classes; the rest are records.

| Entity type | Primary key | Unique index | Other indexes |
| --- | --- | --- | --- |
| The user (default name `IdentityUser`) | User id | `ByNormalizedUserName` | `ByNormalizedEmail` |
| The role (default name `IdentityRole`) | Role id | `ByNormalizedName` | |
| `IdentityUserRole` | Generated id | `ByUserAndRole` (user id, role id) | `ByRole` |
| `IdentityUserClaim` | Generated id | | `ByUser`; `ByClaim` (type, value) |
| `IdentityUserLogin` | Generated id | `ByProviderAndKey` (provider, provider key) | `ByUser` |
| `IdentityUserToken` | Generated id | `ByUserProviderAndName` (user id, provider, name) | |
| `IdentityRoleClaim` | Generated id | | `ByRole` |

An index on several fields also finds by its leading fields, so `ByUserAndRole` lists the
roles of a user and `ByUserProviderAndName` lists the tokens of a user.

**Uniqueness is kept by the database.** User names, role names, external logins, the
membership of a user in a role and the name of a token are unique because the schema
declares a [unique index](../decisions/unique-indexes.md) for each. A store never reads
first to see whether a value is free: it writes, and the master refuses the write that
would give a value a second owner. The store turns that refusal into the Identity error
for the case, such as a duplicate user name. A user without a user name, or a role
without a name, does not take part: a unique index does not compare missing values.

**Every lookup is a key or a named index.** A user by name, a user by external login and
a role by name are one search through a unique index. Nothing is found by enumerating.

**A change of several entities is one write batch.** Deleting a user removes the user and
its roles, claims, logins and tokens, either all or none. Adding or replacing several
claims is one batch. By decision O18 of Epic 1 a batch has no limit of its own, so a user
is deleted in one step however many belongings it has; a batch that the master cannot
hold is refused as a whole, and the store reports that and does not delete in parts.

**Concurrency.** Saving a user is: read the stored user together with its revision; fail
with a concurrency error if its `ConcurrencyStamp` differs from the one in the copy being
saved; set a new stamp; replace the entity, conditional on the revision that was read. A
revision conflict is reported as the same concurrency error.

**Decided on 14 November 2026: separate entity types, not one entity per user with
collections inside.** Each role, claim, login and token of a user is found and made unique
by an index, which a value inside a collection cannot be in the first milestone, and the
Identity base classes already work in these terms.

### The schema belongs to the application

**Decided on 14 November 2026.** A Lattice database has one compiled schema, and the
application compiles it. The package does not merge anything into it at run time.

- **The default.** `identity.schema.json` in the package sources declares the seven entity
  types. `LatticeIdentitySchema.CreateDescriptor<TUser, TRole>()` returns the same text
  with the user and the role bound to the application's classes. It is enough for a
  database that holds only the Identity data, with classes that add no stored properties.
- **The application's own.** An application whose user or role class has properties of its
  own, or which keeps other data in the same database, saves the default as its schema
  file and edits it: other names for the user and role entity types, more fields, more
  indexes, its own entity types next to the Identity ones. A property is stored only when
  the schema declares a field for it.
- **The check.** `LatticeIdentitySchema.Validate<TUser, TRole>(schema)` compares a schema
  with the default declaration. The user and the role are found by their classes, the
  other entity types by name. Every field and index of the default must be there, with the
  same type, nullability and string comparison; the string storage of a field and the
  identities are the application's to choose. That is the owner's answer of 14 November
  2026 to Q4 of the [open questions](open-questions.md); today the check compares type
  and nullability, and the comparison is added in this epic. A unique index must be
  unique. More is allowed, less is refused with
  `InvalidSchema` and the list of what is missing. The stores make the same check when
  they are created, because on a child the schema comes from the master.

The names of the five record entity types and of the indexes are fixed by the package.
Making them configurable is not planned until an application needs it.

## What the package needs from the database

| # | Requirement | Needed for | State in the core |
| --- | --- | --- | --- |
| R1 | A unique index, on one field or several, enforced by the master on every write | User names, role names, external logins, memberships, token names; one user per name under concurrent registration | Declared in the schema and reported as `UniqueConstraintViolation` in the contract; **not implemented** — an entity type with a unique index cannot be written yet |
| R2 | Search through a declared index, by equality on all or on the leading fields | Every lookup that is not by primary key | In the contract; **not implemented** |
| R3 | Fields that hold collections | — | Not used by this package. Part of the engine scope regardless |
| R4 | Find entities by a value inside one of their collections | — | Not used by this package. Outside the first engine milestone |
| R5 | A .NET class bound to an entity type may take its properties from a base class in another assembly and add its own | The application's user class derives from `IdentityUser<string>` | **Works**; covered by tests |
| R6 | The application supplies one complete schema; the package provides a default and validates what it is given | A custom user class; Identity data next to the application's own | **Works**, in the package: see [above](#the-schema-belongs-to-the-application). The core needs nothing for it |
| R7 | Durable persistence | Users and roles survive a restart | Not implemented |
| R8 | A write that succeeds only if the entity still has the revision that was read | Optimistic concurrency | **Works** |
| R9 | Keys of type String and Guid. Fields of type String, Boolean, Int32, nullable DateTimeOffset | The fields of the Identity data classes | **Works** |
| R10 | Several databases in one application, each obtainable by name from dependency injection | Identity data in a database of its own | **Works** |
| R11 | Enumerating all entities of a type, within a budget | `IQueryable` over users and roles, for administrative lists | Works in the placeholder; cost to be measured at 100,000 users |
| R12 | A write submitted through a child completes when that child has applied it, and related operations can require a position | An application on a replica creates a user and reads it back | In the contract; works in the placeholder |
| R13 | A write batch: several inserts, replaces and deletes, also of different entity types, with their conditions, applied atomically at one position | Deleting a user or a role with what belongs to it; several claims at once | In the contract (`ILatticeWriteBatch`); **not implemented** |

R1, R2 and R13 stand between the present contract and working stores. All three are in
the accepted scope of [Epic 1](epic-1/README.md), and each has its tests written and skipped
in `Moltaro.Lattice.Tests`.

**Not required by this epic:** a query language, joins, sorting or paging inside the
database; referential integrity between entities; notifications about changes;
participation in transactions of another database. Notifications about changes are a
requirement of [Epic 3](epic-3-after-the-first-milestone.md).

## Acceptance

1. **The skipped tests pass.** The scenarios in `Moltaro.Lattice.AspNet.Identity.Tests` are
   the first statement of the behavior.
2. **Reference behavior.** The stores also pass Microsoft's Identity specification tests.
   The suite is not published as a current NuGet package (the latest on nuget.org is
   5.0.17), so its source is kept in this repository unchanged, in
   [the test project](../../src/Moltaro.Lattice.AspNet.Identity.Tests/Specification/README.md),
   and `LatticeSpecificationTests` runs it against the Lattice stores. A test that the
   stores cannot pass for a stated reason is listed here with that reason; none is known.
3. **The sample runs to the end.**
4. **Uniqueness under concurrency.** Registering the same user name from many callers at
   once creates exactly one user. Adding the same external login to two users at once
   gives it to one of them.
5. **Concurrency.** Two updates based on the same copy of a user: one succeeds, the other
   gets a concurrency error.
6. **A custom schema.** The tests run on a schema the application edited: its own user
   class with its own fields, and its own entity type in the same database.
7. **Release only:** the data survives a restart of the application.

## Open questions

1. **When does a pending change reach the database?** Identity calls the store to add a
   role and then calls it again to save the user. The store either writes each call at
   once, or collects them and writes one batch when the user is saved. The second is
   atomic and is what the relational stores do.
2. **Which key types does the first version support?** String now. Guid costs little.
   Integer keys need generated ids, which the database does not provide.
3. **How does the store carry a session between requests (R12)?** A write through a child is
   visible on that child at once. A following request can land on another node; the store
   needs a `LatticeSession` for it, and Identity's store interfaces have no place for one.
   The options are a session held by the store for its lifetime (one web request), or one
   taken from the application's request context. The host has to carry the position from a
   response to the next request either way.
4. **What does deleting a user with more belongings than one batch holds do?** Refuse, and
   let the administrator remove claims or tokens first, or raise the limit of the
   database. Not: delete in several acknowledged steps.
5. **How does the default declaration reach an application that installs the package?**
   Today it is a file in the sources and a method that returns its text. It can also be
   shipped as a content file of the package.

## Stages

1. The core delivers the index search (R2), unique indexes (R1) and the write batch (R13);
   see [Epic 1](epic-1/README.md).
2. The user by itself: create, read, update with the concurrency check, delete; passwords,
   lockout, stamps.
3. Everything that belongs to a user: roles, claims, logins, tokens, the authenticator key
   and recovery codes. The role store with role claims.
4. The specification tests pass. Measure R11.
5. Durable persistence from Epic 1 (R7), then release.
