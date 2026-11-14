namespace Moltaro.Lattice.Samples;

// An ordinary class. The schema names it as the .NET class of the entity type "User":
//   "ClrType": "Moltaro.Lattice.Samples.User"
// Its properties carry the names and types of the fields declared there.
internal sealed class User
{
    public long Id { get; set; }

    public string Name { get; set; } = "";

    public string? Department { get; set; }

    public bool Enabled { get; set; }

    public string? Email { get; set; }

    public override string ToString() => $"#{Id} {Name}, {Department ?? "no department"}, {(Enabled ? "enabled" : "disabled")}";
}
