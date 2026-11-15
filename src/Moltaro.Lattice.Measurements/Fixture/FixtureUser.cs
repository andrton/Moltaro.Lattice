namespace Moltaro.Lattice.Measurements.Fixture;

// A user of the scalar form of the reference fixture as an object of a class, for the scenarios
// that read through a class: the entity type User with this class named as its ClrType. The class
// changes neither fingerprint of the schema, so the schema is still the scalar form.
internal sealed class FixtureUser
{
    public long Id { get; set; }

    public string Login { get; set; } = "";

    public string Email { get; set; } = "";

    public string DisplayName { get; set; } = "";

    public string Region { get; set; } = "";

    public bool IsActive { get; set; }
}
