# Moltaro.Lattice

A schema-driven in-memory object database for .NET, designed for compact storage,
low-allocation access, master-coordinated writes, PostgreSQL persistence and local
read replicas.

## Status

Early stage. The public contract is being shaped, and it already runs: a simple
managed in-memory store stands behind it, so the samples and tests in this repository
work today.

That store is a **placeholder, not the database engine**. It supports MemoryOnly mode,
scalar fields, and connects a master with its children inside one process. A schema can
already declare more than the placeholder stores: an entity type with a comparison that
ignores case, with Dictionary storage, with an enum field, or with an owned value, a
List, a Map, a Set or a reference compiles, and so does a schema that declares UTF-16,
but none of them can be written yet. The native storage engine,
PostgreSQL persistence and network replication are not implemented yet, and the contract
may still change.

The [engine design reference](docs/design/README.md),
[accepted decisions](docs/decisions/README.md) and
[prototype evidence](docs/design/prototype-evidence.md) are now maintained here.
For milestone planning, start with the [planning handoff](docs/design/planning-handoff.md).
Design acceptance and prototype results do not mean the production engine exists.

## Schema

A JSON descriptor declares the entity types, their fields, primary keys and indexes, and
the enum types. Entity types, fields, indexes and enum types each have a stable identity,
which a rename keeps. A String field may say how it is compared (`Comparison`: `Ordinal`
or `OrdinalIgnoreCase`) and stored (`StringStorage`: `Plain` or `Dictionary`), and the
schema says how its text is encoded (`StringEncoding`: `Utf8` or `Utf16`). The descriptor
is closed: a property it does not know is an error, not something to ignore. An entity
type may name the .NET class that represents it; this is optional.

A field may also hold more than one value. An owned type (`OwnedTypes`) is a group of
fields that an entity holds as one nested value; a `List`, a `Map` and a `Set` hold
elements, and a `Reference` holds the key of another entity, which is not checked. An
owned type does not contain itself, and a declaration nests at most 32 deep, counted
through collections and owned types together. An index may cover a field inside an owned value: its
part is then written as the path of field identities to it, such as `[5, 1]`. Without a
.NET class such an entity is a `LatticeRecord` that holds `LatticeOwnedValue`s and
collections; a record copies what is set into it.

```json
{
  "SchemaVersion": 1,
  "EntityTypes": [
    {
      "Id": 1,
      "Name": "User",
      "ClrType": "MyApp.User",
      "Fields": [
        { "Id": 1, "Name": "Id", "Type": "Int64" },
        { "Id": 2, "Name": "Name", "Type": "String" },
        { "Id": 3, "Name": "Department", "Type": "String", "Nullable": true }
      ],
      "PrimaryKey": [1]
    }
  ]
}
```

The descriptor is compiled once. The compiled schema can be saved and loaded, so an
application starts from the ready form:

```csharp
// At build or deployment time
var compiled = LatticeSchema.Compile(File.ReadAllText("directory.schema.json"));
compiled.Save("directory.schema.bin");          // or Save(stream)

// At startup
var schema = LatticeSchema.Load("directory.schema.bin");   // or Load(stream)
```

A compiled schema has a fingerprint, `schema.Fingerprint`: the same for the same
declarations on every platform, changed by anything that decides how data is laid out,
compared or kept unique, and not changed by a rename. Each entity type also has a
`LayoutFingerprint`, which covers only how its entities are stored. The saved form ends
with a digest of itself: a file that was changed in any byte is refused when it is
loaded.

## Using the database

```csharp
await using var database = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
{
    PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
    Schema = schema,
});
```

**With a .NET class.** The schema already knows the entity type of `User`, its key and
its fields:

```csharp
var users = database.Set<User>();

await users.InsertAsync(new User { Id = 1, Name = "Ada" });       // create

User? ada = users.Get(1);                                         // read: your own copy, or null

if (users.TryGet(1, out var user, out var revision))              // read with the revision
{
    user.Name = "Ada Lovelace";
    await users.ReplaceAsync(user, revision);                     // update, checked by revision
}

foreach (var item in users.Enumerate()) { }                       // enumerate
bool deleted = (await users.DeleteAsync(1)).Changed;              // delete
```

**By name, without a .NET class.** Every entity type is available as records:

```csharp
var settings = database.Set("Setting");

await settings.InsertAsync(new LatticeRecord(settings.EntityType)
{
    ["Name"] = "theme",
    ["Value"] = "dark",
});

foreach (var record in settings.Enumerate())
{
    Console.WriteLine($"{record["Name"]} = {record["Value"]}");
}
```

Both ways work on the same data and follow the same rules.

**Finding by index.** *The contract below is in place; the search itself is not implemented
yet, and `Find` and `FindAsync` throw `NotSupportedException`.* An entity type declares its
secondary indexes in the schema, each with a name and the fields it covers, in order:

```json
"Indexes": [
  { "Id": 1, "Name": "ByEmail", "Fields": [5] },
  { "Id": 2, "Name": "BySectionAndName", "Fields": [2, 3] }
]
```

The application names the index it searches; there is no query language, and nothing
falls back to reading every entity. An index answers equality on all of its fields, or on
the leading ones followed by a range on the next:

```csharp
var byEmail = users.Index("ByEmail");                             // keep and reuse
foreach (var user in byEmail.Find(IndexKey.Equal(email))) { }

var bySectionAndName = settings.Index("BySectionAndName");
bySectionAndName.Find(IndexKey.Equal("mail", "sender"));          // both fields
bySectionAndName.Find(IndexKey.Equal("mail"));                    // one section, ordered by name
bySectionAndName.Find(IndexKey.Equal("mail")                      // names "a" up to, not including, "n"
    .Range(IndexBound.Inclusive("a"), IndexBound.Exclusive("n")));

// With options: consistency, direction and paging.
var page = await bySectionAndName.FindAsync(
    IndexKey.Equal("mail"),
    new LatticeFindOptions { Session = session, Reverse = true, Limit = 20 });
```

**Unique indexes.** *Declared in the schema and part of the contract; keeping the rule is
not implemented yet, so an entity type that declares a unique index cannot be written.*
Several entities can have the same indexed values unless the index says otherwise:

```json
{ "Id": 1, "Name": "ByEmail", "Fields": [5], "Unique": true }
```

The master then refuses a write that would give a value a second owner, with
`UniqueConstraintViolation` and the name of the index. A value in which any field is
missing is not compared, so any number of entities may have no email.

**Writing several entities together.** *The contract below is in place; executing a batch
is not implemented yet, and `CommitAsync` throws `NotSupportedException`.* One write is
atomic for one entity. Writes that have to succeed or fail together are submitted as one
write batch, which may address different entity types:

```csharp
var batch = database.CreateWriteBatch();
batch.Insert(new User { Id = 7, Name = "Ada" });
batch.Replace(setting, settingRevision);       // only if nobody changed it since it was read
batch.Delete<User>(3);

LatticeWriteResult result = await batch.CommitAsync();
```

The master executes the whole batch at one position or refuses it as a whole; no reader,
on any node, sees a part of it. A batch takes one operation per entity and is bounded in
size. It is not a transaction the application works inside: it cannot read, and it holds
no lock while it is built.

**Master and children.** A master and its read replicas can run in one process or in
several. Application code uses both through the same interface: a write through a master
is executed in place, a write through a child is sent to its master and executed there.

```csharp
var address = new Uri("lattice://localhost:7400");

await using var master = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
{
    PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
    Schema = schema,
    ListenUri = address,
});

// The child loads the schema and the whole database from the master, then follows it.
await using var child = await LatticeDatabase.OpenAsync(new LatticeDatabaseOptions
{
    Role = LatticeRoleEnum.Child,
    PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
    MasterUri = address,
});
```

**Reading your own writes.** A child follows its master with a small delay. Two rules make
that safe to build on:

- A write submitted through a child completes only when that child has applied it, so the
  child's next read sees it. It does not wait for other children.
- A `LatticeSession` carries "what I have already seen" from one operation to the next. An
  operation that takes a session first waits until the node that serves it has caught up,
  and fails with `SynchronizationTimeout` rather than answer from older data.

```csharp
var session = new LatticeSession();

// Written on one node ...
await master.Set<User>().InsertAsync(user, session);

// ... and read on another: the read waits until that node has the write.
var result = await child.Set<User>().GetAsync(user.Id, session);

// Between requests the session travels as text, for example in a header.
string text = session.MinPosition!.Value.ToString();
var nextRequest = new LatticeSession(LatticePosition.Parse(text));
```

The synchronous reads, `Get`, `TryGet`, `Enumerate` and an index's `Find`, take no session
and answer at once from what the node has. The asynchronous ones, `GetAsync`,
`EnumerateAsync` and `FindAsync`, take a session. Every result carries its position,
including "not found".

A request can also wait once and then read as often as it needs, for example through
several indexes:

```csharp
await child.WaitForPositionAsync(position);        // or WaitForPositionAsync(session)

var user = child.Set<User>().Index("ByEmail").Find(IndexKey.Equal(email)).FirstOrDefault();
```

**With dependency injection.** The `Moltaro.Lattice.DependencyInjection` package registers
the database in the container as a singleton. A background service opens it when the
application starts, before a web application accepts requests, and closes it when the
application stops. Services take `ILatticeDatabase` from the container:

```csharp
using Moltaro.Lattice.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLattice(new LatticeDatabaseOptions
{
    PersistenceMode = LatticePersistenceModeEnum.MemoryOnly,
    Schema = LatticeSchema.Load("directory.schema.bin"),
});

var app = builder.Build();

app.MapGet("/users/{id}", (long id, ILatticeDatabase database) =>
    database.Set<User>().Get(id) is { } user ? Results.Ok(user) : Results.NotFound());

app.Run();
```

A background service or any other class gets the database the same way, through its
constructor. Register the database before the background services that use it.

An application is not limited to one database. Besides the default one it can register
any number of named databases, masters and replicas alike; each is injected by its name:

```csharp
builder.Services.AddLattice("directory", directoryOptions);
builder.Services.AddLattice("settings", settingsOptions);

sealed class Report(
    [FromKeyedServices("directory")] ILatticeDatabase directory,
    [FromKeyedServices("settings")] ILatticeDatabase settings) { }
```

**ASP.NET Core Identity.** *Contract only; the stores are not implemented yet.* The
`Moltaro.Lattice.AspNet.Identity` project keeps the users and roles of a standard ASP.NET
Core Identity application in a Lattice database: `AddLatticeStores()` where the Entity
Framework stores would be registered. The schema stays with the application.
`LatticeIdentitySchema` provides a default declaration of the Identity data, which an
application uses as it is or saves, extends for its own user class and its own entity
types, and compiles itself; `LatticeIdentitySchema.Validate` tells whether a schema has
what the stores need.
Without a container, or to control the moment yourself, create the database first and
open it later: `LatticeDatabase.Create(options)`, then `await database.OpenAsync()`.

Runnable versions live in [src/Moltaro.Lattice.Samples](src/Moltaro.Lattice.Samples);
the [tests](src/Moltaro.Lattice.Tests) show the behavior case by case.

## Database model

- One database instance opens one database with its own schema and entities.
- An entity belongs to an entity type declared in the schema and is addressed by its
  immutable primary key.
- Reads are served from local memory and return owned copies. A revision read with an
  entity lets a later write detect intervening changes.
- One master executes all writes. Children load the whole database from the master,
  apply its changes and serve reads locally; a child can lag behind its master.
- Three identities are kept apart: a revision identifies one state of one entity, a
  position says how far the whole database has been applied, and neither identifies a write.
- A master is Durable by default or explicitly MemoryOnly. Children are always MemoryOnly.

## Public API

| Namespace | Type | Purpose |
| --- | --- | --- |
| `Moltaro.Lattice` | `LatticeDatabase` | Entry point: `OpenAsync`, or `Create` and open later |
| | `LatticeDatabaseOptions` | Role, persistence mode, schema and addresses of one instance |
| | `ILatticeDatabase` | An open database: `Set<TEntity>()`, `Set(name)`, `CreateWriteBatch()`, state, close |
| | `LatticeRevision` | Opaque token of one entity state, used for conditional writes |
| | `LatticePosition` | How far a database history has been applied; returned with every result |
| | `LatticeSession` | Consistency context of related operations: the position they depend on |
| | `LatticeException` | Failures, told apart by `LatticeErrorEnum` |
| `Moltaro.Lattice.Schema` | `LatticeSchema` | Compiled schema: `Compile`, `Save`, `Load` |
| | `LatticeEntityType`, `LatticeField`, `LatticeIndex` | Entity types, their fields and secondary indexes |
| | `LatticeValueDeclaration`, `LatticeEnumType`, `LatticeOwnedType` | What a field, an element, a key or a value holds: its type, nullability and text policies; the enum types and the owned types of the schema |
| | `LatticeIndexPart` | One part of an index key: a field of the entity type, or the path to a field inside an owned value |
| | `LatticeFingerprint` | The fingerprint of a schema and the layout fingerprint of an entity type |
| `Moltaro.Lattice.Access` | `ILatticeSet<TEntity>` | Entities as objects of a .NET class |
| | `ILatticeSet`, `LatticeRecord`, `LatticeOwnedValue` | Entities by name, as records, and the owned values a record holds |
| | `ILatticeIndex<TEntity>`, `IndexKey`, `IndexBound` | Finding entities through a declared secondary index |
| | `ILatticeWriteBatch` | Several writes, also of different entity types, executed together or not at all |
| `Moltaro.Lattice.DependencyInjection` | `AddLattice` | Registration in a dependency injection container; a separate package |
| `Moltaro.Lattice.AspNet.Identity` | `AddLatticeStores`, `LatticeIdentitySchema` | Storage for ASP.NET Core Identity; contract only, not published yet |

In the contract but not implemented yet: searching an index, unique indexes, executing a
write batch, Durable persistence, and storing by what a schema can declare beyond the
defaults: a comparison that ignores case, Dictionary storage, enum fields, UTF-16, and
owned values, Lists, Maps, Sets and references, which a schema declares, a record and a
class hold, and no write stores yet. Not in the contract yet: replication between
processes, partial updates, safe retry of a write, schema migration and replication
status. A query language is not planned for the first version.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Moltaro.Lattice.slnx` | Product solution with libraries, tests and samples |
| `src/Moltaro.Lattice` | The core library; its folders are its namespaces |
| `src/Moltaro.Lattice.Tests` | Tests of the core library |
| `src/Moltaro.Lattice.Engine` | The engine; depends on the core library and owns the index package `Arc.Collections`. It holds the first internal mechanisms, the native memory arena and the table of root entries; nothing of it is behind the contract yet, where the placeholder in the core library still stands |
| `src/Moltaro.Lattice.Engine.Tests` | Tests of the engine, including its internal mechanisms |
| `src/Moltaro.Lattice.DependencyInjection` | Dependency injection support |
| `src/Moltaro.Lattice.DependencyInjection.Tests` | Tests of the dependency injection support |
| `src/Moltaro.Lattice.AspNet.Identity` | Storage for ASP.NET Core Identity: the contract; the stores are not implemented yet |
| `src/Moltaro.Lattice.AspNet.Identity.Tests` | Tests of the Identity storage; most are written ahead and skipped. Includes Microsoft's Identity specification tests, see [third-party notices](THIRD-PARTY-NOTICES.md) |
| `src/Moltaro.Lattice.TestSupport` | Code shared by the test projects; never shipped |
| `src/Moltaro.Lattice.Samples` | Runnable usage samples |
| `src/Prototypes` | [Synthetic experiments](src/Prototypes/README.md), isolated from the product solution |
| `docs/design`, `docs/decisions` | [Engine design reference](docs/design/README.md), decisions, measurements and planning handoff |
| `docs` | [Documentation](docs/README.md): product documentation and [current work](docs/work/README.md) |
| `AGENTS.md` | [How the project is built](AGENTS.md): architectural principles, code style and working rules, for people and coding agents |

Inside the core library:

| Folder | Responsibility |
| --- | --- |
| (root) | Opening a database, its options, state and errors |
| `Schema` | Schema model, JSON compilation, binary save and load |
| `Access` | Sets, records and the binding of .NET classes to entity types |
| `Storage` | Local data of one database instance |
| `Replication` | Link between a master and its children |
| `Engine` | The database instance: lifecycle, role and routing of reads and writes |

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Run the commands
from the `src` directory.

```bash
dotnet build
```

```bash
dotnet test
```

```bash
dotnet run --project Moltaro.Lattice.Samples
```

## Package

Two NuGet packages are published to the GitHub Packages feed of the repository owner:
`Moltaro.Lattice`, the core with no dependencies, and
`Moltaro.Lattice.DependencyInjection`, the dependency injection support.

**Releasing a version.** Tag a commit of `main` and push the tag; the version is the tag
without the leading `v`. The [Publish](.github/workflows/publish.yml) workflow builds,
tests and publishes it. [Branches and releases](docs/work/branches.md) describes how
work reaches `main` and what a release requires.

```bash
git tag v0.1.0-alpha.1
```

```bash
git push origin v0.1.0-alpha.1
```

**Using the package.** Register the feed once per machine with a GitHub personal access
token that has the `read:packages` scope, then reference the package as usual.

```bash
dotnet nuget add source "https://nuget.pkg.github.com/andrton/index.json" --name github-andrton --username YOUR_GITHUB_LOGIN --password YOUR_TOKEN --store-password-in-clear-text
```

```bash
dotnet add package Moltaro.Lattice --prerelease
```

Never commit a token: keep the feed registration in the user-level NuGet configuration,
or pass the token through an environment variable in CI.

## License

[Apache License 2.0](LICENSE)
