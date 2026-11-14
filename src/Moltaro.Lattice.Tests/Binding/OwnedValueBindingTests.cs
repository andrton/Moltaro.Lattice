namespace Moltaro.Lattice.Tests.Binding;

// In a bound class an owned value is an object of the class that its owned type names, at
// every position: a property, an element of a List or an array, a value of a Dictionary. The
// class of an owned type is bound as the class of an entity type is: a public parameterless
// constructor and, for every field, the property C# finds by that name, an instance property
// with get and set of the type the field needs; its own owned values and collections follow
// the same rules. An owned type that names no class has no form in a class, and an owned value
// without a class, LatticeOwnedValue, is for records only: both are refused with BindingFailed,
// the property and the owned type named.
public sealed class OwnedValueBindingTests
{
    private const string OwnedAddress = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Address' }";
    private const string NullableOwnedAddress = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }";
    private const string ListOfAddresses = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Address' } }";
    private const string MapOfAddresses = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Address' } }";
    private const string OwnedNote = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Note', 'Nullable': true }";
    private const string ListOfNotes = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Note' } }";
    private const string MapOfNotes = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Map', 'Key': { 'Type': 'Int32' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Note' } }";
    private const string OwnedEnvelope = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Envelope' }";
    private const string OwnedBadge = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Badge' }";

    [Fact]
    public async Task Owned_value_binds_as_the_class_of_its_owned_type_with_its_own_owned_values_and_collections()
    {
        // Address holds a Point, a List of strings and an array of Points.
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<BindingSchema.Address>>(OwnedAddress);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<BindingSchema.Address?>>(NullableOwnedAddress);
    }

    [Fact]
    public async Task Owned_values_in_a_List_or_a_Map_bind_as_the_class_of_their_owned_type()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<List<BindingSchema.Address>>>(ListOfAddresses);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<BindingSchema.Address[]>>(ListOfAddresses);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<Dictionary<string, BindingSchema.Address>>>(MapOfAddresses);
    }

    [Fact]
    public async Task Owned_value_as_another_class_is_refused_with_the_property_named()
    {
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<BindingSchema.Point>>(OwnedAddress, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<object>>(OwnedAddress, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<string, object?>>>(OwnedAddress, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<BindingSchema.Point>>>(ListOfAddresses, "Value");
    }

    [Fact]
    public async Task LatticeOwnedValue_in_a_class_is_refused_with_the_property_and_the_owned_type_named()
    {
        // As a property, as an element and as a value.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<LatticeOwnedValue>>(OwnedAddress, "Value", "Address");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<LatticeOwnedValue>>>(ListOfAddresses, "Value", "Address");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<string, LatticeOwnedValue>>>(MapOfAddresses, "Value", "Address");
    }

    [Fact]
    public async Task Owned_type_without_a_class_is_refused_at_every_position_with_the_property_and_the_owned_type_named()
    {
        // Whatever the property is declared as.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<object>>(OwnedNote, "Value", "Note");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<LatticeOwnedValue>>(OwnedNote, "Value", "Note");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<List<object>>>(ListOfNotes, "Value", "Note");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Dictionary<int, LatticeOwnedValue>>>(MapOfNotes, "Value", "Note");

        // Inside the class of another owned type: Envelope has a class, its field Memo holds a
        // Note.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<BindingSchema.Envelope>>(OwnedEnvelope, "Memo", "Note");
    }

    [Fact]
    public async Task Record_holds_an_owned_value_whose_owned_type_names_no_class()
    {
        // A class cannot hold a Note; a record can, as it can any owned value.
        await using var database = await TestData.OpenMasterAsync(schema: BindingSchema.For<BindingSchema.Holder<object>>(OwnedNote));
        var holders = database.Set("Holder");
        Assert.True(database.Schema.TryGetOwnedType("Note", out var note));

        var record = new LatticeRecord(holders.EntityType) { ["Id"] = 1L, ["Value"] = new LatticeOwnedValue(note) { ["Text"] = "kept" } };

        Assert.Equal("kept", Assert.IsType<LatticeOwnedValue>(record["Value"])["Text"]);
    }

    [Fact]
    public async Task Class_of_an_owned_type_with_inherited_or_hiding_properties_of_the_type_the_field_needs_binds()
    {
        await ExpectBadgeBoundAsync<InheritingBadge>();
        await ExpectBadgeBoundAsync<HidingBadge>();
        await ExpectBadgeBoundAsync<HidingNumberBadge>();
    }

    [Fact]
    public async Task Class_of_an_owned_type_that_does_not_agree_with_it_is_refused_with_the_property_named()
    {
        // No property for a field, a property of another type, a property without set.
        await ExpectBadgeNotBoundAsync<BadgeWithoutLevel>("Level");
        await ExpectBadgeNotBoundAsync<BadgeWithTextAsNumber>("Text");
        await ExpectBadgeNotBoundAsync<BadgeWithReadOnlyText>("Text");

        // By the rule of how a property is found: a static one, and a hiding one of another
        // type than the field needs.
        await ExpectBadgeNotBoundAsync<BadgeWithStaticText>("Text");
        await ExpectBadgeNotBoundAsync<HidingTextWithNumberBadge>("Text");
    }

    [Fact]
    public async Task Class_of_an_owned_type_without_a_public_parameterless_constructor_is_refused()
    {
        await ExpectBadgeNotBoundAsync<BadgeWithoutParameterlessConstructor>();
    }

    // The owned type Badge, bound to TBadge, held by the field Value of Holder.
    private static LatticeSchema BadgeSchema<TBadge>() => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Badge", "ClrType": "{{typeof(TBadge).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" }, { "Id": 2, "Name": "Level", "Type": "Int32" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(BindingSchema.Holder<TBadge>).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, {{OwnedBadge.Replace('\'', '"')}} ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    private static async Task ExpectBadgeBoundAsync<TBadge>()
    {
        await using var database = await TestData.OpenMasterAsync(schema: BadgeSchema<TBadge>());

        Assert.Equal("Holder", database.Set<BindingSchema.Holder<TBadge>>().EntityType.Name);
    }

    // The holder is refused, from a set and from the typed members of a write batch, because
    // of the class of its owned value, with the given names in the message.
    private static async Task ExpectBadgeNotBoundAsync<TBadge>(params string[] named)
    {
        await using var database = await TestData.OpenMasterAsync(schema: BadgeSchema<TBadge>());
        var batch = database.CreateWriteBatch();

        ExpectBindingFailed(() => database.Set<BindingSchema.Holder<TBadge>>());
        ExpectBindingFailed(() => batch.Insert(new BindingSchema.Holder<TBadge> { Id = 1 }));
        ExpectBindingFailed(() => batch.Upsert(new BindingSchema.Holder<TBadge> { Id = 1 }));
        ExpectBindingFailed(() => batch.Delete<BindingSchema.Holder<TBadge>>(1L));
        Assert.Equal(0, batch.Count);

        void ExpectBindingFailed(Action bind)
        {
            var exception = Assert.Throws<LatticeException>(bind);
            Assert.Equal(LatticeErrorEnum.BindingFailed, exception.Error);
            Assert.All(named, name => Assert.Contains(name, exception.Message));
        }
    }

    private class BadgeBase
    {
        public string Text { get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class InheritingBadge : BadgeBase;

    private sealed class HidingBadge : BadgeBase
    {
        public new string Text { get; set; } = "";
    }

    private class NumberBadgeBase
    {
        public int Text { get; set; }

        public int Level { get; set; }
    }

    private sealed class HidingNumberBadge : NumberBadgeBase
    {
        public new string Text { get; set; } = "";
    }

    private sealed class HidingTextWithNumberBadge : BadgeBase
    {
        public new int Text { get; set; }
    }

    private sealed class BadgeWithStaticText
    {
        public static string Text { get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class BadgeWithoutLevel
    {
        public string Text { get; set; } = "";
    }

    private sealed class BadgeWithTextAsNumber
    {
        public int Text { get; set; }

        public int Level { get; set; }
    }

    private sealed class BadgeWithReadOnlyText
    {
        public string Text { get; } = "";

        public int Level { get; set; }
    }

    private sealed class BadgeWithoutParameterlessConstructor(string text)
    {
        public string Text { get; set; } = text;

        public int Level { get; set; }
    }
}
