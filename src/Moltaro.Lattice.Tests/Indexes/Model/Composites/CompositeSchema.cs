namespace Moltaro.Lattice.Tests.Indexes.Model.Composites;

// The schema of the composite keys of the model tests, in the string encoding of their cell, Of: the entity
// types Payment, Reading and Visit, each bound to its class, with an index ByKey over its fields in the
// order the class states.
internal static class CompositeSchema
{
    // The schema of the composite keys in the encoding given.
    public static LatticeSchema Of(string encoding) => LatticeSchema.Compile($$"""
        {
          "StringEncoding": "{{encoding}}",
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
              "Indexes": [ { "Id": 1, "Name": "ByKey", "Fields": [2, 4, 3] } ]
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
