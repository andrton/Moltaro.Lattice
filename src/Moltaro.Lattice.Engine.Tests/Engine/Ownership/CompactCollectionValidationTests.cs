using Moltaro.Lattice.Engine.Tests.Storage.Values;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Engine.Ownership;

// A failed field placement keeps no owned blocks or inline space. Retrying it or abandoning
// the writer gives the same ownership as a writer whose first attempt never took place.
public sealed class CompactCollectionValidationTests
{
    [Theory(Skip = SkipReason.MapsAndSets)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Compact_Map_refusal_releases_its_storage_before_the_writer_retries_or_abandons(bool packed, bool retry)
    {
        using var item = RootArena.Of(packed
            ? "{ 'Id': 2, 'Name': 'Values', 'Type': 'Map', 'Key': { 'Type': 'UInt8' }, 'Value': { 'Type': 'Int32' } }"
            : "{ 'Id': 2, 'Name': 'Values', 'Type': 'Map', 'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }, 'Value': { 'Type': 'Int32' } }");
        var map = item.Field("Values");
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);
        try
        {
            // The bulk input binder normally rejects these keys. Give the writer two directly
            // so it checks the uniqueness of the entries it placed.
            object invalid = packed
                ? new Dictionary<byte, int>(EqualityComparer<byte>.Create((_, _) => false, _ => 0)) { { 1, 3 }, { 1, 4 } }
                : new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 3, ["A"] = 4 };
            writer.SetMap(map, invalid);
            Assert.Fail("The compact Map accepted a duplicate key.");
        }
        catch (LatticeException error)
        {
            Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
            Assert.Contains("Values", error.Message, StringComparison.Ordinal);
        }

        Assert.Equal(0, writer.SlotBytes);
        Assert.Equal(0, item.Arena.Counters.PreparedBytes);

        if (retry)
        {
            object valid = packed ? new Dictionary<byte, int> { [2] = 9 } : new Dictionary<string, int> { ["b"] = 9 };
            writer.SetMap(map, valid);
            var root = writer.Complete();
            // The fixed part payload is 8 bytes of Id and one 16-byte descriptor. Only the
            // successful packed entry, one byte of key and four of value, can extend it.
            Assert.Equal(8u + 16u + (packed ? 5u : 0u), item.Arena.Header(root).PayloadLength);
            var copy = item.Reader(root).GetNested(map);
            if (packed)
            {
                Assert.Equal(new KeyValuePair<byte, int>(2, 9), Assert.Single(Assert.IsType<Dictionary<byte, int>>(copy)));
            }
            else
            {
                Assert.Equal(new KeyValuePair<string, int>("b", 9), Assert.Single(Assert.IsType<Dictionary<string, int>>(copy)));
            }

            RootStorage.Free(item.Arena, item.Layout, root);
        }
        else
        {
            writer.Abandon();
        }

        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
        Assert.Equal(0, item.Arena.Counters.LiveBytes);
    }

    [Theory(Skip = SkipReason.MapsAndSets)]
    [InlineData(true)]
    [InlineData(false)]
    public void Nullable_collection_remains_null_when_its_write_is_refused_and_the_root_completes(bool map)
    {
        using var item = RootArena.Of(map
            ? "{ 'Id': 2, 'Name': 'Values', 'Type': 'Map', 'Nullable': true, 'Key': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' }, 'Value': { 'Type': 'Int32' } }"
            : "{ 'Id': 2, 'Name': 'Values', 'Type': 'Set', 'Nullable': true, 'Element': { 'Type': 'String', 'Comparison': 'OrdinalIgnoreCase' } }");
        var values = item.Field("Values");
        var writer = item.Writer();
        writer.SetInteger(item.Field("Id"), 1);

        try
        {
            if (map)
            {
                writer.SetMap(values, new Dictionary<string, int>(StringComparer.Ordinal) { ["a"] = 1, ["A"] = 2 });
            }
            else
            {
                writer.SetSet(values, new HashSet<string>(StringComparer.Ordinal) { "a", "A" });
            }

            Assert.Fail("The collection accepted two equal entries.");
        }
        catch (LatticeException error)
        {
            Assert.Equal(LatticeErrorEnum.ValidationFailed, error.Error);
        }

        Assert.Equal(0, writer.SlotBytes);
        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
        var root = writer.Complete();
        var reader = item.Reader(root);
        Assert.True(reader.IsNull(values));
        Assert.Null(reader.GetNested(values));
        RootStorage.Free(item.Arena, item.Layout, root);
        Assert.Equal(0, item.Arena.Counters.PreparedBytes);
    }
}
