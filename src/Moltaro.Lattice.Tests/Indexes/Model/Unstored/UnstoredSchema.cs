namespace Moltaro.Lattice.Tests.Indexes.Model.Unstored;

// The schemas of the parts the engine cannot store yet (UnstoredPartSearchModelTests): References and
// OwnedPath.
internal static class UnstoredSchema
{
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
}
