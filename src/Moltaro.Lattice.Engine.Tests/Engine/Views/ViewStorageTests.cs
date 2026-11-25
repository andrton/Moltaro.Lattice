using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Moltaro.Lattice.Access;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Values;

namespace Moltaro.Lattice.Engine.Tests.Engine.Views;

public sealed class ViewStorageTests
{
    private const int ChunkBytes = 65_504;

    private static LatticeSchema SchemaFor(LatticeStringEncodingEnum encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
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

    [Theory]
    [InlineData(LatticeStringEncodingEnum.Utf8)]
    [InlineData(LatticeStringEncodingEnum.Utf16)]
    public async Task Borrowed_parts_keep_their_native_addresses_and_decoding_uses_the_schema_encoding(LatticeStringEncodingEnum format)
    {
        await using var database = await EngineMaster.OpenAsync(SchemaFor(format));
        var documents = database.Set("Document");
        var utf8 = format == LatticeStringEncodingEnum.Utf8;
        var encoding = utf8 ? Encoding.UTF8 : Encoding.Unicode;

        // Leave one byte of the UTF-8 code point or one UTF-16 code unit in the first chunk.
        var prefix = utf8 ? ChunkBytes - 1 : ChunkBytes / 2 - 1;
        var text = new string('a', prefix) + "💡z";
        const string Key = "short";
        const string Shared = "shared ą";
        var binary = Enumerable.Repeat((byte)57, 391).ToArray();
        await documents.InsertAsync(new LatticeRecord(documents.EntityType)
        {
            ["Id"] = Key,
            ["Separate"] = binary,
            ["Shared"] = Shared,
            ["Chunked"] = text,
            ["State"] = (byte)1,
        });
        var owned = documents.Get(Key)!;
        Assert.Equal(Key, owned.Get<string>("Id"));
        Assert.Equal(Shared, owned.Get<string>("Shared"));
        Assert.Equal(text, owned.Get<string>("Chunked"));
        Assert.Equal(binary, owned.Get<byte[]>("Separate"));

        var state = (
            Copy: owned,
            Text: text,
            KeyBytes: encoding.GetBytes(Key),
            SharedBytes: encoding.GetBytes(Shared),
            ChunkedBytes: encoding.GetBytes(text),
            BinaryBytes: binary,
            Inline: AddressOf(database, "Id", DescriptorKindEnum.Inline),
            Separate: AddressOf(database, "Separate", DescriptorKindEnum.Block),
            Shared: AddressOf(database, "Shared", DescriptorKindEnum.Text),
            Chunked: AddressOf(database, "Chunked", DescriptorKindEnum.Chunked));
        Assert.True(documents.TryView(Key, state, static (record, addresses) =>
        {
            AssertPart(record["Id"], addresses.Inline, 0, 1, addresses.KeyBytes);
            AssertPart(record["Separate"], addresses.Separate, 0, 1, addresses.BinaryBytes);
            AssertPart(record["Shared"], addresses.Shared, 0, 1, addresses.SharedBytes);
            Assert.Equal(addresses.Copy.Get<string>("Id"), record["Id"].GetString());
            Assert.Equal(addresses.Copy.Get<string>("Shared"), record["Shared"].GetString());

            var encoded = addresses.ChunkedBytes;
            var chunked = record["Chunked"];
            Assert.Equal(ChunkBytes + 4, chunked.ByteLength);
            AssertPart(chunked, addresses.Chunked, 0, 2, encoded.AsSpan(0, ChunkBytes));
            AssertPart(chunked, addresses.Chunked, 1, 2, encoded.AsSpan(ChunkBytes));
            var decoded = chunked.GetString();
            Assert.Equal(addresses.Text, decoded);
            Assert.Equal(addresses.Copy.Get<string>("Chunked"), decoded);
            Assert.Equal(addresses.Copy.Get<byte>("State"), record["State"].Get<byte>());
            return chunked.ByteLength;
        }, out var bytes));
        Assert.Equal(ChunkBytes + 4, bytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => documents.TryView(Key, static view => view["Chunked"].GetPart(2).Length, out _));
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
