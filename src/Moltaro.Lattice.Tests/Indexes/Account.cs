namespace Moltaro.Lattice.Tests.Indexes;

// Bound to the entity type "Account" by "ClrType" in the schema of UniqueIndexTests.
internal sealed class Account
{
    public long Id { get; set; }

    public string Login { get; set; } = "";

    public string Tenant { get; set; } = "";

    public string? Email { get; set; }
}
