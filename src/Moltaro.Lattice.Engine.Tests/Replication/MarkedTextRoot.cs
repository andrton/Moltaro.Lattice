using System.Buffers.Binary;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A root with a Dictionary text whose parts a test can find where it travels — in a Blocks frame of a
// load, in a Write of a transaction, in the request of a write forwarded through a child — so that it can
// damage the form of the text on its way, as MarkedRoot lets a test damage a root of Plain values. By the
// planner's decision after the study of S13, point 8, text travels as bytes with each root: for a
// Dictionary field a descriptor of the kind Text with its length and the location 0, and its bytes out of
// line, in the order of the descriptors, as a Plain value out of line travels; the wire format is
// otherwise that of S10. The entity type Regional has the key Code, a short Plain text, inline; Marker, an
// Int64 whose eight bytes appear once, which marks where the root payload stands (MarkedRoot); Region, a
// String with Dictionary storage; Data, a Binary value of 1,000 zero bytes in a block of its own, which
// travels after the text and last. Region is the text RegionText unless a test gives another, which a
// frame that holds one root holds once.
internal static class MarkedTextRoot
{
    public const uint TypeId = 0x5EFE0002;

    public const long Marker = 0x1122334455667788;

    public const string RegionText = "a region of its own";

    // The damages a test makes to the Region of a root, or to Data.
    public const string LocationNotZero = "a Text descriptor whose location is not 0";

    public const string PlainKind = "a Dictionary text in a descriptor of the kind Block, the form of a Plain value";

    public const string TextInAPlainField = "a Text descriptor in a field of Plain storage";

    public const string LongerThanItsBytes = "a Text descriptor that announces one byte more than the root holds";

    public const string BytesMissing = "the bytes of the text missing from the root";

    public const string EmptyText = "a Text descriptor of the empty text, which stays inline with length 0";

    public static IEnumerable<TheoryDataRow<string>> Damages =>
        [new(LocationNotZero), new(PlainKind), new(TextInAPlainField), new(LongerThanItsBytes), new(BytesMissing), new(EmptyText)];

    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": {{TypeId}}, "Name": "Regional",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Marker", "Type": "Int64" },
                { "Id": 3, "Name": "Region", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Data", "Type": "Binary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeEntityType EntityType => Schema.EntityTypes.Single();

    // The Region the root of a test has for the damage it makes: the empty text for EmptyText, whose
    // descriptor is then inline, RegionText for every other.
    public static string RegionFor(string damage) => damage == EmptyText ? "" : RegionText;

    public static LatticeRecord RecordOf(LatticeEntityType entityType, string code, string region = RegionText) => new(entityType)
    {
        ["Code"] = code,
        ["Marker"] = Marker,
        ["Region"] = region,
        ["Data"] = new byte[1_000],
    };

    // Where the root payload begins in the bytes: as far before the marker as the field Marker stands
    // after the header of a root block.
    public static int PayloadAt(ReadOnlySpan<byte> bytes) =>
        MarkedRoot.OnlyPlaceOf(bytes, BitConverter.GetBytes(Marker), "the marker") - (FieldOf("Marker").Offset - BlockHeader.Size);

    // Where the descriptor of the String or Binary field stands in the bytes.
    public static int DescriptorAt(ReadOnlySpan<byte> bytes, string field) =>
        PayloadAt(bytes) + (FieldOf(field).Offset - BlockHeader.Size);

    public static StoredField FieldOf(string name)
    {
        var layout = RootLayout.Compile(EntityType, Schema);

        Assert.True(EntityType.TryGetField(name, out var field));
        return layout.StoredFieldOf(field);
    }

    // The bytes that hold one root of the type with the damage made to it. A damage that removes bytes
    // lowers the length of the root by as many where the bytes carry it, at rootLengthAt, a UInt64; -1
    // when they do not, for a root whose length its container computes. Before it damages anything it
    // checks that the root carries its Region in the form of point 8: a Text descriptor of the length of
    // the text with the location 0, or inline with length 0 for the empty text.
    public static byte[] Damaged(byte[] bytes, string damage, int rootLengthAt)
    {
        var region = DescriptorAt(bytes, "Region");
        var kind = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(region));
        var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(region + 4));
        var location = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(region + 8));

        if (damage == EmptyText)
        {
            Assert.Equal(((uint)DescriptorKindEnum.Inline, 0u), (kind, length));

            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region), (uint)DescriptorKindEnum.Text);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(region + 8), 0);

            return bytes;
        }

        Assert.Equal(((uint)DescriptorKindEnum.Text, (uint)Encoding.UTF8.GetByteCount(RegionText), 0ul), (kind, length, location));

        switch (damage)
        {
            case LocationNotZero:
                BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(region + 8), 64);
                return bytes;

            case PlainKind:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region), (uint)DescriptorKindEnum.Block);
                return bytes;

            case TextInAPlainField:
                var data = DescriptorAt(bytes, "Data");

                Assert.Equal((uint)DescriptorKindEnum.Block, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data)));
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(data), (uint)DescriptorKindEnum.Text);
                return bytes;

            case LongerThanItsBytes:
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(region + 4), length + 1);
                return bytes;

            case BytesMissing:
                var text = Encoding.UTF8.GetBytes(RegionText);
                var textAt = MarkedRoot.OnlyPlaceOf(bytes, text, "the text");

                if (rootLengthAt >= 0)
                {
                    var rootLength = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(rootLengthAt));

                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(rootLengthAt), rootLength - (ulong)text.Length);
                }

                return [.. bytes.AsSpan(0, textAt), .. bytes.AsSpan(textAt + text.Length)];

            default:
                throw new ArgumentOutOfRangeException(nameof(damage), damage, "No such damage.");
        }
    }
}
