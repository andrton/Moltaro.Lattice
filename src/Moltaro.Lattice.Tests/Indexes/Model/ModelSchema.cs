namespace Moltaro.Lattice.Tests.Indexes.Model;

// The schemas of the model tests, in the string encoding of their cell:
// - for one scalar type, Of: the entity type Sample, bound to the class Sample of the .NET type of the
//   field, with the key Id, an Int64, and the field Value of the type, nullable or not, indexed alone by
//   ByValue;
// - for composite keys, Composite: the entity types Payment, Reading and Visit, each bound to its class,
//   with an index ByKey over its fields in the order the class states;
// - for the parts the engine cannot store yet, References and OwnedPath (UnstoredPartSearchModelTests).
// The first two declare the enum types of the model tests, whose members are values of both signs and at the ends
// of the underlying type, and one member each that no test stores (ScalarTypes):
// - Grade over Int16, Span over Int64, Wide over UInt64;
// - Rights, a Flags type over Int32 whose bits include the sign bit.
internal static class ModelSchema
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

    // The schema of the Reference parts, both to Colleague: Colleague, with ByManager over the nullable
    // Reference Manager, and Apprentice, with ByMentor over the Reference Mentor, never null; each bound to
    // its class.
    public static LatticeSchema References { get; } = LatticeSchema.Compile($$"""
        {
          "EntityTypes": [
            {
              "Id": 1, "Name": "Colleague", "ClrType": "{{typeof(Colleague).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Manager", "Type": "Reference", "EntityType": "Colleague", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByManager", "Fields": [2] } ]
            },
            {
              "Id": 2, "Name": "Apprentice", "ClrType": "{{typeof(Apprentice).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Mentor", "Type": "Reference", "EntityType": "Colleague" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByMentor", "Fields": [2] } ]
            }
          ]
        }
        """);

    // The schema of a part on a path into an owned value: Resident, bound to its class, with ByHomeCity over
    // Home.City, the City of the nullable owned value Home, of the owned type Residence.
    public static LatticeSchema OwnedPath { get; } = LatticeSchema.Compile($$"""
        {
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Residence", "ClrType": "{{typeof(Residence).FullName}}",
              "Fields": [ { "Id": 1, "Name": "City", "Type": "String" } ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Resident", "ClrType": "{{typeof(Resident).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Home", "Type": "Owned", "OwnedType": "Residence", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByHomeCity", "Fields": [ [2, 1] ] } ]
            }
          ]
        }
        """);

    // The schema of the composite keys in the encoding given.
    public static LatticeSchema Composite(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
          {{Enums}},
          "EntityTypes": [
            {
              "Id": 1, "Name": "Payment", "ClrType": "{{typeof(Payment).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Code", "Type": "Guid" },
                { "Id": 3, "Name": "Amount", "Type": "Decimal", "Nullable": true },
                { "Id": 4, "Name": "Payee", "Type": "String", "Comparison": "OrdinalIgnoreCase" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByKey", "Fields": [2, 3, 4] } ]
            },
            {
              "Id": 2, "Name": "Reading", "ClrType": "{{typeof(Reading).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Day", "Type": "DateOnly" },
                { "Id": 3, "Name": "Level", "Type": "Float64", "Nullable": true },
                { "Id": 4, "Name": "Sensor", "Type": "Int16" }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByKey", "Fields": [2, 3, 4] } ]
            },
            {
              "Id": 3, "Name": "Visit", "ClrType": "{{typeof(Visit).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Confirmed", "Type": "Boolean", "Nullable": true },
                { "Id": 3, "Name": "Room", "Type": "UInt32" },
                { "Id": 4, "Name": "At", "Type": "DateTimeOffset", "Nullable": true },
                { "Id": 5, "Name": "Note", "Type": "String", "Nullable": true }
              ],
              "PrimaryKey": [1],
              "Indexes": [ { "Id": 1, "Name": "ByKey", "Fields": [2, 3, 4, 5] } ]
            }
          ]
        }
        """);
}
