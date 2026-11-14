using System.Globalization;

namespace Moltaro.Lattice.Tests;

// A schema with one enum type, Code, and an entity type with a field of it, for the tests of
// what an enum type declares and accepts. The members are written as the parts of
// Descriptor.With are, with ' in place of ": { 'Name': 'Read', 'Value': 1 }.
internal static class EnumSchema
{
    public static string Json(string underlyingType, bool isFlags, string members) => $$"""
        {
          "Enums": [
            {
              "Id": 1, "Name": "Code", "UnderlyingType": "{{underlyingType}}", "Flags": {{(isFlags ? "true" : "false")}},
              "Members": [ {{members.Replace('\'', '"')}} ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Item",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Code", "Type": "Enum", "Enum": "Code" }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """;

    // An integer of any width written in a test attribute, which cannot hold an Int128.
    public static Int128 Value(string text) => Int128.Parse(text, CultureInfo.InvariantCulture);

    public static LatticeEnumType Ordinary(string underlyingType, string members) => Compile(underlyingType, isFlags: false, members);

    public static LatticeEnumType Flags(string underlyingType, string members) => Compile(underlyingType, isFlags: true, members);

    private static LatticeEnumType Compile(string underlyingType, bool isFlags, string members)
    {
        var schema = LatticeSchema.Compile(Json(underlyingType, isFlags, members));

        Assert.True(schema.TryGetEnumType("Code", out var code));
        return code;
    }
}
