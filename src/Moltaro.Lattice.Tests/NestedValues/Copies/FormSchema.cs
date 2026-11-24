namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// The schema of the tests of the forms a class holds (ClassFormsTests, ArrayCopiesTests): Form, whose key Id
// is an Int64, with Numbers, a List of Int64; Matrix, a List of Lists of Int32; Grades, MaybeGrades and
// GradeValues, Lists of the Enum Grade, nullable in MaybeGrades; Blobs, a List of Binary; Main and Spare,
// owned Labels, Spare nullable; Labels, a List of Labels; LabelRows, a List of Lists of Labels. A Label holds
// Text, a String, Grade, a nullable Grade, Codes, a List of Int32, Data, a nullable Binary, and Files, a List
// of Binary. Form, Label and Grade are the classes and the enum the schema names; every List of Form and
// Label is an array in the class but for Grades, Files and LabelRows, whose elements are arrays.
internal static class FormSchema
{
    public static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "Enums": [ { "Id": 1, "Name": "Grade", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] } ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Label", "ClrType": "{{typeof(Label).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Text", "Type": "String" },
                { "Id": 2, "Name": "Grade", "Type": "Enum", "Enum": "Grade", "Nullable": true },
                { "Id": 3, "Name": "Codes", "Type": "List", "Element": { "Type": "Int32" } },
                { "Id": 4, "Name": "Data", "Type": "Binary", "Nullable": true },
                { "Id": 5, "Name": "Files", "Type": "List", "Element": { "Type": "Binary" } }
              ]
            }
          ],
          "EntityTypes": [
            {
              "Id": 1, "Name": "Form", "ClrType": "{{typeof(Form).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Id", "Type": "Int64" },
                { "Id": 2, "Name": "Numbers", "Type": "List", "Element": { "Type": "Int64" } },
                { "Id": 3, "Name": "Matrix", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Int32" } } },
                { "Id": 4, "Name": "Grades", "Type": "List", "Element": { "Type": "Enum", "Enum": "Grade" } },
                { "Id": 5, "Name": "MaybeGrades", "Type": "List", "Element": { "Type": "Enum", "Enum": "Grade", "Nullable": true } },
                { "Id": 6, "Name": "GradeValues", "Type": "List", "Element": { "Type": "Enum", "Enum": "Grade" } },
                { "Id": 7, "Name": "Blobs", "Type": "List", "Element": { "Type": "Binary" } },
                { "Id": 8, "Name": "Main", "Type": "Owned", "OwnedType": "Label" },
                { "Id": 9, "Name": "Spare", "Type": "Owned", "OwnedType": "Label", "Nullable": true },
                { "Id": 10, "Name": "Labels", "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Label" } },
                { "Id": 11, "Name": "LabelRows", "Type": "List", "Element": { "Type": "List", "Element": { "Type": "Owned", "OwnedType": "Label" } } }
              ],
              "PrimaryKey": [1]
            }
          ]
        }
        """);
}
