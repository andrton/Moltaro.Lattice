using System.Text;
using Moltaro.Lattice.Schema;
using Moltaro.Lattice.Storage;
using Moltaro.Lattice.Storage.Roots;

namespace Moltaro.Lattice.Engine.Tests.Storage.Values;

// Schema descriptors of an entity type whose fixed part is as large as a test needs, for the tests
// at the bound of one block. The entity type has the key Id, an Int64 of identity 1; the fields a
// test gives, written as ItemSchema writes them, with identities below 1000; and filler fields,
// none of them nullable: decimals Decimal fields and bytes UInt8 fields, with identities from 1000.
// By the formula of the brief of S05 its fixed part is therefore 32 + 8 + 16 × decimals + bytes,
// plus what the given fields take.
internal static class WideSchema
{
    public static LatticeSchema Compile(string entityTypeName, int decimals, int bytes, string fields = "")
    {
        var declarations = new StringBuilder();

        declarations.Append("{ \"Id\": 1, \"Name\": \"Id\", \"Type\": \"Int64\" }");

        if (fields.Length > 0)
        {
            declarations.Append(", ").Append(fields.Replace('\'', '"'));
        }

        for (var index = 0; index < decimals; index++)
        {
            declarations.Append($", {{ \"Id\": {1000 + index}, \"Name\": \"Decimal{index}\", \"Type\": \"Decimal\" }}");
        }

        for (var index = 0; index < bytes; index++)
        {
            declarations.Append($", {{ \"Id\": {1000 + decimals + index}, \"Name\": \"Byte{index}\", \"Type\": \"UInt8\" }}");
        }

        return LatticeSchema.Compile($$"""
            {
              "EntityTypes": [
                { "Id": 1, "Name": "{{entityTypeName}}", "Fields": [ {{declarations}} ], "PrimaryKey": [1] }
              ]
            }
            """);
    }

    // Sets Id and every filler field, so that only the fields the test gave are left to it.
    public static void SetKeyAndFillers(ref RootWriter writer, RootArena wide)
    {
        foreach (var field in wide.EntityType.Fields)
        {
            switch (field.Declaration.Type)
            {
                case LatticeFieldTypeEnum.Int64 when field.Name == "Id":
                    writer.SetInteger(field, 1);
                    break;
                case LatticeFieldTypeEnum.Decimal when field.Id >= 1000:
                    writer.SetDecimal(field, field.Id);
                    break;
                case LatticeFieldTypeEnum.UInt8 when field.Id >= 1000:
                    writer.SetUnsigned(field, (ulong)(field.Id % 256));
                    break;
            }
        }
    }
}
