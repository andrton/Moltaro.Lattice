using System.Security.Cryptography;
using System.Text;

namespace Moltaro.Lattice.Tests.Schemas.SavedForm;

// A declaration inside a collection is itself a declaration, so a saved schema holds them one
// inside the other. A schema with a declaration nested as deep as a descriptor can write it is
// saved and loaded; a saved form whose declaration is nested deeper than any descriptor can
// declare is damage and refused with InvalidSchema, however deep it is, and the load does not
// follow it down.
public sealed class NestedDeclarationFormTests
{
    [Fact]
    public void Declaration_nested_as_deep_as_a_descriptor_can_write_it_is_saved_and_loaded()
    {
        // The JSON reader reads a descriptor to a depth of 64: the field is at a depth of 5, and
        // each element one deeper, so the Int64 inside 58 Lists inside the field is at 64.
        const int Elements = 58;
        var declaration = string.Concat(Enumerable.Repeat("""{ "Type": "List", "Element": """, Elements)) + """{ "Type": "Int64" }""" + new string('}', Elements);
        var compiled = LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                {
                  "Id": 1, "Name": "Deep",
                  "Fields": [ { "Id": 1, "Name": "Id", "Type": "Int64" }, { "Id": 2, "Name": "Value", "Type": "List", "Element": {{declaration}} } ],
                  "PrimaryKey": [1]
                }
              ]
            }
            """);

        using var stream = new MemoryStream();
        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        Assert.Equal(compiled.Fingerprint, loaded.Fingerprint);
        Assert.True(loaded.EntityTypes[0].TryGetField("Value", out var value));
        var inner = value.Declaration;
        for (var depth = 0; depth < Elements; depth++)
        {
            inner = inner.Element!;
        }

        Assert.Equal(LatticeFieldTypeEnum.List, inner.Type);
        Assert.Equal(LatticeFieldTypeEnum.Int64, inner.Element!.Type);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(100_000)]
    public void Form_with_a_declaration_nested_deeper_than_a_descriptor_can_declare_is_refused(int depth)
    {
        // A whole form of format 5 whose digest is right: an entity type N with the key Id and
        // a field Deep that is a List of Lists, depth times, of Int64.
        var body = Bytes(writer =>
        {
            writer.Write(1); // catalog version
            writer.Write((byte)LatticeStringEncodingEnum.Utf8);
            writer.Write(0); // owned types
            writer.Write(0); // enum types
            writer.Write(1); // entity types
            writer.Write(1); // identity of N
            writer.Write("N");
            writer.Write((byte)0); // no class
            writer.Write(1); // key field
            writer.Write(2); // fields
            writer.Write(1);
            writer.Write("Id");
            WriteDeclaration(writer, LatticeFieldTypeEnum.Int64);
            writer.Write(2);
            writer.Write("Deep");
            for (var level = 0; level < depth; level++)
            {
                WriteDeclaration(writer, LatticeFieldTypeEnum.List);
            }

            WriteDeclaration(writer, LatticeFieldTypeEnum.Int64);
            writer.Write(0); // indexes
        });

        var form = Bytes(writer =>
        {
            writer.Write("MLSC"u8);
            writer.Write(5);
            writer.Write(body.Length);
            writer.Write(body);
        });

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => LatticeSchema.Load(new MemoryStream([.. form, .. SHA256.HashData(form)])));
    }

    // The part of a declaration that every type has: the type, not nullable, no text policies
    // and no enum type. A List is followed by the declaration of its element.
    private static void WriteDeclaration(BinaryWriter writer, LatticeFieldTypeEnum type)
    {
        writer.Write((byte)type);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
    }

    private static byte[] Bytes(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            write(writer);
        }

        return stream.ToArray();
    }
}
