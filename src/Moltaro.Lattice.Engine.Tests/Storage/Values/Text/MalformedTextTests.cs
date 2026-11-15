using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Text;

// Text that is not well-formed UTF-16, a surrogate without its pair, is refused in either encoding
// (brief of S05, "Text" and "The life of a writer"; Q04.5): SetString fails with ValidationFailed
// at the call, before anything is allocated for the value, so the counters of the arena are the
// same before and after the call; no character is put in place of the surrogate; the field is not
// set, and the writer stays usable, so that a valid value may follow and reads back exactly.
public sealed class MalformedTextTests
{
    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Malformed_text_is_refused_at_SetString_and_allocates_nothing(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        foreach (var text in TextSamples.Malformed)
        {
            var writer = item.Writer();
            var before = item.Arena.Counters;

            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Value"), text));

            Assert.Equal(before, item.Arena.Counters);
            writer.Abandon();
        }
    }

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void After_malformed_text_the_writer_takes_a_valid_value_which_reads_back_unchanged(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        foreach (var text in TextSamples.Malformed)
        {
            // The valid text is the malformed one with its lone surrogates taken out, so that a
            // stored replacement character or a kept surrogate would show.
            var valid = WithoutLoneSurrogates(text);
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);

            WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Value"), text));
            writer.SetString(item.Field("Value"), valid);
            var reader = item.Reader(writer.Complete());

            Assert.Equal(valid, reader.GetString(item.Field("Value")));
            Assert.Equal(TextSamples.EncodedLength(valid, encoding), reader.GetLength(item.Field("Value")));
        }
    }

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Refused_text_does_not_set_the_field(string encoding)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);

        // A nullable field that saw only malformed text is null, never a replaced text.
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), "kept");
        WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Maybe"), "ab\uD83Dcd"));
        var reader = item.Reader(writer.Complete());

        Assert.True(reader.IsNull(item.Field("Maybe")));

        // A field that is not nullable and saw only malformed text is missing at Complete.
        var missing = item.Writer();
        missing.SetInteger(item.Field("Id"), 1);
        WriterAssert.Refused(ref missing, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Value"), "\uDE00"));

        WriterAssert.CompleteRefused(ref missing, LatticeErrorEnum.ValidationFailed);
    }

    [Theory]
    [InlineData("Utf8")]
    [InlineData("Utf16")]
    public void Long_malformed_text_allocates_nothing_wherever_the_lone_surrogate_lies(string encoding)
    {
        // Lengths that would be stored in one block of its own and in chunks; the surrogate at the
        // start, in the middle, at the first byte of the second chunk and at the very end.
        using var item = TypedItem.Open("'Type': 'String'", encoding: encoding);
        var secondChunk = encoding == "Utf8" ? RootLayout.ChunkPayload : RootLayout.ChunkPayload / 2;

        foreach (var length in new[] { 1_000, 40_000, 100_000 })
        {
            int[] positions = length > secondChunk ? [0, length / 2, secondChunk, length - 1] : [0, length / 2, length - 1];

            foreach (var position in positions)
            {
                var text = new string('x', position) + "\uD83D" + new string('y', length - position - 1);
                var writer = item.Writer();
                writer.SetInteger(item.Field("Id"), 1);

                // Another value of the root is placed already, so the writer has allocated before.
                writer.SetString(item.Field("Maybe"), new string('z', 5_000));
                var before = item.Arena.Counters;

                WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Value"), text));

                Assert.Equal(before, item.Arena.Counters);
                writer.Abandon();
            }
        }
    }

    private static string WithoutLoneSurrogates(string text)
    {
        var kept = new List<char>();

        for (var index = 0; index < text.Length; index++)
        {
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                kept.Add(text[index]);
                kept.Add(text[index + 1]);
                index++;
            }
            else if (!char.IsSurrogate(text[index]))
            {
                kept.Add(text[index]);
            }
        }

        return new string([.. kept]);
    }
}
