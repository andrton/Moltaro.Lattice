using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// The size of an entity (brief of S05, Acceptance and "The size of an entity"). What maxEntitySize
// bounds is FixedSize plus the encoded bytes of every String and Binary value, wherever a value
// went; a null value has none. An entity of exactly maxEntitySize bytes is stored, one byte more is
// WriteRejected, reported by Complete: no Set… member fails for the size. When Complete fails,
// everything the writer allocated is freed, so LiveBytes and ClassSlackBytes are as they were
// before the writer; Segments and ReservedSegments may differ, because a segment obtained for the
// writer may stay in the reserve, and no test reads them. The 1 MiB starting limit is the value
// passed here, as it will be the default of the option in S09.
public sealed class EntitySizeTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Payload', 'Type': 'Binary' },
        { 'Id': 3, 'Name': 'Note', 'Type': 'String', 'Nullable': true }
        """;

    // The header, the bitmap of one nullable field, Id and two descriptors.
    private const int FixedSize = 32 + 1 + 8 + 16 + 16;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-RootLayout.ChunkPayload)]
    public void An_entity_of_the_starting_limit_or_below_is_stored(int belowLimit)
    {
        using var item = RootArena.Of(Fields);
        Assert.Equal(FixedSize, item.Layout.FixedSize);
        var payload = new byte[RootArena.StartingEntityLimit - FixedSize + belowLimit];
        payload.AsSpan().Fill(0x5A);

        var writer = item.Writer(RootArena.StartingEntityLimit);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), payload);
        var reader = item.Reader(writer.Complete());

        Assert.Equal(payload, reader.GetBinary(item.Field("Payload")));
    }

    [Fact]
    public void An_entity_one_byte_above_the_starting_limit_is_WriteRejected_by_Complete()
    {
        using var item = RootArena.Of(Fields);
        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer(RootArena.StartingEntityLimit);
        writer.SetInteger(item.Field("Id"), 1);

        // No Set… member fails for the size.
        writer.SetBinary(item.Field("Payload"), new byte[RootArena.StartingEntityLimit - FixedSize + 1]);

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void No_Set_member_fails_for_a_value_far_above_the_limit()
    {
        using var item = RootArena.Of(Fields);

        var writer = item.Writer(1_000);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), new byte[3 * RootLayout.ChunkPayload]);
        writer.SetString(item.Field("Note"), new string('n', 10_000));

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(RootLayout.InlineLimit)]
    [InlineData(RootLayout.InlineLimit + 1)]
    [InlineData(RootLayout.SingleBlockLimit + 1)]
    public void The_limit_counts_every_value_wherever_it_went(int noteLength)
    {
        // The note is inline, in a block of its own or chunked; the payload is chunked. The limit is
        // the same in every case.
        using var item = RootArena.Of(Fields);
        var payloadLength = RootArena.StartingEntityLimit - FixedSize - noteLength;

        Assert.True(TryWrite(item, RootArena.StartingEntityLimit, payloadLength, new string('n', noteLength)));
        Assert.False(TryWrite(item, RootArena.StartingEntityLimit, payloadLength + 1, new string('n', noteLength)));
        Assert.False(TryWrite(item, RootArena.StartingEntityLimit, payloadLength, new string('n', noteLength + 1)));
    }

    [Fact]
    public void A_small_limit_bounds_the_entity_in_the_same_way()
    {
        using var item = RootArena.Of(Fields);

        // 73 + 60 + 67 = 200 bytes, every value inline.
        Assert.True(TryWrite(item, 200, 67, new string('n', 60)));
        Assert.False(TryWrite(item, 200, 68, new string('n', 60)));
        Assert.False(TryWrite(item, 200, 67, new string('n', 61)));
        Assert.True(TryWrite(item, 200, 127, null));
        Assert.False(TryWrite(item, 200, 128, null));
    }

    [Fact]
    public void The_fixed_part_counts_toward_the_limit_and_a_null_or_empty_value_adds_nothing()
    {
        using var item = RootArena.Of(Fields);

        Assert.True(TryWrite(item, FixedSize, 0, null));
        Assert.True(TryWrite(item, FixedSize, 0, ""));
        Assert.False(TryWrite(item, FixedSize - 1, 0, null));
        Assert.False(TryWrite(item, 1, 0, null));
    }

    [Theory]
    [InlineData("Utf8", "é", 2)]
    [InlineData("Utf8", "😀", 4)]
    [InlineData("Utf16", "a", 2)]
    [InlineData("Utf16", "😀", 4)]
    public void The_limit_counts_text_in_the_encoding_of_the_database(string encoding, string text, int encodedLength)
    {
        using var item = RootArena.Of(Fields, encoding);

        Assert.True(TryWrite(item, FixedSize + encodedLength, 0, text));
        Assert.False(TryWrite(item, FixedSize + encodedLength - 1, 0, text));
    }

    [Fact]
    public void A_Complete_refused_for_the_size_leaves_the_live_bytes_and_slack_as_before_the_writer()
    {
        using var item = RootArena.Of(Fields);

        // A root that stays, so that the arena holds something before the writer.
        Assert.True(TryWrite(item, RootArena.StartingEntityLimit, 100_000, "kept"));
        var before = StoredBytes.In(item.Arena);

        var writer = item.Writer(RootArena.StartingEntityLimit);
        writer.SetInteger(item.Field("Id"), 2);
        writer.SetString(item.Field("Note"), new string('n', 5_000));
        writer.SetBinary(item.Field("Payload"), new byte[RootArena.StartingEntityLimit]);

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    // Writes an entity under the limit and reports whether Complete stored it. A refusal has to be
    // WriteRejected, and then the arena holds what it held before the writer. A stored root reads
    // back and is freed again.
    private static bool TryWrite(RootArena item, int maxEntitySize, int payloadLength, string? note)
    {
        var before = StoredBytes.In(item.Arena);
        var payload = new byte[payloadLength];
        var writer = item.Writer(maxEntitySize);
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), payload);

        if (note is not null)
        {
            writer.SetString(item.Field("Note"), note);
        }

        ArenaAddress root;

        try
        {
            root = writer.Complete();
        }
        catch (LatticeException exception)
        {
            Assert.Equal(LatticeErrorEnum.WriteRejected, exception.Error);
            Assert.Equal(before, StoredBytes.In(item.Arena));

            return false;
        }

        var reader = item.Reader(root);
        Assert.Equal(payload, reader.GetBinary(item.Field("Payload")));
        Assert.Equal(note, reader.IsNull(item.Field("Note")) ? null : reader.GetString(item.Field("Note")));
        RootStorage.Free(item.Arena, item.Layout, root);

        return true;
    }
}
