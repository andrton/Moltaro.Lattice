namespace Moltaro.Lattice.Tests.Binding;

// By the owner's answer to Q29 (the remarks of ILatticeDatabase.Set<TEntity>(); brief of Epic 1,
// sprint S06): the property of a field has a public get accessor and a set accessor of any
// accessibility, public, internal, protected, private or init. The database sets the value through
// that accessor when it builds an object, so a class may keep its properties closed for writing by
// the application, and an entity read from the database has the stored values in them. A property
// without a set accessor, and one whose get accessor is not public, are BindingFailed with the
// property named. The rule holds for the class of an owned type as for the class of an entity type;
// an entity with an owned value cannot be stored yet (S18), so of the class of an owned type the
// binding is stated, not a read.
public sealed class SetAccessorTests
{
    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Properties_with_a_set_accessor_of_any_accessibility_bind_and_hold_the_stored_values(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, TicketSchema());
        var tickets = database.Set<Ticket>();
        var records = database.Set("Ticket");

        // Written by name, so that every value the object shows comes from the database.
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Title"] = "Printer", ["Owner"] = "Ada", ["Level"] = 3, ["Code"] = "T-1" });

        ExpectTicket(tickets.Get(1L), 1, "Printer", "Ada", 3, "T-1");
        Assert.True(tickets.TryGet(1L, out var read, out _));
        ExpectTicket(read, 1, "Printer", "Ada", 3, "T-1");
        ExpectTicket((await tickets.GetAsync(1L, new LatticeSession())).Entity, 1, "Printer", "Ada", 3, "T-1");
        ExpectTicket(Assert.Single(tickets.Enumerate()), 1, "Printer", "Ada", 3, "T-1");

        // Written through the class, which shows its values through its public get accessors.
        await tickets.InsertAsync(new Ticket(2, "Network", "Grace", 5, "T-2"));

        Assert.True(records.TryGet(2L, out var record));
        Assert.Equal("Network", record["Title"]);
        Assert.Equal("Grace", record["Owner"]);
        Assert.Equal(5, record["Level"]);
        Assert.Equal("T-2", record["Code"]);
        ExpectTicket(tickets.Get(2L), 2, "Network", "Grace", 5, "T-2");
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Record_class_with_init_accessors_binds_and_holds_the_stored_values(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, AccessorSchema.Note<InitNote>());
        var notes = database.Set<InitNote>();
        var records = database.Set("Note");

        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Text"] = "stored" });
        await notes.InsertAsync(new InitNote { Id = 2, Text = "written" });

        Assert.Equal(new InitNote { Id = 1, Text = "stored" }, notes.Get(1L));
        Assert.Equal(new InitNote { Id = 2, Text = "written" }, notes.Get(2L));
        Assert.Equal("written", records.Get(2L)!["Text"]);
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Class_of_an_owned_type_binds_with_set_accessors_of_any_accessibility(DatabaseImplementationEnum implementation)
    {
        // Text has a private and Level a protected set accessor in one class; in the other, Text an
        // internal one and Level init.
        await AccessorSchema.ExpectBoundAsync(
            implementation,
            AccessorSchema.Stamped<PrivatelySetStamp>(),
            new BindingSchema.Holder<PrivatelySetStamp> { Id = 1, Value = new PrivatelySetStamp("sealed", 2) });

        await AccessorSchema.ExpectBoundAsync(
            implementation,
            AccessorSchema.Stamped<InitStamp>(),
            new BindingSchema.Holder<InitStamp> { Id = 1, Value = new InitStamp("sealed") { Level = 2 } });
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Property_without_a_set_accessor_is_refused_with_the_property_named(DatabaseImplementationEnum implementation)
    {
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<GetOnlyNote>(), new GetOnlyNote { Id = 1 });
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<ComputedNote>(), new ComputedNote { Id = 1 });

        await AccessorSchema.ExpectNotBoundAsync(
            implementation,
            AccessorSchema.Stamped<GetOnlyStamp>(),
            new BindingSchema.Holder<GetOnlyStamp> { Id = 1, Value = new GetOnlyStamp() });
    }

    [Theory]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Property_whose_get_accessor_is_not_public_is_refused_with_the_property_named(DatabaseImplementationEnum implementation)
    {
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<InternalGetNote>(), new InternalGetNote { Id = 1 });
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<ProtectedGetNote>(), new ProtectedGetNote { Id = 1 });
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<PrivateGetNote>(), new PrivateGetNote { Id = 1 });
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<InternalNote>(), new InternalNote { Id = 1 });

        await AccessorSchema.ExpectNotBoundAsync(
            implementation,
            AccessorSchema.Stamped<PrivateGetStamp>(),
            new BindingSchema.Holder<PrivateGetStamp> { Id = 1, Value = new PrivateGetStamp() });
        await AccessorSchema.ExpectNotBoundAsync(
            implementation,
            AccessorSchema.Stamped<InternalGetStamp>(),
            new BindingSchema.Holder<InternalGetStamp> { Id = 1, Value = new InternalGetStamp() });
    }

    private static void ExpectTicket(Ticket? ticket, long id, string title, string owner, int level, string code)
    {
        Assert.NotNull(ticket);
        Assert.Equal(id, ticket.Id);
        Assert.Equal(title, ticket.Title);
        Assert.Equal(owner, ticket.Owner);
        Assert.Equal(level, ticket.Level);
        Assert.Equal(code, ticket.Code);
    }

    // The entity type Ticket: one field for every accessibility of a set accessor.
    private static LatticeSchema TicketSchema() => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Ticket", "ClrType": "{{typeof(Ticket).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Title", "Type": "String" },
                { "Id": 3, "Name": "Owner", "Type": "String" },
                { "Id": 4, "Name": "Level", "Type": "Int32" },
                { "Id": 5, "Name": "Code", "Type": "String" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // Closed for writing by the application but for Title. The constructor with values is the
    // application's; the database builds an object with the parameterless one and sets every
    // property through its own set accessor. Not sealed, so that it may have a protected one.
    private class Ticket
    {
        public Ticket()
        {
        }

        public Ticket(long id, string title, string owner, int level, string code)
        {
            Id = id;
            Title = title;
            Owner = owner;
            Level = level;
            Code = code;
        }

        public long Id { get; private set; }

        public string Title { get; set; } = "";

        public string Owner { get; internal set; } = "";

        public int Level { get; protected set; }

        public string Code { get; init; } = "";
    }

    private sealed record InitNote
    {
        public long Id { get; init; }

        public string Text { get; init; } = "";
    }

    private class PrivatelySetStamp
    {
        public PrivatelySetStamp()
        {
        }

        public PrivatelySetStamp(string text, int level)
        {
            Text = text;
            Level = level;
        }

        public string Text { get; private set; } = "";

        public int Level { get; protected set; }
    }

    private sealed class InitStamp
    {
        public InitStamp()
        {
        }

        public InitStamp(string text)
        {
            Text = text;
        }

        public string Text { get; internal set; } = "";

        public int Level { get; init; }
    }

    // Not one of the following has the name of the field Text in its own name, so that a message
    // that names only the class does not name the property.
    private sealed class GetOnlyNote
    {
        public long Id { get; set; }

        public string Text { get; } = "";
    }

    private sealed class ComputedNote
    {
        public long Id { get; set; }

        public string Text => "computed";
    }

    private sealed class InternalGetNote
    {
        public long Id { get; set; }

        public string Text { internal get; set; } = "";
    }

    private class ProtectedGetNote
    {
        public long Id { get; set; }

        public string Text { protected get; set; } = "";
    }

    private sealed class PrivateGetNote
    {
        public long Id { get; set; }

        public string Text { private get; set; } = "";
    }

    private sealed class InternalNote
    {
        public long Id { get; set; }

        internal string Text { get; set; } = "";
    }

    private sealed class GetOnlyStamp
    {
        public string Text { get; } = "";

        public int Level { get; set; }
    }

    private sealed class PrivateGetStamp
    {
        public string Text { private get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class InternalGetStamp
    {
        public string Text { internal get; set; } = "";

        public int Level { get; set; }
    }
}
