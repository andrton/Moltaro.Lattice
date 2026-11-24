namespace Moltaro.Lattice.Tests.NestedValues.Copies;

// The class of the owned type Label of FormSchema: a String, a nullable Enum as a nullable .NET enum, a List of
// Int32 as an array, a nullable Binary and a List of Binary.
internal sealed class Label
{
    public string Text { get; set; } = "";

    public Grade? Grade { get; set; } = Copies.Grade.High;

    public int[] Codes { get; set; } = [];

    public byte[]? Data { get; set; }

    public List<byte[]> Files { get; set; } = [];
}
