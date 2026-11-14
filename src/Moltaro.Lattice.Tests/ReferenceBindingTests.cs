namespace Moltaro.Lattice.Tests;

// In a bound class a reference is a property of the key type of its target, Nullable<> of it
// when the reference is nullable and the key is a value type. The class of the target does
// not stand for its key.
public sealed class ReferenceBindingTests
{
    // Group has a Guid key; Holder, which refers to itself, an Int64 key.
    private const string ToGroup = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Reference', 'EntityType': 'Group' }";
    private const string ToNullableGroup = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Reference', 'EntityType': 'Group', 'Nullable': true }";
    private const string ToNullableHolder = "{ 'Id': 2, 'Name': 'Value', 'Type': 'Reference', 'EntityType': 'Holder', 'Nullable': true }";

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Reference_binds_as_the_key_type_of_its_target()
    {
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<Guid>>(ToGroup);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<Guid?>>(ToNullableGroup);
        await BindingSchema.ExpectBoundAsync<BindingSchema.Holder<long?>>(ToNullableHolder);
    }

    [Fact(Skip = NotImplemented.SchemaValues)]
    public async Task Reference_as_another_type_is_refused_with_the_property_named()
    {
        // Another key type, the class of the target, nullability that does not fit.
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<string>>(ToGroup, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<int?>>(ToNullableHolder, "Value");
        await BindingSchema.ExpectNotBoundAsync<SelfReferencing>(ToNullableHolder, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Guid?>>(ToGroup, "Value");
        await BindingSchema.ExpectNotBoundAsync<BindingSchema.Holder<Guid>>(ToNullableGroup, "Value");
    }

    // The class of the entity type Holder, which the reference names.
    private sealed class SelfReferencing
    {
        public long Id { get; set; }

        public SelfReferencing? Value { get; set; }
    }
}
