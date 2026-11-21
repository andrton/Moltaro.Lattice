using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// A String field with Dictionary storage is laid out as every String field is (brief of Epic 1, sprint
// S13, the planner's decision after the study, point 1): one descriptor of 16 bytes among the
// descriptors of the String and Binary fields, in the order of their identities, which for a Dictionary
// text holds the kind Text, its length and the relative address of its entry. So the layout of an entity
// type is the same whichever storage its String fields select, in a UTF-8 and in a UTF-16 database, and
// so is the most its root block can take with its Plain values inline.
public sealed class DictionaryLayoutTests
{
    private const string Fields =
        "{ 'Id': 2, 'Name': 'Login', 'Type': 'String' }, { 'Id': 3, 'Name': 'Region', 'Type': 'String'STORAGE }, { 'Id': 4, 'Name': 'Count', 'Type': 'Int32' }, { 'Id': 5, 'Name': 'Zone', 'Type': 'String', 'Nullable': true STORAGE }";

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void String_field_with_Dictionary_storage_has_the_descriptor_of_16_bytes_every_String_field_has(string encoding)
    {
        var dictionary = LayoutOf(", 'StringStorage': 'Dictionary'", encoding);
        var plain = LayoutOf("", encoding);

        // 32 + 1 + 8 + 4 + 3 × 16 bytes.
        Assert.Equal(93, dictionary.FixedSize);
        Assert.Equal(plain.FixedSize, dictionary.FixedSize);
        Assert.Equal(plain.DescriptorOffset, dictionary.DescriptorOffset);
        Assert.Equal(3, dictionary.DescriptorCount);

        foreach (var name in new[] { "Login", "Region", "Count", "Zone" })
        {
            Assert.True(dictionary.EntityType.TryGetField(name, out var field));
            Assert.True(plain.EntityType.TryGetField(name, out var plainField));

            var stored = dictionary.StoredFieldOf(field);
            var plainStored = plain.StoredFieldOf(plainField);

            Assert.Equal((plainStored.Type, plainStored.Width, plainStored.Offset, plainStored.NullBit), (stored.Type, stored.Width, stored.Offset, stored.NullBit));
        }

        Assert.Equal(StoredTypeEnum.String, dictionary.DescriptorField(1).Type);
        Assert.Equal(ValueBlocks.DescriptorSize, dictionary.DescriptorField(1).Width);
    }

    private static RootLayout LayoutOf(string storage, string encoding)
    {
        var schema = ItemSchema.Compile(Fields.Replace("STORAGE", storage, StringComparison.Ordinal), encoding);

        Assert.True(schema.TryGetEntityType(ItemSchema.EntityTypeName, out var item));
        return RootLayout.Compile(item, schema);
    }
}
