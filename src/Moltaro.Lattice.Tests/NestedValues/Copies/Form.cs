namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// The class of the entity type Form of FormSchema: each List in a form of the class other than the one
// a record holds, an array or a List of a .NET enum, at every depth, and the owned values of Label in a
// property, an array and a List.
internal sealed class Form
{
    public long Id { get; set; }

    public long[] Numbers { get; set; } = [];

    public int[][] Matrix { get; set; } = [];

    public List<Grade> Grades { get; set; } = [];

    public Grade?[] MaybeGrades { get; set; } = [];

    public List<byte> GradeValues { get; set; } = [];

    public byte[][] Blobs { get; set; } = [];

    public Label Main { get; set; } = new();

    public Label? Spare { get; set; } = new();

    public Label[] Labels { get; set; } = [];

    public List<Label[]> LabelRows { get; set; } = [];
}
