namespace Moltaro.Lattice.Tests.Indexes.Walk;

// Bound to the entity type "Row" of WalkSchema.
internal sealed class Row
{
    public long Id { get; set; }

    public int? A { get; set; }

    public string? B { get; set; }

    public long? C { get; set; }

    public string? D { get; set; }

    public Row Copy() => new() { Id = Id, A = A, B = B, C = C, D = D };

    public override string ToString() => $"{Id}: ({Show(A)}, {Show(B)}, {Show(C)}, {Show(D)})";

    private static string Show(object? value) => value switch
    {
        null => "null",
        string text when text.Length > 12 => $"\"{text[..12]}…\" ({text.Length})",
        string text => $"\"{text}\"",
        _ => value.ToString()!,
    };
}
