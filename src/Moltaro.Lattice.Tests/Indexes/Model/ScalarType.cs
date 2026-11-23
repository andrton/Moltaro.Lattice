namespace Moltaro.Lattice.Tests.Indexes.Model;

// A scalar type an index part can be, as the model tests store and search it (ScalarTypes):
// - its declaration in a field of the schema, and the order of its values (IndexModel);
// - the edge values every cell stores, and the random values a seeded generator draws, given in the .NET
//   type a record holds for the field;
// - values that no cell stores, between the stored ones, which a search finds nothing for and a range
//   takes as a bound between them; the generator never draws them;
// - the least and the greatest value of the type, where it has them: a nullable cell stores neither, so
//   that they are bounds beyond both ends of what it stores, and a cell that is never null stores both;
// - how a cell of it runs, with the .NET type of its field (ScalarRun.Of).
internal sealed record ScalarType
{
    public required string Name { get; init; }

    // The members of the declaration of a field of the type, after its identity and its name.
    public required string Declaration { get; init; }

    public IComparer<object> Order { get; init; } = IndexModel.ByValue;

    public required IReadOnlyList<object> Edges { get; init; }

    public required Func<Random, object> Random { get; init; }

    public IReadOnlyList<object> Between { get; init; } = [];

    public object? Least { get; init; }

    public object? Greatest { get; init; }

    public required Func<ScalarCell, Task> Run { get; init; }

    // Runs the cell of this type, its field nullable or not, in a database of the string encoding given.
    public Task RunAsync(bool nullable, string encoding = "Utf8") => Run(new ScalarCell(this, nullable, encoding));
}
