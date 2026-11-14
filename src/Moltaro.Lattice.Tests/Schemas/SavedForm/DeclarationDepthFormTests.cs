using static Moltaro.Lattice.Tests.Schemas.DeepDeclarations;
using static Moltaro.Lattice.Tests.Schemas.SavedForm.HandWrittenForm;

namespace Moltaro.Lattice.Tests.Schemas.SavedForm;

// The depth of a declaration, at most 32, is a rule of the model, so it holds for a saved schema
// as for a descriptor. A schema whose declarations are 32 deep is saved and loaded; a saved form
// nested deeper is refused however deep it is, and the load does not follow it down.
public sealed class DeclarationDepthFormTests
{
    [Fact]
    public void Schema_with_declarations_32_deep_is_saved_and_loaded_and_is_equal()
    {
        // Lists is a List 31 times over of Int64; Deep holds Lists around a chain of owned types.
        var compiled = LatticeSchema.Compile(HolderSchema(
            OwnedChain(15),
            Field(2, "Lists", InLists(31, Scalar)),
            Field(3, "Deep", InLists(16, OwnedLevel1))));

        using var stream = new MemoryStream();
        compiled.Save(stream);
        stream.Position = 0;
        var loaded = LatticeSchema.Load(stream);

        Assert.Equal(compiled.Fingerprint, loaded.Fingerprint);
        Assert.Equal(compiled.EntityTypes[0].LayoutFingerprint, loaded.EntityTypes[0].LayoutFingerprint);
        Assert.Equal(15, loaded.OwnedTypes.Count);

        Assert.True(loaded.EntityTypes[0].TryGetField("Lists", out var lists));
        var inner = lists.Declaration;
        for (var list = 0; list < 31; list++)
        {
            Assert.Equal(LatticeFieldTypeEnum.List, inner.Type);
            inner = inner.Element!;
        }

        Assert.Equal(LatticeFieldTypeEnum.Int64, inner.Type);
    }

    [Fact(Skip = NotImplemented.DeclarationDepth)]
    public void Saved_schema_nested_33_deep_is_refused()
    {
        // The same form 32 deep loads.
        Load(Form(ListsBody(depth: 32)));

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => Load(Form(ListsBody(depth: 33))));
    }

    [Theory]
    [InlineData(1_000)]
    [InlineData(1_000_000)]
    public void Saved_schema_nested_very_much_deeper_is_refused_without_exhausting_the_stack(int depth)
    {
        // A reader that followed the declarations down would end the test process.
        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => Load(Form(ListsBody(depth))));
    }

    [Fact(Skip = NotImplemented.DeclarationDepth)]
    public void Saved_schema_with_a_chain_of_owned_types_33_deep_is_refused()
    {
        // 31 owned types make the field Deep 32 deep; 32 owned types make it 33.
        Load(Form(OwnedChainBody(ownedTypes: 31)));

        TestData.ExpectError(LatticeErrorEnum.InvalidSchema, () => Load(Form(OwnedChainBody(ownedTypes: 32))));
    }

    private static LatticeSchema Load(byte[] data) => LatticeSchema.Load(new MemoryStream(data));

    // The body of a schema with an entity type N whose field Deep is a List of Lists of Int64,
    // depth deep in all.
    private static byte[] ListsBody(int depth) => Bytes(writer =>
    {
        writer.Write(1); // catalog version
        writer.Write((byte)LatticeStringEncodingEnum.Utf8);
        writer.Write(0); // owned types
        writer.Write(0); // enum types
        WriteEntityType(writer, deep =>
        {
            for (var list = 1; list < depth; list++)
            {
                WriteDeclaration(deep, LatticeFieldTypeEnum.List);
            }

            WriteDeclaration(deep, LatticeFieldTypeEnum.Int64);
        });
    });

    // The body of a schema with the owned types Level1 to LevelN, each holding the next in its
    // field Next and the last an Int64 in its field Value, and an entity type N whose field Deep
    // holds a Level1: ownedTypes + 1 deep.
    private static byte[] OwnedChainBody(int ownedTypes) => Bytes(writer =>
    {
        writer.Write(1); // catalog version
        writer.Write((byte)LatticeStringEncodingEnum.Utf8);
        writer.Write(ownedTypes);
        for (var level = 1; level <= ownedTypes; level++)
        {
            writer.Write(level); // identity
            writer.Write($"Level{level}");
            writer.Write((byte)0); // no class
            writer.Write(1); // fields
            writer.Write(1); // identity of the field
            if (level < ownedTypes)
            {
                writer.Write("Next");
                WriteDeclaration(writer, LatticeFieldTypeEnum.Owned);
                writer.Write($"Level{level + 1}");
            }
            else
            {
                writer.Write("Value");
                WriteDeclaration(writer, LatticeFieldTypeEnum.Int64);
            }
        }

        writer.Write(0); // enum types
        WriteEntityType(writer, deep =>
        {
            WriteDeclaration(deep, LatticeFieldTypeEnum.Owned);
            deep.Write("Level1");
        });
    });

    // One entity type N with the key Id and the field Deep, whose declaration writeDeep writes.
    private static void WriteEntityType(BinaryWriter writer, Action<BinaryWriter> writeDeep)
    {
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
        writeDeep(writer);
        writer.Write(0); // indexes
    }
}
