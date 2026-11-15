using Moltaro.Lattice.Engine.Tests.Storage.Values.Placement;
using Moltaro.Lattice.Storage;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Limits;

// A failure of the native allocator met inside a Set… member (brief of S05, "The size of an
// entity", the decision after review round 1): the OutOfMemoryException passes through after the
// member has freed everything it allocated for that value; the field is not set, as after any
// refused call, and the writer stays usable: the caller may set the field again, complete or
// abandon. The value is chunked, three chunks of ChunkPayload bytes, and the failure is made to meet
// each of its four requests in turn: the directory (0), the first chunk (1), the second (2) and the
// last (3); see OneRegionArena.
public sealed class SetFailureTests
{
    private static readonly byte[] Chunked = Payloads.Bytes(3 * RootLayout.ChunkPayload);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_member_that_meets_the_failure_frees_the_value_and_Abandon_leaves_nothing(int failingRequest)
    {
        using var item = new OneRegionArena("'Type': 'Binary'", reservedSegments: failingRequest);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);

        FailSetting(item, ref writer, "Value");

        writer.Abandon();

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
        item.AssertNativeMemoryIsSegments();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_field_that_met_the_failure_is_not_set_and_may_be_set_again(int failingRequest)
    {
        // The limit is the size of the entity exactly: the bytes of the value that failed are not
        // counted, or the entity would be over it.
        using var item = new OneRegionArena("'Type': 'Binary'", reservedSegments: failingRequest);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);

        FailSetting(item, ref writer, "Value");

        // The allocator works again; the second SetBinary of the field is not refused as a second set.
        writer.SetBinary(item.Field("Value"), Chunked);
        var root = writer.Complete();

        Assert.Equal(Chunked, item.Reader(root).GetBinary(item.Field("Value")));
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, Chunked.Length), StoredBytes.In(item.Arena));

        RootStorage.Free(item.Arena, item.Layout, root);

        Assert.Equal(StoredBytes.Zero, StoredBytes.In(item.Arena));
        item.AssertNativeMemoryIsSegments();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void A_nullable_field_that_met_the_failure_is_null_when_the_writer_completes(int failingRequest)
    {
        using var item = new OneRegionArena("'Type': 'Binary'", reservedSegments: failingRequest);
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        writer.SetBinary(item.Field("Value"), Payloads.Bytes(10));

        FailSetting(item, ref writer, "Maybe");

        var root = writer.Complete();
        var reader = item.Reader(root);

        Assert.True(reader.IsNull(item.Field("Maybe")));
        Assert.Equal(Payloads.Bytes(10), reader.GetBinary(item.Field("Value")));

        // Nothing but the root block, which holds the fixed part and the inline Value.
        Assert.Equal(StoredBytes.Of(item.Layout.FixedSize, 10), StoredBytes.In(item.Arena));
    }

    // Sets the field to the chunked value with the allocator told to fail its next request, which the
    // member meets; afterwards the arena holds nothing for the value, and nothing was left behind in
    // native memory either.
    private static void FailSetting(OneRegionArena item, ref RootWriter writer, string fieldName)
    {
        var field = item.Field(fieldName);
        var before = StoredBytes.In(item.Arena);
        item.Allocator.FailNextAllocation = true;

        WriterAssert.Throws<OutOfMemoryException>(ref writer, (ref RootWriter w) => w.SetBinary(field, Chunked));

        Assert.False(item.Allocator.FailNextAllocation, "The failure was not met in SetBinary.");
        Assert.Equal(before, StoredBytes.In(item.Arena));
        item.AssertNativeMemoryIsSegments();
    }
}
