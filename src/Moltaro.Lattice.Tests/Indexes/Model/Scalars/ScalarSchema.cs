namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// The schema of a model test of one scalar type, in the string encoding of its cell, Of: the entity type
// Sample, bound to the class Sample of the .NET type of the field, with the key Id, an Int64, and the field
// Value of the type, nullable or not, indexed alone by ByValue. It declares the enum types of the model
// tests, whose members are values of both signs and at the ends of the underlying type, and one member each
// that no test stores (ScalarTypes):
// - Grade over Int16, Span over Int64, Wide over UInt64;
// - Rights, a Flags type over Int32 whose bits include the sign bit.
internal static class ScalarSchema
{
    private const string Enums = """
        "Enums": [
          {
            "Id": 1, "Name": "Grade", "UnderlyingType": "Int16",
            "Members": [
              { "Name": "Lowest", "Value": -32768 }, { "Name": "Lower", "Value": -300 }, { "Name": "Low", "Value": -2 },
              { "Name": "MinusOne", "Value": -1 }, { "Name": "Zero", "Value": 0 }, { "Name": "One", "Value": 1 },
              { "Name": "High", "Value": 5 }, { "Name": "Unused", "Value": 7 }, { "Name": "Byte", "Value": 255 },
              { "Name": "AboveByte", "Value": 256 }, { "Name": "Higher", "Value": 300 }, { "Name": "Highest", "Value": 32767 }
            ]
          },
          {
            "Id": 2, "Name": "Span", "UnderlyingType": "Int64",
            "Members": [
              { "Name": "Lowest", "Value": -9223372036854775808 }, { "Name": "BelowInt32", "Value": -4294967296 },
              { "Name": "Low", "Value": -2 }, { "Name": "MinusOne", "Value": -1 }, { "Name": "Zero", "Value": 0 },
              { "Name": "One", "Value": 1 }, { "Name": "Unused", "Value": 7 }, { "Name": "AboveInt32", "Value": 4294967296 },
              { "Name": "Highest", "Value": 9223372036854775807 }
            ]
          },
          {
            "Id": 3, "Name": "Wide", "UnderlyingType": "UInt64",
            "Members": [
              { "Name": "Zero", "Value": 0 }, { "Name": "One", "Value": 1 }, { "Name": "Unused", "Value": 7 },
              { "Name": "LastInt64", "Value": 9223372036854775807 }, { "Name": "FirstAbove", "Value": 9223372036854775808 },
              { "Name": "Above", "Value": 9223372036854775813 }, { "Name": "BeforeLast", "Value": 18446744073709551614 },
              { "Name": "Last", "Value": 18446744073709551615 }
            ]
          },
          {
            "Id": 4, "Name": "Rights", "UnderlyingType": "Int32", "Flags": true,
            "Members": [
              { "Name": "Read", "Value": 1 }, { "Name": "Write", "Value": 2 }, { "Name": "Delete", "Value": 4 },
              { "Name": "Unused", "Value": 8 }, { "Name": "Share", "Value": 1073741824 }, { "Name": "Sign", "Value": -2147483648 }
            ]
          }
        ]
        """;

    // The schema of one scalar type in the encoding of the cell, Sample bound to the class given.
    public static LatticeSchema Of(ScalarCell cell, Type sample) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{cell.Encoding}}",
          {{Enums}},
          "EntityTypes": [
            {
              "Id": 1, "Name": "Sample", "ClrType": "{{sample.FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Value", {{cell.Type.Declaration}}, "Nullable": {{(cell.Nullable ? "true" : "false")}} }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByValue", "Fields": [2] } ]
            }
          ]
        }
        """);
}
