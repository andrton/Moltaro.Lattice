namespace Moltaro.Lattice.Tests.Binding;

// By the owner's answer to Q29, as consequences of the rule of Q14 (the remarks of
// ILatticeDatabase.Set<TEntity>(); brief of Epic 1, sprint S06): the accessors of the property of a
// field are those C# sees for the property on the class, inherited ones included. A class that
// overrides only the get accessor of a virtual property binds, and its value is set through the
// accessor of its base class; one that overrides only the set accessor is read through the get
// accessor of its base class in the same way. When the public member C# finds on the class by the
// name of a field is not a property, a public field that hides a property of a base class, the class
// is BindingFailed with the member named: the hidden property is not bound in its place. Both rules
// hold for the class of an owned type as for the class of an entity type.
//
// Both are fixes of the binding in the core library. Until they are made, a class that overrides one
// accessor is BindingFailed and a hidden property binds; these tests are skipped with their own
// reason.
public sealed class InheritedMemberTests
{
    [Theory(Skip = NotImplemented.BindingFixes)]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Class_that_overrides_only_the_get_accessor_binds_and_is_set_through_the_accessor_of_its_base_class(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, AccessorSchema.Note<GetterOverridingNote>());
        var notes = database.Set<GetterOverridingNote>();
        var records = database.Set("Note");

        // A stored value reaches the object through the set accessor of the base class, and the
        // object shows it through its own get accessor.
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Text"] = "stored" });

        var read = notes.Get(1L)!;
        Assert.Equal("stored", read.LastSetThroughBase);
        Assert.Equal("STORED", read.Text);

        // A write takes the value through the get accessor that C# sees, the overriding one.
        await notes.InsertAsync(new GetterOverridingNote { Id = 2, Text = "written" });

        Assert.Equal("WRITTEN", records.Get(2L)!["Text"]);
    }

    [Theory(Skip = NotImplemented.BindingFixes)]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Class_that_overrides_only_the_set_accessor_binds_and_is_read_through_the_accessor_of_its_base_class(DatabaseImplementationEnum implementation)
    {
        await using var database = await TestDatabases.OpenMasterAsync(implementation, AccessorSchema.Note<SetterOverridingNote>());
        var notes = database.Set<SetterOverridingNote>();
        var records = database.Set("Note");

        // A stored value reaches the object through the overriding set accessor, which trims it.
        await records.InsertAsync(new LatticeRecord(records.EntityType) { ["Id"] = 1L, ["Text"] = "  stored  " });

        var read = notes.Get(1L)!;
        Assert.Equal("stored", read.LastSetThroughBase);
        Assert.Equal("stored", read.Text);

        // A write takes the value through the get accessor of the base class.
        await notes.InsertAsync(new SetterOverridingNote { Id = 2, Text = "  written  " });

        Assert.Equal("written", records.Get(2L)!["Text"]);
    }

    [Theory(Skip = NotImplemented.BindingFixes)]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Class_of_an_owned_type_that_overrides_one_accessor_binds(DatabaseImplementationEnum implementation)
    {
        await AccessorSchema.ExpectBoundAsync(
            implementation,
            AccessorSchema.Stamped<GetterOverridingStamp>(),
            new BindingSchema.Holder<GetterOverridingStamp> { Id = 1, Value = new GetterOverridingStamp { Text = "sealed" } });

        await AccessorSchema.ExpectBoundAsync(
            implementation,
            AccessorSchema.Stamped<SetterOverridingStamp>(),
            new BindingSchema.Holder<SetterOverridingStamp> { Id = 1, Value = new SetterOverridingStamp { Text = "sealed" } });
    }

    [Theory(Skip = NotImplemented.BindingFixes)]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Public_field_that_hides_a_property_of_the_base_class_is_refused_with_the_member_named(DatabaseImplementationEnum implementation)
    {
        await AccessorSchema.ExpectNotBoundAsync(implementation, AccessorSchema.Note<FieldHidingNote>(), new FieldHidingNote { Id = 1 });
    }

    [Theory(Skip = NotImplemented.BindingFixes)]
    [MemberData(nameof(TestDatabases.Implementations), MemberType = typeof(TestDatabases))]
    public async Task Public_field_that_hides_a_property_in_the_class_of_an_owned_type_is_refused_with_the_member_named(DatabaseImplementationEnum implementation)
    {
        await AccessorSchema.ExpectNotBoundAsync(
            implementation,
            AccessorSchema.Stamped<FieldHidingStamp>(),
            new BindingSchema.Holder<FieldHidingStamp> { Id = 1, Value = new FieldHidingStamp() });
    }

    // Not one of the following has the name of the field Text in its own name, so that a message
    // that names only the class does not name the member.
    private class VirtualNote
    {
        private string _value = "";

        public long Id { get; set; }

        // The value the set accessor of this class was given last. Not a field of the schema, so the
        // binding does not look at it.
        public string? LastSetThroughBase { get; private set; }

        public virtual string Text
        {
            get => _value;
            set
            {
                _value = value;
                LastSetThroughBase = value;
            }
        }
    }

    // C# sees the get accessor declared here and the set accessor of the base class.
    private sealed class GetterOverridingNote : VirtualNote
    {
        public override string Text => base.Text.ToUpperInvariant();
    }

    // C# sees the set accessor declared here and the get accessor of the base class.
    private sealed class SetterOverridingNote : VirtualNote
    {
        public override string Text
        {
            set => base.Text = value.Trim();
        }
    }

    private class PlainNote
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }

    // C# finds the field by the name Text, not the property of the base class.
    private sealed class FieldHidingNote : PlainNote
    {
        public new string Text = "";
    }

    private class VirtualStamp
    {
        public virtual string Text { get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class GetterOverridingStamp : VirtualStamp
    {
        public override string Text => base.Text.ToUpperInvariant();
    }

    private sealed class SetterOverridingStamp : VirtualStamp
    {
        public override string Text
        {
            set => base.Text = value.Trim();
        }
    }

    private class PlainStamp
    {
        public string Text { get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class FieldHidingStamp : PlainStamp
    {
        public new string Text = "";
    }
}
