# Samples

Runnable samples of the intended use of Moltaro.Lattice, as a learning path from simple to
complex. From `src`, `dotnet run --project Moltaro.Lattice.Samples` runs them in the order
below and prints "Sample 3 of 16: …" before each.

A folder holds the samples of one topic and is their namespace: `Basics`, `Schema`, `Writes`,
`Limits`, `Replication`, `Indexes` and `Hosting`. The folders are topics, not steps, so the
path visits `Schema` twice: its first sample compiles the schema that the basics run on, and
the rest of it builds on the basics. `User.cs`, the class of the users of that schema, and
`Program.cs` are at the root.

A sample of a feature that is in the contract but not implemented yet is written as it will
be used. It stops where the feature is missing, the program reports where, and the path goes
on with the next sample.

`Program.cs` holds the one list of this order. It refuses to run when this table lists other
samples or another order, so a sample is added or moved in both places at once.

| # | Sample | What it teaches | Read before | Today |
| --- | --- | --- | --- | --- |
| 1 | [Schema/SchemaSample.cs](Schema/SchemaSample.cs) | Compiling the schema from its JSON descriptor, `directory.schema.json`, saving the compiled form and loading it at startup | Nothing | Runs to the end |
| 2 | [Basics/BasicCrudSample.cs](Basics/BasicCrudSample.cs) | Entities as objects of a .NET class: open a database, insert, read an owned copy with its revision, replace on that revision, upsert, enumerate, delete | Schema | Runs to the end |
| 3 | [Basics/DynamicAccessSample.cs](Basics/DynamicAccessSample.cs) | The same operations by name, as records, without a .NET class; one entity type used both ways | BasicCrud | Runs to the end |
| 4 | [Basics/KeyTypesSample.cs](Basics/KeyTypesSample.cs) | The types a primary key may have, and which key values are accepted or refused | DynamicAccess | Runs to the end |
| 5 | [Basics/ClosedPropertiesSample.cs](Basics/ClosedPropertiesSample.cs) | An entity class that keeps its state behind its constructor and its methods | BasicCrud | Runs to the end |
| 6 | [Schema/SchemaDeclarationsSample.cs](Schema/SchemaDeclarationsSample.cs) | Enum types, how text is compared and stored, stable identities, and the fingerprints of a schema | Schema | Runs to the end |
| 7 | [Schema/DictionaryStringsSample.cs](Schema/DictionaryStringsSample.cs) | Text that repeats stored once for the whole database, and read back as it was written | DynamicAccess, SchemaDeclarations | Runs to the end |
| 8 | [Schema/NestedValuesSample.cs](Schema/NestedValuesSample.cs) | Owned values, Lists, Maps, Sets and references: their declarations, and the records and classes that hold them | DynamicAccess, SchemaDeclarations | Runs |
| 9 | [Writes/WriteBatchSample.cs](Writes/WriteBatchSample.cs) | Several writes, also of several entity types, executed together or not at all | BasicCrud, DynamicAccess | Runs to the end |
| 10 | [Limits/MemoryLimitSample.cs](Limits/MemoryLimitSample.cs) | The memory budget of an instance, many writes at once, and a write the budget cannot hold | BasicCrud | Runs to the end: the budget holds every write it makes, so none is refused |
| 11 | [Limits/ReadLimitsSample.cs](Limits/ReadLimitsSample.cs) | Limits on the temporary memory and the time of one enumeration and of one search through an index: one that stays within them, one that a limit stops, and a page that fits where the whole search does not | BasicCrud | Runs to the end: the second enumeration and the search of a department are stopped by the small limit, and the page fits |
| 12 | [Replication/MasterAndChildSample.cs](Replication/MasterAndChildSample.cs) | A master and its child through one interface: writes through the child, a session that waits for a write, a child without its master | BasicCrud | Runs to the end |
| 13 | [Indexes/IndexSample.cs](Indexes/IndexSample.cs) | Finding entities through a named index: by its full key, by its leading fields, with a range, in reverse, in pages, and on a child; a key given as a number of another type and an Enum field searched by its integer | DynamicAccess, MasterAndChild | Runs to the end |
| 14 | [Indexes/UniqueIndexSample.cs](Indexes/UniqueIndexSample.cs) | A value that belongs to one entity only, two entities that exchange their values in one batch, and a login unique within its tenant over two fields, compared without case and found by the tenant alone | WriteBatch, Index | Runs to the end |
| 15 | [Hosting/DependencyInjectionSample.cs](Hosting/DependencyInjectionSample.cs) | The database as a singleton of an application with dependency injection, opened and closed with it; several named databases | BasicCrud | Runs to the end |
| 16 | [Hosting/IdentitySample.cs](Hosting/IdentitySample.cs) | ASP.NET Core Identity with its users and roles in a Lattice database | DependencyInjection | Stops at the first operation: the stores are not implemented yet |
