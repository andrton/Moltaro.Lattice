using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// A String and a Binary value read back equal at every threshold of their placement, and the
// arena holds for the root what the placement of the brief gives (brief of S05, Acceptance and
// "Where a value goes"; StoredBytes): 0, 1, InlineLimit and its neighbours, SingleBlockLimit and
// its neighbours, ChunkPayload and its neighbours, several chunks, and a value of seventeen chunks.
// In a UTF-16 database a text has two bytes for every code unit, so its lengths are the even
// neighbours of the thresholds. Each length is written into an arena of its own, which then holds
// that one root.
public sealed class ThresholdTests
{
    // Sixteen full chunks and 412 bytes in a seventeenth.
    private const int SeventeenChunks = 1_048_476;

    // Encoded lengths in bytes.
    public static readonly TheoryData<int> Lengths = new(
        0, 1,
        ValueBlocks.InlineLimit - 1, ValueBlocks.InlineLimit, ValueBlocks.InlineLimit + 1,
        ValueBlocks.SingleBlockLimit - 1, ValueBlocks.SingleBlockLimit, ValueBlocks.SingleBlockLimit + 1,
        ValueBlocks.ChunkPayload - 1, ValueBlocks.ChunkPayload, ValueBlocks.ChunkPayload + 1,
        2 * ValueBlocks.ChunkPayload, 3 * ValueBlocks.ChunkPayload + 17,
        SeventeenChunks);

    // Encoded lengths of UTF-16 text, in bytes: two for every code unit.
    public static readonly TheoryData<int> EvenLengths = new(
        0, 2,
        ValueBlocks.InlineLimit - 2, ValueBlocks.InlineLimit, ValueBlocks.InlineLimit + 2,
        ValueBlocks.SingleBlockLimit - 2, ValueBlocks.SingleBlockLimit, ValueBlocks.SingleBlockLimit + 2,
        ValueBlocks.ChunkPayload - 2, ValueBlocks.ChunkPayload, ValueBlocks.ChunkPayload + 2,
        3 * ValueBlocks.ChunkPayload + 18,
        SeventeenChunks);

    [Theory]
    [MemberData(nameof(Lengths))]
    public void A_Binary_value_reads_back_equal_and_is_placed_by_its_length(int length)
    {
        using var item = TypedItem.OfType("Binary");
        var value = Payloads.Bytes(length);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), value);
        var reader = item.Reader(item.Publish(writer.Complete()));

        Assert.Equal(value, reader.GetBinary(item.Field("Value")));
        Assert.Equal(length, reader.GetLength(item.Field("Value")));
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, length), StoredBytes.In(item.Arena));
    }

    [Theory]
    [MemberData(nameof(Lengths))]
    public void A_UTF8_text_reads_back_equal_and_is_placed_by_its_encoded_length(int length)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: "Utf8");
        var value = Payloads.Ascii(length);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), value);
        var reader = item.Reader(item.Publish(writer.Complete()));

        Assert.Equal(value, reader.GetString(item.Field("Value")));
        Assert.Equal(length, reader.GetLength(item.Field("Value")));
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, length), StoredBytes.In(item.Arena));
    }

    [Theory]
    [MemberData(nameof(EvenLengths))]
    public void A_UTF16_text_reads_back_equal_and_is_placed_by_its_encoded_length(int length)
    {
        using var item = TypedItem.Open("'Type': 'String'", encoding: "Utf16");
        var value = Payloads.Ascii(length / 2);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Value"), value);
        var reader = item.Reader(item.Publish(writer.Complete()));

        Assert.Equal(value, reader.GetString(item.Field("Value")));
        Assert.Equal(length, reader.GetLength(item.Field("Value")));
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, length), StoredBytes.In(item.Arena));
    }
}
