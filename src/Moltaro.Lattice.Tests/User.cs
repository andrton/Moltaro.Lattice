namespace Moltaro.Lattice.Tests;

// Bound to the entity type "User" by "ClrType" in the schema below.
internal sealed class User
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Email { get; set; }

    public int? Age { get; set; }

    public bool Enabled { get; set; }

    // Not declared in the schema, so it is not stored.
    public string? Note { get; set; }
}
