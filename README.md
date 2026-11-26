# Moltaro.Lattice

A schema-driven in-memory object database for .NET, designed for compact storage,
low-allocation access, master-coordinated writes, PostgreSQL persistence and local
read replicas.

## Status

Early stage. The public contract is being shaped, and the native storage engine stands
behind it: `LatticeDatabase` opens the engine, so the samples and tests in this
repository run on it.

The engine is being built in steps. Today it works in MemoryOnly mode: it stores and
reads entities of scalar and Enum fields by primary key, under every key type and both
text comparisons, in UTF-8 and UTF-16. Each instance enforces one memory budget for
its storage, write preparation and active connections. Every
declared secondary index is kept with the data, and an application finds entities
through an index by its full key, by its leading fields and a range on the next, in the
order of the index or its reverse and in pages, each entity at most once. A child loads
the whole database from its master over TCP while the master keeps writing, builds its
own indexes, then follows its changes, and forwards its own writes to the master. A
unique index gives a value to one entity only, whichever node submits the write. An entity
may hold owned values, Lists, Maps, Sets and references at every permitted depth. Both
record and class access read them as owned copies. A write can include them in a batch, and
a child loads them, follows their changes and forwards writes that contain them. An index
can address a field inside an owned value. The fixed part of an entity fits in one block,
including the fields of its owned values. A record for a collection entry fits in one chunk.
A database whose schema breaks either layout rule fails to open with `InvalidSchema`,
which names the entity type. Partial updates assign named fields and edit Lists, Maps and
Sets. The master executes them alone or in a write batch, also when a child submits them.
Children apply the changed fields and collection entries at the committed position.
Both by-name and generated typed views read one entity directly from its local arena
inside a short synchronous callback. Master and child run bounded storage maintenance
in the background. The worker can evacuate large blocks from a nearly empty segment
into existing space in another active segment. Earlier readers retain their original
storage until they leave. The arena reuses and returns empty segments, and collections
can release persistently unused lookup capacity. PostgreSQL persistence is planned,
and the contract may still change.

The [engine design reference](docs/design/README.md),
[accepted decisions](docs/decisions/README.md) and
[prototype evidence](docs/design/prototype-evidence.md) are now maintained here.
For milestone planning, start with the [planning handoff](docs/design/planning-handoff.md).
Design acceptance and prototype results do not mean the production engine exists.

## What it is, and what it is not

Lattice is one database with one master. The master executes every write. A child is a
full copy of the database in the memory of another process, kept current by the master;
it answers reads from its own memory, without a call over the network. This is the
design; what of it runs today is under [Status](#status).

An application uses a master and a child through the same interface. A write called on
a child is sent to the master, executed there, and its result is returned to the caller.
The calling code is the same on both and does not need to know which it runs on.

Lattice is not a distributed database that accepts a write on any node. There is no
second master, and a child never executes a write by itself: when a child cannot reach
the master, its writes fail with `Unavailable` — or with `IndeterminateOutcome` when the
reply of a write already sent is lost — while reads from its own memory that wait for no
position go on. A fair picture is a database with a master and, in every other process,
a read replica that works like a cache which never misses and which the application
never has to invalidate.

## Schema

A JSON descriptor declares the entity types, their fields, primary keys and indexes, and
the enum types. Entity types, fields, indexes and enum types each have a stable identity,
which a rename keeps. A String field may say how it is compared (`Comparison`: `Ordinal`
or `OrdinalIgnoreCase`) and stored (`StringStorage`: `Plain` or `Dictionary`), and the
schema says how its text is encoded (`StringEncoding`: `Utf8` or `Utf16`). A field stored
as `Dictionary` keeps each distinct text once for the whole database: it pays for a field
with few distinct values and costs memory and write time for identifiers, names and other
mostly distinct text, which stay `Plain`
([the measured figures](docs/decisions/strings.md#q04.3-where-should-identical-strings-share-storage)). The descriptor
is closed: a property it does not know is an error, not something to ignore. An entity
type may name the .NET class that represents it; this is optional.

A field may also hold more than one value. An owned type (`OwnedTypes`) is a group of
fields that an entity holds as one nested value; a `List`, a `Map` and a `Set` hold
elements, and a `Reference` holds the key of another entity, which is not checked. An
owned type does not contain itself, and a declaration nests at most 32 deep, counted
through collections and owned types together. An index may cover a field inside an owned value: its
part is then written as the path of field identities to it, such as `[5, 1]`. Without a
.NET class such an entity is a `LatticeRecord` that holds `LatticeOwnedValue`s and
collections. A record copies what is set into it. A List preserves order and duplicates.
A Map rejects duplicate keys. A Set keeps the first representation of equal members.
The schema determines equality for Maps and Sets, including lookups in the copies returned
by a read.

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
    },
    {
      "Id": 2,
      "Name": "Setting",
      "Fields": [
        { "Id": 1, "Name": "Id", "Type": "String" },
        { "Id": 2, "Name": "Section", "Type": "String" },
        { "Id": 3, "Name": "Name", "Type": "String" },
        { "Id": 4, "Name": "Value", "Type": "String" },
        { "Id": 5, "Name": "Priority", "Type": "Int32" }
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
var settingsSession = new LatticeSession();

await settings.InsertAsync(new LatticeRecord(settings.EntityType)
{
    ["Id"] = "ui.theme",
    ["Section"] = "ui",
    ["Name"] = "theme",
    ["Value"] = "dark",
    ["Priority"] = 10,
}, settingsSession);

foreach (var record in settings.Enumerate())
{
    Console.WriteLine($"{record["Name"]} = {record["Value"]}");
}
```

Both ways work on the same data and follow the same rules.

**Reading in place by name.** A short synchronous viewer can work directly with the
fields of one written state. Resolve a field once when the same field is read frequently.

```csharp
if (settings.TryView("ui.theme", static view => view["Priority"].Get<int>(), out var priority))
{
    Console.WriteLine(priority);
}

var valueField = settings.EntityType.GetField("Value");
var theme = await settings.ViewAsync(
    "ui.theme", settingsSession, valueField,
    static (view, field) => view[field].GetString());
if (theme.Found)
{
    Console.WriteLine(theme.Result);
}
```

`ViewAsync` waits for the session before taking a view and advances it to the position
of the answer. The callback can read scalars, nested fields, collections and the stored
bytes of text or binary values. `GetPart` borrows a read-only span and `GetString`
returns an owned string. The callback must finish promptly and avoid waiting, I/O and
database calls. A concurrent write can publish while the viewer reads its original
state. A viewer delays reclamation of memory retired while it is active, which can
delay a write or a child's application under memory pressure, new reads once all
protection slots are occupied, and closing. See the [view sample](src/Moltaro.Lattice.Samples/Basics/ViewSample.cs).

**Reading in place through a class.** Reference the optional
`Moltaro.Lattice.Generators` build-time package, then declare a view for the entity
class. The compiler generates properties for the view. A nested view declaration
requires partial containing types.

```csharp
var users = database.Set<User>();
if (users.TryView(1L, static (UserView view) => view.Name.GetString(), out var name))
{
    Console.WriteLine(name);
}

[LatticeView(typeof(User))]
public readonly ref partial struct UserView;
```

Use `ViewAsync` with a session for the same position guarantees as reads by name.
When the schema is a file, include it as an `AdditionalFiles` item with a
`.schema.json` suffix to check field binding during the build. For a schema built
in code, the first view checks its fields against the database's schema.
`LatticeViewField` on the partial view selects Binary or List for a `byte[]`
property, supplies nullability for a borrowed field, or omits a CLR-only property
with `Ignore = true`. Nullable owned, List, Map and Set fields use
`LatticeOptionalView<TView>`. Check `HasValue` before using `Value`.

**Finding by index.** An entity type
declares its secondary indexes in the schema, each with a name and the ids of the fields it
covers, in order; here `ByEmail` covers the field `Email`, id 5, of a user, and
`BySectionAndName` the fields `Section` and `Name`, ids 2 and 3, of a setting:

```json
"Indexes": [
  { "Id": 1, "Name": "ByEmail", "Fields": [5] },
  { "Id": 2, "Name": "BySectionAndName", "Fields": [2, 3] }
]
```

The application names the index it searches; there is no query language, and nothing
falls back to reading every entity. An index answers equality on all of its fields, or on
the leading ones followed by a range on the next, in its order, where an entity without a
value in a field comes after those with one, or in the exact reverse of it. A search is not
a snapshot; it returns each entity at most once, also one deleted and created again while
it runs, and every entity that keeps its indexed values for the whole search. A value is
given in the type of its field, or as the same number in another type, such as an `int`
for a `Decimal` field; a value that would have to be rounded is refused, and so is a
floating-point or `Decimal` value for an integer field, 42.0 included, and a `Decimal`
for a floating-point field or the reverse:

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

**Unique indexes.** Several entities can have the same indexed values unless the index says
otherwise; an index over several fields compares them together, so the same login may
belong to one account in each tenant, here with the fields `Email`, `Tenant` and `Login`,
ids 5, 6 and 7, of an account:

```json
"Indexes": [
  { "Id": 1, "Name": "ByEmail", "Fields": [5], "Unique": true },
  { "Id": 2, "Name": "ByTenantAndLogin", "Fields": [6, 7], "Unique": true }
]
```

The master then refuses a write that would give a value a second owner, with
`UniqueConstraintViolation` and the name of the index, and nothing is written. A value in
which any field is missing is not compared, so any number of entities may have no email.
Values are compared as the index orders them: a text by the comparison of its field. Of
writes of one value made at the same time, through the master or through a child, at most
one succeeds; a write of a value that another write still in progress takes or gives up
waits for it. A batch is checked on the state it leaves, so it may exchange two values, or
delete an entity and give its value to another.

**Writing several entities together.** One write is atomic for one entity. Writes that have to succeed or fail together are submitted as one
write batch, which may address different entity types:

```csharp
var batch = database.CreateWriteBatch();
batch.Insert(new User { Id = 7, Name = "Ada" });
batch.Replace(setting, settingRevision);       // only if nobody changed it since it was read
batch.Delete<User>(3);

LatticeWriteResult result = await batch.CommitAsync();
```

The master executes the whole batch at one position or refuses it as a whole; a batch
committed through a child reaches the master as one request and completes when that child
has applied all of it. No reader,
on any node, sees an entity of the batch changed before that position on that node, and a
read that begins once its node has reached that position, as a read with the session of the
commit does, sees all of it; a search or an enumeration that runs while the batch is
published is no snapshot and may return some of its entities as they were before and
others as after. A batch takes one operation per entity. Building the batch takes local
memory for its snapshots. If the local instance cannot hold an addition, the adding
method throws `WriteRejected` with `Limit` set to `Memory`. The adding method stops
at the first snapshot allocation it cannot fund. Later properties may still need
validation. The previous operations stay in the batch and the addition can be tried again. `CommitAsync` checks separately whether
execution can fit. A batch cannot read and takes no place in the write queue while it is
being built.

**Editing part of an entity.** Create an update for an existing key, give it commands in
execution order and commit it once. Both the class set and the set by name take schema
field names. For example, with the User schema above:

```csharp
var edit = database.Set<User>().CreateUpdate(1L);
LatticeCommandResult renamed = edit.Assign("Name", "Ada Lovelace");
edit.Assign<string?>("Department", null);
await edit.CommitAsync();

Console.WriteLine(renamed.Changed);
```

Each command copies its operands when added. An update can also be added to a write batch
with `batch.Update(edit)` before either is submitted. A successful commit exposes each
command's `Changed` result, including after local application on a child that stays open.
Assignment counts as a change even when the value compares equal. Removing an absent
Map key or Set member and adding an existing Set member leave the revision as it was when
the update has no other changes. A successful no-op still has a position.

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

A master and its children connect over TCP, in one process or in several. An address is
`lattice://host:port`. With port 0
the system chooses a free port, and the open master reports it in its `ListenUri` property,
which is the address to give its children. A child that cannot reach its master, or
receives nothing from it, for `OpenTimeout`, 30 seconds by default, fails its open with
`Unavailable`; until then it keeps trying, so it may be started before its master. A load
that keeps arriving is not ended by the timeout, however long it takes, as long as each of
its frames, at most 64 KiB and a header of 32 bytes, arrives whole within the timeout.
If a child cannot hold its initial state, opening fails with `Unavailable`. An open
child short of memory or usable root slots pauses at the next change. It serves local
reads at its applied position and resumes in order when capacity returns on the same
connection. The master keeps publishing. A stalled child can retain a growing delivery
history on the master until the connection ends or the child catches up.

**The replication port must be reachable only from the nodes of the database.** This is a
precondition of every deployment with children. The engine authenticates nobody at that
port: its handshake checks that a peer is compatible — the same database, history, schema
and format — not who it is, and the engine adds no shared secret and no TLS of its own.
Whoever reaches the port can load the whole database, and write to it as a child does. Keep
it on a network, or behind rules, that only the master and its children reach.
The decision and its reasons are in
[Q15](docs/decisions/replication-and-hosting.md#q15-configured-master-and-child-connection).

**Reading your own writes.** A child follows its master as it applies each change. Two rules make
that safe to build on:

- A write submitted through an open child completes when that child has applied it, so
  the child's next read sees it. If the child closes after receiving the master's
  successful reply, the caller still receives the committed result. Other children
  can catch up later.
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

**Instance memory.** `LatticeDatabaseOptions.MemoryLimit` covers the capacity the engine
owns for storage, preparing and publishing mutations, and active connections. It counts
whole native segments, including free space and the reserve, and backing kept for readers
or index growth. A write that cannot fit returns `WriteRejected` with `Limit` set to
`Memory`. When every root slot is occupied or has exhausted its generation and no new
slot can be addressed, the limit is `RootSlots`. For a batch refused by either resource,
`OperationIndex` is null. Caller-owned data, managed enumeration workspace and the
master's retained delivery history are outside `MemoryLimit`.

**Closing a database.** By the time a master or child reports `Closed`, it has stopped
admitting new reads and writes. A fresh uncancelled asynchronous read or write
with valid arguments reports `ObjectDisposedException` through its task.
This includes operations through sets obtained earlier, the first step of an
asynchronous enumeration and commits of prepared updates or batches. Await
successful completion of `CloseAsync` to know that the instance has released
its native memory.

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
the [tests](src/Moltaro.Lattice.Tests) show the behavior case by case. The
[README of the samples](src/Moltaro.Lattice.Samples/README.md) orders them as a learning
path, from simple to complex.

## Database model

- One database instance opens one database with its own schema and entities.
- An entity belongs to an entity type declared in the schema and is addressed by its
  immutable primary key.
- Reads are served from local memory. Ordinary reads return owned copies, and a
  synchronous protected viewer can read one state in place. A revision read with an
  entity lets a later write detect intervening changes.
- One master executes all writes. Children load the whole database from the master,
  apply its changes and serve reads locally; a child can lag behind its master.
- Three identities are kept apart: a revision identifies one state of one entity, a
  position says how far the whole database has been applied, and neither identifies a write.
- A master is Durable by default or explicitly MemoryOnly. Children are always MemoryOnly.

## Public API

| Namespace | Type | Purpose |
| --- | --- | --- |
| `Moltaro.Lattice` | `LatticeDatabase` | Entry point: `OpenAsync`, or `Create` and open later. It ships in the engine package `Moltaro.Lattice.Engine`; every other type of this namespace is in the core library |
| | `LatticeDatabaseOptions` | Role, persistence mode, schema and addresses of one instance, its timeouts, its memory budget and the limits of a read |
| | `ILatticeDatabase` | An open database: `Set<TEntity>()`, `Set(name)`, `CreateWriteBatch()`, state, the address a master listens at, close |
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
| | `LatticeRecordView`, `LatticeValueView`, `LatticeViewFunc`, `LatticeViewResult` | Scoped access to one entity, its owned values and collections, and the result of the viewer |
| | `LatticeViewAttribute`, `LatticeViewFieldAttribute`, `LatticeOptionalView<TView>` | Generated typed views for a .NET class and access to nullable borrowed values |
| | `ILatticeIndex<TEntity>`, `IndexKey`, `IndexBound` | Finding entities through a declared secondary index |
| | `LatticeUpdate`, `LatticeCommandResult` | Ordered field assignments and collection edits for one entity, with each command's result after commit |
| | `ILatticeWriteBatch` | Several writes, also of different entity types, executed together or not at all |
| `Moltaro.Lattice.DependencyInjection` | `AddLattice` | Registration in a dependency injection container; a separate package |
| `Moltaro.Lattice.AspNet.Identity` | `AddLatticeStores`, `LatticeIdentitySchema` | Storage for ASP.NET Core Identity; contract only, not published yet |

Durable persistence and automatic child reconnection are defined in the contract and await
implementation. Safe write retries, schema migration and replication status are planned.
A query language is not planned for the first version.

## Repository layout

| Path | Contents |
| --- | --- |
| `src/Moltaro.Lattice.slnx` | Product solution with libraries, tests, samples and the measurement program |
| `src/Moltaro.Lattice` | The core library; its folders are its namespaces |
| `src/Moltaro.Lattice.Tests` | Tests of the public contract: the schema, and scenario tests of reads, writes, master and child, which run on the engine |
| `src/Moltaro.Lattice.Engine` | The engine depends only on the core library and .NET. It includes the native arena, root table and layouts that store owned values, Lists, Maps, Sets and references. It serves reads by primary and secondary index, executes writes and batches on a master and handles the transport for child loading, replication and forwarding. `LatticeDatabase` opens an instance. |
| `src/Moltaro.Lattice.Engine.Tests` | Tests of the engine, including its internal mechanisms |
| `src/Moltaro.Lattice.DependencyInjection` | Dependency injection support |
| `src/Moltaro.Lattice.DependencyInjection.Tests` | Tests of the dependency injection support |
| `src/Moltaro.Lattice.AspNet.Identity` | Storage for ASP.NET Core Identity: the contract; the stores are not implemented yet |
| `src/Moltaro.Lattice.AspNet.Identity.Tests` | Tests of the Identity storage; most are written ahead and skipped. Includes Microsoft's Identity specification tests, see [third-party notices](THIRD-PARTY-NOTICES.md) |
| `src/Moltaro.Lattice.TestSupport` | Code shared by the test projects; never shipped |
| `src/Moltaro.Lattice.Measurements` | The measurement program: the reference fixture, the scenarios and their manifests. Started by hand; never shipped and not a test project |
| `src/Moltaro.Lattice.Samples` | Runnable usage samples |
| `src/Prototypes` | [Synthetic experiments](src/Prototypes/README.md), isolated from the product solution |
| `docs/design`, `docs/decisions` | [Engine design reference](docs/design/README.md), decisions, measurements and planning handoff |
| `docs` | [Documentation](docs/README.md): product documentation and [current work](docs/work/README.md) |
| `AGENTS.md` | [How the project is built](AGENTS.md): architectural principles, code style and working rules, for people and coding agents |

Inside the core library:

| Folder | Responsibility |
| --- | --- |
| (root) | The database interface, its options, state and errors, positions, revisions and sessions |
| `Schema` | Schema model, JSON compilation, binary save and load |
| `Access` | Sets, records, write batches, the kind of a write and the binding of .NET classes to entity types |

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

A release publishes four packages to [nuget.org](https://www.nuget.org).
`Moltaro.Lattice` is the public contract. `Moltaro.Lattice.Engine` adds
`LatticeDatabase` and brings the contract with it. `Moltaro.Lattice.DependencyInjection`
supports container registration. `Moltaro.Lattice.Generators` provides typed
protected views at build time. The first release is pending.

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

**Using the package.** Reference the engine; an application that registers the database
in a container references the dependency injection support as well.

```bash
dotnet add package Moltaro.Lattice.Engine --prerelease
```

```bash
dotnet add package Moltaro.Lattice.DependencyInjection --prerelease
```

An application that declares generated views adds the analyzer package.

```bash
dotnet add package Moltaro.Lattice.Generators --prerelease
```

## License

[Apache License 2.0](LICENSE)
