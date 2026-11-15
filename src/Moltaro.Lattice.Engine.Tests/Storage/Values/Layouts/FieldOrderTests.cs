using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// The fixed part lays out the fields in the order of their identities, not in the order the
// descriptor declares them (brief of S05, "The fixed part"; milestone contract, "stable field-ID
// order"). No member shows where a field lies, so the tests see it as far as they can: two entity
// types with the same fields declared in another order have the same fixed size, and a root written
// with the layout of one reads back the same values with the layout of the other. The second rests
// on the contract of the schema: entity types with the same layout fingerprint are stored alike
// (LatticeEntityType.LayoutFingerprint), and the order of declaration is not part of it.
public sealed class FieldOrderTests
{
    // Fields of several widths, nullable and not, with descriptors among them, so that two fields
    // that changed places would be read at the wrong offset, with the wrong bit or the wrong
    // descriptor.
    private const string InIdentityOrder = """
        { 'Id': 2, 'Name': 'Small', 'Type': 'Int8' },
        { 'Id': 3, 'Name': 'Wide', 'Type': 'Int32', 'Nullable': true },
        { 'Id': 4, 'Name': 'Medium', 'Type': 'Int16' },
        { 'Id': 5, 'Name': 'Text', 'Type': 'String' },
        { 'Id': 6, 'Name': 'Flag', 'Type': 'Boolean', 'Nullable': true },
        { 'Id': 7, 'Name': 'Data', 'Type': 'Binary', 'Nullable': true },
        { 'Id': 8, 'Name': 'Count', 'Type': 'UInt16', 'Nullable': true },
        { 'Id': 9, 'Name': 'Label', 'Type': 'String', 'Nullable': true }
        """;

    // The same fields, declared in another order. Id stays first only because ItemSchema writes it
    // first.
    private const string InAnotherOrder = """
        { 'Id': 9, 'Name': 'Label', 'Type': 'String', 'Nullable': true },
        { 'Id': 7, 'Name': 'Data', 'Type': 'Binary', 'Nullable': true },
        { 'Id': 8, 'Name': 'Count', 'Type': 'UInt16', 'Nullable': true },
        { 'Id': 5, 'Name': 'Text', 'Type': 'String' },
        { 'Id': 6, 'Name': 'Flag', 'Type': 'Boolean', 'Nullable': true },
        { 'Id': 4, 'Name': 'Medium', 'Type': 'Int16' },
        { 'Id': 2, 'Name': 'Small', 'Type': 'Int8' },
        { 'Id': 3, 'Name': 'Wide', 'Type': 'Int32', 'Nullable': true }
        """;

    [Fact(Skip = NotImplemented.RootLayout)]
    public void The_fixed_size_does_not_depend_on_the_order_of_declaration()
    {
        var first = Layout(InIdentityOrder);
        var second = Layout(InAnotherOrder);

        Assert.Equal(first.FixedSize, second.FixedSize);
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_root_written_with_one_order_of_declaration_reads_back_with_another()
    {
        using var written = RootArena.Of(InIdentityOrder);
        var otherSchema = ItemSchema.Compile(InAnotherOrder);
        Assert.True(otherSchema.TryGetEntityType(ItemSchema.EntityTypeName, out var other));
        var otherLayout = RootLayout.Compile(other, otherSchema);

        // The precondition the test rests on: the two are stored alike by the contract of the schema.
        Assert.Equal(written.EntityType.LayoutFingerprint, other.LayoutFingerprint);
        Assert.NotEqual(written.EntityType.Fields.Select(field => field.Id), other.Fields.Select(field => field.Id));

        var writer = written.Writer();
        writer.SetInteger(written.Field("Id"), 42);
        writer.SetInteger(written.Field("Small"), -7);
        writer.SetNull(written.Field("Wide"));
        writer.SetInteger(written.Field("Medium"), 30_000);
        writer.SetString(written.Field("Text"), "in order");
        writer.SetBoolean(written.Field("Flag"), true);
        writer.SetBinary(written.Field("Data"), [9, 8, 7]);
        writer.SetNull(written.Field("Count"));
        writer.SetString(written.Field("Label"), "label");
        var root = writer.Complete();

        var reader = new RootReader(written.Arena, otherLayout, root);

        Assert.Equal(42, reader.GetInteger(FieldOf(other, "Id")));
        Assert.Equal(-7, reader.GetInteger(FieldOf(other, "Small")));
        Assert.True(reader.IsNull(FieldOf(other, "Wide")));
        Assert.Equal(30_000, reader.GetInteger(FieldOf(other, "Medium")));
        Assert.Equal("in order", reader.GetString(FieldOf(other, "Text")));
        Assert.False(reader.IsNull(FieldOf(other, "Flag")));
        Assert.True(reader.GetBoolean(FieldOf(other, "Flag")));
        Assert.Equal([9, 8, 7], reader.GetBinary(FieldOf(other, "Data")));
        Assert.True(reader.IsNull(FieldOf(other, "Count")));
        Assert.Equal("label", reader.GetString(FieldOf(other, "Label")));
    }

    private static RootLayout Layout(string fields)
    {
        var schema = ItemSchema.Compile(fields);
        Assert.True(schema.TryGetEntityType(ItemSchema.EntityTypeName, out var item));

        return RootLayout.Compile(item, schema);
    }

    private static LatticeField FieldOf(LatticeEntityType entityType, string name)
    {
        Assert.True(entityType.TryGetField(name, out var field));

        return field;
    }
}
