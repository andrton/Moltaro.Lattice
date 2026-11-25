using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;
using Moltaro.Lattice.TestSupport.Views;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewStorageTests
{
    private const int ChunkBytes = 65_504;

    private static readonly LatticeSchema Schema = LatticeSchema.Compile("""
        {
          "StringEncoding": "Utf8",
          "Enums": [
            { "Id": 1, "Name": "State", "UnderlyingType": "UInt8", "Members": [
              { "Name": "Closed", "Value": 0 }, { "Name": "Open", "Value": 1 }
            ] }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Document",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "String" },
                { "Id": 2, "Name": "Separate", "Type": "Binary" },
                { "Id": 3, "Name": "Shared", "Type": "String", "StringStorage": "Dictionary" },
                { "Id": 4, "Name": "Chunked", "Type": "String" },
                { "Id": 5, "Name": "State", "Type": "Enum", "Enum": "State" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);

    [Fact(Skip = ProtectedViewsRequired.Skip)]
    public async Task Every_borrowed_part_is_at_its_native_address_and_UTF8_decoding_crosses_the_chunk_seam()
    {
        await using var database = await EngineMaster.OpenAsync(Schema);
        var documents = database.Set("Document");
        var text = new string('a', ChunkBytes - 1) + "💡z";
        await documents.InsertAsync(new LatticeRecord(documents.EntityType)
        {
            ["Id"] = "short",
            ["Separate"] = Enumerable.Repeat((byte)57, 391).ToArray(),
            ["Shared"] = "shared ą",
            ["Chunked"] = text,
            ["State"] = (byte)1,
        });
        var owned = documents.Get("short")!;

        var state = (
            Copy: owned,
            Inline: AddressOf(database, "Id", DescriptorKindEnum.Inline),
            Separate: AddressOf(database, "Separate", DescriptorKindEnum.Block),
            Shared: AddressOf(database, "Shared", DescriptorKindEnum.Text),
            Chunked: AddressOf(database, "Chunked", DescriptorKindEnum.Chunked));
        Assert.True(documents.TryView("short", state, static (record, addresses) =>
        {
            AssertPart(record["Id"], addresses.Inline, 0, 1, Encoding.UTF8.GetBytes(addresses.Copy.Get<string>("Id")!));
            AssertPart(record["Separate"], addresses.Separate, 0, 1, addresses.Copy.Get<byte[]>("Separate")!);
            AssertPart(record["Shared"], addresses.Shared, 0, 1, Encoding.UTF8.GetBytes(addresses.Copy.Get<string>("Shared")!));

            var encoded = Encoding.UTF8.GetBytes(addresses.Copy.Get<string>("Chunked")!);
            var chunked = record["Chunked"];
            Assert.Equal(ChunkBytes + 4, chunked.ByteLength);
            AssertPart(chunked, addresses.Chunked, 0, 2, encoded.AsSpan(0, ChunkBytes));
            AssertPart(chunked, addresses.Chunked, 1, 2, encoded.AsSpan(ChunkBytes));
            Assert.Equal(addresses.Copy.Get<string>("Chunked"), chunked.GetString());
            Assert.Equal(addresses.Copy.Get<byte>("State"), record["State"].Get<byte>());
            return chunked.ByteLength;
        }, out var bytes));
        Assert.Equal(ChunkBytes + 4, bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => documents.TryView("short", static view => view["Chunked"].GetPart(2).Length, out _));
    }

    private static (Arena Arena, ArenaAddress Root, ValueDescriptor Descriptor) AddressOf(EngineDatabase database, string field, DescriptorKindEnum kind)
    {
        (Arena Arena, ArenaAddress Root, ValueDescriptor Descriptor)? expected = null;
        database.ForEachLiveRoot((arena, layout, _, root) =>
        {
            var offset = layout.StoredFieldOf(layout.EntityType.GetField(field)).Offset - BlockHeader.Size;
            var descriptor = ValueBlocks.Read(arena.Payload(root)[offset..]);
            Assert.Equal(kind, descriptor.Kind);
            expected = (arena, root, descriptor);
        });

        Assert.True(expected.HasValue);
        return expected.Value;
    }

    private static void AssertPart(
        scoped LatticeValueView view,
        (Arena Arena, ArenaAddress Root, ValueDescriptor Descriptor) source,
        int number,
        int count,
        scoped ReadOnlySpan<byte> expected)
    {
        var native = count == 1
            ? ValueBlocks.Contiguous(source.Arena, source.Descriptor, source.Arena.Payload(source.Root))
            : ValueBlocks.Chunk(source.Arena, ValueBlocks.Directory(source.Arena, source.Descriptor), number);
        var part = view.GetPart(number);

        Assert.Equal(count, view.PartCount);
        Assert.Equal((int)source.Descriptor.Length, view.ByteLength);
        Assert.Equal(expected.Length, part.Length);
        Assert.True(expected.SequenceEqual(part));
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(native), ref MemoryMarshal.GetReference(part)));
    }
}
