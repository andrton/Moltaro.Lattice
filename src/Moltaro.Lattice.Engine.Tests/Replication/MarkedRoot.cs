using System.Buffers.Binary;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Replication;

// A root whose parts a test can find in a frame of a load, or in a frame of a transaction that carries it
// in a Write (Transactions), without the encoding of the frame, so that it can damage one part of it on
// its way to a child. By the planner's decision of 17 November 2026 after
// the study of S10 (point 2) a root travels as its type id, its last mutation sequence, its layout
// version, the bytes of its root payload and then the bytes of each value stored out of line; the
// stored bytes travel as they are. Two values mark the root:
// - the identity of its entity type, TypeId, whose four bytes appear once in the frame, where the type
//   id of the root travels. The tests assume the natural widths of the envelope, as the planner's order
//   gives it: the type id in 4 bytes, the last mutation sequence in 8, the layout version in 4;
// - the value of its field Marker, an Int64 whose eight bytes appear once, where that field stands in
//   the root payload. The payload is the root block after its header (RootLayout.RootPayload), so where
//   the stored layout puts the null bitmap and the descriptor of a field, relative to Marker, is where
//   they travel.
// The key Code is a short text, inline, as Name is; Note a nullable text, inline and not null; Data a
// Binary value of 1,000 zero bytes, in a block of its own, which travels last, so the root ends where its
// bytes end. Every record of the tests has these values but for its key, so that a frame that holds one
// root holds each mark once.
internal static class MarkedRoot
{
    public const uint TypeId = 0x5EFE0001;

    public const long Marker = 0x0123456789ABCDEF;

    public static LatticeSchema Schema { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": {{TypeId}}, "Name": "Marked",
              "Fields": [
                { "Id": 1, "Name": "Code", "Type": "String" },
                { "Id": 2, "Name": "Marker", "Type": "Int64" },
                { "Id": 3, "Name": "Name", "Type": "String" },
                { "Id": 4, "Name": "Note", "Type": "String", "Nullable": true },
                { "Id": 5, "Name": "Data", "Type": "Binary" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    public static LatticeEntityType EntityType => Schema.EntityTypes.Single();

    public static LatticeRecord Record() => Record("code");

    public static LatticeRecord Record(string code) => new(EntityType)
    {
        ["Code"] = code,
        ["Marker"] = Marker,
        ["Name"] = "name",
        ["Note"] = "a note",
        ["Data"] = new byte[1_000],
    };

    // Where the type id of the root stands in the bytes.
    public static int TypeIdAt(ReadOnlySpan<byte> bytes)
    {
        Span<byte> typeId = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(typeId, TypeId);

        return OnlyPlaceOf(bytes, typeId, "the type id");
    }

    // Where the root payload begins in the bytes: as far before the marker as the field Marker stands
    // after the header of a root block.
    public static int PayloadAt(ReadOnlySpan<byte> bytes) =>
        OnlyPlaceOf(bytes, BitConverter.GetBytes(Marker), "the marker") - (FieldOf("Marker").Offset - BlockHeader.Size);

    // How the stored layout places a field: its offset from the start of the root block, the header
    // included, and its bit in the null bitmap, which begins the root payload.
    public static StoredField FieldOf(string name)
    {
        var layout = RootLayout.Compile(EntityType, Schema);

        Assert.True(EntityType.TryGetField(name, out var field));
        return layout.StoredFieldOf(field);
    }

    private static int OnlyPlaceOf(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> value, string what)
    {
        var first = bytes.IndexOf(value);

        Assert.True(first >= 0, $"The frame of the root does not hold {what} of the root.");
        Assert.True(bytes[(first + 1)..].IndexOf(value) < 0, $"The frame of the root holds {what} of the root more than once.");

        return first;
    }
}
