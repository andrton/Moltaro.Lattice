using Moltaro.Lattice.Access;
using Moltaro.Lattice.Engine.Tests.Engine;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values.Layouts;

// An absent owned value keeps its width in the root of its entity: every bit beneath it is set, its presence
// bit included, and every byte of its fields is zero, its descriptors included, so the root of an entity with
// the owned value absent has the fixed part of one with it present (StructureLayout). The roots are written by
// the database, by name, and read where they lie (EngineDatabase.ForEachLiveRoot).
//
// Item: Id, an Int64; Home, a nullable owned Address; Work, one that is not; an Address holds City, a String,
// Zip, an Int32, and Geo, a nullable owned Point of Lat and Lon, two Float64. The bits: Home 0, its City 1,
// Zip 2, Geo 3, Lat 4, Lon 5; Work's Geo 6, Lat 7, Lon 8: 9 bits, 2 bytes. The fixed fields from 32 + 2 = 34:
// Id 8, Home.Zip 4, Home.Geo.Lat 8, Home.Geo.Lon 8, Work.Zip 4, Work.Geo.Lat 8, Work.Geo.Lon 8, to 82; the
// descriptors of Home.City and Work.City, to 114.
public sealed class AbsentOwnedValueTests
{
    private const int FixedSize = 114;

    private static readonly LatticeSchema Schema = ItemSchema.Compile(
        "{ 'Id': 2, 'Name': 'Home', 'Type': 'Owned', 'OwnedType': 'Address', 'Nullable': true }, { 'Id': 3, 'Name': 'Work', 'Type': 'Owned', 'OwnedType': 'Address' }",
        ownedTypes: """
            { 'Id': 1, 'Name': 'Address', 'Fields': [
              { 'Id': 1, 'Name': 'City', 'Type': 'String' }, { 'Id': 2, 'Name': 'Zip', 'Type': 'Int32' },
              { 'Id': 3, 'Name': 'Geo', 'Type': 'Owned', 'OwnedType': 'Point', 'Nullable': true } ] },
            { 'Id': 2, 'Name': 'Point', 'Fields': [ { 'Id': 1, 'Name': 'Lat', 'Type': 'Float64' }, { 'Id': 2, 'Name': 'Lon', 'Type': 'Float64' } ] }
            """);

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Absent_owned_value_keeps_its_width_with_every_bit_beneath_it_set_and_every_byte_zero()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var items = database.Set(ItemSchema.EntityTypeName);

        // Home absent; Work with a City of two bytes, inline after the fixed part, and its Geo.
        await items.InsertAsync(RecordOf(items.EntityType, 1, home: null, work: ("NY", 10001, 40.7)));

        var payload = PayloadOf(database);

        Assert.Equal(FixedSize - BlockHeader.Size + 2, payload.Length);

        // Bits 0 to 5, Home and all beneath it, set; Work's Geo present, its bits clear.
        Assert.Equal(0b0011_1111, payload[0]);
        Assert.Equal(0, payload[1]);

        // Home.Zip, Home.Geo.Lat, Home.Geo.Lon, from 42 to 62, and the descriptor of Home.City, 82 to 98: zero.
        Assert.All(payload.AsSpan(42 - 32, 20).ToArray(), value => Assert.Equal(0, value));
        Assert.All(payload.AsSpan(82 - 32, 16).ToArray(), value => Assert.Equal(0, value));

        // Work.Zip at 62 and the descriptor of Work.City at 98 hold the values.
        Assert.Equal(10001, BitConverter.ToInt32(payload, 62 - 32));
        Assert.Equal(DescriptorKindEnum.Inline, ValueBlocks.Read(payload.AsSpan(98 - 32)).Kind);
    }

    [Fact(Skip = SkipReason.NestedValues)]
    public async Task Owned_value_absent_inside_a_present_one_keeps_its_width_and_the_present_one_has_its_bits_clear()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var items = database.Set(ItemSchema.EntityTypeName);

        // Home present without its Geo, Work without its Geo.
        await items.InsertAsync(RecordOf(items.EntityType, 1, home: ("Oslo", 150, null), work: ("NY", 10001, null)));

        var payload = PayloadOf(database);

        // The fixed part of the same size as with Home absent, and the two Cities inline.
        Assert.Equal(FixedSize - BlockHeader.Size + 4 + 2, payload.Length);

        // Bits 3 to 5, Home's Geo and its two fields, and 6 to 8, Work's: the first byte 0b1111_1000 and the
        // second 0b0000_0001. Home.Zip holds its value; the fields of both Geos, from 46 and from 66, are zero.
        Assert.Equal(0b1111_1000, payload[0]);
        Assert.Equal(0b0000_0001, payload[1]);
        Assert.Equal(150, BitConverter.ToInt32(payload, 42 - 32));
        Assert.All(payload.AsSpan(46 - 32, 16).ToArray(), value => Assert.Equal(0, value));
        Assert.All(payload.AsSpan(66 - 32, 16).ToArray(), value => Assert.Equal(0, value));
    }

    // The payload of the one live root of the database, copied while it lies in the arena.
    private static byte[] PayloadOf(EngineDatabase database)
    {
        byte[]? payload = null;

        database.ForEachLiveRoot((arena, layout, _, root) =>
        {
            Assert.Null(payload);
            Assert.Equal(FixedSize, layout.FixedSize);

            payload = arena.Payload(root).ToArray();
        });

        return Assert.IsType<byte[]>(payload);
    }

    private static LatticeRecord RecordOf(LatticeEntityType item, long id, (string City, int Zip, double? Lat)? home, (string City, int Zip, double? Lat) work)
    {
        Assert.True(item.TryGetField("Work", out var workField));

        var address = workField.Declaration.OwnedType!;

        return new LatticeRecord(item)
        {
            ["Id"] = id,
            ["Home"] = home is { } value ? AddressOf(address, value) : null,
            ["Work"] = AddressOf(address, work),
        };
    }

    private static LatticeOwnedValue AddressOf(LatticeOwnedType address, (string City, int Zip, double? Lat) value)
    {
        Assert.True(address.TryGetField("Geo", out var geo));

        var point = value.Lat is { } lat ? new LatticeOwnedValue(geo.Declaration.OwnedType!) { ["Lat"] = lat, ["Lon"] = -lat } : null;

        return new LatticeOwnedValue(address) { ["City"] = value.City, ["Zip"] = value.Zip, ["Geo"] = point };
    }
}
