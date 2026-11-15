using Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// The bound of one value (decision O19; RootWriter and RootLayout; ValueBound): one String or Binary
// value takes at most 268,173,376 bytes of its stored encoding. A larger value is refused by its
// Set… member with ValidationFailed and the field named, as every other validation of a value:
// nothing is allocated for it, so the counters of the arena are the same before and after the call,
// the field is not set, and the writer stays usable. A value of exactly the bound is accepted by its
// Set… member. An entity itself has no size limit; only the memory of the arena bounds it, and only
// Complete reports that.
//
// The values are large: an array of 268 MB, whose bytes nothing reads, and the text of ValueBound,
// 179 MB. A value of the bound is not stored here, which would take as much again in the arena: the
// tests of what a Set… member accepts write into an arena of one segment, where Complete then refuses
// the entity for its memory.
public sealed class ValueBoundTests
{
    private const string Fields = """
        { 'Id': 2, 'Name': 'Payload', 'Type': 'Binary' },
        { 'Id': 3, 'Name': 'Note', 'Type': 'String', 'Nullable': true }
        """;

    // One byte more than the bound. Nothing reads its bytes, so they are not initialized.
    private static readonly Lazy<byte[]> OneByteAbove = new(() => GC.AllocateUninitializedArray<byte>(ValueBound.Bytes + 1));

    [Fact]
    public void Binary_value_above_the_bound_is_ValidationFailed_by_SetBinary_with_the_field_named_and_the_writer_stays_usable()
    {
        using var item = RootArena.Of(Fields);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        var before = item.Arena.Counters;

        var exception = WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetBinary(item.Field("Payload"), OneByteAbove.Value));

        Assert.Contains("Payload", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, item.Arena.Counters);

        // The field was not set: a valid value follows and reads back.
        var payload = Payloads.Bytes(100);
        writer.SetBinary(item.Field("Payload"), payload);

        Assert.Equal(payload, item.Reader(writer.Complete()).GetBinary(item.Field("Payload")));
    }

    [Fact]
    public void String_value_whose_encoding_is_above_the_bound_is_ValidationFailed_by_SetString_with_the_field_named_and_the_writer_stays_usable()
    {
        // After a value in chunks, which the writer has stored already.
        using var item = RootArena.Of(Fields);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), Payloads.Bytes(70_000));
        var before = item.Arena.Counters;

        var exception = WriterAssert.Refused(ref writer, LatticeErrorEnum.ValidationFailed, (ref RootWriter w) => w.SetString(item.Field("Note"), ValueBound.TextOneByteAboveInUtf8));

        Assert.Contains("Note", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, item.Arena.Counters);

        writer.SetString(item.Field("Note"), "kept");

        Assert.Equal("kept", item.Reader(writer.Complete()).GetString(item.Field("Note")));
    }

    [Fact]
    public void Binary_value_of_exactly_the_bound_is_accepted_by_SetBinary()
    {
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: RootArena.SegmentSize);
        var before = StoredBytes.In(item.Arena);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);

        writer.SetBinary(item.Field("Payload"), OneByteAbove.Value.AsSpan(0, ValueBound.Bytes));

        // Only the memory of the arena of one segment stands in the way, at Complete.
        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void String_value_of_exactly_the_bound_in_its_stored_encoding_is_accepted_by_SetString()
    {
        using var item = new RootArena(ItemSchema.Compile(Fields), memoryLimit: RootArena.SegmentSize);
        var before = StoredBytes.In(item.Arena);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), []);

        writer.SetString(item.Field("Note"), ValueBound.TextOneByteAboveInUtf8.AsSpan(0, ValueBound.TextOneByteAboveInUtf8.Length - 1));

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }

    [Fact]
    public void Text_above_the_bound_in_UTF8_is_accepted_by_SetString_in_a_UTF16_database_where_its_encoding_is_below_it()
    {
        using var item = new RootArena(ItemSchema.Compile(Fields, encoding: "Utf16"), memoryLimit: RootArena.SegmentSize);
        var before = StoredBytes.In(item.Arena);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Payload"), []);

        writer.SetString(item.Field("Note"), ValueBound.TextOneByteAboveInUtf8);

        WriterAssert.CompleteRefused(ref writer, LatticeErrorEnum.WriteRejected);
        Assert.Equal(before, StoredBytes.In(item.Arena));
    }
}
