namespace Moltaro.Lattice.Tests.Binding;

// In a bound class an owned value is an object of the class that its owned type names, at
// every position: a property, an element of a List or an array, a value of a Dictionary. The
// class of an owned type is bound as the class of an entity type is: a public parameterless
// constructor and, for every field, the property C# finds by that name, an instance property
// of the type the field needs with a public get accessor and a set accessor of any accessibility
// (SetAccessorTests); its own owned values and collections follow
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
    private const string OwnedInsignia = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Owned', 'OwnedType': 'Insignia' }";
    private const string ListOfInsignia = "{ 'Id': 2, 'Name': 'Value', 'Type': 'List', 'Element': { 'Type': 'Owned', 'OwnedType': 'Insignia' } }";
    private const string MapOfInsignia = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Map', 'Key': { 'Type': 'String' }, 'Value': { 'Type': 'Owned', 'OwnedType': 'Insignia' } }";

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
        // As a property, as an element and as a value. The owned type Insignia names the class
        // Emblem, so that a message that names only the class does not name the owned type.
        await ExpectHolderNotBoundAsync<BindingSchema.Holder<LatticeOwnedValue>>(InsigniaSchema<BindingSchema.Holder<LatticeOwnedValue>>(OwnedInsignia), "Value", "Insignia");
        await ExpectHolderNotBoundAsync<BindingSchema.Holder<List<LatticeOwnedValue>>>(InsigniaSchema<BindingSchema.Holder<List<LatticeOwnedValue>>>(ListOfInsignia), "Value", "Insignia");
        await ExpectHolderNotBoundAsync<BindingSchema.Holder<Dictionary<string, LatticeOwnedValue>>>(InsigniaSchema<BindingSchema.Holder<Dictionary<string, LatticeOwnedValue>>>(MapOfInsignia), "Value", "Insignia");

        // The class it names binds.
        await using var database = await TestData.OpenMasterAsync(schema: InsigniaSchema<BindingSchema.Holder<Emblem>>(OwnedInsignia));
        Assert.Equal("Holder", database.Set<BindingSchema.Holder<Emblem>>().EntityType.Name);
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
        await ExpectBadgeNotBoundAsync<IncompleteBadge>("Level");
        await ExpectBadgeNotBoundAsync<MistypedBadge>("Text");
        await ExpectBadgeNotBoundAsync<GetOnlyBadge>("Text");

        // By the rule of how a property is found: a static one, and a hiding one of another
        // type than the field needs.
        await ExpectBadgeNotBoundAsync<StaticBadge>("Text");
        await ExpectBadgeNotBoundAsync<MistypedHidingBadge>("Text");
    }

    [Fact]
    public async Task Class_of_an_owned_type_with_an_indexer_or_a_property_without_a_public_get_is_refused_with_the_property_named()
    {
        await ExpectBadgeNotBoundAsync<IndexerBadge>("Text");
        await ExpectBadgeNotBoundAsync<PrivateGetBadge>("Text");
    }

    [Fact]
    public async Task Class_of_an_owned_type_without_a_public_parameterless_constructor_is_refused()
    {
        await ExpectBadgeNotBoundAsync<BadgeWithoutParameterlessConstructor>(nameof(BadgeWithoutParameterlessConstructor));
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
    private static Task ExpectBadgeNotBoundAsync<TBadge>(params string[] named) =>
        ExpectHolderNotBoundAsync<BindingSchema.Holder<TBadge>>(BadgeSchema<TBadge>(), named);

    // The owned type Insignia, bound to the class Emblem, held by the given field of Holder,
    // which is bound to THolder.
    private static LatticeSchema InsigniaSchema<THolder>(string field) => LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            { "Id": 1, "Name": "Insignia", "ClrType": "{{typeof(Emblem).FullName}}", "Fields": [ { "Id": 1, "Name": "Text", "Type": "String" } ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Holder", "ClrType": "{{typeof(THolder).FullName}}",
              "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, {{field.Replace('\'', '"')}} ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    // The holder is refused, from a set and from the typed members of a write batch, with the
    // given names in the message.
    private static async Task ExpectHolderNotBoundAsync<THolder>(LatticeSchema schema, params string[] named)
        where THolder : class, new()
    {
        await using var database = await TestData.OpenMasterAsync(schema: schema);
        var batch = database.CreateWriteBatch();

        ExpectBindingFailed(() => database.Set<THolder>());
        ExpectBindingFailed(() => batch.Insert(new THolder()));
        ExpectBindingFailed(() => batch.Upsert(new THolder()));
        ExpectBindingFailed(() => batch.Delete<THolder>(1L));
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

    private sealed class MistypedHidingBadge : BadgeBase
    {
        public new int Text { get; set; }
    }

    private sealed class StaticBadge
    {
        public static string Text { get; set; } = "";

        public int Level { get; set; }
    }

    private sealed class IncompleteBadge
    {
        public string Text { get; set; } = "";
    }

    private sealed class MistypedBadge
    {
        public int Text { get; set; }

        public int Level { get; set; }
    }

    private sealed class GetOnlyBadge
    {
        public string Text { get; } = "";

        public int Level { get; set; }
    }

    private sealed class BadgeWithoutParameterlessConstructor(string text)
    {
        public string Text { get; set; } = text;

        public int Level { get; set; }
    }

    // Not one of the following has the name of a field of Badge in its own name, so that a
    // message that names only the class does not name the property.
    private sealed class IndexerBadge
    {
        [System.Runtime.CompilerServices.IndexerName("Text")]
        public string this[int index]
        {
            get => "";
            set { }
        }

        public int Level { get; set; }
    }

    private sealed class PrivateGetBadge
    {
        public string Text { private get; set; } = "";

        public int Level { get; set; }
    }

    // The class of the owned type Insignia, whose name it does not repeat.
    private sealed class Emblem
    {
        public string Text { get; set; } = "";
    }
}
