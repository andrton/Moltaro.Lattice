using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;

// Where a value went, as far as the counters of the arena show it (brief of S05, "Where a value
// goes"). LiveBytes is a 32-byte header and the payload of every block, so it tells how many
// blocks a root has and what they hold: the root block, the fixed part and the inline values;
// a value block, the bytes of its value; a chunk directory, 16 bytes for each chunk; a chunk, at
// most ChunkPayload bytes of the value. These tests spell the sums out for one root each; the
// thresholds are in ThresholdTests.
public sealed class PlacementTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Login', 'Type': 'String' },
        { 'Id': 3, 'Name': 'Nickname', 'Type': 'String', 'Nullable': true },
        { 'Id': 4, 'Name': 'Photo', 'Type': 'Binary' },
        { 'Id': 5, 'Name': 'Thumbnail', 'Type': 'Binary', 'Nullable': true }
        """;

    [Fact]
    public void An_entity_whose_values_are_all_inline_is_one_block()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), Payloads.Ascii(RootLayout.InlineLimit));
        writer.SetString(item.Field("Nickname"), "");
        writer.SetBinary(item.Field("Photo"), Payloads.Bytes(10));
        writer.SetNull(item.Field("Thumbnail"));
        writer.Complete();

        // One header, the fixed part and the 74 bytes of the inline values.
        Assert.Equal(item.Layout.FixedSize + RootLayout.InlineLimit + 10, item.Arena.Counters.LiveBytes);
    }

    [Fact]
    public void A_value_above_the_inline_limit_adds_one_block_of_its_own()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), "short");
        writer.SetBinary(item.Field("Photo"), Payloads.Bytes(RootLayout.InlineLimit + 1));
        writer.Complete();

        Assert.Equal(item.Layout.FixedSize + 5 + (32 + RootLayout.InlineLimit + 1), item.Arena.Counters.LiveBytes);
    }

    [Fact]
    public void A_value_of_the_single_block_limit_still_has_one_block_of_its_own()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), Payloads.Ascii(RootLayout.SingleBlockLimit));
        writer.SetBinary(item.Field("Photo"), []);
        writer.Complete();

        Assert.Equal(item.Layout.FixedSize + (32 + RootLayout.SingleBlockLimit), item.Arena.Counters.LiveBytes);
    }

    [Fact]
    public void A_value_above_the_single_block_limit_is_one_chunk_and_a_directory()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), "");
        writer.SetBinary(item.Field("Photo"), Payloads.Bytes(RootLayout.SingleBlockLimit + 1));
        writer.Complete();

        // The directory holds one descriptor of 16 bytes; the chunk holds the whole value.
        Assert.Equal(item.Layout.FixedSize + (32 + 16) + (32 + RootLayout.SingleBlockLimit + 1), item.Arena.Counters.LiveBytes);
    }

    [Fact]
    public void A_value_above_the_chunk_payload_is_a_full_chunk_a_partial_chunk_and_a_directory()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("Login"), "");
        writer.SetBinary(item.Field("Photo"), Payloads.Bytes(RootLayout.ChunkPayload + 1));
        writer.Complete();

        Assert.Equal(
            item.Layout.FixedSize + (32 + 2 * 16) + (32 + RootLayout.ChunkPayload) + (32 + 1),
            item.Arena.Counters.LiveBytes);
    }

    [Fact]
    public void Several_large_values_of_one_entity_each_read_back_equal()
    {
        const string ManyFields = """
            { 'Id': 2, 'Name': 'A', 'Type': 'String' },
            { 'Id': 3, 'Name': 'B', 'Type': 'Binary' },
            { 'Id': 4, 'Name': 'C', 'Type': 'String' },
            { 'Id': 5, 'Name': 'D', 'Type': 'Binary', 'Nullable': true },
            { 'Id': 6, 'Name': 'E', 'Type': 'String', 'Nullable': true },
            { 'Id': 7, 'Name': 'F', 'Type': 'Binary' }
            """;
        using var item = RootArena.Of(ManyFields);
        var a = Payloads.Ascii(200_000, seed: 1);
        var b = Payloads.Bytes(3 * RootLayout.ChunkPayload, seed: 2);
        var c = Payloads.Ascii(RootLayout.InlineLimit, seed: 3);
        var d = Payloads.Bytes(RootLayout.SingleBlockLimit, seed: 4);
        var e = Payloads.Ascii(RootLayout.ChunkPayload + 1, seed: 5);
        var f = Payloads.Bytes(RootLayout.InlineLimit + 1, seed: 6);

        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetString(item.Field("A"), a);
        writer.SetBinary(item.Field("B"), b);
        writer.SetString(item.Field("C"), c);
        writer.SetBinary(item.Field("D"), d);
        writer.SetString(item.Field("E"), e);
        writer.SetBinary(item.Field("F"), f);
        var reader = item.Reader(writer.Complete());

        Assert.Equal(a, reader.GetString(item.Field("A")));
        Assert.Equal(b, reader.GetBinary(item.Field("B")));
        Assert.Equal(c, reader.GetString(item.Field("C")));
        Assert.Equal(d, reader.GetBinary(item.Field("D")));
        Assert.Equal(e, reader.GetString(item.Field("E")));
        Assert.Equal(f, reader.GetBinary(item.Field("F")));
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, a.Length, b.Length, c.Length, d.Length, e.Length, f.Length), StoredBytes.In(item.Arena));
    }

    [Fact]
    public void A_short_value_that_no_longer_fits_the_root_block_has_one_block_of_its_own()
    {
        // A fixed part of 65,526 bytes, 10 short of one block: 32 + 8 + 16 (the descriptor of Text)
        // + 16 × 4,091 + 14.
        using var wide = new RootArena(WideSchema.Compile("Wide", decimals: 4_091, bytes: 14, "{ 'Id': 2, 'Name': 'Text', 'Type': 'String' }"), "Wide");
        Assert.Equal(Arena.MaxBlockSize - 10, wide.Layout.FixedSize);

        // Ten bytes still fit the root block, which is then exactly one block.
        var fits = wide.Writer();
        WideSchema.SetKeyAndFillers(ref fits, wide);
        fits.SetString(wide.Field("Text"), Payloads.Ascii(10));
        var inline = fits.Complete();

        Assert.Equal(Arena.MaxBlockSize, wide.Arena.Counters.LiveBytes);
        Assert.Equal(Payloads.Ascii(10), wide.Reader(inline).GetString(wide.Field("Text")));

        // Eleven do not, though they are below the inline limit.
        RootStorage.Free(wide.Arena, wide.Layout, inline);
        var overflows = wide.Writer();
        WideSchema.SetKeyAndFillers(ref overflows, wide);
        overflows.SetString(wide.Field("Text"), Payloads.Ascii(11));
        var outside = overflows.Complete();

        Assert.Equal(wide.Layout.FixedSize + (32 + 11), wide.Arena.Counters.LiveBytes);
        Assert.Equal(Payloads.Ascii(11), wide.Reader(outside).GetString(wide.Field("Text")));
    }

    [Fact]
    public void A_root_whose_fixed_part_fills_one_block_is_stored_and_read_back()
    {
        using var widest = new RootArena(WideSchema.Compile("Widest", decimals: 4_093, bytes: 8), "Widest");

        var writer = widest.Writer();
        WideSchema.SetKeyAndFillers(ref writer, widest);
        var reader = widest.Reader(writer.Complete());

        Assert.Equal(Arena.MaxBlockSize, widest.Arena.Counters.LiveBytes);
        Assert.Equal(1, reader.GetInteger(widest.Field("Id")));
        Assert.Equal(1000m + 4_092, reader.GetDecimal(widest.Field("Decimal4092")));
        Assert.Equal((ulong)((1000 + 4_093 + 7) % 256), reader.GetUnsigned(widest.Field("Byte7")));
    }
}
