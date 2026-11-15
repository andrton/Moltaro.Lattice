using System.Text;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Text;

// Text is preserved byte for byte in a UTF-8 and in a UTF-16 database (brief of S05, Acceptance and
// "Text"; Q04.1, Q04.5): in a UTF-8 database a String is stored as the UTF-8 of its characters, in
// a UTF-16 database as its code units, and GetLength is the number of encoded bytes as stored. Well-
// formed text reads back as the same characters, with its case, its combining sequences and its
// normalization form as given; an empty string is a value, not null. Encoding is a one-to-one
// mapping for well-formed text, so the same characters and the length of their encoding are what
// the tests can see of the stored bytes.
public sealed class TextPreservationTests
{
    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Well_formed_text_reads_back_as_the_same_characters(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        foreach (var text in TextSamples.WellFormed)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Value"), text);
            writer.SetString(item.Field("Maybe"), text);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(text, reader.GetString(item.Field("Value")));
            Assert.Equal(text, reader.GetString(item.Field("Maybe")));
            Assert.False(reader.IsNull(item.Field("Maybe")));
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void The_length_of_a_text_is_the_number_of_its_bytes_in_the_database_encoding(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        foreach (var text in TextSamples.WellFormed)
        {
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Value"), text);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(TextSamples.EncodedLength(text, encoding), reader.GetLength(item.Field("Value")));
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("abc", 3, 6)]
    [InlineData("é", 2, 2)]
    [InlineData("Ж", 2, 2)]
    [InlineData("漢", 3, 2)]
    [InlineData("😀", 4, 4)]
    [InlineData("é", 3, 4)]
    [InlineData("", 0, 0)]
    public void A_character_takes_the_bytes_of_its_encoding(string text, int utf8Length, int utf16Length)
    {
        // The widths of Q04.1, "Representation costs".
        foreach (var (encoding, length) in new[] { ("Utf8", utf8Length), ("Utf16", utf16Length) })
        {
            using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Value"), text);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(length, reader.GetLength(item.Field("Value")));
            Assert.Equal(text, reader.GetString(item.Field("Value")));
        }
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Text_is_not_normalized(string encoding)
    {
        // The same letter composed and decomposed: equal to a reader of Unicode, two different texts
        // to the database, and each stays as it was given (Q04.5).
        const string Composed = "été";
        const string Decomposed = "été";
        Assert.Equal(Composed, Decomposed.Normalize(NormalizationForm.FormC));
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), Composed);
        writer.SetString(item.Field("Maybe"), Decomposed);
        var reader = item.Reader(writer.Complete());

        Assert.Equal(Composed, reader.GetString(item.Field("Value")));
        Assert.Equal(Decomposed, reader.GetString(item.Field("Maybe")));
    }

    [Theory(Skip = NotImplemented.RootLayout)]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Long_text_of_every_width_reads_back_as_the_same_characters(string encoding)
    {
        // The samples repeated until the text needs several chunks in either encoding.
        var builder = new StringBuilder();

        while (builder.Length < 3 * RootLayout.ChunkPayload)
        {
            foreach (var sample in TextSamples.WellFormed)
            {
                builder.Append(sample);
            }
        }

        var text = builder.ToString();
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), text);
        var reader = item.Reader(writer.Complete());

        Assert.Equal(text, reader.GetString(item.Field("Value")));
        Assert.Equal(TextSamples.EncodedLength(text, encoding), reader.GetLength(item.Field("Value")));
    }
}
