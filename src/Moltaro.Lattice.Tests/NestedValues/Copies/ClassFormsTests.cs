namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// A read through a class gives every position the type its property declares by the table of
// ILatticeDatabase.Set<TEntity>(), at every depth, not the form a record holds there: an array for a List
// declared as T[], also an array of arrays and of owned values; a .NET enum, and a nullable one, for an Enum,
// in a List and in an owned value; the integer for an Enum whose property is its integer; an object of the
// class of an owned type in a property, in an array and in an array inside a List. A null is set as null,
// also where the constructor of the class gives the property another value: Spare, and the Grade of a Label.
// Two reads share none of these objects.
//
// The schema: Form, whose key Id is an Int64, with Numbers, a List of Int64; Matrix, a List of Lists of
// Int32; Grades, MaybeGrades and GradeValues, Lists of the Enum Grade, nullable in MaybeGrades; Blobs, a List
// of Binary; Main and Spare, owned Labels, Spare nullable; Labels, a List of Labels; LabelRows, a List of
// Lists of Labels. A Label holds Text, a String, Grade, a nullable Grade, and Codes, a List of Int32.
public sealed class ClassFormsTests
{
    private static readonly LatticeSchema Schema = LatticeSchema.Compile($$"""
        {
          "Enums": [ { "Id": 1, "Name": "Grade", "UnderlyingType": "UInt8", "Members": [ { "Name": "Low", "Value": 0 }, { "Name": "High", "Value": 1 } ] } ],
          "OwnedTypes": [
            {
              "Id": 1, "Name": "Label", "ClrType": "{{typeof(Label).FullName}}",
              "Fields": [
                { "Id": 1, "Name": "Text", "Type": "String" },
                { "Id": 2, "Name": "Grade", "Type": "Enum", "Enum": "Grade", "Nullable": true },
                { "Id": 3, "Name": "Codes", "Type": "List", "Element": { "Type": "Int32" } }
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

    [Fact]
    public async Task Read_through_a_class_gives_every_position_the_type_its_property_declares_at_every_depth()
    {
        await using var database = await TestDatabases.OpenMasterAsync(Schema);
        var forms = database.Set<Form>();
        var written = new Form
        {
            Id = 1,
            Numbers = [3, -1, 3, long.MaxValue],
            Matrix = [[], [1, 2], [2, 2]],
            Grades = [Grade.High, Grade.Low, Grade.High],
            MaybeGrades = [null, Grade.Low, Grade.High, null],
            GradeValues = [1, 0, 1],
            Blobs = [[], [7], [8, 9]],
            Main = new Label { Text = "main", Grade = Grade.Low, Codes = [5, 4] },
            Spare = null,
            Labels = [new Label { Text = "a", Grade = Grade.High, Codes = [1] }, new Label { Text = "b", Grade = null, Codes = [] }],
            LabelRows = [[], [new Label { Text = "c", Grade = Grade.Low, Codes = [3, 3] }]],
        };

        await forms.InsertAsync(written);

        var read = forms.Get(1L)!;

        Assert.Equal(written.Numbers, read.Numbers);
        Assert.Equal(written.Matrix, read.Matrix);
        Assert.Equal(written.Grades, read.Grades);
        Assert.Equal(written.MaybeGrades, read.MaybeGrades);
        Assert.Equal(written.GradeValues, read.GradeValues);
        Assert.Equal(written.Blobs, read.Blobs);
        Assert.Equal(Show(written.Main), Show(read.Main));
        Assert.Null(read.Spare);
        Assert.Equal(written.Labels.Select(Show), read.Labels.Select(Show));
        Assert.Null(read.Labels[1].Grade);
        Assert.Equal(written.LabelRows.Select(row => row.Select(Show)), read.LabelRows.Select(row => row.Select(Show)));

        // A second read makes its own objects at every depth.
        var again = forms.Get(1L)!;

        Assert.NotSame(read.Numbers, again.Numbers);
        Assert.NotSame(read.Matrix[1], again.Matrix[1]);
        Assert.NotSame(read.Grades, again.Grades);
        Assert.NotSame(read.GradeValues, again.GradeValues);
        Assert.NotSame(read.Blobs[2], again.Blobs[2]);
        Assert.NotSame(read.Main.Codes, again.Main.Codes);
        Assert.NotSame(read.Labels[0], again.Labels[0]);
        Assert.NotSame(read.LabelRows[1][0], again.LabelRows[1][0]);
    }

    // A Label as one text, its Grade and its Codes included.
    private static string Show(Label label) => $"{label.Text} {label.Grade?.ToString() ?? "null"} [{string.Join(", ", label.Codes)}]";
}
