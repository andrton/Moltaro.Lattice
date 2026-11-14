namespace Moltaro.Lattice.Tests.Binding;

// The property of a field is found as C# finds it by that name on the class: the public
// property of the most derived class that declares one. It is an instance property with get
// and set, of the type the field needs. A class that does not agree is refused with
// BindingFailed and the property named, from Set<TEntity>() and from the typed members of a
// write batch alike, and never with another exception.
public sealed class PropertyLookupTests
{
    [Fact]
    public async Task Class_with_inherited_properties_binds_and_is_read_and_written_through_them()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<InheritingNote>());
        var notes = database.Set<InheritingNote>();

        await notes.InsertAsync(new InheritingNote { Id = 1, Text = "inherited" });

        Assert.Equal("inherited", notes.Get(1L)!.Text);
    }

    [Fact]
    public async Task Hiding_property_of_the_type_the_field_needs_binds_and_holds_the_field()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<HidingNote>());
        var notes = database.Set<HidingNote>();
        var note = new HidingNote { Id = 1, Text = "derived" };
        ((NoteBase)note).Text = "base";

        await notes.InsertAsync(note);

        // The field is read from the hiding property and written to it.
        var read = notes.Get(1L)!;
        Assert.Equal("derived", read.Text);
        Assert.Equal("", ((NoteBase)read).Text);
    }

    [Fact]
    public async Task Hiding_property_of_the_type_the_field_needs_binds_where_the_base_has_another_type()
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<HidingNumberNote>());
        var notes = database.Set<HidingNumberNote>();

        await notes.InsertAsync(new HidingNumberNote { Id = 1, Text = "derived" });

        var read = notes.Get(1L)!;
        Assert.Equal("derived", read.Text);
        Assert.Equal(0, ((NumberNoteBase)read).Text);
    }

    [Fact]
    public async Task Hiding_property_of_a_type_the_field_does_not_accept_is_refused_with_the_property_named()
    {
        // The base has a String Text, as the field needs; the most derived class has an Int32.
        await ExpectNotBoundAsync(new MistypedHidingNote { Id = 1 });
    }

    [Fact]
    public async Task Static_property_is_refused_with_the_property_named()
    {
        await ExpectNotBoundAsync(new StaticNote { Id = 1 });
    }

    [Fact]
    public async Task Static_property_that_hides_an_instance_property_is_refused_with_the_property_named()
    {
        await ExpectNotBoundAsync(new StaticHidingNote { Id = 1 });
    }

    [Fact]
    public async Task Class_without_a_property_for_a_field_is_refused_with_the_property_named()
    {
        await ExpectNotBoundAsync(new IncompleteNote { Id = 1 });
    }

    [Fact]
    public async Task Indexer_named_like_a_field_is_refused_with_the_property_named()
    {
        // An indexer is a property of that name to reflection, and holds no field.
        await ExpectNotBoundAsync(new IndexerNote { Id = 1 });
    }

    [Fact]
    public async Task Property_without_a_public_get_is_refused_with_the_property_named()
    {
        await ExpectNotBoundAsync(new PrivateGetNote { Id = 1 });
    }

    [Fact]
    public async Task Property_without_a_set_is_refused_with_the_property_named()
    {
        await ExpectNotBoundAsync(new GetOnlyNote { Id = 1 });
    }

    [Fact]
    public async Task Class_without_a_public_parameterless_constructor_is_refused_with_the_class_named()
    {
        await ExpectNotBoundAsync(new NoteWithoutDefaultConstructor("made by the test") { Id = 1 }, nameof(NoteWithoutDefaultConstructor));
    }

    // The class is refused through a set and through every typed member of a batch, each time
    // with BindingFailed and, unless another name is given, the property named.
    private static async Task ExpectNotBoundAsync<TNote>(TNote note, string named = "Text")
        where TNote : class
    {
        await using var database = await TestData.OpenMasterAsync(schema: SchemaFor<TNote>());
        var batch = database.CreateWriteBatch();

        ExpectBindingFailed(() => database.Set<TNote>());
        ExpectBindingFailed(() => batch.Insert(note));
        ExpectBindingFailed(() => batch.Replace(note));
        ExpectBindingFailed(() => batch.Upsert(note));
        ExpectBindingFailed(() => batch.Delete<TNote>(1L));
        Assert.Equal(0, batch.Count);

        void ExpectBindingFailed(Action bind)
        {
            var exception = Assert.Throws<LatticeException>(bind);
            Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
            Assert.Contains(named, exception.Message);
        }
    }

    private static LatticeSchema SchemaFor<TNote>() => LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Note", "ClrType": "{{typeof(TNote).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Text", "Type": "String" } ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private class NoteBase
    {
        public long Id { get; set; }

        public string Text { get; set; } = "";
    }

    private sealed class InheritingNote : NoteBase;

    private sealed class HidingNote : NoteBase
    {
        public new string Text { get; set; } = "";
    }

    private sealed class MistypedHidingNote : NoteBase
    {
        public new int Text { get; set; }
    }

    private sealed class StaticHidingNote : NoteBase
    {
        public static new string Text { get; set; } = "";
    }

    private class NumberNoteBase
    {
        public long Id { get; set; }

        public int Text { get; set; }
    }

    private sealed class HidingNumberNote : NumberNoteBase
    {
        public new string Text { get; set; } = "";
    }

    private sealed class StaticNote
    {
        public long Id { get; set; }

        public static string Text { get; set; } = "";
    }

    // Not one of the following has the name of the field in its own name, so that a message
    // that names only the class does not name the property.
    private sealed class IncompleteNote
    {
        public long Id { get; set; }
    }

    private sealed class IndexerNote
    {
        public long Id { get; set; }

        [System.Runtime.CompilerServices.IndexerName("Text")]
        public string this[int index]
        {
            get => "";
            set { }
        }
    }

    private sealed class PrivateGetNote
    {
        public long Id { get; set; }

        public string Text { private get; set; } = "";
    }

    private sealed class GetOnlyNote
    {
        public long Id { get; set; }

        public string Text { get; } = "";
    }

    private sealed class NoteWithoutDefaultConstructor(string text)
    {
        public long Id { get; set; }

        public string Text { get; set; } = text;
    }
}
