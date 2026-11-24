namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// A read through a class gives every position the type its property declares by the table of
// ILatticeDatabase.Set<TEntity>(), at every depth, not the form a record holds there: an array for a List
// declared as T[], also an array of arrays and of owned values; a .NET enum, and a nullable one, for an Enum,
// in a List and in an owned value; the integer for an Enum whose property is its integer; an object of the
// class of an owned type in a property, in an array and in an array inside a List. A null is set as null,
// also where the constructor of the class gives the property another value: Spare, and the Grade of a Label.
// Two reads share none of these objects, but for an empty array, which is the one empty array of its type on
// every path (FormSchema).
public sealed class ClassFormsTests
{
    [Fact]
    public async Task Read_through_a_class_gives_every_position_the_type_its_property_declares_at_every_depth()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
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

    [Fact]
    public async Task Empty_array_read_through_a_class_is_the_one_empty_array_of_its_type_on_every_path()
    {
        await using var database = await TestDatabases.OpenMasterAsync(FormSchema.Schema);
        var forms = database.Set<Form>();

        await forms.InsertAsync(new Form { Id = 1, Matrix = [[]], Blobs = [[]], LabelRows = [[]], Main = new Label { Data = [] } });

        // Of elements a record holds as the array does (Numbers, Main.Codes), of converted ones (Matrix,
        // MaybeGrades, Labels), inside a List (Matrix, LabelRows), and the Binary values (Blobs, Main.Data);
        // the same in two reads.
        foreach (var read in new[] { forms.Get(1L)!, forms.Get(1L)! })
        {
            Assert.Same(Array.Empty<long>(), read.Numbers);
            Assert.Same(Array.Empty<int>(), read.Matrix[0]);
            Assert.Same(Array.Empty<Grade?>(), read.MaybeGrades);
            Assert.Same(Array.Empty<Label>(), read.Labels);
            Assert.Same(Array.Empty<Label>(), read.LabelRows[0]);
            Assert.Same(Array.Empty<byte>(), read.Blobs[0]);
            Assert.Same(Array.Empty<int>(), read.Main.Codes);
            Assert.Same(Array.Empty<byte>(), read.Main.Data);
        }
    }

    // A Label as one text, its Grade and its Codes included.
    private static string Show(Label label) => $"{label.Text} {label.Grade?.ToString() ?? "null"} [{string.Join(", ", label.Codes)}]";
}
