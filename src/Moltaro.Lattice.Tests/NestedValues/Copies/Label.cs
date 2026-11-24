namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// The class of the owned type Label of ClassFormsTests: a String, a nullable Enum as a nullable .NET enum and
// a List of Int32 as an array.
internal sealed class Label
{
    public string Text { get; set; } = "";

    public Grade? Grade { get; set; } = Copies.Grade.High;

    public int[] Codes { get; set; } = [];
}
