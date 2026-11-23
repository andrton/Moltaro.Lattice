namespace Moltaro.Lattice.Tests.Indexes.Model.Scalars;

// One cell of the model tests of one scalar type: the type, whether its field is nullable, and the string
// encoding of the database, which only a text depends on.
internal sealed record ScalarCell(ScalarType Type, bool Nullable, string Encoding = "Utf8")
{
    public override string ToString() =>
        $"{Type.Name} {(Nullable ? "nullable" : "not nullable")} in {Encoding}, {ScalarRun.Count} entities of seed {ModelRun.Seed}";
}
