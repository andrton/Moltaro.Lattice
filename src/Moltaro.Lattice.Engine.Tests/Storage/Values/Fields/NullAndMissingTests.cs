using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Fields;

// Null and missing values (brief of S05, "Null and missing"; Q03.2). A nullable field of every type
// holds null, set by SetNull or never set; SetNull of a field that is not nullable is
// ValidationFailed at the call; a field that is not nullable and was never set is ValidationFailed
// at Complete. A Get… member and GetLength of a null field are InvalidOperationException: the
// caller asks IsNull first. An empty String or Binary is a value, not null. The entity type has a
// field of every type, see EveryType.
public sealed class NullAndMissingTests
{
    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_nullable_field_of_every_type_set_to_null_reads_back_null()
    {
        using var item = EveryType.Open();

        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);

        foreach (var name in EveryType.Names)
        {
            writer.SetNull(item.Field(name + "OrNull"));
        }

        var root = writer.Complete();

        foreach (var name in EveryType.Names)
        {
            Assert.True(item.Reader(root).IsNull(item.Field(name + "OrNull")), $"{name}OrNull is not null.");
            Assert.False(item.Reader(root).IsNull(item.Field(name)), $"{name} is null.");
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_nullable_field_that_was_never_set_is_null()
    {
        using var item = EveryType.Open();

        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);
        var root = writer.Complete();

        foreach (var name in EveryType.Names)
        {
            Assert.True(item.Reader(root).IsNull(item.Field(name + "OrNull")), $"{name}OrNull is not null.");
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_nullable_field_that_was_set_is_not_null()
    {
        using var item = EveryType.Open();

        var root = EveryType.WriteFull(item);

        foreach (var field in item.EntityType.Fields)
        {
            Assert.False(item.Reader(root).IsNull(field), $"{field.Name} is null.");
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void SetNull_of_a_field_that_is_not_nullable_is_ValidationFailed_at_the_call()
    {
        using var item = EveryType.Open();

        foreach (var field in item.EntityType.Fields.Where(field => !field.Declaration.IsNullable))
        {
            var writer = item.Writer();

            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetNull(field));

            writer.Abandon();
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_field_that_is_not_nullable_and_was_never_set_is_ValidationFailed_at_Complete()
    {
        using var item = EveryType.Open();

        foreach (var field in item.EntityType.Fields.Where(field => !field.Declaration.IsNullable))
        {
            var before = StoredBytes.In(item.Arena);
            var writer = item.Writer();
            EveryType.SetRequired(ref writer, item, field.Name);

            WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.ValidationFailed);

            // A failed Complete frees everything the writer allocated.
            Assert.Equal(before, StoredBytes.In(item.Arena));
        }
    }

    [Fact(Skip = NotImplemented.RootLayout)]
    public void A_Get_member_and_GetLength_of_a_null_field_are_InvalidOperationException()
    {
        using var item = EveryType.Open();
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);
        var root = writer.Complete();

        foreach (var (member, get, serves) in ReaderMembers.Getters)
        {
            foreach (var field in item.EntityType.Fields.Where(field => field.Declaration.IsNullable && serves(field.Declaration)))
            {
                var exception = Record.Exception(() => get(item.Reader(root), field));

                Assert.True(exception?.GetType() == typeof(InvalidOperationException), $"{member} of the null field {field.Name} threw {exception?.GetType().Name ?? "nothing"}.");
            }
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void An_empty_String_or_Binary_is_a_value_and_not_null(string encoding)
    {
        using var item = EveryType.Open(encoding);
        var writer = item.Writer();
        EveryType.SetRequired(ref writer, item);
        writer.SetString(item.Field("StringOrNull"), "");
        writer.SetBinary(item.Field("BinaryOrNull"), []);
        var reader = item.Reader(writer.Complete());

        Assert.False(reader.IsNull(item.Field("StringOrNull")));
        Assert.Equal("", reader.GetString(item.Field("StringOrNull")));
        Assert.Equal(0, reader.GetLength(item.Field("StringOrNull")));
        Assert.False(reader.IsNull(item.Field("BinaryOrNull")));
        Assert.Empty(reader.GetBinary(item.Field("BinaryOrNull")));
        Assert.Equal(0, reader.GetLength(item.Field("BinaryOrNull")));
    }
}
