using System.Text;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// A chunk boundary is not a boundary of the value: a character may begin in one chunk and end in
// the next, and it reads back undamaged (brief of S05, Acceptance; milestone contract, "Native
// layout and allocation": decoders carry Unicode state across chunks; Q04.5: a chunk boundary must
// not invalidate a valid sequence). In UTF-8 a character of two, three or four bytes is split after
// each of its bytes; in UTF-16 every chunk holds whole code units, since ChunkPayload is even, and
// the split falls between the two halves of a surrogate pair.
public sealed class ChunkBoundaryTests
{
    [Theory]
    [InlineData("é")]
    [InlineData("€")]
    [InlineData("漢")]
    [InlineData("😀")]
    public void A_UTF8_character_split_across_a_chunk_boundary_reads_back_undamaged(string character)
    {
        var width = Encoding.UTF8.GetByteCount(character);
        using var item = TypedItem.Open("'Type': 'String'", encoding: "Utf8");

        foreach (var boundary in new[] { RootLayout.ChunkPayload, 2 * RootLayout.ChunkPayload })
        {
            // The character begins 1 to width - 1 bytes before the boundary.
            for (var before = 1; before < width; before++)
            {
                var text = Payloads.Ascii(boundary - before) + character + "tail after the boundary";
                var writer = item.Writer();
                writer.SetInteger(item.Field("Id"), 1);
                writer.SetString(item.Field("Value"), text);
                var root = writer.Complete();
                var reader = item.Reader(root);

                Assert.Equal(text, reader.GetString(item.Field("Value")));
                Assert.Equal(Encoding.UTF8.GetByteCount(text), reader.GetLength(item.Field("Value")));
                RootStorage.Free(item.Arena, item.Layout, root);
            }
        }
    }

    [Fact]
    public void A_surrogate_pair_split_across_a_chunk_boundary_reads_back_undamaged_in_UTF16()
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: "Utf16");

        foreach (var boundary in new[] { RootLayout.ChunkPayload, 2 * RootLayout.ChunkPayload })
        {
            // The high surrogate takes the last two bytes before the boundary, the low one the first
            // two after it.
            var text = Payloads.Ascii((boundary / 2) - 1) + "😀" + "tail after the boundary";
            var writer = item.Writer();
            writer.SetInteger(item.Field("Id"), 1);
            writer.SetString(item.Field("Value"), text);
            var root = writer.Complete();
            var reader = item.Reader(root);

            Assert.Equal(text, reader.GetString(item.Field("Value")));
            Assert.Equal(2 * text.Length, reader.GetLength(item.Field("Value")));
            RootStorage.Free(item.Arena, item.Layout, root);
        }
    }
}
